namespace MagicLogger;

public enum ArchiveClassification
{
    Personal,
    Project,
    Mixed,
    Noise
}

public enum FragmentSectionType
{
    Main,
    Reference
}

public enum CollectionFolderKind
{
    Bookmark,
    Reference
}

public sealed record SourceMessage(
    string ConversationId,
    string ConversationTitle,
    string MessageId,
    string? ParentMessageId,
    DateTimeOffset Timestamp,
    string Role,
    string RawText,
    string SourceHash);

public sealed record NormalizedConversation(
    string Id,
    string Title,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<SourceMessage> Messages)
{
    // ChatGPT source metadata. null means the Export did not provide the field.
    // This is independent from MagicLogger classification, visibility, and folders.
    public bool? SourceIsArchived { get; init; }
}

internal sealed record RawConversationSaveResult(int MessagesAdded, bool ContentChanged);

public static class SourceMetadataPresentation
{
    public static string? ChatGptArchiveStatus(bool? sourceIsArchived) =>
        sourceIsArchived is true ? "出典状態：ChatGPTでアーカイブ済み" : null;
}

public sealed record FragmentDraft(
    string Id,
    string ConversationId,
    DateTimeOffset Date,
    string Title,
    string Excerpt,
    string Summary,
    ArchiveClassification Classification,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> SourceMessageIds,
    string GenerationMetadata)
{
    public IReadOnlyList<string> Categories { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    public FragmentSectionType SectionType { get; init; } = FragmentSectionType.Main;
}

public sealed record FragmentRecord(
    string Id,
    string ConversationId,
    DateTimeOffset Date,
    string Title,
    string Excerpt,
    string Summary,
    ArchiveClassification Classification,
    IReadOnlyList<string> Keywords,
    bool Bookmarked,
    bool Hidden,
    string EditedFields,
    string GenerationMetadata)
{
    public IReadOnlyList<string> Categories { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<string> AutoTags { get; init; } = [];
    public IReadOnlyList<string> UserTags { get; init; } = [];
    public IReadOnlyList<string> ExcludedAutoTags { get; init; } = [];
    public IReadOnlyList<string> PromotedIndexTags { get; init; } = [];
    public FragmentSectionType SectionType { get; init; } = FragmentSectionType.Main;
    public long? BookmarkFolderId { get; init; }
    public string? BookmarkFolderName { get; init; }
    public long? ReferenceFolderId { get; init; }
    public string? ReferenceFolderName { get; init; }
}

public sealed record MonthCount(string Month, int Count);

public sealed record FacetCount(string Kind, string Value, int Count);

public static class ReaderClickBehavior
{
    public static bool OpensRaw(bool hasTextSelection, bool isLinkTarget) => !hasTextSelection && !isLinkTarget;
}

public sealed record FragmentQuery(
    string? Month,
    string? SearchText,
    string? FacetKind,
    string? FacetValue,
    bool NewestFirst,
    int Offset = 0,
    int Limit = 60,
    bool BookmarkedOnly = false,
    FragmentSectionType SectionType = FragmentSectionType.Main,
    long? FolderId = null);

public sealed record FragmentPage(IReadOnlyList<FragmentRecord> Items, int TotalCount);

public sealed record CollectionFolder(long Id, string Name, int Count);

public sealed record CollectionFolderOverview(
    int TotalCount,
    int UnfiledCount,
    IReadOnlyList<CollectionFolder> Folders);

public enum ImportStage { FileCheck, ZipCheck, JsonDetection, JsonParsing, Database, Failed, Completed }

public sealed record ImportProgress(
    int CompletedFiles, int TotalFiles, string CurrentPath, ImportStage Stage,
    int CompletedConversations = 0, int TotalConversations = 0,
    int CurrentJson = 0, int TotalJson = 0,
    long ProcessedBytes = 0, long TotalBytes = 0);

public sealed record ImportErrorSource(
    long ImportId, string Path, string Status, string Error, DateTimeOffset FailedAt, bool Dismissed);

public sealed record ImportFileFailure(string Path, string Message);

public sealed record ImportResult(
    int ConversationsSeen,
    int MessagesAdded,
    int FragmentsAdded,
    int Skipped,
    int FilesSeen = 1,
    int FilesSucceeded = 1,
    IReadOnlyList<ImportFileFailure>? FileFailures = null)
{
    public IReadOnlyList<ImportFileFailure> Failures => FileFailures ?? [];
    public int FilesFailed => Failures.Count;
    // Includes successful retries whose source was committed in an earlier failed run.
    public IReadOnlyList<string> ChangedConversationIds { get; init; } = [];
}

public sealed record NavigationEntry(string FragmentId, double Offset, string? Keyword);
