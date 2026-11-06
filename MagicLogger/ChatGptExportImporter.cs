using System.IO.Compression;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MagicLogger;

public sealed class ChatGptExportImporter
{
    private readonly LocalDatabase _database;
    private readonly IAnalysisProvider _analysis;

    public ChatGptExportImporter(LocalDatabase database, IAnalysisProvider analysis)
    {
        _database = database;
        _analysis = analysis;
    }

    public ImportResult Import(string path)
        => ImportCore([path], null, throwOnUnreadableSingleFile: true);

    public ImportResult Import(IEnumerable<string> paths, IProgress<ImportProgress>? progress = null)
        => ImportCore(paths, progress, throwOnUnreadableSingleFile: false);

    private ImportResult ImportCore(IEnumerable<string> paths, IProgress<ImportProgress>? progress, bool throwOnUnreadableSingleFile)
    {
        var sourcePaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (sourcePaths.Count == 0)
            throw new ArgumentException("取り込むファイルが選択されていません。", nameof(paths));

        progress?.Report(new ImportProgress(0, sourcePaths.Count, sourcePaths[0], ImportStage.FileCheck));

        // A partial prior run must still re-run analysis even when its raw source
        // was already committed before the failure.
        var retryableSourcePaths = _database.GetRetryableImportFiles()
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var prepared = sourcePaths.Select((path, index) => PrepareFile(path, index, sourcePaths.Count, progress)).ToList();
        _database.CreatePhase02SafetyBackup();
        var files = prepared.Select(file => (file.Path, file.Hash)).ToList();
        var manifestHash = Hash(string.Join("\n", files.Select(file => $"{file.Path}\t{file.Hash}")));
        var manifestPath = sourcePaths.Count == 1 ? sourcePaths[0] : JsonSerializer.Serialize(sourcePaths);
        var importId = _database.BeginImport(manifestPath, manifestHash, files);
        try
        {
            var fileStates = files.ToDictionary(
                file => file.Path,
                file => new FileImportState(file.Path),
                StringComparer.OrdinalIgnoreCase);
            var conversations = new Dictionary<string, ConversationAccumulator>(StringComparer.Ordinal);
            var messagesAdded = 0;
            var fragmentsAdded = 0;
            var skipped = 0;
            var changedConversationIds = new List<string>();

            for (var fileIndex = 0; fileIndex < files.Count; fileIndex++)
            {
                var file = files[fileIndex];
                var fileState = fileStates[file.Path];
                try
                {
                    if (prepared[fileIndex].Error is { } preparationError)
                        throw new InvalidDataException(preparationError);
                    progress?.Report(new ImportProgress(fileIndex, files.Count, file.Path, ImportStage.JsonDetection));
                    var fileConversations = ReadConversations(file.Path,
                            (jsonIndex, jsonCount) => progress?.Report(new ImportProgress(
                                fileIndex, files.Count, file.Path, ImportStage.JsonParsing,
                                CurrentJson: jsonIndex, TotalJson: jsonCount)))
                        .OrderBy(conversation => conversation.Id, StringComparer.Ordinal)
                        .ThenBy(conversation => conversation.UpdatedAt)
                        .ToList();
                    if (fileConversations.Count == 0)
                        throw new InvalidDataException("会話データが見つかりませんでした。");
                    foreach (var conversation in fileConversations)
                    {
                        if (!conversations.TryGetValue(conversation.Id, out var accumulator))
                        {
                            accumulator = new ConversationAccumulator(conversation.Id);
                            conversations.Add(conversation.Id, accumulator);
                        }
                        accumulator.Add(conversation, file.Path);
                    }
                }
                catch (Exception ex)
                {
                    fileState.Fail(ex.Message);
                    System.Diagnostics.Trace.WriteLine($"Import retry needed for file {file.Path}: {ex.Message}");
                    if (throwOnUnreadableSingleFile && files.Count == 1)
                    {
                        _database.FinishImportFile(importId, file.Path, fileState.Status, ex.Message);
                        progress?.Report(new ImportProgress(1, 1, file.Path, ImportStage.Failed));
                        throw;
                    }
                }
                progress?.Report(new ImportProgress(fileIndex + 1, files.Count, file.Path,
                    fileState.Status == "failed" ? ImportStage.Failed : ImportStage.JsonParsing));
            }

            var orderedConversations = conversations.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToList();
            progress?.Report(new ImportProgress(files.Count, files.Count, "", ImportStage.Database, 0, orderedConversations.Count));
            for (var conversationIndex = 0; conversationIndex < orderedConversations.Count; conversationIndex++)
            {
                var accumulator = orderedConversations[conversationIndex];
                if (accumulator.ConflictMessage is not null)
                {
                    skipped++;
                    MarkConversationPartial(fileStates, accumulator.SourcePaths, accumulator.ConflictMessage);
                    if ((conversationIndex + 1) % 25 == 0 || conversationIndex + 1 == orderedConversations.Count)
                        progress?.Report(new ImportProgress(files.Count, files.Count, "", ImportStage.Database,
                            conversationIndex + 1, orderedConversations.Count));
                    continue;
                }

                try
                {
                    var conversation = accumulator.Build();
                    // The immutable source layer is committed before derived analysis.
                    // A provider failure can therefore be retried without losing the original conversation.
                    var rawSave = _database.SaveRawConversationWithResult(conversation);
                    messagesAdded += rawSave.MessagesAdded;
                    if (rawSave.ContentChanged || accumulator.SourcePaths.Any(retryableSourcePaths.Contains))
                    {
                        var drafts = _analysis.Analyze(conversation);
                        fragmentsAdded += _database.SaveAnalysis(conversation.Id, drafts);
                        changedConversationIds.Add(conversation.Id);
                    }
                }
                catch (Exception ex)
                {
                    skipped++;
                    var error = $"{accumulator.Id}: {ex.Message}";
                    MarkConversationPartial(fileStates, accumulator.SourcePaths, error);
                    System.Diagnostics.Trace.WriteLine($"Import retry needed for conversation {accumulator.Id}: {ex.Message}");
                }
                if ((conversationIndex + 1) % 25 == 0 || conversationIndex + 1 == orderedConversations.Count)
                    progress?.Report(new ImportProgress(files.Count, files.Count, "", ImportStage.Database,
                        conversationIndex + 1, orderedConversations.Count));
            }

            var failures = new List<ImportFileFailure>();
            foreach (var file in files)
            {
                var fileState = fileStates[file.Path];
                var errorText = fileState.ErrorText;
                _database.FinishImportFile(importId, file.Path, fileState.Status, errorText);
                if (fileState.Status != "completed")
                    failures.Add(new ImportFileFailure(file.Path, errorText ?? "読み込みに失敗しました。"));
            }
            var filesSucceeded = fileStates.Values.Count(file => file.Status == "completed");
            var status = failures.Count == 0
                ? "completed"
                : fileStates.Values.Any(file => file.Status is "completed" or "partial")
                    ? "partial"
                    : "failed";
            var summary = failures.Count == 0 ? null : $"{failures.Count} file(s) require retry";
            _database.FinishImport(importId, status, summary);
            progress?.Report(new ImportProgress(files.Count, files.Count, "", ImportStage.Completed));
            return new ImportResult(
                conversations.Count,
                messagesAdded,
                fragmentsAdded,
                skipped,
                files.Count,
                filesSucceeded,
                failures) { ChangedConversationIds = changedConversationIds };
        }
        catch (Exception ex)
        {
            _database.FinishImport(importId, "failed", ex.Message);
            throw;
        }
    }

    private static void MarkConversationPartial(
        IReadOnlyDictionary<string, FileImportState> fileStates,
        IEnumerable<string> sourcePaths,
        string error)
    {
        foreach (var sourcePath in sourcePaths)
            fileStates[sourcePath].Partial(error);
    }

    private sealed class FileImportState(string path)
    {
        private readonly List<string> _errors = [];
        public string Path { get; } = path;
        public string Status { get; private set; } = "completed";
        public string? ErrorText => _errors.Count == 0 ? null : string.Join(Environment.NewLine, _errors.Distinct());

        public void Partial(string error)
        {
            if (Status == "completed") Status = "partial";
            _errors.Add(error);
        }

        public void Fail(string error)
        {
            Status = "failed";
            _errors.Add(error);
        }
    }

    private sealed class ConversationAccumulator(string id)
    {
        private readonly Dictionary<string, (SourceMessage Message, HashSet<string> Paths)> _messages = new(StringComparer.Ordinal);
        private string _title = "題のない対話";
        private DateTimeOffset _updatedAt = DateTimeOffset.UnixEpoch;
        private bool? _sourceIsArchived;
        private bool _hasMetadata;

        public string Id { get; } = id;
        public HashSet<string> SourcePaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? ConflictMessage { get; private set; }

        public void Add(NormalizedConversation conversation, string sourcePath)
        {
            SourcePaths.Add(sourcePath);
            if (!_hasMetadata
                || conversation.UpdatedAt > _updatedAt
                || (conversation.UpdatedAt == _updatedAt && string.CompareOrdinal(conversation.Title, _title) < 0))
            {
                _hasMetadata = true;
                _updatedAt = conversation.UpdatedAt;
                _title = conversation.Title;
                _sourceIsArchived = conversation.SourceIsArchived;
            }

            foreach (var message in conversation.Messages)
            {
                if (!_messages.TryGetValue(message.MessageId, out var existing))
                {
                    _messages.Add(message.MessageId, (message, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sourcePath }));
                    continue;
                }

                existing.Paths.Add(sourcePath);
                _messages[message.MessageId] = existing;
                if (!existing.Message.SourceHash.Equals(message.SourceHash, StringComparison.Ordinal))
                    ConflictMessage ??= $"{Id}: 同じメッセージID「{message.MessageId}」に異なる原文があるため保留しました。";
            }
        }

        public NormalizedConversation Build()
        {
            var messages = _messages.Values
                .Select(item => item.Message with { ConversationTitle = _title })
                .OrderBy(message => message.Timestamp)
                .ThenBy(message => message.MessageId, StringComparer.Ordinal)
                .ToList();
            return new NormalizedConversation(Id, _title, _updatedAt, messages)
            {
                SourceIsArchived = _sourceIsArchived
            };
        }
    }

    private static string FileHashOrMarker(string path, Action<long, long>? hashProgress = null)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 1024];
            long processed = 0;
            long lastReport = 0;
            int read;
            while ((read = stream.Read(buffer)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                processed += read;
                if (processed - lastReport >= 32L * 1024 * 1024 || processed == stream.Length)
                {
                    hashProgress?.Invoke(processed, stream.Length);
                    lastReport = processed;
                }
            }
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        catch (Exception ex)
        {
            return "unreadable-" + Hash($"{path}\n{ex.GetType().FullName}");
        }
    }

    private static (string Path, string Hash, string? Error) PrepareFile(
        string path, int fileIndex, int totalFiles, IProgress<ImportProgress>? progress)
    {
        progress?.Report(new ImportProgress(fileIndex, totalFiles, path, ImportStage.FileCheck));
        if (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report(new ImportProgress(fileIndex, totalFiles, path, ImportStage.ZipCheck));
            try
            {
                using var archive = ZipFile.OpenRead(path);
                _ = archive.Entries.Count; // Forces central-directory validation before hashing a large ZIP.
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                return (path, "unreadable-" + Hash($"{path}\n{ex.GetType().FullName}"),
                    "ZIPファイルを読み取れませんでした。ChatGPT ExportのZIPが大きい場合、展開後のフォルダまたはconversations-*.jsonを直接読み込めます。" +
                    $" ({ex.Message})");
            }
        }
        progress?.Report(new ImportProgress(fileIndex, totalFiles, path, ImportStage.FileCheck));
        return (path, FileHashOrMarker(path, (processed, total) =>
            progress?.Report(new ImportProgress(fileIndex, totalFiles, path, ImportStage.FileCheck,
                ProcessedBytes: processed, TotalBytes: total))), null);
    }

    public static bool IsConversationJsonName(string name) =>
        name.Equals("conversations.json", StringComparison.OrdinalIgnoreCase)
        || (name.StartsWith("conversations-", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            && name.Length > "conversations-.json".Length);

    public static IReadOnlyList<string> DiscoverConversationFiles(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .Where(path => IsConversationJsonName(Path.GetFileName(path)))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<NormalizedConversation> ReadConversations(string path, Action<int, int> beforeParse)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.OpenRead(path);
            var entries = archive.Entries.Where(e => IsConversationJsonName(e.Name))
                .ToList();
            if (entries.Count == 0) throw new InvalidDataException("ZIP内に会話JSONが見つかりません。");
            for (var entryIndex = 0; entryIndex < entries.Count; entryIndex++)
            {
                var entry = entries[entryIndex];
                beforeParse(entryIndex + 1, entries.Count);
                using var stream = entry.Open();
                using var document = JsonDocument.Parse(stream);
                foreach (var conversation in NormalizeRoot(document.RootElement)) yield return conversation;
            }
            yield break;
        }

        if (!extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ChatGPT Export ZIP または conversations.json を選んでください。");
        beforeParse(1, 1);
        using var jsonStream = File.OpenRead(path);
        using var json = JsonDocument.Parse(jsonStream);
        foreach (var conversation in NormalizeRoot(json.RootElement)) yield return conversation;
    }

    private static IEnumerable<NormalizedConversation> NormalizeRoot(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (!TryNormalize(item, out var conversation))
                    throw new InvalidDataException("会話IDがないデータが含まれているため、このファイルを保留しました。");
                yield return conversation;
            }
        }
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("conversations", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (!TryNormalize(item, out var conversation))
                    throw new InvalidDataException("会話IDがないデータが含まれているため、このファイルを保留しました。");
                yield return conversation;
            }
        }
        else if (root.ValueKind == JsonValueKind.Object && TryNormalize(root, out var single))
            yield return single;
        else
            throw new InvalidDataException("ChatGPT会話データとして認識できませんでした。");
    }

    private static bool TryNormalize(JsonElement item, out NormalizedConversation conversation)
    {
        var id = Text(item, "id") ?? Text(item, "conversation_id") ?? string.Empty;
        if (id.Length == 0)
        {
            conversation = null!;
            return false;
        }
        var title = Text(item, "title") ?? "題のない対話";
        var updated = FromUnix(Number(item, "update_time") ?? Number(item, "create_time") ?? 0);
        var messages = new List<SourceMessage>();
        if (item.TryGetProperty("mapping", out var mapping) && mapping.ValueKind == JsonValueKind.Object)
        {
            foreach (var node in mapping.EnumerateObject())
            {
                if (!node.Value.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) continue;
                var messageId = Text(message, "id") ?? node.Name;
                var parent = Text(node.Value, "parent");
                var role = message.TryGetProperty("author", out var author) ? Text(author, "role") ?? "unknown" : "unknown";
                var raw = ExtractText(message);
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var timestamp = FromUnix(Number(message, "create_time") ?? updated.ToUnixTimeSeconds());
                var sourceHash = Hash($"{id}\n{messageId}\n{parent}\n{timestamp:O}\n{role}\n{raw}");
                messages.Add(new SourceMessage(id, title, messageId, parent, timestamp, role, raw, sourceHash));
            }
        }
        else if (item.TryGetProperty("messages", out var simple) && simple.ValueKind == JsonValueKind.Array)
        {
            foreach (var message in simple.EnumerateArray())
            {
                var messageId = Text(message, "id") ?? Hash(message.GetRawText())[..24];
                var parent = Text(message, "parent_message_id");
                var role = Text(message, "role") ?? "unknown";
                var raw = Text(message, "text") ?? Text(message, "raw_text") ?? ExtractText(message);
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var timestamp = FromUnix(Number(message, "timestamp") ?? Number(message, "create_time") ?? updated.ToUnixTimeSeconds());
                messages.Add(new SourceMessage(id, title, messageId, parent, timestamp, role, raw,
                    Hash($"{id}\n{messageId}\n{parent}\n{timestamp:O}\n{role}\n{raw}")));
            }
        }
        messages = messages.OrderBy(m => m.Timestamp).ThenBy(m => m.MessageId, StringComparer.Ordinal).ToList();
        conversation = new NormalizedConversation(id, title, updated, messages)
        {
            SourceIsArchived = Boolean(item, "is_archived")
        };
        return true;
    }

    private static string ExtractText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content)) return string.Empty;
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? string.Empty;
        if (!content.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array) return string.Empty;
        return string.Join("\n", parts.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;

    private static bool? Boolean(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static DateTimeOffset FromUnix(double value)
    {
        try { return DateTimeOffset.FromUnixTimeMilliseconds((long)(value * 1000)); }
        catch { return DateTimeOffset.UnixEpoch; }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
