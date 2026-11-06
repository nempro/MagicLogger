using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace MagicLogger;

public partial class MainWindow
{
    private IWorkspaceExtension? _workspace;
    private string? _workspaceLoadError;
    private bool _workspaceActive, _workspaceSwitching, _workspaceReaderReady;
    private ReaderViewState? _workspaceReaderState;
    private double _workspaceScroll;
    private const string WorkspacePendingKey = "workspace_import_pending_ids";
    private sealed record ExtensionEntry(string Assembly, string Type);

    private void InitializeWorkspace(string dataDirectory)
    {
        var manifest = Path.Combine(AppContext.BaseDirectory, "workspace-extension.json");
        if (!File.Exists(manifest)) return;
        try
        {
            var entry = JsonSerializer.Deserialize<ExtensionEntry>(File.ReadAllText(manifest)) ?? throw new InvalidDataException("拡張設定が空です。");
            if (Path.GetFileName(entry.Assembly) != entry.Assembly || !entry.Assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("拡張DLLはアプリ内のファイル名で指定してください。");
            var path = Path.Combine(AppContext.BaseDirectory, entry.Assembly);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("拡張DLLにリンクは使えません。");
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            var extension = Activator.CreateInstance(assembly.GetType(entry.Type, throwOnError: true)!) as IWorkspaceExtension
                ?? throw new InvalidDataException("拡張の接続形式が違います。");
            extension.Initialize(new(dataDirectory, new SqliteConversationReadPort(_database.DatabasePath, _database.IsReadOnly),
                this, Status, () => _ = OpenWorkspaceAsync()));
            _workspace = extension;
            WorkspaceButton.Content = extension.Title;
            WorkspaceButton.Visibility = Visibility.Visible;
            WorkspaceCaption.Text = $"{extension.Title}  /  {dataDirectory}";
            WorkspaceCaption.ToolTip = dataDirectory;
        }
        catch (Exception ex) { _workspaceLoadError = "拡張を開けませんでした（魔道書は利用できます）: " + ex.GetBaseException().Message; }
    }
    private async Task WorkspaceLoadedAsync()
    {
        _workspaceReaderReady = true;
        if (_workspaceLoadError is not null) Status(_workspaceLoadError);
        if (_workspace is null) return;
        var args = Environment.GetCommandLineArgs();
        var conversation = Array.IndexOf(args, "--workspace-conversation");
        if (conversation >= 0 && conversation + 1 < args.Length)
        {
            try { await _workspace.CollectConversationAsync(args[conversation + 1]); }
            catch (Exception ex) { Status("会話の拡張操作に失敗しました: " + ex.Message); }
        }
        else if (args.Contains("--open-workspace")) await OpenWorkspaceAsync();
    }
    internal async Task OpenWorkspaceAsync()
    {
        if (_workspace is null || !_workspaceReaderReady || _workspaceSwitching || _workspaceActive) return;
        _workspaceSwitching = true;
        try
        {
            _searchTimer.Stop();
            if (!_rawMode && SearchBox.Text.Trim() != (_searchText ?? "")) await ApplySearchAsync();
            await Task.WhenAll(_readerReloadTask, _readerRawTask);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            if (!await FlushPendingEditsAsync()) return;
            _workspaceReaderState = CaptureState(); _workspaceScroll = ReaderScroll.VerticalOffset;
            if (IsNormalReading() && !_rawMode) _normalReadingState = _workspaceReaderState;
            WorkspaceHost.Content = _workspace.View;
            _workspace.Refresh();
            _positionTimer.Stop(); _scrollFadeTimer.Stop();
            GrimoireRoot.Visibility = Visibility.Hidden;
            WorkspaceHost.Visibility = Visibility.Visible; _workspaceActive = true;
            WorkspaceButton.Background = new SolidColorBrush(Color.FromRgb(54,64,46));
        }
        catch (Exception ex) { Status("空間を切り替えられませんでした: " + ex.Message); }
        finally { _workspaceSwitching = false; }
    }
    internal void ReturnToReader()
    {
        if (!_workspaceActive || _workspaceSwitching) return;
        WorkspaceHost.Visibility = Visibility.Collapsed; GrimoireRoot.Visibility = Visibility.Visible;
        _workspaceActive = false; _restoring = true;
        ReaderScroll.UpdateLayout(); ReaderScroll.ScrollToVerticalOffset(_workspaceScroll); _restoring = false;
        WorkspaceButton.Background = Brushes.Transparent;
    }
    private async void Workspace_Click(object sender, RoutedEventArgs e) => await OpenWorkspaceAsync();
    private void ReaderSpace_Click(object sender, RoutedEventArgs e) => ReturnToReader();
    private Button? BuildWorkspaceConversationButton(string conversationId)
    {
        if (_workspace is null) return null;
        var button = SmallButton(_workspace.ConversationActionTitle, "#68704D");
        button.Click += async (_, _) =>
        {
            try { await _workspace.CollectConversationAsync(conversationId); }
            catch (Exception ex) { Status("会話の拡張操作に失敗しました: " + ex.Message); }
        };
        return button;
    }
    private string[] WorkspacePending() => JsonSerializer.Deserialize<string[]>(_database.GetMeta(WorkspacePendingKey) ?? "[]")
        ?? throw new InvalidDataException("拡張処理の再試行記録が壊れています。");
    private void RememberWorkspaceImport(ImportResult result) => _database.SetMeta(WorkspacePendingKey,
        JsonSerializer.Serialize(WorkspacePending().Concat(result.ChangedConversationIds).Distinct(StringComparer.Ordinal).ToArray()));
    private void AcknowledgeWorkspaceImport(WorkspaceImportOutcome result) => _database.SetMeta(WorkspacePendingKey,
        JsonSerializer.Serialize(WorkspacePending().Except(result.CompletedConversationIds, StringComparer.Ordinal).ToArray()));
    private bool WorkspaceHasPending()
    {
        if (_workspace is null) return false;
        try { return WorkspacePending().Length > 0 || _workspace.HasPending; }
        catch { return true; }
    }
}
