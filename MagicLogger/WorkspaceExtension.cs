using System.Windows;

namespace MagicLogger;

// Optional, local, in-process workspaces. Core does not reference any concrete
// workspace assembly or its storage/extraction/UI types.
public sealed record WorkspaceContext(string DataDirectory, IConversationReadPort Conversations,
    Window Owner, Action<string> ReportStatus, Action ShowWorkspace);
public sealed record WorkspaceImportOutcome(string Summary, IReadOnlyList<string> CompletedConversationIds);
public interface IWorkspaceExtension
{
    string Title { get; }
    string ConversationActionTitle { get; }
    void Initialize(WorkspaceContext context);
    FrameworkElement View { get; }
    IDisposable BeginImport();
    WorkspaceImportOutcome AfterImport(ImportResult result);
    bool HasPending { get; }
    void Refresh();
    Task CollectConversationAsync(string conversationId);
}
