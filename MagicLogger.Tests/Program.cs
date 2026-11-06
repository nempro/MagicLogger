using MagicLogger;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

var tests = new Phase0AcceptanceTests();
tests.Run();

internal sealed class Phase0AcceptanceTests
{
    private int _assertions;

    public void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "MagicLoggerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var data = Path.Combine(root, "data");
            var export = Path.Combine(root, "conversations.json");
            File.WriteAllText(export, Fixture(includeSecondPersonal: false));
            var database = new LocalDatabase(data);
            database.Initialize();
            var importer = new ChatGptExportImporter(database, new HeuristicAnalysisProvider());

            var first = importer.Import(export);
            Assert(first.ConversationsSeen == 4, "4会話を読み込む");
            Assert(first.MessagesAdded == 10, "全原文メッセージを保存する");
            Assert(database.Count("conversations") == 4, "会話を保存する");
            Assert(database.Count("messages") == 10, "原文を保存する");
            Assert(database.Count("fragments") == 2, "PERSONALとMIXEDだけを断章化する");

            var fragments = database.GetFragments();
            Assert(fragments.All(f => f.Classification is ArchiveClassification.Personal or ArchiveClassification.Mixed), "本編にPROJECTを出さない");
            Assert(fragments.All(f => f.Categories.Count > 0), "各断章にCategoryを付与する");
            Assert(fragments.All(f => f.Keywords.Count is >= 1 and <= 16), "Index Wordを独立した内部索引として保持する");
            Assert(fragments.All(f => !f.Summary.Contains("この日の会話では") && !f.Summary.Contains("言葉を重ねている")), "定型句を使わない意味要約を生成する");
            foreach (var fragment in fragments)
                Assert(database.GetSourceMessages(fragment.Id).Any(m => m.RawText.Contains(fragment.Excerpt, StringComparison.Ordinal)), "代表引用は原文由来");

            var mixed = fragments.Single(f => f.Classification == ArchiveClassification.Mixed);
            var mixedSources = database.GetSourceMessages(mixed.Id);
            Assert(mixedSources.All(m => !m.RawText.Contains("SQLite")), "MIXED断章の本編から実装作業区間を除く");
            Assert(mixedSources.Any(m => m.RawText.Contains("自由")), "MIXED断章はPersonal部分を拾う");

            var originalMessageCount = database.Count("messages");
            var originalFragmentCount = database.Count("fragments");
            var duplicate = importer.Import(export);
            Assert(duplicate.MessagesAdded == 0 && duplicate.FragmentsAdded == 0, "同じExportは重複しない");
            Assert(database.Count("messages") == originalMessageCount && database.Count("fragments") == originalFragmentCount, "再Importで件数が変わらない");

            var zipPath = Path.Combine(root, "export.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create)) zip.CreateEntryFromFile(export, "conversations.json");
            var zipDuplicate = importer.Import(zipPath);
            Assert(zipDuplicate.MessagesAdded == 0, "ZIP版の同一原文も重複しない");

            var personal = fragments.Single(f => f.Classification == ArchiveClassification.Personal);
            database.SaveFragmentText(personal.Id, "title", "手で直した自由の記録");
            database.SaveFragmentText(personal.Id, "summary", "これは手動で残した編纂文です。");
            database.SaveKeywords(personal.Id, ["自由", "信頼", "選択"]);
            Assert(database.GetFragment(personal.Id)!.EditedFields.Contains("title"), "手動編集フィールドを記録する");

            File.WriteAllText(export, Fixture(includeSecondPersonal: true));
            var delta = importer.Import(export);
            Assert(delta.MessagesAdded == 3, "差分メッセージだけを追加する");
            Assert(database.Count("conversations") == 5, "差分会話を追加する");
            Assert(database.Count("fragments") == originalFragmentCount + 1, "既存会話の差分で断章を二重化しない");
            Assert(database.GetFragment(personal.Id)!.Title == "手で直した自由の記録", "再解析で手動タイトルを上書きしない");
            Assert(database.GetFragment(personal.Id)!.Summary == "これは手動で残した編纂文です。", "再解析で手動編纂文を上書きしない");
            Assert(database.GetSourceMessages(personal.Id).Any(m => m.MessageId == "p1-u2"), "既存断章へ差分原文を関連付ける");

            var beforeRaw = database.GetSourceMessages(personal.Id)[0].RawText;
            var immutable = false;
            try { database.AttemptRawTextUpdateForTest("p1", "p1-u1", "改変"); }
            catch (SqliteException) { immutable = true; }
            Assert(immutable, "原文のUPDATEをDBトリガーで拒否する");
            Assert(database.GetSourceMessages(personal.Id)[0].RawText == beforeRaw, "原文が不変");

            var bookmark = database.ToggleFlag(personal.Id, "bookmarked");
            Assert(bookmark && database.GetFragment(personal.Id)!.Bookmarked, "栞を即時保存する");
            database.SetMeta("scroll_offset", "123.5");
            database.SetMeta("last_fragment_id", personal.Id);
            var reopened = new LocalDatabase(data);
            reopened.Initialize();
            Assert(reopened.GetMeta("scroll_offset") == "123.5", "前回位置を再起動後も保持する");
            Assert(reopened.GetFragment(personal.Id)!.Bookmarked, "栞を再起動後も保持する");
            Assert(reopened.FindByKeyword("自由").Count >= 2, "同じ索引語の別断章を探せる");
            Assert(reopened.FindRelated(personal.Id).Count >= 1, "後から見つかった関連記憶を返せる");

            var backup = reopened.CreateBackup();
            Assert(File.Exists(backup) && new FileInfo(backup).Length > 0, "手動バックアップを作成する");

            var broken = Path.Combine(root, "broken.json");
            File.WriteAllText(broken, "{broken");
            var failed = false;
            try { importer.Import(broken); } catch (JsonException) { failed = true; }
            Assert(failed, "壊れたImportは失敗として扱う");
            var afterFailure = importer.Import(export);
            Assert(afterFailure.MessagesAdded == 0, "失敗後も重複なしで再開できる");

            var partialData = Path.Combine(root, "partial-data");
            var partialDatabase = new LocalDatabase(partialData);
            partialDatabase.Initialize();
            var partialImporter = new ChatGptExportImporter(partialDatabase, new FailingProvider("pr1"));
            var partial = partialImporter.Import(export);
            Assert(partial.Skipped == 1 && partialDatabase.GetLatestImportStatus() == "partial", "会話単位失敗を成功扱いしない");
            var retry = new ChatGptExportImporter(partialDatabase, new HeuristicAnalysisProvider()).Import(export);
            Assert(partialDatabase.Count("messages") == 13, "解析失敗時も原文を先に保存する");
            Assert(retry.MessagesAdded == 0 && partialDatabase.GetLatestImportStatus() == "completed", "保存済み原文から解析だけを再試行して回復する");

            RunPhase01QueryTests(root);
            RunSegmentationPrecisionTests();
            RunPhase02Tests(root);
            RunPhase03Tests(root);
            RunPhase04Tests(root);
            RunPhase041Tests();
            RunPhase041WpfInputRouteTests();
            RunPhase042SafetyTests(root);
            RunSourceArchiveTests(root);

            RunSplitImportTests(root);
            RunImportErrorTests(root);

            CoreConversationReadPortTests.Run(root, Assert);

            Console.WriteLine($"PASS: Phase 0 core acceptance ({_assertions} assertions)");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private void RunSegmentationPrecisionTests()
    {
        var timestamp = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        SourceMessage Source(string id, int minute, string role, string raw) =>
            new("precision", "開発ログ", id, null, timestamp.AddMinutes(minute), role, raw, "precision-" + id);
        var conversation = new NormalizedConversation("precision", "開発ログ", timestamp,
        [
            Source("work-1", 1, "user", "依存関係の選択状態を修正してgitのテストを実行してください。"),
            Source("work-a", 2, "assistant", "コードを修正します。"),
            Source("personal-1", 3, "user", "私はBDSMを通じて信頼や安心について考えた。"),
            Source("personal-a", 4, "assistant", "信頼と安心を大切にしているのですね。"),
            Source("personal-2", 5, "user", "自分にとって自由を守れる関係が大切だと思う。"),
            Source("personal-b", 6, "assistant", "自己決定を守る関係についての考えですね。"),
            Source("work-2", 7, "user", "続けてbuild結果とcommitログを確認してください。")
        ]);
        var drafts = new HeuristicAnalysisProvider().Analyze(conversation);
        Assert(drafts.Count == 1, "連続するPersonalターンを一つの区間へまとめる");
        Assert(drafts[0].SourceMessageIds.Contains("personal-1") && drafts[0].SourceMessageIds.Contains("personal-2"), "Project内のPersonal区間を本編に残す");
        Assert(!drafts[0].SourceMessageIds.Contains("work-1") && !drafts[0].SourceMessageIds.Contains("work-2"), "技術文中の選択・依存関係をPersonalと誤判定しない");
    }

    private void RunPhase01QueryTests(string root)
    {
        var data = Path.Combine(root, "phase01-query-data");
        var database = new LocalDatabase(data);
        database.Initialize();
        var provider = new HeuristicAnalysisProvider();
        for (var index = 0; index < 1605; index++)
        {
            var month = index % 2 == 0 ? 8 : 9;
            var conversationId = $"bulk-{index:0000}";
            var timestamp = new DateTimeOffset(2026, month, index % 27 + 1, 12, 0, 0, TimeSpan.Zero);
            var message = new SourceMessage(conversationId, $"Midjourney UIの記録 {index}", $"m-{index}", null, timestamp,
                "user", $"MidjourneyでUI画像を生成するPromptを考えた。自由な選択を大切にする記録 {index}。", $"hash-{index}");
            var conversation = new NormalizedConversation(conversationId, message.ConversationTitle, timestamp, [message]);
            database.SaveConversation(conversation, provider.Analyze(conversation));
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var page = database.QueryFragments(new FragmentQuery("2026-09", null, null, null, true));
        watch.Stop();
        Assert(page.TotalCount == 802 && page.Items.Count == 60, "1,600件超でも月表示を60件に遅延制限する");
        Assert(watch.Elapsed < TimeSpan.FromSeconds(2), "1,600件超の月ページを実用時間で取得する");
        Assert(page.Items.SequenceEqual(page.Items.OrderByDescending(item => item.Date).ThenByDescending(item => item.Id)), "新しい順を安定して返す");
        var oldest = database.QueryFragments(new FragmentQuery("2026-09", null, null, null, false));
        Assert(oldest.Items.SequenceEqual(oldest.Items.OrderBy(item => item.Date).ThenBy(item => item.Id)), "古い順を安定して返す");
        var search = database.QueryFragments(new FragmentQuery(null, "Midjourney", null, null, true));
        Assert(search.TotalCount == 1605 && search.Items.Count == 60, "タイトル・原文をFTS検索して結果をページングする");
        var facets = database.GetFacetCounts(new FragmentQuery("2026-09", null, null, null, true));
        Assert(facets.Any(item => item.Kind == "category" && item.Value == "画像")
               && facets.Any(item => item.Kind == "tag" && item.Value == "Midjourney")
               && facets.Any(item => item.Kind == "index"), "Category・Tag・頻出語をDB側で別集計する");
        Assert(database.GetMonthCounts().Count == 2, "年月件数をDB側で集計する");

        var ten = database.QueryFragments(new FragmentQuery("2026-09", null, null, null, true, 0, 10));
        var thirty = database.QueryFragments(new FragmentQuery("2026-09", null, null, null, true, 0, 30));
        Assert(ten.Items.Count == 10 && thirty.Items.Count == 30 && page.Items.Count == 60, "表示件数10・30・60をDBページとして切り替える");
        var globalFacets = database.GetFacetCounts(new FragmentQuery("2026-09", null, null, null, true));
        Assert(globalFacets.Single(item => item.Kind == "tag" && item.Value == "Midjourney").Count == 1605, "通常時Facetは選択月ではなく全記録を集計する");
        var searchedQuery = new FragmentQuery("2026-08", "記録 100", null, null, true);
        var searchedTotal = database.CountFragments(searchedQuery with { Month = null });
        var searchedFacets = database.GetFacetCounts(searchedQuery);
        Assert(searchedFacets.Single(item => item.Kind == "tag" && item.Value == "Midjourney").Count == searchedTotal,
            "検索中Facetは月をまたいだ検索結果だけを集計する");
    }

    private void RunPhase02Tests(string root)
    {
        const string marked = "本文の前\uE200memcite\uE201\u0001本文の後";
        var cleaned = DisplaySanitizer.Clean(marked);
        Assert(cleaned == "本文の前本文の後" && marked.Contains("memcite", StringComparison.Ordinal), "内部表示トークンと制御文字を表示時だけ除去する");

        var data = Path.Combine(root, "phase02-continuation-data");
        var export = Path.Combine(root, "phase02-continuation.json");
        var database = new LocalDatabase(data);
        database.Initialize();
        var importer = new ChatGptExportImporter(database, new HeuristicAnalysisProvider());

        File.WriteAllText(export, ContinuedFixture(1, marked));
        var first = importer.Import(export);
        var original = database.GetFragments().Single();
        var originalDate = original.Date;
        database.SaveFragmentText(original.Id, "title", "手で残した九月一日の題");
        database.MoveFragmentToSection(original.Id, FragmentSectionType.Reference);

        File.WriteAllText(export, ContinuedFixture(2, marked));
        var second = importer.Import(export);
        Assert(second.MessagesAdded == 2 && second.FragmentsAdded == 1, "後日の同一チャットは新規メッセージだけを追加して新しい断章を作る");
        Assert(database.Count("messages") == 4 && database.Count("fragments") == 2, "後日Importで既存会話・断章を複製しない");
        var preserved = database.GetFragment(original.Id)!;
        Assert(preserved.Date == originalDate && preserved.Title == "手で残した九月一日の題", "既存断章の日付と手動タイトルを再解析で動かさない");
        Assert(preserved.SectionType == FragmentSectionType.Reference, "資料へ送った状態を再Importで本編へ戻さない");
        var later = database.QueryFragments(new FragmentQuery(null, null, null, null, true, 0, 30)).Items.Single();
        Assert(later.Date.UtcDateTime.Date == new DateTime(2026, 9, 8), "後日の意味区間を構成メッセージの日付で表示する");
        Assert(database.GetSourceMessages(later.Id).Select(message => message.MessageId).SequenceEqual(new[] { "later-u", "later-a" }), "新しい断章を後日追加分だけに関連付ける");

        var backupPath = database.GetMeta("phase02_backup_path");
        Assert(backupPath is not null && File.Exists(backupPath), "継続Import前にPhase 0.2専用バックアップを作る");
        var rawBefore = database.GetSourceMessages(original.Id).Single(message => message.MessageId == "first-a").RawText;
        Assert(rawBefore == marked && DisplaySanitizer.Clean(rawBefore) == cleaned, "保存原文は内部トークンを含む元値のまま保持する");

        database.MoveFragmentToSection(original.Id, FragmentSectionType.Main);
        Assert(database.QueryFragments(new FragmentQuery(null, null, null, null, true, 0, 30, false, FragmentSectionType.Reference)).TotalCount == 0,
            "資料から本編へ戻せる");

        File.WriteAllText(export, ContinuedFixture(3, marked));
        var sameDay = importer.Import(export);
        Assert(sameDay.MessagesAdded == 2 && sameDay.FragmentsAdded == 0, "同日の連続した追加発言は既存の新しい断章へまとめる");
        Assert(database.GetSourceMessages(later.Id).Count == 4, "同日の差分原文を既存区間へ重複なく関連付ける");
        var duplicate = importer.Import(export);
        Assert(duplicate.MessagesAdded == 0 && duplicate.FragmentsAdded == 0 && database.Count("fragments") == 2, "継続Exportの再Importでも断章と原文を重複させない");
    }

    private void RunPhase03Tests(string root)
    {
        var provider = new HeuristicAnalysisProvider();
        var timestamp = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        NormalizedConversation ConversationFor(string id, string title, string raw) => new(
            id, title, timestamp,
            [new SourceMessage(id, title, id + "-u", null, timestamp, "user", raw, "hash-" + id)]);

        var generation = ConversationFor("summary-image", "ラーメン画像", "ラーメンを食べる女性の画像を生成してください。");
        var generationDraft = provider.Analyze(generation).Single();
        Assert(generationDraft.Summary == "ラーメンを食べる女性の画像生成を依頼した。", "生成依頼を原文以上に解釈せず中立的な1文へ要約する");
        Assert(!generationDraft.Summary.Contains("整理した") && !generationDraft.Summary.Contains("検討した") && !generationDraft.Summary.Contains("捉え方"), "原文にない整理・検討・思想を要約へ足さない");
        Assert(generationDraft.Excerpt == "ラーメンを食べる女性の画像を生成してください。" && generationDraft.Summary != generationDraft.Excerpt, "Quoteは原文のまま、Summaryは役割を分離する");

        var uiRequest = ConversationFor("summary-ui", "UI変更", "UIのボタン順やUndo / Redoの挙動を変更してほしい。");
        var uiSummary = provider.Analyze(uiRequest).Single().Summary;
        Assert(uiSummary == "UIのボタン順とUndo / Redoの挙動の変更を依頼した。", "修正依頼を行っていない検討として誤要約しない");
        var assistantOnly = new NormalizedConversation("assistant-only", "孤立発言", timestamp,
            [new SourceMessage("assistant-only", "孤立発言", "a1", null, timestamp, "assistant", "先頭に孤立した応答があります。", "hash-assistant-only")]);
        Assert(provider.Analyze(assistantOnly).Count == 0, "先頭がassistantだけの旧会話でも要約migrationを止めない");

        var data = Path.Combine(root, "phase03-data");
        var database = new LocalDatabase(data);
        database.Initialize();
        Assert(database.GetMeta("schema_version") == "4" && database.GetMeta("page_size") == "10", "Schema v4へ移行し初期表示を10断章にする");

        var taggedConversation = ConversationFor("tagged", "BDSMとUI", "私はBDSMとUIについて考えた。");
        var taggedDrafts = provider.Analyze(taggedConversation);
        database.SaveConversation(taggedConversation, taggedDrafts);
        var taggedId = taggedDrafts.Single().Id;
        database.AddUserTag(taggedId, "記事候補");
        Assert(database.GetFragment(taggedId)!.UserTags.Contains("記事候補"), "手動Tagをその場で追加して保存する");
        database.SaveAnalysis(taggedConversation.Id, provider.Analyze(taggedConversation));
        Assert(database.GetFragment(taggedId)!.UserTags.Contains("記事候補"), "USER Tagを再解析で失わない");
        Assert(database.QueryFragments(new FragmentQuery(null, "記事候補", null, null, true)).TotalCount == 1, "USER Tagを全文検索対象に含める");
        database.RemoveUserTag(taggedId, "記事候補");
        Assert(!database.GetFragment(taggedId)!.UserTags.Contains("記事候補"), "手動Tagを確認なしで削除する");

        var autoTag = database.GetFragment(taggedId)!.AutoTags.First();
        database.ExcludeAutoTag(taggedId, autoTag);
        database.SaveAnalysis(taggedConversation.Id, provider.Analyze(taggedConversation));
        var excluded = database.GetFragment(taggedId)!;
        Assert(excluded.ExcludedAutoTags.Contains(autoTag) && !excluded.Tags.Contains(autoTag), "除外したAUTO Tagを原本情報つきで非表示にする");
        Assert(!database.GetFacetCounts(new FragmentQuery(null, null, null, null, true)).Any(item => item.Kind == "tag" && item.Value == autoTag), "除外AUTO TagをFacet集計から外す");
        database.RestoreAutoTag(taggedId, autoTag);
        Assert(database.GetFragment(taggedId)!.AutoTags.Contains(autoTag), "除外したAUTO Tagだけを明示操作で戻せる");

        var indexConversation = ConversationFor("index-source", "Promptの記録", "UIを使うための短い個人メモ。");
        var indexDraft = new FragmentDraft(
            "frag-index-source", indexConversation.Id, timestamp, "Promptの記録", "UIを使うための短い個人メモ。",
            "PromptとUIについて書かれた記録。", ArchiveClassification.Personal, ["OFUSE"], ["index-source-u"], "phase03-v1")
        {
            Categories = ["アイデア"], Tags = [], SectionType = FragmentSectionType.Main
        };
        database.SaveConversation(indexConversation, [indexDraft]);
        var promotedCount = database.PromoteIndexWordToTag("OFUSE");
        var promoted = database.GetFragment(indexDraft.Id)!;
        Assert(promotedCount == 1 && promoted.UserTags.Contains("OFUSE") && promoted.PromotedIndexTags.Contains("OFUSE"), "Index WordをUSER Tagへ昇格する");
        Assert(promoted.Keywords.Contains("OFUSE"), "Tag昇格後も元のIndex Wordを保持する");
        database.SaveAnalysis(indexConversation.Id, [indexDraft]);
        Assert(database.GetFragment(indexDraft.Id)!.UserTags.Contains("OFUSE"), "昇格Tagを再解析後も維持する");
        database.DemoteIndexWordTag("OFUSE");
        Assert(!database.GetFragment(indexDraft.Id)!.UserTags.Contains("OFUSE") && database.GetFragment(indexDraft.Id)!.Keywords.Contains("OFUSE"), "昇格TagをIndex Word扱いへ戻す");
        database.AddUserTag(indexDraft.Id, "参考資料");
        database.MoveUserTagToIndex(indexDraft.Id, "参考資料");
        Assert(!database.GetFragment(indexDraft.Id)!.UserTags.Contains("参考資料") && database.GetFragment(indexDraft.Id)!.Keywords.Contains("参考資料"), "手動TagをIndex Wordへ移せる");

        var bookmarkFolder = database.CreateFolder(CollectionFolderKind.Bookmark, "あとで読む");
        database.AssignFolder(taggedId, CollectionFolderKind.Bookmark, bookmarkFolder);
        Assert(database.QueryFragments(new FragmentQuery(null, null, null, null, true, 0, 10, true, FragmentSectionType.Main, bookmarkFolder)).TotalCount == 1,
            "しおりを任意フォルダへ分類して絞り込む");
        var bookmarkOverview = database.GetFolderOverview(CollectionFolderKind.Bookmark);
        Assert(bookmarkOverview.Folders.Single(folder => folder.Id == bookmarkFolder).Count == 1, "しおりフォルダ件数を集計する");
        var scopedFacets = database.GetFacetCounts(new FragmentQuery(null, null, null, null, true, 0, 10, true, FragmentSectionType.Main, bookmarkFolder));
        Assert(scopedFacets.Any(item => item.Kind == "tag" && item.Value == "BDSM") && !scopedFacets.Any(item => item.Kind == "tag" && item.Value == "OFUSE"),
            "しおりフォルダ内だけをCategory・Tag・Index Wordで探索する");
        database.DeleteFolder(CollectionFolderKind.Bookmark, bookmarkFolder);
        var afterBookmarkFolderDelete = database.GetFragment(taggedId)!;
        Assert(afterBookmarkFolderDelete.Bookmarked && afterBookmarkFolderDelete.BookmarkFolderId is null, "しおりフォルダ削除時も断章としおりを残す");

        var referenceFolder = database.CreateFolder(CollectionFolderKind.Reference, "Magic Logger");
        database.AssignFolder(indexDraft.Id, CollectionFolderKind.Reference, referenceFolder);
        Assert(database.QueryFragments(new FragmentQuery(null, null, null, null, true, 0, 10, false, FragmentSectionType.Reference, referenceFolder)).TotalCount == 1,
            "資料を任意の資料フォルダへ分類する");
        database.DeleteFolder(CollectionFolderKind.Reference, referenceFolder);
        var afterReferenceFolderDelete = database.GetFragment(indexDraft.Id)!;
        Assert(afterReferenceFolderDelete.SectionType == FragmentSectionType.Reference && afterReferenceFolderDelete.ReferenceFolderId is null,
            "資料フォルダ削除時も資料と原文を残す");
        database.MoveFragmentToSection(indexDraft.Id, FragmentSectionType.Main);
        Assert(database.GetFragment(indexDraft.Id)!.SectionType == FragmentSectionType.Main, "資料フォルダから本編へ戻せる");

        SaveSearchFragment("and-both", "BDSMの記録", "心理的な依存についてのBDSMメモ。", ["BDSM"]);
        SaveSearchFragment("and-dependency", "依存関係", "ソフトウェアの依存関係だけを扱う。", ["開発"]);
        SaveSearchFragment("and-bdsm", "BDSM単独", "BDSMの境界についての記録。", ["BDSM"]);
        var andResult = database.QueryFragments(new FragmentQuery(null, "依存 BDSM", null, null, true));
        var reverseAnd = database.QueryFragments(new FragmentQuery(null, "BDSM　依存", null, null, true));
        Assert(andResult.TotalCount == 1 && andResult.Items.Single().Id == "frag-and-both", "空白区切り複数語をAND検索する");
        Assert(reverseAnd.TotalCount == andResult.TotalCount, "語順と全角空白に依存せずAND検索する");
        Assert(database.QueryFragments(new FragmentQuery(null, "Prompt UI", null, null, true)).Items.Any(item => item.Id == indexDraft.Id),
            "title・raw textをまたぐAND検索を行う");

        var summaryData = Path.Combine(root, "phase03-summary-data");
        var summaryDatabase = new LocalDatabase(summaryData);
        summaryDatabase.Initialize();
        summaryDatabase.SaveConversation(generation, [generationDraft]);
        var manualConversation = ConversationFor("summary-manual", "手動要約", "私は信頼について考えた。");
        var manualDraft = provider.Analyze(manualConversation).Single();
        summaryDatabase.SaveConversation(manualConversation, [manualDraft]);
        using (var connection = new SqliteConnection($"Data Source={summaryDatabase.DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE fragments SET summary='古い解釈を整理した。' WHERE fragment_id=$id";
            command.Parameters.AddWithValue("$id", generationDraft.Id);
            command.ExecuteNonQuery();
        }
        summaryDatabase.SaveFragmentText(manualDraft.Id, "summary", "これは手動で残す要約です。");
        summaryDatabase.MigratePhase03Summaries(provider);
        Assert(summaryDatabase.GetFragment(generationDraft.Id)!.Summary == generationDraft.Summary, "既存の自動要約だけをPhase 0.3方針へ更新する");
        Assert(summaryDatabase.GetFragment(manualDraft.Id)!.Summary == "これは手動で残す要約です。", "要約migrationで手動要約を上書きしない");
        Assert(summaryDatabase.Count("fragment_search_fts") == summaryDatabase.Count("fragments"), "既存断章の検索索引欠落を安全に修復する");
        var phase03Backup = summaryDatabase.GetMeta("phase03_backup_path");
        Assert(phase03Backup is not null && File.Exists(phase03Backup), "要約更新前にPhase 0.3専用バックアップを作る");

        var legacyData = Path.Combine(root, "phase03-legacy-data");
        Directory.CreateDirectory(legacyData);
        var legacyPath = Path.Combine(legacyData, "magic-logger.db");
        using (var legacy = new SqliteConnection($"Data Source={legacyPath}"))
        {
            legacy.Open();
            using var command = legacy.CreateCommand();
            command.CommandText = """
                CREATE TABLE meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
                INSERT INTO meta(key,value) VALUES('schema_version','2');
                CREATE TABLE messages(
                    conversation_id TEXT NOT NULL,message_id TEXT NOT NULL,parent_message_id TEXT,timestamp TEXT NOT NULL,
                    role TEXT NOT NULL,raw_text TEXT NOT NULL,source_hash TEXT NOT NULL UNIQUE,imported_at TEXT NOT NULL,
                    PRIMARY KEY(conversation_id,message_id));
                INSERT INTO messages VALUES('legacy','m1',NULL,'2026-09-08T00:00:00Z','user','変えてはいけない原文','legacy-hash','2026-09-08T00:00:00Z');
                """;
            command.ExecuteNonQuery();
        }
        var legacyDatabase = new LocalDatabase(legacyData);
        legacyDatabase.Initialize();
        Assert(legacyDatabase.GetMeta("schema_version") == "4" && legacyDatabase.Count("messages") == 1, "既存v2 DBを破壊せずadditive migrationする");
        var legacyBackup = legacyDatabase.GetMeta("phase03_backup_path");
        Assert(legacyBackup is not null && File.Exists(legacyBackup), "Schema変更前にPhase 0.3専用バックアップを作る");
        using (var legacy = new SqliteConnection($"Data Source={legacyDatabase.DatabasePath}"))
        {
            legacy.Open();
            using var command = legacy.CreateCommand();
            command.CommandText = "SELECT raw_text FROM messages WHERE source_hash='legacy-hash'";
            Assert((string?)command.ExecuteScalar() == "変えてはいけない原文", "Schema migrationでも原文を変更しない");
        }

        void SaveSearchFragment(string id, string title, string raw, IReadOnlyList<string> tags)
        {
            var conversation = ConversationFor(id, title, raw);
            var draft = new FragmentDraft("frag-" + id, id, timestamp, title, raw, title + "についての記録。",
                ArchiveClassification.Personal, tags, [id + "-u"], "phase03-v1")
            {
                Categories = ["思考・雑談"], Tags = tags, SectionType = FragmentSectionType.Main
            };
            database.SaveConversation(conversation, [draft]);
        }
    }

    private void RunPhase04Tests(string root)
    {
        var data = Path.Combine(root, "phase04-data");
        var database = new LocalDatabase(data);
        database.Initialize();
        var baseTime = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

        void Save(string id, int day, string category)
        {
            var time = baseTime.AddDays(day - 1);
            var source = new SourceMessage(id, id, id + "-m", null, time, "user", id + "の原文", id + "-hash");
            var conversation = new NormalizedConversation(id, id, time, [source]);
            var draft = new FragmentDraft(id, id, time, id, id + "の原文", id + "の要約", ArchiveClassification.Personal,
                ["Phase04"], [source.MessageId], "phase04-v1") { Categories = [category], Tags = ["Phase04"] };
            database.SaveConversation(conversation, [draft]);
        }

        Save("phase04-a", 1, "分類01");
        Save("phase04-b", 2, "分類02");
        Save("phase04-c", 3, "分類03");
        for (var index = 4; index <= 12; index++) Save($"phase04-{index:00}", index, $"分類{index:00}");

        var newest = new FragmentQuery("2026-09", null, null, null, true);
        Assert(database.GetAdjacentFragmentId("phase04-b", newest) == "phase04-a", "非表示前の新しい順では直後の断章をanchorに選ぶ");
        Assert(database.GetAdjacentFragmentId("phase04-a", newest) == "phase04-b", "新しい順の末尾では直前の断章へfallbackする");
        var oldest = newest with { NewestFirst = false };
        Assert(database.GetAdjacentFragmentId("phase04-b", oldest) == "phase04-c", "古い順でも表示順の直後をanchorに選ぶ");
        Assert(database.GetAdjacentFragmentId("phase04-12", oldest) == "phase04-11", "古い順の末尾では直前の断章へfallbackする");

        var beforeRaw = database.GetSourceMessages("phase04-b").Single().RawText;
        database.ToggleFlag("phase04-b", "hidden");
        var hidden = database.GetFragment("phase04-b")!;
        Assert(hidden.Hidden && database.GetSourceMessages("phase04-b").Single().RawText == beforeRaw && database.Count("fragments") == 12,
            "非表示は断章だけを可逆flag化し、原文と件数を変えない");

        var capped = database.GetFacetCounts(new FragmentQuery("2026-09", null, null, null, true), 11);
        Assert(capped.Count(item => item.Kind == "category") == 11, "Facet通常取得はもっと見る判定用の11件だけ返す");
        var all = database.GetFacetCounts(new FragmentQuery("2026-09", null, null, null, true), 0);
        Assert(all.Count(item => item.Kind == "category") == 11, "Facetの全件取得は表示中の全Categoryを返す");
        database.SetMeta("facet_category_open", "0");
        database.SetMeta("facet_tag_open", "1");
        database.SetMeta("facet_index_open", "0");
        var reopened = new LocalDatabase(data);
        reopened.Initialize();
        Assert(reopened.GetMeta("facet_category_open") == "0" && reopened.GetMeta("facet_tag_open") == "1" && reopened.GetMeta("facet_index_open") == "0",
            "Facetの折りたたみ状態を独立して保存する");
    }

    private void RunPhase041Tests()
    {
        Assert(ReaderClickBehavior.OpensRaw(false, false), "未選択の通常クリックは原文を開く");
        Assert(!ReaderClickBehavior.OpensRaw(true, false), "文字列を選択したクリックは原文を開かない");
        Assert(!ReaderClickBehavior.OpensRaw(false, true), "Tag・Index Wordのリンククリックは横断探索を優先する");
    }

    private void RunPhase042SafetyTests(string root)
    {
        var data = Path.Combine(root, "phase042-backup-data");
        var database = new LocalDatabase(data);
        database.Initialize();
        database.SetMeta("preserved_before_backup", "yes");
        database.SetMeta("page_size", "30");
        var time = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var source = new SourceMessage("synthetic-restore", "架空の灯台模型", "synthetic-restore-message", null,
            time, "user", "架空の灯台模型を記録する。", "synthetic-restore-hash");
        var conversation = new NormalizedConversation(source.ConversationId, source.ConversationTitle, time, [source]);
        var draft = new FragmentDraft("synthetic-restore-fragment", source.ConversationId, time, "灯台模型の断章",
            source.RawText, "灯台模型の要約", ArchiveClassification.Personal, ["模型"], [source.MessageId], "synthetic-v1")
        { Categories = ["資料"], Tags = ["模型"] };
        database.SaveConversation(conversation, [draft]);

        var backup = database.CreateBackup();
        Assert(File.Exists(backup), "保全バックアップを確定名で作成する");
        using (var snapshot = new SqliteConnection($"Data Source={backup};Mode=ReadOnly;Cache=Private;Pooling=False"))
        {
            snapshot.Open();
            using var integrity = snapshot.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check;";
            Assert(string.Equals(integrity.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase), "保全バックアップのSQLite整合性を確認する");
        }
        Assert(!Directory.EnumerateFiles(Path.Combine(data, "backups"))
            .Any(path => Path.GetFileName(path).Contains(".partial-", StringComparison.Ordinal)), "未完成の保全バックアップを残さない");
        var restoredDirectory = Path.Combine(root, "core-restored-copy");
        Directory.CreateDirectory(restoredDirectory);
        File.Copy(backup, Path.Combine(restoredDirectory, "magic-logger.db"));
        var restored = new LocalDatabase(restoredDirectory);
        restored.Initialize();
        using (var restoredConnection = new SqliteConnection($"Data Source={restored.DatabasePath};Mode=ReadOnly;Cache=Private;Pooling=False"))
        {
            restoredConnection.Open();
            using var check = restoredConnection.CreateCommand();
            check.CommandText = "PRAGMA integrity_check;";
            Assert((string?)check.ExecuteScalar() == "ok", "隔離復元した会話DBのintegrity_checkが成功する");
        }
        Assert(restored.GetSourceMessages(draft.Id).Single().RawText == source.RawText &&
            restored.GetFragment(draft.Id)?.Title == draft.Title && restored.GetMeta("page_size") == "30",
            "隔離復元後も原文・断章・設定を保持する");
        Assert(restored.Count("messages") == 1 && restored.Count("fragments") == 1,
            "隔離復元後の会話件数と断章件数が一致する");
        database.SetMeta("preserved_after_backup", "yes");
        Assert(database.GetMeta("preserved_before_backup") == "yes" && database.GetMeta("preserved_after_backup") == "yes",
            "バックアップ後も実DBを書込み可能なまま保持する");

        var countBeforeDueCheck = Directory.GetFiles(Path.Combine(data, "backups"), "magic-logger-*.db").Length;
        database.AutoBackupIfDue();
        Assert(Directory.GetFiles(Path.Combine(data, "backups"), "magic-logger-*.db").Length == countBeforeDueCheck,
            "24時間以内の自動保全を重複実行しない");

        var futureDirectory = Path.Combine(root, "core-future-schema");
        Directory.CreateDirectory(futureDirectory);
        var futurePath = Path.Combine(futureDirectory, "magic-logger.db");
        File.Copy(backup, futurePath);
        using (var future = new SqliteConnection($"Data Source={futurePath};Mode=ReadWrite;Cache=Private;Pooling=False"))
        {
            future.Open();
            using var command = future.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=DELETE; PRAGMA user_version=999; UPDATE meta SET value='999' WHERE key='schema_version';";
            command.ExecuteNonQuery();
        }
        var futureBytes = File.ReadAllBytes(futurePath);
        var futureRejected = false;
        try { new LocalDatabase(futureDirectory).Initialize(); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Schema v999", StringComparison.Ordinal)) { futureRejected = true; }
        Assert(futureRejected && futureBytes.SequenceEqual(File.ReadAllBytes(futurePath)) &&
            !Directory.Exists(Path.Combine(futureDirectory, "backups")),
            "新版会話DBは初期化・migration・backup・version書換え前に拒否する");
        using (var future = new SqliteConnection($"Data Source={futurePath};Mode=ReadWrite;Cache=Private;Pooling=False"))
        {
            future.Open();
            using var command = future.CreateCommand();
            command.CommandText = "PRAGMA user_version=4;";
            command.ExecuteNonQuery();
        }
        futureBytes = File.ReadAllBytes(futurePath);
        futureRejected = false;
        try { new LocalDatabase(futureDirectory).Initialize(); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Schema v999", StringComparison.Ordinal)) { futureRejected = true; }
        Assert(futureRejected && futureBytes.SequenceEqual(File.ReadAllBytes(futurePath)),
            "meta側だけが新版schemaでもDBを変更せず拒否する");

        var externallyLockedData = Path.Combine(root, "phase042-external-lock-data");
        var externallyLockedDatabase = new LocalDatabase(externallyLockedData);
        externallyLockedDatabase.Initialize();
        using (var heldWriter = new SqliteConnection($"Data Source={externallyLockedDatabase.DatabasePath};Cache=Private"))
        {
            heldWriter.Open();
            using var hold = heldWriter.CreateCommand();
            hold.CommandText = "BEGIN IMMEDIATE; INSERT INTO meta(key,value) VALUES('external_lock_probe','held');";
            hold.ExecuteNonQuery();
            var backupWhileLocked = Task.Run(externallyLockedDatabase.CreateBackup);
            Thread.Sleep(3300); // Exceeds the 3s SQLite wait once, then lets bounded retry recover.
            hold.CommandText = "COMMIT;";
            hold.ExecuteNonQuery();
            var recoveredBackup = backupWhileLocked.GetAwaiter().GetResult();
            Assert(File.Exists(recoveredBackup) && new FileInfo(recoveredBackup).Length > 0,
                "外部writerの短時間ロック後は有限retryで保全を完了する");
        }
    }

    private void RunSourceArchiveTests(string root)
    {
        var data = Path.Combine(root, "source-archive-data");
        var export = Path.Combine(root, "source-archive.json");
        var database = new LocalDatabase(data);
        database.Initialize();
        Assert(database.GetMeta("schema_version") == "4", "ChatGPT出典Archive対応のSchema v4を使用する");
        Assert(SourceMetadataPresentation.ChatGptArchiveStatus(true) == "出典状態：ChatGPTでアーカイブ済み",
            "Archive済みの出典状態を原文詳細へ表示する");
        Assert(SourceMetadataPresentation.ChatGptArchiveStatus(false) is null
               && SourceMetadataPresentation.ChatGptArchiveStatus(null) is null,
            "未Archiveとunknownは通常の出典表示へ追加しない");

        var importer = new ChatGptExportImporter(database, new HeuristicAnalysisProvider());
        File.WriteAllText(export, ArchiveFixture("archive-source", false));
        importer.Import(export);
        Assert(database.GetChatGptSourceArchiveState("archive-source") is false, "is_archived=falseを出典メタデータとして保存する");

        var fragment = database.GetFragments().Single();
        database.SaveFragmentText(fragment.Id, "title", "手動で残す題");
        database.SaveFragmentText(fragment.Id, "summary", "手動で残す要約です。");
        database.AddUserTag(fragment.Id, "保存するUSER Tag");
        var autoTag = database.GetFragment(fragment.Id)!.AutoTags.First();
        database.ExcludeAutoTag(fragment.Id, autoTag);
        database.ToggleFlag(fragment.Id, "bookmarked");
        database.ToggleFlag(fragment.Id, "hidden");
        database.MoveFragmentToSection(fragment.Id, FragmentSectionType.Reference);
        var bookmarkFolder = database.CreateFolder(CollectionFolderKind.Bookmark, "Archiveテストしおり");
        var referenceFolder = database.CreateFolder(CollectionFolderKind.Reference, "Archiveテスト資料");
        database.AssignFolder(fragment.Id, CollectionFolderKind.Bookmark, bookmarkFolder);
        database.AssignFolder(fragment.Id, CollectionFolderKind.Reference, referenceFolder);
        var before = database.GetFragment(fragment.Id)!;
        var sourceBefore = database.GetSourceMessages(fragment.Id).ToList();

        File.WriteAllText(export, ArchiveFixture("archive-source", true));
        var archivedImport = importer.Import(export);
        var after = database.GetFragment(fragment.Id)!;
        Assert(database.GetChatGptSourceArchiveState("archive-source") is true, "差分Importでfalseからtrueへ更新する");
        Assert(archivedImport.MessagesAdded == 0 && archivedImport.FragmentsAdded == 0,
            "Archive状態だけの差分では原文を再取込せず断章を再生成しない");
        Assert(sourceBefore.SequenceEqual(database.GetSourceMessages(fragment.Id)), "Archive状態更新で保存原文と出典リンクを変更しない");
        Assert(after.Title == before.Title && after.Summary == before.Summary && after.Excerpt == before.Excerpt
               && after.Bookmarked == before.Bookmarked && after.Hidden == before.Hidden
               && after.SectionType == before.SectionType && after.EditedFields == before.EditedFields
               && after.GenerationMetadata == before.GenerationMetadata,
            "Archive状態更新で手動編集・しおり・資料・非表示・断章本体を変更しない");
        Assert(after.UserTags.SequenceEqual(before.UserTags) && after.AutoTags.SequenceEqual(before.AutoTags)
               && after.ExcludedAutoTags.SequenceEqual(before.ExcludedAutoTags)
               && after.Keywords.SequenceEqual(before.Keywords) && after.Categories.SequenceEqual(before.Categories)
               && after.Tags.SequenceEqual(before.Tags),
            "Archive状態更新でUSER Tag・AUTO Tag除外・分類索引を変更しない");
        Assert(after.BookmarkFolderId == before.BookmarkFolderId && after.ReferenceFolderId == before.ReferenceFolderId,
            "Archive状態更新でしおり・資料フォルダを変更しない");

        File.WriteAllText(export, ArchiveFixture("archive-source", false));
        importer.Import(export);
        Assert(database.GetChatGptSourceArchiveState("archive-source") is false, "差分Importでtrueからfalseへ更新する");
        File.WriteAllText(export, ArchiveFixture("archive-source", null));
        importer.Import(export);
        Assert(database.GetChatGptSourceArchiveState("archive-source") is false, "フィールド欠落時はImportを継続して既知値を失わない");

        var unknownExport = Path.Combine(root, "source-archive-unknown.json");
        File.WriteAllText(unknownExport, ArchiveFixture("archive-unknown", null));
        importer.Import(unknownExport);
        Assert(database.GetChatGptSourceArchiveState("archive-unknown") is null, "フィールドのない新規会話はunknownのまま保存する");

        var legacyData = Path.Combine(root, "source-archive-legacy-data");
        Directory.CreateDirectory(legacyData);
        var legacyPath = Path.Combine(legacyData, "magic-logger.db");
        using (var legacy = new SqliteConnection($"Data Source={legacyPath}"))
        {
            legacy.Open();
            using var command = legacy.CreateCommand();
            command.CommandText = """
                CREATE TABLE meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
                INSERT INTO meta(key,value) VALUES('schema_version','3');
                CREATE TABLE conversations(
                    conversation_id TEXT PRIMARY KEY,title TEXT NOT NULL,updated_at TEXT NOT NULL,imported_at TEXT NOT NULL);
                INSERT INTO conversations VALUES('legacy-source','既存会話','2026-09-01T00:00:00Z','2026-09-02T00:00:00Z');
                CREATE TABLE messages(
                    conversation_id TEXT NOT NULL,message_id TEXT NOT NULL,parent_message_id TEXT,timestamp TEXT NOT NULL,
                    role TEXT NOT NULL,raw_text TEXT NOT NULL,source_hash TEXT NOT NULL UNIQUE,imported_at TEXT NOT NULL,
                    PRIMARY KEY(conversation_id,message_id));
                INSERT INTO messages VALUES('legacy-source','legacy-message',NULL,'2026-09-01T00:00:00Z','user',
                    'migrationで変えてはいけない原文','legacy-source-hash','2026-09-02T00:00:00Z');
                CREATE TABLE fragment_user_tags(fragment_id TEXT,tag_id INTEGER,source_kind TEXT,sequence_no INTEGER,created_at TEXT);
                """;
            command.ExecuteNonQuery();
        }

        var legacyDatabase = new LocalDatabase(legacyData);
        legacyDatabase.Initialize();
        Assert(legacyDatabase.GetMeta("schema_version") == "4" && legacyDatabase.GetChatGptSourceArchiveState("legacy-source") is null,
            "既存conversationを推測せずNULLのままSchema v4へ移行する");
        var migrationBackup = legacyDatabase.GetMeta("source_archive_migration_backup_path");
        Assert(migrationBackup is not null && File.Exists(migrationBackup), "Archive列migration前に専用バックアップを作成する");
        using (var backup = new SqliteConnection($"Data Source={migrationBackup};Mode=ReadOnly;Cache=Private;Pooling=False"))
        {
            backup.Open();
            using var integrity = backup.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check";
            Assert(string.Equals(integrity.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase),
                "Archive列migration前バックアップの整合性を確認する");
            integrity.CommandText = "SELECT COUNT(*) FROM pragma_table_info('conversations') WHERE name='source_is_archived'";
            Assert(Convert.ToInt64(integrity.ExecuteScalar()) == 0, "専用バックアップが列追加前のSchemaを保持する");
            integrity.CommandText = "SELECT raw_text FROM messages WHERE message_id='legacy-message'";
            Assert((string?)integrity.ExecuteScalar() == "migrationで変えてはいけない原文", "専用バックアップが既存原文を保持する");
        }
        for (var generation = 0; generation < 6; generation++) legacyDatabase.CreateBackup();
        Assert(File.Exists(migrationBackup), "通常バックアップの世代整理からArchive migration前バックアップを保護する");
    }

    private void RunPhase041WpfInputRouteTests()
    {
        var ordinaryHandlerCalled = false;
        var handledEventHandlerCalled = false;
        var parentHandledEventHandlerCalled = false;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var card = new Border();
                var selectableText = new TextBox { Text = "選択できる本文" };
                card.Child = selectableText;
                var mouseUpEvent = EventManager.RegisterRoutedEvent("Phase041HandledMouseUp", RoutingStrategy.Bubble,
                    typeof(RoutedEventHandler), typeof(TextBox));
                selectableText.AddHandler(mouseUpEvent, new RoutedEventHandler((_, _) => ordinaryHandlerCalled = true));
                selectableText.AddHandler(mouseUpEvent, new RoutedEventHandler((_, _) => handledEventHandlerCalled = true), true);
                card.AddHandler(mouseUpEvent, new RoutedEventHandler((_, _) => parentHandledEventHandlerCalled = true), true);
                var mouseUp = new RoutedEventArgs(mouseUpEvent, selectableText)
                {
                    Handled = true
                };
                selectableText.RaiseEvent(mouseUp);
            }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException("WPF入力経路の検証を実行できませんでした", failure);
        Assert(!ordinaryHandlerCalled && handledEventHandlerCalled && parentHandledEventHandlerCalled,
            "処理済みMouseLeftButtonUpでもhandledEventsToo購読は選択コントロールとカード親まで届く");
    }

    private void RunSplitImportTests(string root)
    {
        var splitDirectory = Path.Combine(root, "split-export");
        Directory.CreateDirectory(splitDirectory);
        var splitFiles = Enumerable.Range(0, 12)
            .Select(index =>
            {
                var path = Path.Combine(splitDirectory, $"conversations-{index:000}.json");
                File.WriteAllText(path, SplitFixture(index));
                return path;
            })
            .ToList();
        File.WriteAllText(Path.Combine(splitDirectory, "account.json"), "{}");
        var nestedDirectory = Path.Combine(splitDirectory, "nested");
        Directory.CreateDirectory(nestedDirectory);
        File.WriteAllText(Path.Combine(nestedDirectory, "conversations-999.json"), SplitFixture(999));

        var discovered = ChatGptExportImporter.DiscoverConversationFiles(splitDirectory);
        Assert(discovered.Count == 12, "フォルダから12分割JSONだけを検出する");
        Assert(Path.GetFileName(discovered[0]) == "conversations-000.json" && Path.GetFileName(discovered[^1]) == "conversations-011.json", "フォルダ検出順が安定している");

        var splitData = Path.Combine(root, "split-data");
        var splitDatabase = new LocalDatabase(splitData);
        splitDatabase.Initialize();
        var splitImporter = new ChatGptExportImporter(splitDatabase, new HeuristicAnalysisProvider());
        var progress = new RecordingProgress();
        var reversed = splitFiles.AsEnumerable().Reverse().ToList();
        var imported = splitImporter.Import(reversed, progress);

        Assert(imported.FilesSeen == 12 && imported.FilesSucceeded == 12 && imported.FilesFailed == 0, "12分割を1回のImportとして完了する");
        Assert(splitDatabase.Count("import_runs") == 1 && splitDatabase.Count("import_run_files") == 12, "一括Importを1セッション・12ファイルとして記録する");
        Assert(splitDatabase.Count("conversations") == 12 && splitDatabase.Count("messages") == 24 && splitDatabase.Count("fragments") == 12, "12分割の全会話・原文・断章を保存する");
        Assert(progress.Values.Last().Stage == ImportStage.Completed && progress.Values.Last().CompletedFiles == 12
            && progress.Values.Last().TotalFiles == 12, "進捗を12 / 12の完了まで通知する");
        Assert(Path.GetFileName(progress.Values.First(value => value.Stage == ImportStage.FileCheck).CurrentPath) == "conversations-000.json", "選択順に依存せず正規化順で処理する");

        var fragmentIds = splitDatabase.GetFragments().Select(fragment => fragment.Id).OrderBy(id => id).ToList();
        var shuffled = splitFiles.Where((_, index) => index % 2 == 1).Concat(splitFiles.Where((_, index) => index % 2 == 0)).ToList();
        var duplicate = splitImporter.Import(shuffled);
        Assert(duplicate.MessagesAdded == 0 && duplicate.FragmentsAdded == 0, "12分割を別順で再Importしても重複しない");
        Assert(splitDatabase.Count("conversations") == 12 && splitDatabase.Count("messages") == 24 && splitDatabase.Count("fragments") == 12, "別順の再Import後も件数が不変");

        var orderData = Path.Combine(root, "split-order-data");
        var orderDatabase = new LocalDatabase(orderData);
        orderDatabase.Initialize();
        new ChatGptExportImporter(orderDatabase, new HeuristicAnalysisProvider()).Import(shuffled);
        var otherFragmentIds = orderDatabase.GetFragments().Select(fragment => fragment.Id).OrderBy(id => id).ToList();
        Assert(fragmentIds.SequenceEqual(otherFragmentIds), "入力順が違っても同じ断章を生成する");

        var partialDirectory = Path.Combine(root, "split-partial-export");
        Directory.CreateDirectory(partialDirectory);
        var partialFiles = Enumerable.Range(0, 12)
            .Select(index =>
            {
                var path = Path.Combine(partialDirectory, $"conversations-{index:000}.json");
                File.WriteAllText(path, index == 7 ? "{broken" : SplitFixture(index));
                return path;
            })
            .ToList();
        var partialSplitData = Path.Combine(root, "split-partial-data");
        var partialSplitDatabase = new LocalDatabase(partialSplitData);
        partialSplitDatabase.Initialize();
        var partialSplitImporter = new ChatGptExportImporter(partialSplitDatabase, new HeuristicAnalysisProvider());
        var partialProgress = new RecordingProgress();
        var partialResult = partialSplitImporter.Import(partialFiles.AsEnumerable().Reverse(), partialProgress);

        Assert(partialResult.FilesSeen == 12 && partialResult.FilesSucceeded == 11 && partialResult.FilesFailed == 1, "1ファイル失敗でも残り11ファイルを完了する");
        Assert(partialSplitDatabase.Count("conversations") == 11 && partialSplitDatabase.Count("messages") == 22 && partialSplitDatabase.Count("fragments") == 11, "部分失敗時も成功分を保持する");
        Assert(partialSplitDatabase.GetLatestImportStatus() == "partial", "部分失敗を一括Importのpartialとして記録する");
        Assert(partialProgress.Values.Last().Stage == ImportStage.Completed && partialProgress.Values.Last().CompletedFiles == 12, "失敗があっても進捗は12 / 12まで進む");

        var retryFiles = partialSplitDatabase.GetRetryableImportFiles();
        Assert(retryFiles.Count == 1 && Path.GetFileName(retryFiles[0]) == "conversations-007.json", "失敗したファイルだけを再試行対象にする");
        File.WriteAllText(partialFiles[7], SplitFixture(7));
        var recovered = partialSplitImporter.Import(retryFiles);
        Assert(recovered.FilesSeen == 1 && recovered.FilesSucceeded == 1 && recovered.MessagesAdded == 2, "修復した失敗ファイルだけを再試行する");
        Assert(partialSplitDatabase.Count("conversations") == 12 && partialSplitDatabase.Count("messages") == 24 && partialSplitDatabase.Count("fragments") == 12, "再試行後に12分割すべてが揃う");
        Assert(partialSplitDatabase.GetRetryableImportFiles().Count == 0 && partialSplitDatabase.GetLatestImportStatus() == "completed", "成功した再試行対象を解消する");

        RunOverlappingConversationTests(root);
        RunMultiFileConversationFailureTest(root);
        RunInvalidConversationTest(root);
    }

    private void RunImportErrorTests(string root)
    {
        var data = Path.Combine(root, "import-error-data");
        var database = new LocalDatabase(data);
        database.Initialize();
        var importer = new ChatGptExportImporter(database, new HeuristicAnalysisProvider());
        var zipPath = Path.Combine(root, "realistic-export.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            for (var index = 0; index < 2; index++)
            {
                var entry = zip.CreateEntry($"export/conversations-{index:000}.json");
                using var writer = new StreamWriter(entry.Open());
                writer.Write(SplitFixture(100 + index));
            }
            var asset = zip.CreateEntry("export/conversation_asset_file_names.json");
            using var assetWriter = new StreamWriter(asset.Open());
            assetWriter.Write("{\"not_a_conversation\":true}");
        }
        var zipProgress = new RecordingProgress();
        var imported = importer.Import(new[] { zipPath }, zipProgress);
        Assert(imported.FilesFailed == 0 && database.Count("conversations") == 2,
            "余分なconversation系JSONを含む分割ZIPでも会話だけを取り込む");
        Assert(zipProgress.Values.Select(value => value.Stage).Distinct().Contains(ImportStage.ZipCheck)
            && zipProgress.Values.Any(value => value.Stage == ImportStage.FileCheck
                && value.ProcessedBytes > 0 && value.ProcessedBytes == value.TotalBytes)
            && zipProgress.Values.Any(value => value.Stage == ImportStage.JsonDetection)
            && zipProgress.Values.Any(value => value.Stage == ImportStage.JsonParsing
                && value.CurrentJson == 2 && value.TotalJson == 2)
            && zipProgress.Values.Any(value => value.Stage == ImportStage.Database)
            && zipProgress.Values.Last().Stage == ImportStage.Completed,
            "ZIP確認からDB反映、完了まで段階を通知する");
        Assert(importer.Import(zipPath).MessagesAdded == 0, "分割ZIPの再取込で原文を重複させない");

        var folder = Path.Combine(root, "realistic-folder");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "conversations-000.json"), SplitFixture(200));
        File.WriteAllText(Path.Combine(folder, "conversation_asset_file_names.json"), "{}");
        Assert(ChatGptExportImporter.DiscoverConversationFiles(folder).Count == 1,
            "フォルダも同じ会話JSON名だけを選ぶ");
        Assert(ChatGptExportImporter.IsConversationJsonName("conversations.json")
            && ChatGptExportImporter.IsConversationJsonName("conversations-013.json")
            && !ChatGptExportImporter.IsConversationJsonName("conversation_asset_file_names.json"),
            "複数JSON選択にも同じ名前判定を使用できる");

        var brokenZip = Path.Combine(root, "broken-export.zip");
        File.WriteAllText(brokenZip, "broken central directory");
        var brokenProgress = new RecordingProgress();
        var failure = importer.Import(new[] { brokenZip }, brokenProgress);
        var error = database.GetImportErrors().Single();
        Assert(failure.FilesFailed == 1 && error.Path == Path.GetFullPath(brokenZip)
            && error.Error.Contains("ZIPファイルを読み取れませんでした", StringComparison.Ordinal)
            && error.Error.Contains("conversations-*.json", StringComparison.Ordinal),
            "破損ZIPを明確な理由と代替手順つきで失敗記録する");
        Assert(brokenProgress.Values.Any(value => value.Stage == ImportStage.ZipCheck)
            && !brokenProgress.Values.Any(value => value.Stage == ImportStage.JsonParsing),
            "破損ZIPは解析へ進まずに止める");
        using (var connection = new SqliteConnection($"Data Source={database.DatabasePath};Mode=ReadOnly;Cache=Private;Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT source_hash FROM import_run_files WHERE source_path=$path ORDER BY import_id DESC LIMIT 1";
            command.Parameters.AddWithValue("$path", Path.GetFullPath(brokenZip));
            Assert(((string?)command.ExecuteScalar())?.StartsWith("unreadable-", StringComparison.Ordinal) == true,
                "破損ZIPの全量ハッシュを計算せずに履歴を記録する");
        }
        Assert(database.GetRetryableImportFiles().Single() == Path.GetFullPath(brokenZip),
            "取込エラー数は会話数ではなく失敗元ファイル数を示す");
        var firstFailureId = error.ImportId;
        database.DismissImportError(error);
        var reopened = new LocalDatabase(data);
        reopened.Initialize();
        Assert(reopened.GetRetryableImportFiles().Count == 0
            && reopened.GetImportErrors().Single().Dismissed
            && reopened.GetImportErrors().Single().ImportId == firstFailureId,
            "通知を閉じてもschema v4の失敗履歴を保持する");
        Assert(reopened.GetMeta("schema_version") == "4", "通知状態にschema migrationを要しない");

        File.Delete(brokenZip);
        using (var zip = ZipFile.Open(brokenZip, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("conversations.json");
            using var writer = new StreamWriter(entry.Open());
            writer.Write(SplitFixture(300));
        }
        var recovered = new ChatGptExportImporter(reopened, new HeuristicAnalysisProvider()).Import(new[] { brokenZip });
        Assert(recovered.FilesFailed == 0 && reopened.GetRetryableImportFiles().Count == 0
            && reopened.Count("conversations") == 3, "修復ZIPの再試行で成功し通知を解消する");
        Assert(reopened.Count("import_run_files") >= 3, "古い失敗行を削除せず新しい取込結果を追加する");
    }

    private void RunOverlappingConversationTests(string root)
    {
        var firstDirectory = Path.Combine(root, "overlap-first");
        var secondDirectory = Path.Combine(root, "overlap-second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var oldJson = OverlapFixture("以前の題", 1730000000, includeReply: false, conflict: false);
        var newJson = OverlapFixture("新しい題", 1730000100, includeReply: true, conflict: false);
        var firstPaths = new[]
        {
            Path.Combine(firstDirectory, "conversations-000.json"),
            Path.Combine(firstDirectory, "conversations-001.json")
        };
        File.WriteAllText(firstPaths[0], oldJson);
        File.WriteAllText(firstPaths[1], newJson);
        var secondPaths = new[]
        {
            Path.Combine(secondDirectory, "conversations-000.json"),
            Path.Combine(secondDirectory, "conversations-001.json")
        };
        File.WriteAllText(secondPaths[0], newJson);
        File.WriteAllText(secondPaths[1], oldJson);

        var firstDatabase = new LocalDatabase(Path.Combine(root, "overlap-first-data"));
        firstDatabase.Initialize();
        var firstResult = new ChatGptExportImporter(firstDatabase, new HeuristicAnalysisProvider()).Import(firstPaths);
        var secondDatabase = new LocalDatabase(Path.Combine(root, "overlap-second-data"));
        secondDatabase.Initialize();
        new ChatGptExportImporter(secondDatabase, new HeuristicAnalysisProvider()).Import(secondPaths);

        var firstFragment = firstDatabase.GetFragments().Single();
        var secondFragment = secondDatabase.GetFragments().Single();
        var firstMessages = firstDatabase.GetSourceMessages(firstFragment.Id);
        var secondMessages = secondDatabase.GetSourceMessages(secondFragment.Id);
        Assert(firstResult.ConversationsSeen == 1 && firstDatabase.Count("messages") == 2, "複数ファイルにまたがる同一会話を1件へ統合する");
        Assert(firstFragment.Title == "新しい題" && secondFragment.Title == "新しい題", "会話ファイルの配置に依存せず最新の題を採用する");
        Assert(firstMessages.Select(message => message.MessageId).SequenceEqual(secondMessages.Select(message => message.MessageId)), "重複会話の原文リンク順がファイル配置に依存しない");
        Assert(firstMessages.Select(message => message.RawText).SequenceEqual(secondMessages.Select(message => message.RawText)), "重複会話の原文内容がファイル配置に依存しない");

        var conflictDirectory = Path.Combine(root, "overlap-conflict");
        Directory.CreateDirectory(conflictDirectory);
        var conflictPaths = new[]
        {
            Path.Combine(conflictDirectory, "conversations-000.json"),
            Path.Combine(conflictDirectory, "conversations-001.json")
        };
        File.WriteAllText(conflictPaths[0], OverlapFixture("衝突A", 1730000000, includeReply: false, conflict: false));
        File.WriteAllText(conflictPaths[1], OverlapFixture("衝突B", 1730000100, includeReply: false, conflict: true));
        var conflictDatabase = new LocalDatabase(Path.Combine(root, "overlap-conflict-data"));
        conflictDatabase.Initialize();
        var conflictResult = new ChatGptExportImporter(conflictDatabase, new HeuristicAnalysisProvider()).Import(conflictPaths);
        Assert(conflictResult.Skipped == 1 && conflictResult.FilesFailed == 2 && conflictDatabase.GetLatestImportStatus() == "partial", "同一メッセージIDの原文衝突を上書きせず保留する");
        Assert(conflictDatabase.Count("messages") == 0 && conflictDatabase.GetRetryableImportFiles().Count == 2, "原文衝突を両ファイルの再試行対象として残す");
    }

    private void RunMultiFileConversationFailureTest(string root)
    {
        var directory = Path.Combine(root, "multi-analysis-failure");
        Directory.CreateDirectory(directory);
        var paths = new[]
        {
            Path.Combine(directory, "conversations-000.json"),
            Path.Combine(directory, "conversations-001.json")
        };
        File.WriteAllText(paths[0], SplitFixture(0));
        File.WriteAllText(paths[1], SplitFixture(1));
        var database = new LocalDatabase(Path.Combine(root, "multi-analysis-failure-data"));
        database.Initialize();
        var result = new ChatGptExportImporter(database, new FailingProvider("split-001")).Import(paths);
        Assert(result.FilesSucceeded == 1 && result.FilesFailed == 1 && database.GetLatestImportStatus() == "partial", "複数ファイル中の会話解析失敗をpartialとして記録する");
        var retry = database.GetRetryableImportFiles();
        Assert(retry.Count == 1 && Path.GetFileName(retry[0]) == "conversations-001.json" && database.Count("conversations") == 2, "解析に失敗しても原文を保存し、そのファイルだけを再試行対象にする");
        new ChatGptExportImporter(database, new HeuristicAnalysisProvider()).Import(retry);
        Assert(database.Count("conversations") == 2 && database.GetRetryableImportFiles().Count == 0, "会話解析失敗も対象ファイルだけで回復する");
    }

    private void RunInvalidConversationTest(string root)
    {
        var path = Path.Combine(root, "invalid-conversation.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new object[]
        {
            Conversation("valid-before-invalid", "有効な会話", 1740000000,
                Message("valid-u", null, 1740000001, "user", "私は自分の記憶を大切にしたい。")),
            new { title = "会話IDがない要素", mapping = new { } }
        }));
        var database = new LocalDatabase(Path.Combine(root, "invalid-conversation-data"));
        database.Initialize();
        var result = new ChatGptExportImporter(database, new HeuristicAnalysisProvider()).Import(new[] { path });
        Assert(result.FilesFailed == 1 && database.GetLatestImportStatus() == "failed", "ID欠落を黙って捨てずファイル失敗として記録する");
        Assert(database.Count("conversations") == 0 && database.GetRetryableImportFiles().Single() == Path.GetFullPath(path), "ID欠落ファイルを再試行対象として残す");
    }

    private void Assert(bool condition, string label)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException($"FAIL [{_assertions}]: {label}");
        Console.WriteLine($"  OK {_assertions:00}  {label}");
    }

    private static string Fixture(bool includeSecondPersonal)
    {
        var firstPersonalMessages = new List<object>
        {
            Message("p1-u1", null, 1710000001, "user", "私は自由を失わずに誰かを信頼したい。自分で選べる関係が大切だと思う。"),
            Message("p1-a1", "p1-u1", 1710000002, "assistant", "自由と信頼を両立させたいという考えなのですね。")
        };
        if (includeSecondPersonal)
            firstPersonalMessages.Add(Message("p1-u2", "p1-a1", 1710000003, "user", "この考えは今も変わらず、安心して選択できる関係を望んでいる。"));
        var conversations = new List<object>
        {
            Conversation("p1", "自由について", 1710000000, firstPersonalMessages.ToArray()),
            Conversation("pr1", "Magic logger実装", 1710100000,
                Message("pr-u1", null, 1710100001, "user", "SQLiteのschemaとimportコードを実装してテストしてください。"),
                Message("pr-a1", "pr-u1", 1710100002, "assistant", "C#でdatabase classとunit testを作ります。")),
            Conversation("m1", "実装と最近の不安", 1710200000,
                Message("m-u1", null, 1710200001, "user", "SQLite importの設計を修正したい。"),
                Message("m-a1", "m-u1", 1710200002, "assistant", "差分importならsource hashを使えます。"),
                Message("m-u2", "m-a1", 1710200003, "user", "最近、誰かに決められると自由を失うのが怖い。安心して自分で選択したい。"),
                Message("m-a2", "m-u2", 1710200004, "assistant", "その不安には自己決定を守りたい気持ちがありそうです。")),
            Conversation("n1", "短いやりとり", 1710300000,
                Message("n-u1", null, 1710300001, "user", "了解"),
                Message("n-a1", "n-u1", 1710300002, "assistant", "ありがとう"))
        };
        if (includeSecondPersonal)
        {
            conversations.Add(Conversation("p2", "信頼の続き", 1713000000,
                Message("p2-u1", null, 1713000001, "user", "信頼は自由を奪わず、選択を尊重することだと考えた。"),
                Message("p2-a1", "p2-u1", 1713000002, "assistant", "以前の考えを別の角度から見直しているのですね。")));
        }
        return JsonSerializer.Serialize(conversations, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string SplitFixture(int index) => JsonSerializer.Serialize(new[]
    {
        Conversation($"split-{index:000}", $"分割された記憶 {index:000}", 1720000000 + index,
            Message($"split-{index:000}-u", null, 1720000001 + index, "user", $"私は日常の記憶 {index:000} を振り返り、自分の選択と自由を大切にしたい。"),
            Message($"split-{index:000}-a", $"split-{index:000}-u", 1720000002 + index, "assistant", "自分で選ぶことを大切にしているのですね。"))
    }, new JsonSerializerOptions { WriteIndented = true });

    private static string ArchiveFixture(string id, bool? isArchived)
    {
        var user = Message(id + "-u", null, 1731000001, "user", "私は自由と信頼を大切にする記憶を残したい。");
        var assistant = Message(id + "-a", id + "-u", 1731000002, "assistant", "自由と信頼を大切にしているのですね。");
        var mapping = new Dictionary<string, object>
        {
            [(string)user.id] = new { id = (string)user.id, parent = (string?)user.parent, children = new[] { (string)assistant.id }, message = user.message },
            [(string)assistant.id] = new { id = (string)assistant.id, parent = (string?)assistant.parent, children = Array.Empty<string>(), message = assistant.message }
        };
        var conversation = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["conversation_id"] = id,
            ["title"] = "ChatGPT Archive出典",
            ["update_time"] = 1731000002,
            ["mapping"] = mapping
        };
        if (isArchived is not null) conversation["is_archived"] = isArchived.Value;
        return JsonSerializer.Serialize(new[] { conversation }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string ContinuedFixture(int stage, string markedAssistant)
    {
        var messages = new List<object>
        {
            Message("first-u", null, new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), "user",
                "私は自由と信頼を大切にする関係について考えた。"),
            Message("first-a", "first-u", new DateTimeOffset(2026, 9, 1, 9, 1, 0, TimeSpan.Zero).ToUnixTimeSeconds(), "assistant", markedAssistant)
        };
        if (stage >= 2)
        {
            messages.Add(Message("later-u", "first-a", new DateTimeOffset(2026, 9, 8, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), "user",
                "今日はBDSMと安心できる境界について別の角度から考えたい。"));
            messages.Add(Message("later-a", "later-u", new DateTimeOffset(2026, 9, 8, 10, 1, 0, TimeSpan.Zero).ToUnixTimeSeconds(), "assistant",
                "安心と境界を大切にする新しい振り返りですね。"));
        }
        if (stage >= 3)
        {
            messages.Add(Message("later-u2", "later-a", new DateTimeOffset(2026, 9, 8, 10, 30, 0, TimeSpan.Zero).ToUnixTimeSeconds(), "user",
                "そのBDSMの話を続けて、互いの信頼も整理したい。"));
            messages.Add(Message("later-a2", "later-u2", new DateTimeOffset(2026, 9, 8, 10, 31, 0, TimeSpan.Zero).ToUnixTimeSeconds(), "assistant",
                "同じテーマの続きとして信頼を整理しましょう。"));
        }
        return JsonSerializer.Serialize(new[]
        {
            Conversation("continued-chat", "継続チャット", new DateTimeOffset(2026, 9, 8, 10, 31, 0, TimeSpan.Zero).ToUnixTimeSeconds(), messages.ToArray())
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string OverlapFixture(string title, long updated, bool includeReply, bool conflict)
    {
        var messages = new List<object>
        {
            Message("overlap-u", null, 1730000001, "user", conflict
                ? "私は異なる原文を同じIDで持っています。"
                : "私は同じ記憶を自分の選択として大切にしたい。")
        };
        if (includeReply)
            messages.Add(Message("overlap-a", "overlap-u", 1730000002, "assistant", "その記憶と自由を大切にしているのですね。"));
        return JsonSerializer.Serialize(new[] { Conversation("overlap", title, updated, messages.ToArray()) });
    }

    private static object Conversation(string id, string title, long update, params object[] messages)
    {
        var mapping = new Dictionary<string, object>();
        foreach (dynamic entry in messages)
            mapping[(string)entry.id] = new { id = (string)entry.id, parent = (string?)entry.parent, children = Array.Empty<string>(), message = entry.message };
        return new { id, title, update_time = update, mapping };
    }

    private static dynamic Message(string id, string? parent, long time, string role, string text) =>
        new { id, parent, message = new { id, author = new { role }, create_time = time, content = new { content_type = "text", parts = new[] { text } } } };

    private sealed class FailingProvider(string conversationId) : IAnalysisProvider
    {
        private readonly HeuristicAnalysisProvider _inner = new();
        public IReadOnlyList<FragmentDraft> Analyze(NormalizedConversation conversation) =>
            conversation.Id == conversationId
                ? throw new InvalidOperationException("simulated conversation failure")
                : _inner.Analyze(conversation);
    }

    private sealed class RecordingProgress : IProgress<ImportProgress>
    {
        public List<ImportProgress> Values { get; } = [];
        public void Report(ImportProgress value) => Values.Add(value);
    }
}
