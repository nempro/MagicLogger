using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MagicLogger;

public interface IAnalysisProvider
{
    IReadOnlyList<FragmentDraft> Analyze(NormalizedConversation conversation);
}

public sealed class HeuristicAnalysisProvider : IAnalysisProvider
{
    private static readonly string[] PersonalTerms =
    [
        "気持ち", "感情", "感じ", "不安", "怖", "嬉", "悲", "幸せ", "寂", "好き", "嫌い", "大切",
        "信頼", "自由", "依存", "帰属", "関係", "友達", "家族", "恋", "愛", "人生", "価値観",
        "自己決定", "安心", "居場所", "BDSM", "創作観", "美学", "夢", "記憶", "選択"
    ];

    private static readonly string[] WorkTerms =
    [
        "実装", "コード", "テスト", "バグ", "リポジトリ", "git", "sqlite", "database", "schema",
        "class", "function", "build", "deploy", "修正", "エラー", "stack trace", "ログ出力", "commit",
        "pull request", "ブランチ", "差分", "コンパイル", "migration", "api endpoint", "単体テスト"
    ];

    private static readonly string[] StrongPersonalTerms =
    [
        "気持ち", "感情", "不安", "怖", "嬉", "悲", "幸せ", "寂", "愛", "恋", "人生", "価値観",
        "信頼", "自由", "自己決定", "安心", "居場所", "BDSM", "創作観", "美学", "孤独", "承認", "欲望", "性癖"
    ];

    private static readonly string[] ReflectiveMarkers =
    [
        "私は", "わたしは", "自分", "僕", "俺", "と感じ", "と思う", "と思った", "気づいた", "わかった",
        "大切にしたい", "望んで", "したくない", "してほしい"
    ];

    private static readonly string[] ConceptTerms =
    [
        "信頼", "自由", "自己決定", "依存", "帰属", "関係", "家族", "友達", "恋愛", "愛情", "不安",
        "幸福", "孤独", "創作", "仕事", "日常", "選択", "記憶", "価値観", "安心", "変化", "未来",
        "過去", "対話", "決断", "居場所", "承認", "身体", "欲望", "責任", "境界", "美学"
    ];

    private static readonly string[] KnownTags =
    [
        "Midjourney", "Codex", "ChatGPT", "Whisper Garden", "BDSM", "TTS", "UI", "Daily Character",
        "Stable Diffusion", "DALL-E", "GitHub", "SQLite", "WPF", "C#", "Python", "音声", "画像", "記事化"
    ];

    public IReadOnlyList<FragmentDraft> Analyze(NormalizedConversation conversation)
    {
        var useful = conversation.Messages
            .Where(message => message.Role is "user" or "assistant")
            .Where(message => !string.IsNullOrWhiteSpace(message.RawText) && !IsNoise(message.RawText))
            .OrderBy(message => message.Timestamp)
            .ThenBy(message => message.MessageId, StringComparer.Ordinal)
            .ToList();
        if (useful.Count == 0) return [];

        var blocks = BuildUserTurnBlocks(useful);
        var titleLooksLikeWork = Score(conversation.Title, WorkTerms) > 0;
        var workBlockCount = blocks.Count(block => Score(string.Join("\n", block.Where(message => message.Role == "user").Select(message => message.RawText)), WorkTerms) > 0);
        var personalBlockCount = blocks.Count(IsPersonalBlock);
        var hasWork = titleLooksLikeWork || (workBlockCount > 0 && (personalBlockCount == 0 || workBlockCount >= personalBlockCount));
        List<List<SourceMessage>> selectedSections;

        if (!hasWork)
            selectedSections = [useful];
        else
            selectedSections = SelectPersonalSections(blocks);

        if (selectedSections.Count == 0) return [];
        selectedSections = selectedSections.SelectMany(SplitByContinuity).ToList();
        var drafts = new List<FragmentDraft>();
        for (var sectionIndex = 0; sectionIndex < selectedSections.Count; sectionIndex++)
        {
            var selected = selectedSections[sectionIndex];
            var excerptSource = selected.FirstOrDefault(message => message.Role == "user") ?? selected[0];
            var excerpt = ExactRepresentativeSentence(excerptSource.RawText);
            var combined = string.Join("\n", selected.Select(message => message.RawText));
            var taxonomyTitle = hasWork ? string.Empty : conversation.Title;
            var categories = ExtractCategories(combined, taxonomyTitle);
            var tags = ExtractTags(combined, taxonomyTitle);
            var indexWords = ExtractIndexWords(combined, taxonomyTitle, tags);
            var summary = MakeSummary(selected, categories, tags, indexWords, excerpt);
            var idSeed = sectionIndex == 0 ? conversation.Id : conversation.Id + ":" + excerptSource.MessageId;
            var id = "frag_" + Hash(idSeed)[..20];
            var metadata = JsonSerializer.Serialize(new
            {
                provider = "local",
                model = "heuristic-phase03",
                prompt_version = "phase03-v1",
                analysis_version = "6",
                section = "MAIN",
                anchor_message_id = excerptSource.MessageId
            });
            drafts.Add(new FragmentDraft(
                id,
                conversation.Id,
                excerptSource.Timestamp,
                MakeTitle(conversation.Title, excerpt, sectionIndex, selectedSections.Count),
                excerpt,
                summary,
                hasWork ? ArchiveClassification.Mixed : ArchiveClassification.Personal,
                indexWords,
                selected.Select(message => message.MessageId).ToList(),
                metadata)
            {
                Categories = categories,
                Tags = tags,
                SectionType = FragmentSectionType.Main
            });
        }
        return drafts;
    }

    public string SummarizeFragment(IReadOnlyList<SourceMessage> messages, string title, string excerpt)
    {
        var combined = string.Join("\n", messages.Select(message => message.RawText));
        var categories = ExtractCategories(combined, title);
        var tags = ExtractTags(combined, title);
        var indexWords = ExtractIndexWords(combined, title, tags);
        return MakeSummary(messages, categories, tags, indexWords, excerpt);
    }

    private static IReadOnlyList<List<SourceMessage>> SplitByContinuity(List<SourceMessage> section)
    {
        var blocks = BuildUserTurnBlocks(section);
        if (blocks.Count == 0) return [];
        if (blocks.Count <= 1) return [section];

        var clusters = new List<List<SourceMessage>> { new(blocks[0]) };
        var previous = blocks[0];
        for (var index = 1; index < blocks.Count; index++)
        {
            var current = blocks[index];
            var previousTime = previous.First(message => message.Role == "user").Timestamp;
            var currentTime = current.First(message => message.Role == "user").Timestamp;
            var gap = currentTime - previousTime;
            var split = gap >= TimeSpan.FromDays(3)
                || (gap >= TimeSpan.FromHours(18) && !HasTopicContinuity(clusters[^1], current));
            if (split) clusters.Add([]);
            clusters[^1].AddRange(current);
            previous = current;
        }
        return clusters;
    }

    private static bool HasTopicContinuity(IEnumerable<SourceMessage> earlier, IEnumerable<SourceMessage> later)
    {
        var earlierText = string.Join('\n', earlier.Where(message => message.Role == "user").Select(message => message.RawText));
        var laterText = string.Join('\n', later.Where(message => message.Role == "user").Select(message => message.RawText));
        var topics = KnownTags.Concat(ConceptTerms).Concat(PersonalTerms).Distinct(StringComparer.OrdinalIgnoreCase);
        return topics.Any(topic => earlierText.Contains(topic, StringComparison.OrdinalIgnoreCase)
            && laterText.Contains(topic, StringComparison.OrdinalIgnoreCase));
    }

    private static List<List<SourceMessage>> BuildUserTurnBlocks(IReadOnlyList<SourceMessage> messages)
    {
        var blocks = new List<List<SourceMessage>>();
        foreach (var message in messages)
        {
            if (message.Role == "user") blocks.Add([]);
            else if (blocks.Count == 0) continue;
            blocks[^1].Add(message);
        }
        return blocks.Where(block => block.Count > 0).ToList();
    }

    private static bool IsPersonalBlock(IReadOnlyList<SourceMessage> block)
    {
        var text = string.Join("\n", block.Where(message => message.Role == "user").Select(message => message.RawText));
        var strong = Score(text, StrongPersonalTerms);
        var reflective = Score(text, ReflectiveMarkers);
        var work = Score(text, WorkTerms);
        var explicitPersonalTheme = text.Contains("BDSM", StringComparison.OrdinalIgnoreCase)
            || text.Contains("性癖", StringComparison.OrdinalIgnoreCase)
            || text.Contains("創作観", StringComparison.OrdinalIgnoreCase);
        return explicitPersonalTheme
            || (strong > 0 && reflective > 0 && work <= strong)
            || (strong >= 2 && work == 0);
    }

    private static List<List<SourceMessage>> SelectPersonalSections(IReadOnlyList<List<SourceMessage>> blocks)
    {
        var sections = new List<List<SourceMessage>>();
        List<SourceMessage>? current = null;
        foreach (var block in blocks)
        {
            if (!IsPersonalBlock(block))
            {
                current = null;
                continue;
            }
            current ??= [];
            if (current.Count == 0) sections.Add(current);
            current.AddRange(block);
        }
        return sections;
    }

    private static string BlockText(IEnumerable<SourceMessage> block) => string.Join("\n", block.Select(message => message.RawText));

    private static IReadOnlyList<string> ExtractCategories(string text, string title)
    {
        var joined = text + "\n" + title;
        var result = new List<string>();
        Add("生成", "生成", "作って", "create", "generate");
        Add("プロンプト", "prompt", "プロンプト");
        Add("記事・文章", "記事", "文章", "原稿", "執筆", "ブログ");
        Add("開発", "実装", "コード", "テスト", "開発", "git", "sqlite", "wpf", "api");
        Add("調査", "調査", "検索", "比較", "research");
        Add("アイデア", "アイデア", "企画", "発想", "案を");
        Add("画像", "画像", "midjourney", "dall", "stable diffusion", "写真", "ポスター");
        Add("音声", "音声", "tts", "録音", "ナレーション", "whisper");
        if (result.Count == 0 || Score(joined, PersonalTerms) > 0) result.Add("思考・雑談");
        return result.Distinct().Take(4).ToList();

        void Add(string category, params string[] terms)
        {
            if (terms.Any(term => joined.Contains(term, StringComparison.OrdinalIgnoreCase))) result.Add(category);
        }
    }

    private static IReadOnlyList<string> ExtractTags(string text, string title)
    {
        var joined = text + "\n" + title;
        var tags = KnownTags.Where(tag => joined.Contains(tag, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (Match match in Regex.Matches(joined, @"(?<![A-Za-z0-9])[A-Z][A-Za-z0-9+#.-]{2,}(?:\s+[A-Z][A-Za-z0-9+#.-]{2,})?"))
        {
            var value = match.Value.Trim();
            if (value.Length <= 32 && !tags.Contains(value, StringComparer.OrdinalIgnoreCase)) tags.Add(value);
            if (tags.Count >= 10) break;
        }
        return tags.Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList();
    }

    private static IReadOnlyList<string> ExtractIndexWords(string text, string title, IReadOnlyList<string> tags)
    {
        var joined = text + "\n" + title;
        var found = new List<string>(tags);
        found.AddRange(ConceptTerms.Where(term => joined.Contains(term, StringComparison.OrdinalIgnoreCase)));
        if (found.Count == 0)
        {
            found.AddRange(Regex.Matches(joined, @"[一-龠々ァ-ヶー]{2,12}")
                .Select(match => match.Value)
                .Where(value => value is not ("してください" or "について" or "という" or "あります"))
                .Take(5));
        }
        return found.Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToList();
    }

    private static string MakeSummary(
        IReadOnlyList<SourceMessage> messages,
        IReadOnlyList<string> categories,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> indexWords,
        string excerpt)
    {
        var userText = messages.FirstOrDefault(message => message.Role == "user")?.RawText ?? excerpt;
        var plain = Regex.Replace(userText, @"\s+", " ").Trim();
        var firstSentence = Regex.Match(plain, @"^.{1,180}?[。！？!?](?:\s|$)", RegexOptions.Singleline).Value.Trim();
        var request = (firstSentence.Length > 0 ? firstSentence : plain).Trim();
        if (request.Length > 180) request = request[..180].TrimEnd() + "…";

        var imageRequest = Regex.Match(request,
            @"^(?<subject>.+?)(?:の)?(?:画像|写真|イラスト)?(?:を)?(?:生成|作成|描画)(?:して)?(?:ほしい|ください|下さい|ほしいです|お願い(?:します)?)[。！？!?]*$",
            RegexOptions.IgnoreCase);
        if (imageRequest.Success)
        {
            var subject = CleanSummarySubject(imageRequest.Groups["subject"].Value);
            return subject.Length == 0 ? "画像の生成を依頼した。" : $"{subject}の画像生成を依頼した。";
        }

        var changeRequest = Regex.Match(request,
            @"^(?<subject>.+?)(?:を)?(?:変更|修正)(?:して)?(?:ほしい|ください|下さい|ほしいです|お願い(?:します)?)[。！？!?]*$",
            RegexOptions.IgnoreCase);
        if (changeRequest.Success)
        {
            var subject = CleanSummarySubject(changeRequest.Groups["subject"].Value).Replace("や", "と", StringComparison.Ordinal);
            return subject.Length == 0 ? "修正を依頼した。" : $"{subject}の変更を依頼した。";
        }

        var implementationRequest = Regex.Match(request,
            @"^(?<subject>.+?)(?:を)?(?:実装|追加|削除)(?:して)?(?:ほしい|ください|下さい|ほしいです|お願い(?:します)?)[。！？!?]*$",
            RegexOptions.IgnoreCase);
        if (implementationRequest.Success)
        {
            var subject = CleanSummarySubject(implementationRequest.Groups["subject"].Value);
            return subject.Length == 0 ? "実装を依頼した。" : $"{subject}の実装を依頼した。";
        }

        if (request.Contains('？') || request.Contains('?')
            || Regex.IsMatch(request, @"(?:教えて|何|どこ|どれ|どう|ですか|ますか)[。！？!?]*$"))
        {
            var subject = Regex.Replace(request, @"[。！？!?]+$", string.Empty).Trim();
            if (subject.Length > 90) subject = subject[..90].TrimEnd() + "…";
            return $"{subject}について尋ねた。";
        }

        var explicitAction = new[]
        {
            (Marker: "整理した", Action: "整理した"),
            (Marker: "検討した", Action: "検討した"),
            (Marker: "比較した", Action: "比較した"),
            (Marker: "考えた", Action: "考えた"),
            (Marker: "振り返った", Action: "振り返った")
        }.FirstOrDefault(item => request.Contains(item.Marker, StringComparison.Ordinal));
        if (explicitAction.Marker is not null)
        {
            var subject = request[..request.IndexOf(explicitAction.Marker, StringComparison.Ordinal)]
                .Trim().TrimStart('私', 'は', '、', ' ');
            subject = CleanSummarySubject(subject);
            if (subject.Length > 90) subject = subject[..90].TrimEnd() + "…";
            if (subject.Length > 0)
            {
                if (subject.EndsWith("について", StringComparison.Ordinal)) return $"{subject}{explicitAction.Action}。";
                subject = Regex.Replace(subject, @"[をに]$", string.Empty);
                return $"{subject}について{explicitAction.Action}。";
            }
        }

        var subjects = tags.Concat(indexWords)
            .Where(value => value is not ("対話" or "記憶" or "思索"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
        var topic = subjects.Count switch
        {
            0 => categories.FirstOrDefault() ?? "このログ",
            1 => subjects[0],
            2 => $"{subjects[0]}と{subjects[1]}",
            _ => $"{subjects[0]}、{subjects[1]}、{subjects[2]}"
        };
        if (categories.Contains("画像") || categories.Contains("生成")) return $"{topic}に関する生成内容の記録。";
        if (categories.Contains("開発")) return $"{topic}に関する開発依頼または作業内容の記録。";
        if (categories.Contains("記事・文章")) return $"{topic}に関する文章内容の記録。";
        if (categories.Contains("調査")) return $"{topic}に関する質問または調査内容の記録。";
        return $"{topic}について書かれた記録。";
    }

    private static string CleanSummarySubject(string value) =>
        Regex.Replace(value, @"^[#>*\-\s]+|[、。！？!?\s]+$", string.Empty).Trim();

    private static int Score(string text, IEnumerable<string> terms) =>
        terms.Count(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static bool IsNoise(string text)
    {
        var value = text.Trim();
        if (value.Length == 0) return true;
        if (Regex.IsMatch(value, @"^https?://\S+$", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(value, @"^(了解|次|ありがとう|thanks|ok|okay|はい|うん)[！!。.]*$", RegexOptions.IgnoreCase)) return true;
        return value.StartsWith("```") && value.EndsWith("```");
    }

    private static string ExactRepresentativeSentence(string raw)
    {
        var text = raw.Trim();
        var match = Regex.Match(text, @"^.{1,220}?[。！？!?](?=\s|$)", RegexOptions.Singleline);
        if (match.Success) return match.Value.Trim();
        return text.Length <= 220 ? text : text[..220];
    }

    private static string MakeTitle(string conversationTitle, string excerpt, int sectionIndex, int sectionCount)
    {
        var title = conversationTitle.Trim();
        if (string.IsNullOrWhiteSpace(title) || title.Equals("New chat", StringComparison.OrdinalIgnoreCase))
        {
            var plain = Regex.Replace(excerpt, @"\s+", " ");
            title = plain.Length <= 28 ? plain : plain[..28] + "…";
        }
        else if (title.Length > 48) title = title[..48];
        return sectionCount > 1 ? $"{title} · {sectionIndex + 1}" : title;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
