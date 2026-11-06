using Microsoft.Win32;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MagicLogger;

public partial class MainWindow : Window
{
    private static readonly int[] AllowedPageSizes = [10, 30, 60];
    private const int CollapsedFacetLimit = 10;
    private const int FacetPageSize = 20;
    private readonly LocalDatabase _database;
    private readonly ChatGptExportImporter _importer;
    private readonly bool _runMaintenance;
    private readonly FileStream? _sourceReadLease;
    private Task _readerReloadTask = Task.CompletedTask;
    private Task _readerRawTask = Task.CompletedTask;
    private readonly Dictionary<string, FrameworkElement> _sections = [];
    private readonly Stack<ReaderViewState> _back = [];
    private readonly Stack<ReaderViewState> _forward = [];
    private readonly HashSet<Func<Task<bool>>> _pendingEdits = [];
    private readonly DispatcherTimer _positionTimer;
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _scrollFadeTimer;
    // All writes and preservation snapshots from this window enter one lane.
    // Read queries remain concurrent under WAL, but a scroll save can no longer
    // race a start-up migration, import, or backup for SQLite's single writer.
    private readonly SemaphoreSlim _databaseWriteGate = new(1, 1);
    private static readonly TimeSpan[] DatabaseWriteRetryDelays =
    [
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1)
    ];
    private readonly HashSet<string> _expandedYears = [];
    private IReadOnlyList<string> _retryImportPaths = [];
    private bool _importInProgress;
    private bool _closeAfterImport;
    private bool _closingInProgress;
    private bool _allowClose;
    private string? _currentMonth;
    private string? _monthBeforeSearch;
    private string? _currentFragmentId;
    private string? _facetKind;
    private string? _facetValue;
    private string? _searchText;
    private long? _folderId;
    private bool _newestFirst = true;
    private bool _bookmarksOnly;
    private bool _restoring;
    private bool _rawMode;
    private bool _navOpen = true;
    private bool _contextOpen = true;
    private int _pageSize = 10;
    private FragmentSectionType _sectionType = FragmentSectionType.Main;
    private IReadOnlyList<string> _tagSuggestions = [];
    private CollectionFolderOverview _bookmarkFolders = new(0, 0, []);
    private CollectionFolderOverview _referenceFolders = new(0, 0, []);
    private bool _selectedIndexPromoted;
    private ReaderViewState? _normalReadingState;
    private ReaderViewState? _rawReturnState;
    private int _pageOffset;
    private int _reloadGeneration;
    private bool _categoryFacetOpen = true;
    private bool _tagFacetOpen = true;
    private bool _indexFacetOpen = true;
    private readonly HashSet<string> _showAllFacets = [];
    private readonly Dictionary<string, int> _visibleFacetCounts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _allFacetValuesLoaded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _includeSingleCountFacets = new(StringComparer.Ordinal);
    private bool _draggingScrollIndicator;
    private bool _folderMoveInProgress;
    private readonly bool _conversationReadOnly;

    public MainWindow() : this(null) { }

    internal MainWindow(string? explicitDataDirectory, bool runMaintenance = true, bool readOnlyConversations = false)
    {
        InitializeComponent();
        var args = Environment.GetCommandLineArgs();
        var dataArgument = Array.IndexOf(args, "--data-dir");
        var defaultData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MagicLogger");
        var dataDirectory = explicitDataDirectory ?? (dataArgument >= 0 && dataArgument + 1 < args.Length
            ? Path.GetFullPath(args[dataArgument + 1])
            : defaultData);
        _conversationReadOnly = readOnlyConversations || args.Contains("--read-only-conversations");
        _sourceReadLease = _conversationReadOnly
            ? ReadOnlySourceConnection.AcquireReadLease(Path.Combine(dataDirectory, "magic-logger.db")) : null;
        Closed += (_, _) => _sourceReadLease?.Dispose();
        _runMaintenance = runMaintenance && !_conversationReadOnly;
        _database = new LocalDatabase(dataDirectory, readOnly: _conversationReadOnly);
        _database.Initialize();
        if (_database.IsReadOnly) RecordButton.IsEnabled = RetryImportButton.IsEnabled = BackupButton.IsEnabled = false;
        _importer = new ChatGptExportImporter(_database, new HeuristicAnalysisProvider());
        InitializeWorkspace(dataDirectory);
        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _positionTimer.Tick += (_, _) => QueueReadingPositionSave();
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
        _searchTimer.Tick += async (_, _) =>
        {
            _searchTimer.Stop();
            await ApplySearchAsync();
        };
        _scrollFadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(850) };
        _scrollFadeTimer.Tick += (_, _) =>
        {
            _scrollFadeTimer.Stop();
            ScrollIndicatorLayer.Opacity = _rawMode ? 0.28 : 0;
        };
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var restored = await Task.Run(() =>
        {
            var lastId = _database.GetMeta("last_fragment_id");
            var month = lastId is null ? null : _database.GetFragmentMonth(lastId);
            month ??= _database.GetMeta("last_month");
            month = string.IsNullOrWhiteSpace(month) ? _database.GetLatestMonth() : month;
            var storedPageSize = int.TryParse(_database.GetMeta("page_size"), out var parsed) && AllowedPageSizes.Contains(parsed) ? parsed : 10;
            var navOpen = _database.GetMeta("nav_open") != "0";
            var contextOpen = _database.GetMeta("context_open") != "0";
            return (LastId: lastId, Month: month, Newest: _database.GetMeta("sort_order") != "oldest",
                PageSize: storedPageSize, NavOpen: navOpen, ContextOpen: contextOpen,
                CategoryOpen: _database.GetMeta("facet_category_open") != "0",
                TagOpen: _database.GetMeta("facet_tag_open") != "0",
                IndexOpen: _database.GetMeta("facet_index_open") != "0");
        });
        _currentFragmentId = restored.LastId;
        _currentMonth = restored.Month;
        _monthBeforeSearch = restored.Month;
        _newestFirst = restored.Newest;
        _pageSize = restored.PageSize;
        _navOpen = restored.NavOpen;
        _contextOpen = restored.ContextOpen;
        _categoryFacetOpen = restored.CategoryOpen;
        _tagFacetOpen = restored.TagOpen;
        _indexFacetOpen = restored.IndexOpen;
        ApplyPaneVisibility();
        if (_currentFragmentId is not null && _currentMonth is not null)
        {
            var query = CurrentQuery();
            _pageOffset = await Task.Run(() => (_database.GetFragmentOffset(_currentFragmentId, query) / _pageSize) * _pageSize);
        }
        await ReloadAsync(restoreFragment: _currentFragmentId);
        _normalReadingState = CaptureState();
        await RefreshRetryStateAsync();
        if (_database.IsReadOnly)
            Status("会話DBは読取専用です。");
        if (_runMaintenance) _ = RunBackgroundMaintenanceAsync();
        await WorkspaceLoadedAsync();
    }

    private FragmentQuery CurrentQuery() => new(
        _currentMonth,
        _searchText,
        _facetKind,
        _facetValue,
        _newestFirst,
        _pageOffset,
        _pageSize,
        _bookmarksOnly,
        _sectionType,
        _folderId);

    private Task RunDatabaseWriteAsync(Action operation) =>
        RunDatabaseWriteAsync(() => { operation(); return true; });

    private async Task<T> RunDatabaseWriteAsync<T>(Func<T> operation)
    {
        if (_database.IsReadOnly) throw new InvalidOperationException("実魔道書DBは読取専用です。");
        await _databaseWriteGate.WaitAsync().ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try { return await Task.Run(operation).ConfigureAwait(false); }
                catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6 && attempt < DatabaseWriteRetryDelays.Length)
                {
                    await Task.Delay(DatabaseWriteRetryDelays[attempt]).ConfigureAwait(false);
                }
            }
        }
        finally { _databaseWriteGate.Release(); }
    }

    private Task SaveViewPreferenceAsync(Action operation) =>
        _database.IsReadOnly ? Task.CompletedTask : RunDatabaseWriteAsync(operation);

    private Task ReloadAsync(string? restoreFragment = null, double? restoreOffset = null, double? anchorViewportY = null)
    {
        _readerReloadTask = ReloadCoreAsync(restoreFragment, restoreOffset, anchorViewportY);
        return _readerReloadTask;
    }

    private async Task ReloadCoreAsync(string? restoreFragment = null, double? restoreOffset = null, double? anchorViewportY = null)
    {
        var generation = ++_reloadGeneration;
        var query = CurrentQuery();
        Status("紙面を開いています…");
        try
        {
            var pageTask = Task.Run(() => _database.QueryFragments(query));
            var monthsTask = Task.Run(() => _database.GetMonthCounts());
            var facetQuery = FacetScopeQuery(query);
            var loadAllFacets = _showAllFacets.Count > 0 || _allFacetValuesLoaded.Count > 0 || _facetKind is not null;
            var facetsTask = Task.Run(() => _database.GetFacetCounts(
                facetQuery,
                loadAllFacets ? 0 : CollapsedFacetLimit + 1,
                _includeSingleCountFacets.Contains("tag") ? 1 : 2,
                _includeSingleCountFacets.Contains("index") ? 1 : 2));
            var facetTotalTask = Task.Run(() => _database.CountFragments(facetQuery));
            var foldersTask = Task.Run(() => (_database.GetFolderOverview(CollectionFolderKind.Bookmark), _database.GetFolderOverview(CollectionFolderKind.Reference)));
            var tagsTask = Task.Run(() => _database.GetTagSuggestions());
            var promotionTask = Task.Run(() => _facetKind == "index" && _facetValue is not null && _database.IsIndexWordPromoted(_facetValue));
            await Task.WhenAll(pageTask, monthsTask, facetsTask, facetTotalTask, foldersTask, tagsTask, promotionTask);
            if (generation != _reloadGeneration || _rawMode || _workspaceActive) return;
            (_bookmarkFolders, _referenceFolders) = await foldersTask;
            _tagSuggestions = await tagsTask;
            _selectedIndexPromoted = await promotionTask;
            var page = await pageTask;
            if (_pageOffset >= page.TotalCount && page.TotalCount > 0)
            {
                _pageOffset = Math.Max(0, ((page.TotalCount - 1) / _pageSize) * _pageSize);
                await ReloadAsync(restoreFragment, restoreOffset, anchorViewportY);
                return;
            }
            BuildNavigation(await monthsTask);
            await RenderPageAsync(page);
            BuildContext(await facetsTask, await facetTotalTask);
            if (anchorViewportY is not null && !string.IsNullOrWhiteSpace(restoreFragment) && _sections.TryGetValue(restoreFragment, out var anchor))
            {
                _restoring = true;
                await Dispatcher.InvokeAsync(() =>
                {
                    var newY = anchor.TranslatePoint(new Point(0, 0), ReaderScroll).Y;
                    var targetOffset = ReaderScroll.VerticalOffset + newY - anchorViewportY.Value;
                    ReaderScroll.ScrollToVerticalOffset(Math.Clamp(targetOffset, 0, ReaderScroll.ScrollableHeight));
                    _restoring = false;
                }, DispatcherPriority.Loaded);
            }
            else if (restoreOffset is not null)
            {
                _restoring = true;
                _ = Dispatcher.BeginInvoke(() =>
                {
                    ReaderScroll.ScrollToVerticalOffset(Math.Clamp(restoreOffset.Value, 0, ReaderScroll.ScrollableHeight));
                    _restoring = false;
                }, DispatcherPriority.Loaded);
            }
            else if (!string.IsNullOrWhiteSpace(restoreFragment) && _sections.TryGetValue(restoreFragment, out var target))
            {
                _restoring = true;
                _ = Dispatcher.BeginInvoke(() =>
                {
                    target.BringIntoView();
                    _restoring = false;
                }, DispatcherPriority.Loaded);
            }
            else ReaderScroll.ScrollToTop();
            var unit = _sectionType == FragmentSectionType.Reference ? "資料" : "断章";
            Status(page.TotalCount == 0 ? $"該当する{unit}はありません" : $"{page.TotalCount:N0}{unit}のうち {Math.Min(_pageOffset + 1, page.TotalCount):N0}–{Math.Min(_pageOffset + page.Items.Count, page.TotalCount):N0} を表示");
        }
        catch (Exception ex)
        {
            if (generation == _reloadGeneration) Status("紙面を開けませんでした: " + ex.Message);
        }
    }

    private static FragmentQuery FacetScopeQuery(FragmentQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.SearchText))
            return query with { Month = null, Offset = 0, Limit = 1 };
        if (query.BookmarkedOnly || query.FolderId is not null || query.SectionType == FragmentSectionType.Reference)
            return query with { Month = null, Offset = 0, Limit = 1 };
        return query with
        {
            Month = null, FacetKind = null, FacetValue = null,
            Offset = 0, Limit = 1
        };
    }

    private async Task RenderPageAsync(FragmentPage page)
    {
        if (!await FlushPendingEditsAsync()) throw new InvalidOperationException("未保存の編集を保存できませんでした。");
        Paper.Children.Clear();
        _sections.Clear();
        Paper.Children.Add(BuildViewHeader(page));
        if (page.Items.Count == 0)
        {
            RenderWelcome(page.TotalCount == 0 && string.IsNullOrWhiteSpace(_searchText) && !_bookmarksOnly && _facetValue is null);
            return;
        }
        foreach (var fragment in page.Items)
        {
            var section = BuildFragment(fragment);
            _sections[fragment.Id] = section;
            Paper.Children.Add(section);
        }
        Paper.Children.Add(BuildPager(page));
    }

    private FrameworkElement BuildViewHeader(FragmentPage page)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 34) };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var folderName = SelectedFolderName();
        var headingUnit = _sectionType == FragmentSectionType.Reference ? "資料" : "断章";
        var heading = !string.IsNullOrWhiteSpace(_searchText)
            ? $"{_searchText} — {page.TotalCount:N0}{headingUnit}"
            : folderName is not null ? $"{folderName} — {page.TotalCount:N0}{headingUnit}"
            : _sectionType == FragmentSectionType.Reference ? $"資料 — {page.TotalCount:N0}資料"
            : _bookmarksOnly ? $"しおり — {page.TotalCount:N0}断章"
            : FormatMonth(_currentMonth);
        row.Children.Add(new TextBlock { Text = heading, FontSize = 22, Foreground = Brush("#4B4134"), FontWeight = FontWeights.SemiBold });
        var order = new StackPanel { Orientation = Orientation.Horizontal };
        var newest = SmallButton("新しい順", _newestFirst ? "#5B4932" : "#9A8B75");
        var oldest = SmallButton("古い順", !_newestFirst ? "#5B4932" : "#9A8B75");
        newest.FontWeight = _newestFirst ? FontWeights.Bold : FontWeights.Normal;
        oldest.FontWeight = !_newestFirst ? FontWeights.Bold : FontWeights.Normal;
        newest.Click += async (_, _) => await ChangeSortAsync(true);
        oldest.Click += async (_, _) => await ChangeSortAsync(false);
        order.Children.Add(newest);
        order.Children.Add(oldest);
        Grid.SetColumn(order, 1);
        row.Children.Add(order);
        panel.Children.Add(row);
        var first = page.TotalCount == 0 ? 0 : _pageOffset + 1;
        var last = Math.Min(_pageOffset + page.Items.Count, page.TotalCount);
        var subtitleRow = new Grid { Margin = new Thickness(0, 7, 0, 0) };
        subtitleRow.ColumnDefinitions.Add(new ColumnDefinition());
        subtitleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var viewUnit = _sectionType == FragmentSectionType.Reference ? "資料" : "断章";
        var subtitle = new TextBlock
        {
            Text = page.TotalCount == 0 ? "静かな余白です" : $"{page.TotalCount:N0}{viewUnit}　{first:N0}–{last:N0}を表示",
            Foreground = Brush("#8A7C68"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center
        };
        subtitleRow.Children.Add(subtitle);
        var sizes = new StackPanel { Orientation = Orientation.Horizontal };
        sizes.Children.Add(new TextBlock { Text = "表示件数  ", Foreground = Brush("#9A8B75"), FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
        foreach (var size in AllowedPageSizes)
        {
            var sizeButton = SmallButton(size.ToString(CultureInfo.InvariantCulture), size == _pageSize ? "#5B4932" : "#9A8B75");
            sizeButton.FontWeight = size == _pageSize ? FontWeights.Bold : FontWeights.Normal;
            sizeButton.Click += async (_, _) => await ChangePageSizeAsync(size);
            sizes.Children.Add(sizeButton);
        }
        Grid.SetColumn(sizes, 1);
        subtitleRow.Children.Add(sizes);
        panel.Children.Add(subtitleRow);
        if (_facetValue is not null)
        {
            var filter = SmallButton($"{FacetLabel(_facetKind)}: {_facetValue}  ×", "#6B573D");
            filter.HorizontalAlignment = HorizontalAlignment.Left;
            filter.Margin = new Thickness(0, 12, 0, 0);
            filter.Click += async (_, _) => { _facetKind = null; _facetValue = null; _pageOffset = 0; await ReloadAsync(); };
            panel.Children.Add(filter);
        }
        if (_folderId is not null)
        {
            var folder = SmallButton($"フォルダ: {folderName ?? "未分類"}  ×", "#6B573D");
            folder.HorizontalAlignment = HorizontalAlignment.Left;
            folder.Margin = new Thickness(0, 8, 0, 0);
            folder.Click += async (_, _) => { _folderId = null; _pageOffset = 0; await ReloadAsync(); };
            panel.Children.Add(folder);
        }
        return panel;
    }

    private FrameworkElement BuildPager(FragmentPage page)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 12) };
        var previous = SmallButton("← 前へ", "#6F624F");
        previous.IsEnabled = _pageOffset > 0;
        previous.Click += async (_, _) => { _pageOffset = Math.Max(0, _pageOffset - _pageSize); await ReloadAsync(); };
        var next = SmallButton("次へ →", "#6F624F");
        next.IsEnabled = _pageOffset + page.Items.Count < page.TotalCount;
        next.Click += async (_, _) => { _pageOffset += _pageSize; await ReloadAsync(); };
        row.Children.Add(previous);
        row.Children.Add(new TextBlock
        {
            Text = page.TotalCount == 0 ? "0 / 0" : $"{_pageOffset / _pageSize + 1} / {(page.TotalCount + _pageSize - 1) / _pageSize}",
            Foreground = Brush("#8D7E68"), Margin = new Thickness(15, 7, 15, 0), FontSize = 11
        });
        row.Children.Add(next);
        return row;
    }

    private void RenderWelcome(bool emptyLibrary)
    {
        Paper.Children.Add(new TextBlock
        {
            Text = emptyLibrary ? "まだ、紙面は静かです。" : "この条件に合う断章はありません。",
            FontSize = 27, Foreground = Brush("#4B4237"), HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 70, 0, 18)
        });
        if (!emptyLibrary) return;
        Paper.Children.Add(new TextBlock
        {
            Text = "過去の対話を取り込むと、原文を守ったまま\n読み返せる断章へ静かに編み直します。",
            FontSize = 15, LineHeight = 27, TextAlignment = TextAlignment.Center,
            Foreground = Brush("#6E6252"), HorizontalAlignment = HorizontalAlignment.Center
        });
    }

    private Border BuildFragment(FragmentRecord fragment)
    {
        var body = new StackPanel();
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = fragment.Date.ToLocalTime().ToString("yyyy.MM.dd"), FontSize = 12,
            Foreground = Brush("#887A65"), VerticalAlignment = VerticalAlignment.Center
        });
        var star = SmallButton(fragment.Bookmarked ? "★" : "☆", fragment.Bookmarked ? "#80653A" : "#9A8C77");
        star.IsEnabled = !_database.IsReadOnly;
        star.ToolTip = "栞を付ける / 外す";
        star.Click += async (_, _) =>
        {
            var state = await RunDatabaseWriteAsync(() => _database.ToggleFlag(fragment.Id, "bookmarked"));
            star.Content = state ? "★" : "☆";
            star.Foreground = Brush(state ? "#80653A" : "#9A8C77");
            Status(state ? "栞を挟みました" : "栞を外しました");
            if (_bookmarksOnly && !state) await ReloadAsync(restoreOffset: ReaderScroll.VerticalOffset);
        };
        Grid.SetColumn(star, 1);
        header.Children.Add(star);
        body.Children.Add(header);

        var title = BuildSelectableText(fragment.Title, 25, "#302B25", 0, new Thickness(0, 8, 0, 18), FontWeights.SemiBold);
        title.ToolTip = "クリックで原文を開く／ドラッグして選択・コピー";
        title.AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(async (_, e) => await OpenRawFromTextClickAsync(title, fragment, e)), true);
        body.Children.Add(title);
        var quote = BuildLinkedText("“ " + fragment.Excerpt + " ”", fragment, 15, "#5E5141", 26,
            new Thickness(14, 0, 14, 20), FontStyles.Italic);
        quote.ToolTip = "クリックで原文を開く／ドラッグして選択・コピー。リンク語は横断探索します";
        quote.AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(async (_, e) => await OpenRawFromTextClickAsync(quote, fragment, e)), true);
        body.Children.Add(quote);
        var summary = BuildLinkedText(fragment.Summary, fragment, 15, "#3A352F", 27,
            new Thickness(0, 0, 0, 15), FontStyles.Normal);
        summary.ToolTip = "クリックで原文を開く／ドラッグして選択・コピー。リンク語は横断探索します";
        summary.AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(async (_, e) => await OpenRawFromTextClickAsync(summary, fragment, e)), true);
        body.Children.Add(summary);
        body.Children.Add(BuildTaxonomy(fragment));
        body.Children.Add(BuildTagEditor(fragment));

        var controls = new WrapPanel { Visibility = Visibility.Visible, Margin = new Thickness(0, 8, 0, 0), Opacity = 0.78 };
        var rawButton = SmallButton("原文を読む", "#756A5A");
        rawButton.Click += async (_, _) => await OpenRawModeAsync(fragment);
        controls.Children.Add(rawButton);
        if (BuildWorkspaceConversationButton(fragment.ConversationId) is { } extensionAction) controls.Children.Add(extensionAction);
        var relatedPanel = new WrapPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 14, 0, 0) };
        var relatedLoaded = false;
        var relatedToggle = SmallButton("関連記憶", "#756A5A");
        relatedToggle.Click += async (_, _) =>
        {
            if (!relatedLoaded)
            {
                relatedToggle.Content = "関連を探索中…";
                var related = await Task.Run(() => _database.FindRelated(fragment.Id));
                relatedPanel.Children.Add(new TextBlock { Text = "関連  ", Foreground = Brush("#8A7E6B"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
                foreach (var item in related)
                {
                    var link = SmallButton(item.Title, "#74664F");
                    link.FontSize = 11;
                    link.Click += async (_, _) => await JumpToAsync(item.Id, true);
                    relatedPanel.Children.Add(link);
                }
                if (related.Count == 0) relatedPanel.Children.Add(new TextBlock { Text = "まだ見つかっていません", Foreground = Brush("#938672"), FontSize = 11 });
                relatedLoaded = true;
            }
            relatedPanel.Visibility = relatedPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            relatedToggle.Content = relatedPanel.Visibility == Visibility.Visible ? "関連を閉じる" : "関連記憶";
        };
        controls.Children.Add(relatedToggle);
        var titleEdit = SmallButton("題を編集", "#756A5A");
        titleEdit.Click += (_, _) => BeginInlineEdit(body, title, fragment.Title, value => title.Text = value, fragment.Id, "title", false);
        controls.Children.Add(titleEdit);
        var summaryEdit = SmallButton("要約を編集", "#756A5A");
        summaryEdit.Click += (_, _) => BeginInlineEdit(body, summary, fragment.Summary, value => SetLinkedText(summary, value), fragment.Id, "summary", true);
        controls.Children.Add(summaryEdit);
        var sectionMove = SmallButton(fragment.SectionType == FragmentSectionType.Main ? "資料へ送る" : "本編へ戻す", "#756A5A");
        sectionMove.Click += async (_, _) =>
        {
            var destination = fragment.SectionType == FragmentSectionType.Main ? FragmentSectionType.Reference : FragmentSectionType.Main;
            await RunDatabaseWriteAsync(() => _database.MoveFragmentToSection(fragment.Id, destination));
            _folderId = null;
            Status(destination == FragmentSectionType.Reference ? "断章を資料へ送りました。原文と出典は保持されています" : "断章を本編へ戻しました");
            await ReloadAsync();
        };
        controls.Children.Add(sectionMove);
        var hide = SmallButton("非表示", "#8A7E6D");
        hide.Click += async (_, _) =>
        {
            hide.IsEnabled = false;
            _positionTimer.Stop();
            var query = CurrentQuery();
            var offset = ReaderScroll.VerticalOffset;
            var neighborId = await Task.Run(() => _database.GetAdjacentFragmentId(fragment.Id, query));
            double? neighborY = null;
            if (neighborId is not null && _sections.TryGetValue(neighborId, out var neighbor))
                neighborY = neighbor.TranslatePoint(new Point(0, 0), ReaderScroll).Y;
            try
            {
                await RunDatabaseWriteAsync(() => _database.ToggleFlag(fragment.Id, "hidden"));
                _currentFragmentId = neighborId;
                if (neighborId is null) await RunDatabaseWriteAsync(() => _database.SetMeta("last_fragment_id", ""));
                await ReloadAsync(neighborId, offset, neighborY);
                QueueReadingPositionSave();
                Status("断章を非表示にしました。原文は保存されています");
            }
            catch (Exception ex) { Status("断章を非表示にできませんでした: " + ex.Message); }
            finally { hide.IsEnabled = true; }
        };
        controls.Children.Add(hide);
        if (_database.IsReadOnly)
            titleEdit.IsEnabled = summaryEdit.IsEnabled = sectionMove.IsEnabled = hide.IsEnabled = false;
        body.Children.Add(controls);
        body.Children.Add(BuildFolderEditor(fragment, CollectionFolderKind.Bookmark));
        if (fragment.SectionType == FragmentSectionType.Reference)
            body.Children.Add(BuildFolderEditor(fragment, CollectionFolderKind.Reference));
        body.Children.Add(relatedPanel);
        body.Children.Add(BuildSectionRule());

        var section = new Border
        {
            Child = body, Tag = fragment.Id, Padding = new Thickness(0, 4, 0, 34),
            Margin = new Thickness(0, 0, 0, 32), BorderBrush = Brush("#C9BCA3"),
            BorderThickness = new Thickness(0, 0, 0, 1), Background = Brushes.Transparent
        };
        section.AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(async (_, e) =>
        {
            if (IsWithinInteractiveControl(e.OriginalSource as DependencyObject)) return;
            e.Handled = true;
            await OpenRawModeAsync(fragment);
        }), true);
        section.MouseEnter += (_, _) => { _currentFragmentId = fragment.Id; controls.Opacity = 1; };
        section.MouseLeave += (_, _) => controls.Opacity = 0.78;
        return section;
    }

    private FrameworkElement BuildSectionRule()
    {
        var rule = new Grid { Height = 14, Margin = new Thickness(0, 18, 0, 0), Opacity = 0.52, IsHitTestVisible = false };
        rule.ColumnDefinitions.Add(new ColumnDefinition());
        rule.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rule.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new Border { Height = 1, Background = Brush("#B6A080"), Margin = new Thickness(18, 7, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        var mark = new TextBlock { Text = "◇", FontSize = 8, Foreground = Brush("#8F7654"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) };
        var right = new Border { Height = 1, Background = Brush("#B6A080"), Margin = new Thickness(8, 7, 18, 0), VerticalAlignment = VerticalAlignment.Top };
        rule.Children.Add(left);
        Grid.SetColumn(mark, 1);
        rule.Children.Add(mark);
        Grid.SetColumn(right, 2);
        rule.Children.Add(right);
        return rule;
    }

    private FrameworkElement BuildTagEditor(FragmentRecord fragment)
    {
        if (_database.IsReadOnly) return new StackPanel();
        var root = new StackPanel { Margin = new Thickness(0, 5, 0, 2) };
        var toggle = SmallButton("＋ Tagを整える", "#756A5A");
        toggle.HorizontalAlignment = HorizontalAlignment.Left;
        toggle.ToolTip = "必要な断章だけ、手動Tag・AUTO除外・Index Word昇格を編集";
        root.Children.Add(toggle);
        var content = new StackPanel { Margin = new Thickness(10, 7, 0, 8) };
        var frame = new Border
        {
            Child = content, Visibility = Visibility.Collapsed, Background = Brush("#E8DDC7"),
            BorderBrush = Brush("#C8B99D"), BorderThickness = new Thickness(1), Padding = new Thickness(10)
        };
        toggle.Click += (_, _) => frame.Visibility = frame.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

        var addRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        var input = new ComboBox
        {
            Width = 230, IsEditable = true, ItemsSource = _tagSuggestions,
            Background = Brush("#F7EEDC"), Foreground = Brush("#3F382F"),
            BorderBrush = Brush("#B9A98C"), Padding = new Thickness(7, 4, 7, 4),
            ToolTip = "既存Tagを選ぶか、新しいTagを入力"
        };
        var add = SmallButton("追加", "#66523A");
        async Task AddAsync()
        {
            var value = input.Text.Trim();
            if (value.Length == 0) return;
            try
            {
                add.IsEnabled = false;
                await RunDatabaseWriteAsync(() => _database.AddUserTag(fragment.Id, value));
                await ReloadPreservingOffsetAsync(fragment.Id, "手動Tagを保存しました");
            }
            catch (Exception ex) { Status("Tagを保存できませんでした: " + ex.Message); }
            finally { add.IsEnabled = true; }
        }
        add.Click += async (_, _) => await AddAsync();
        input.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await AddAsync();
        };
        addRow.Children.Add(input);
        addRow.Children.Add(add);
        content.Children.Add(addRow);

        foreach (var tag in fragment.UserTags)
        {
            var promoted = fragment.PromotedIndexTags.Contains(tag, StringComparer.OrdinalIgnoreCase);
            content.Children.Add(TagEditRow(
                $"#{tag}  USER",
                promoted ? "Index Word扱いへ戻す" : "Index Wordへ",
                promoted ? "Index Wordから昇格したUSER Tag。元のIndex Wordは保持されています" : "手動Tag",
                async button =>
                {
                    button.IsEnabled = false;
                    if (promoted) await RunDatabaseWriteAsync(() => _database.DemoteIndexWordTag(tag));
                    else await RunDatabaseWriteAsync(() => _database.MoveUserTagToIndex(fragment.Id, tag));
                    await ReloadPreservingOffsetAsync(fragment.Id, promoted ? "Index Word扱いへ戻しました" : "手動TagをIndex Wordへ移しました");
                },
                extraAction: promoted ? null : async button =>
                {
                    button.IsEnabled = false;
                    await RunDatabaseWriteAsync(() => _database.RemoveUserTag(fragment.Id, tag));
                    await ReloadPreservingOffsetAsync(fragment.Id, "手動Tagを外しました");
                }));
        }
        foreach (var tag in fragment.AutoTags.Where(tag => !fragment.UserTags.Contains(tag, StringComparer.OrdinalIgnoreCase)))
        {
            content.Children.Add(TagEditRow($"#{tag}  AUTO", "除外", "自動Tag。除外後も元の自動判定は保持されます", async button =>
            {
                button.IsEnabled = false;
                await RunDatabaseWriteAsync(() => _database.ExcludeAutoTag(fragment.Id, tag));
                await ReloadPreservingOffsetAsync(fragment.Id, "AUTO Tagをこの断章から除外しました");
            }));
        }
        foreach (var tag in fragment.ExcludedAutoTags)
        {
            content.Children.Add(TagEditRow($"#{tag}  除外中", "戻す", "除外中のAUTO Tag", async button =>
            {
                button.IsEnabled = false;
                await RunDatabaseWriteAsync(() => _database.RestoreAutoTag(fragment.Id, tag));
                await ReloadPreservingOffsetAsync(fragment.Id, "AUTO Tagを戻しました");
            }));
        }
        foreach (var word in fragment.Keywords.Where(word => !fragment.Tags.Contains(word, StringComparer.OrdinalIgnoreCase)).Take(8))
        {
            content.Children.Add(TagEditRow(word, "Tagとして使う", "このIndex Wordを持つ断章へUSER Tagとして昇格", async button =>
            {
                button.IsEnabled = false;
                var affected = await RunDatabaseWriteAsync(() => _database.PromoteIndexWordToTag(word));
                await ReloadPreservingOffsetAsync(fragment.Id, $"{word}をUSER Tagへ昇格しました（{affected:N0}断章）");
            }));
        }
        root.Children.Add(frame);
        return root;
    }

    private FrameworkElement TagEditRow(
        string label,
        string actionText,
        string tooltip,
        Func<Button, Task> action,
        Func<Button, Task>? extraAction = null)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock
        {
            Text = label, Foreground = Brush("#554939"), FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
            ToolTip = tooltip
        });
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var button = SmallButton(actionText, "#6A563C");
        button.FontSize = 10;
        button.Click += async (_, _) =>
        {
            try { await action(button); }
            catch (Exception ex) { button.IsEnabled = true; Status("Tagを変更できませんでした: " + ex.Message); }
        };
        actions.Children.Add(button);
        if (extraAction is not null)
        {
            var remove = SmallButton("削除", "#806C55");
            remove.FontSize = 10;
            remove.Click += async (_, _) =>
            {
                try { await extraAction(remove); }
                catch (Exception ex) { remove.IsEnabled = true; Status("Tagを削除できませんでした: " + ex.Message); }
            };
            actions.Children.Add(remove);
        }
        Grid.SetColumn(actions, 1);
        row.Children.Add(actions);
        return row;
    }

    private FrameworkElement BuildFolderEditor(FragmentRecord fragment, CollectionFolderKind kind)
    {
        if (_database.IsReadOnly) return new StackPanel();
        var bookmark = kind == CollectionFolderKind.Bookmark;
        var selectedId = bookmark ? fragment.BookmarkFolderId : fragment.ReferenceFolderId;
        var selectedName = bookmark ? fragment.BookmarkFolderName : fragment.ReferenceFolderName;
        var overview = bookmark ? _bookmarkFolders : _referenceFolders;
        var noun = bookmark ? "しおり" : "資料";
        var root = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
        var toggle = SmallButton(selectedName is null ? $"{noun}フォルダへ収める" : $"{noun}: {selectedName}", "#756A5A");
        toggle.HorizontalAlignment = HorizontalAlignment.Left;
        root.Children.Add(toggle);
        var panel = new WrapPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(10, 4, 0, 7) };
        toggle.Click += (_, _) => panel.Visibility = panel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

        var unfiled = FacetLikeButton("未分類", selectedId is null && (!bookmark || fragment.Bookmarked));
        unfiled.Click += async (_, _) => await AssignFromButtonAsync(unfiled, null, "未分類");
        panel.Children.Add(unfiled);
        foreach (var folder in overview.Folders)
        {
            var folderButton = FacetLikeButton(folder.Name, selectedId == folder.Id);
            folderButton.ToolTip = $"{folder.Count:N0}{(bookmark ? "断章" : "資料")}";
            folderButton.Click += async (_, _) => await AssignFromButtonAsync(folderButton, folder.Id, folder.Name);
            panel.Children.Add(folderButton);
        }
        var create = new TextBox
        {
            Width = 150, Margin = new Thickness(3, 2, 3, 2), Padding = new Thickness(6, 3, 6, 3),
            Background = Brush("#F7EEDC"), Foreground = Brush("#3F382F"), BorderBrush = Brush("#B9A98C"),
            ToolTip = $"新しい{noun}フォルダ名"
        };
        var createButton = SmallButton("＋作成", "#66523A");
        async Task CreateAsync()
        {
            var name = create.Text.Trim();
            if (name.Length == 0) return;
            try
            {
                createButton.IsEnabled = false;
                var id = await RunDatabaseWriteAsync(() => _database.CreateFolder(kind, name));
                await AssignFolderAsync(fragment.Id, kind, id, name);
            }
            catch (Exception ex) { Status("フォルダを作成できませんでした: " + ex.Message); }
            finally { createButton.IsEnabled = true; }
        }
        createButton.Click += async (_, _) => await CreateAsync();
        create.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await CreateAsync();
        };
        panel.Children.Add(create);
        panel.Children.Add(createButton);
        root.Children.Add(panel);
        return root;

        async Task AssignFromButtonAsync(Button button, long? folderId, string name)
        {
            button.IsEnabled = false;
            try { await AssignFolderAsync(fragment.Id, kind, folderId, name); }
            finally { button.IsEnabled = true; }
        }
    }

    private Button FacetLikeButton(string text, bool active)
    {
        var button = SmallButton(active ? "✦ " + text : text, active ? "#FFF1D2" : "#6F604D");
        button.Background = active ? Brush("#6A573D") : Brush("#E5D8BF");
        button.BorderBrush = active ? Brush("#9D8055") : Brush("#C6B495");
        button.BorderThickness = new Thickness(1);
        button.Margin = new Thickness(0, 2, 5, 2);
        return button;
    }

    private async Task AssignFolderAsync(string fragmentId, CollectionFolderKind kind, long? folderId, string name)
    {
        if (_folderMoveInProgress)
        {
            Status("フォルダを移動しています…");
            return;
        }
        _folderMoveInProgress = true;
        var moved = false;
        try
        {
            try
            {
                await RunDatabaseWriteAsync(() => _database.AssignFolder(fragmentId, kind, folderId));
                moved = true;
            }
            catch (Exception ex)
            {
                var saved = await Task.Run(() => _database.GetFragment(fragmentId));
                moved = saved is not null && (kind == CollectionFolderKind.Bookmark
                    ? saved.BookmarkFolderId == folderId && saved.Bookmarked
                    : saved.ReferenceFolderId == folderId && saved.SectionType == FragmentSectionType.Reference);
                if (!moved)
                {
                    Status("フォルダを変更できませんでした: " + ex.Message);
                    return;
                }
            }
            if ((_bookmarksOnly && kind == CollectionFolderKind.Bookmark) || (_sectionType == FragmentSectionType.Reference && kind == CollectionFolderKind.Reference))
                _folderId = folderId ?? -1;
            await ReloadPreservingOffsetAsync(fragmentId, $"{name}へ移動しました");
        }
        finally { _folderMoveInProgress = false; }
    }

    private async Task ReloadPreservingOffsetAsync(string fragmentId, string status)
    {
        var offset = ReaderScroll.VerticalOffset;
        await ReloadAsync(fragmentId, offset);
        Status(status);
    }

    private RichTextBox BuildLinkedText(
        string text,
        FragmentRecord fragment,
        double fontSize,
        string foreground,
        double lineHeight,
        Thickness margin,
        FontStyle fontStyle)
    {
        var block = new RichTextBox
        {
            IsReadOnly = true, IsDocumentEnabled = true, IsTabStop = false,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            Foreground = Brush(foreground), FontSize = fontSize, FontStyle = fontStyle,
            Margin = margin, Cursor = Cursors.IBeam,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            SelectionBrush = Brush("#8A7048"), SelectionOpacity = 0.32, SelectionTextBrush = Brush("#302B25")
        };
        var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = lineHeight };
        block.Document = new FlowDocument { PagePadding = new Thickness(0), Background = Brushes.Transparent };
        block.Document.Blocks.Add(paragraph);
        AddLinkedInlines(paragraph.Inlines, text, fragment);
        block.ContextMenu = CopyMenu(
            () => block.Selection.Text.TrimEnd('\r', '\n'),
            () => new TextRange(block.Document.ContentStart, block.Document.ContentEnd).Text.TrimEnd('\r', '\n'));
        return block;
    }

    private void SetLinkedText(RichTextBox block, string text)
    {
        var paragraph = block.Document.Blocks.OfType<Paragraph>().FirstOrDefault();
        if (paragraph is null) return;
        paragraph.Inlines.Clear();
        paragraph.Inlines.Add(new Run(text));
    }

    private void AddLinkedInlines(InlineCollection inlines, string text, FragmentRecord fragment)
    {
        var terms = fragment.Categories.Select(value => (Value: value, Kind: "category"))
            .Concat(fragment.Tags.Select(value => (Value: value, Kind: "tag")))
            .Concat(fragment.Keywords.Select(value => (Value: value, Kind: "index")))
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .DistinctBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(item => item.Value.Length)
            .ToList();

        var position = 0;
        while (position < text.Length)
        {
            var match = terms
                .Select(item => (item.Value, item.Kind, Index: text.IndexOf(item.Value, position, StringComparison.OrdinalIgnoreCase)))
                .Where(item => item.Index >= 0)
                .OrderBy(item => item.Index)
                .ThenByDescending(item => item.Value.Length)
                .FirstOrDefault();
            if (string.IsNullOrEmpty(match.Value) || match.Index < 0)
            {
                inlines.Add(new Run(text[position..]));
                break;
            }
            if (match.Index > position) inlines.Add(new Run(text[position..match.Index]));
            var visibleText = text.Substring(match.Index, match.Value.Length);
            var link = new Hyperlink(new Run(visibleText))
            {
                Foreground = Brush(IsSelectedFacet(match.Kind, match.Value) ? "#9A6C25" : "#725934"),
                FontWeight = IsSelectedFacet(match.Kind, match.Value) ? FontWeights.Bold : FontWeights.Normal,
                TextDecorations = TextDecorations.Underline,
                ToolTip = $"{visibleText}を横断探索"
            };
            var kind = match.Kind;
            var value = match.Value;
            link.Click += async (_, e) =>
            {
                e.Handled = true;
                await ApplyFacetAsync(kind, value);
            };
            inlines.Add(link);
            position = match.Index + match.Value.Length;
        }
        if (text.Length == 0) inlines.Add(new Run());
    }

    private TextBox BuildSelectableText(string text, double fontSize, string foreground, double lineHeight, Thickness margin, FontWeight? fontWeight = null)
    {
        var block = new TextBox
        {
            Text = text, IsReadOnly = true, IsTabStop = false, FontSize = fontSize,
            Foreground = Brush(foreground), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            TextWrapping = TextWrapping.Wrap, Margin = margin, Cursor = Cursors.IBeam,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            SelectionBrush = Brush("#8A7048"), SelectionOpacity = 0.32, SelectionTextBrush = Brush("#302B25")
        };
        if (fontWeight is not null) block.FontWeight = fontWeight.Value;
        block.ContextMenu = CopyMenu(() => block.SelectedText, () => block.Text);
        return block;
    }

    private RichTextBox BuildSelectableParagraph(string text, double fontSize, string foreground, double lineHeight, Thickness margin)
    {
        var block = new RichTextBox
        {
            IsReadOnly = true, IsTabStop = false, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            Foreground = Brush(foreground), FontSize = fontSize, Margin = margin, Cursor = Cursors.IBeam,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            SelectionBrush = Brush("#8A7048"), SelectionOpacity = 0.32, SelectionTextBrush = Brush("#302B25")
        };
        block.Document = new FlowDocument { PagePadding = new Thickness(0), Background = Brushes.Transparent };
        block.Document.Blocks.Add(new Paragraph(new Run(text)) { Margin = new Thickness(0), LineHeight = lineHeight });
        block.ContextMenu = CopyMenu(
            () => block.Selection.Text.TrimEnd('\r', '\n'),
            () => new TextRange(block.Document.ContentStart, block.Document.ContentEnd).Text.TrimEnd('\r', '\n'));
        return block;
    }

    private ContextMenu CopyMenu(Func<string> selected, Func<string> all)
    {
        var menu = new ContextMenu { Background = Brush("#2B2721"), BorderBrush = Brush("#5B5144"), Foreground = Brush("#D8CCB4") };
        var copy = new MenuItem { Header = "コピー" };
        copy.Click += (_, _) => CopyText(string.IsNullOrEmpty(selected()) ? all() : selected());
        var copyAll = new MenuItem { Header = "全文をコピー" };
        copyAll.Click += (_, _) => CopyText(all());
        menu.Items.Add(copy);
        menu.Items.Add(copyAll);
        return menu;
    }

    private void CopyText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); }
        catch (Exception ex) { Status("コピーできませんでした: " + ex.Message); }
    }

    private async Task OpenRawFromTextClickAsync(TextBox box, FragmentRecord fragment, MouseButtonEventArgs e)
    {
        var opensRaw = ReaderClickBehavior.OpensRaw(box.SelectionLength > 0, false);
        e.Handled = true;
        if (opensRaw) await OpenRawModeAsync(fragment);
    }

    private async Task OpenRawFromTextClickAsync(RichTextBox box, FragmentRecord fragment, MouseButtonEventArgs e)
    {
        var opensRaw = ReaderClickBehavior.OpensRaw(!box.Selection.IsEmpty, IsWithinHyperlink(e.OriginalSource as DependencyObject));
        e.Handled = true;
        if (opensRaw) await OpenRawModeAsync(fragment);
    }

    private static bool IsWithinInteractiveControl(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button || source is TextBoxBase || source is Hyperlink) return true;
            source = source switch
            {
                FrameworkContentElement content => content.Parent,
                _ => VisualTreeHelper.GetParent(source)
            };
        }
        return false;
    }

    private static bool IsWithinHyperlink(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Hyperlink) return true;
            source = source switch
            {
                FrameworkContentElement content => content.Parent,
                _ => VisualTreeHelper.GetParent(source)
            };
        }
        return false;
    }

    private WrapPanel BuildTaxonomy(FragmentRecord fragment)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        foreach (var category in fragment.Categories)
            panel.Children.Add(FacetButton(category, "category", "#5D4C38", "#DED0B5"));
        foreach (var tag in fragment.Tags.Take(6))
            panel.Children.Add(FacetButton("#" + tag, "tag", "#685942", "#E5D8BF", tag));
        foreach (var word in fragment.Keywords.Take(4))
            if (!fragment.Tags.Contains(word, StringComparer.OrdinalIgnoreCase))
                panel.Children.Add(FacetButton(word, "index", "#766B5C", "#ECE2CF"));
        return panel;
    }

    private Button FacetButton(string text, string kind, string foreground, string background, string? value = null)
    {
        var actualValue = value ?? text;
        var active = IsSelectedFacet(kind, actualValue);
        var button = new Button
        {
            Content = active ? "✦ " + text : text,
            Foreground = Brush(active ? "#3F2E17" : foreground),
            Background = Brush(active ? "#CFB98F" : background),
            BorderBrush = Brush(active ? "#866D45" : "#C6B495"), BorderThickness = new Thickness(1),
            Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(0, 0, 6, 6), FontSize = 11
        };
        button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        button.Click += async (_, _) => await ApplyFacetAsync(kind, actualValue);
        return button;
    }

    private bool IsSelectedFacet(string kind, string value) =>
        _facetKind == kind && string.Equals(_facetValue, value, StringComparison.OrdinalIgnoreCase);

    private Task OpenRawModeAsync(FragmentRecord fragment)
    {
        _readerRawTask = OpenRawModeCoreAsync(fragment);
        return _readerRawTask;
    }

    private async Task OpenRawModeCoreAsync(FragmentRecord fragment)
    {
        if (_rawMode) return;
        if (!await FlushPendingEditsAsync()) return;
        _rawReturnState = CaptureState();
        _rawMode = true;
        UpdateReaderJumpControls();
        SearchBox.IsEnabled = false;
        RawHeader.Visibility = Visibility.Visible;
        RawHeaderTitle.Text = $"{fragment.Date.ToLocalTime():yyyy.MM.dd}　{fragment.Title}";
        PaperBorder.MaxWidth = 920;
        Paper.Children.Clear();
        Paper.Children.Add(new TextBlock
        {
            Text = "原文を開いています…", FontSize = 15, Foreground = Brush("#6D604E"),
            Margin = new Thickness(0, 30, 0, 30), HorizontalAlignment = HorizontalAlignment.Center
        });
        ReaderScroll.ScrollToTop();
        Status("原文を読み込んでいます…");
        try
        {
            var source = await Task.Run(() => (
                Messages: _database.GetSourceMessages(fragment.Id),
                IsArchived: _database.GetChatGptSourceArchiveState(fragment.ConversationId)));
            var messages = source.Messages;
            Paper.Children.Clear();
            Paper.Children.Add(new TextBlock
            {
                Text = fragment.Date.ToLocalTime().ToString("yyyy.MM.dd"), FontSize = 12,
                Foreground = Brush("#8B7C66"), Margin = new Thickness(0, 0, 0, 8)
            });
            Paper.Children.Add(BuildSelectableText(fragment.Title, 27, "#302B25", 0, new Thickness(0, 0, 0, 10), FontWeights.SemiBold));
            if (BuildWorkspaceConversationButton(fragment.ConversationId) is { } extensionAction) Paper.Children.Add(extensionAction);
            var sourceStatus = SourceMetadataPresentation.ChatGptArchiveStatus(source.IsArchived);
            if (sourceStatus is not null)
            {
                Paper.Children.Add(new TextBlock
                {
                    Text = sourceStatus,
                    FontSize = 11, Foreground = Brush("#7B6546"), Margin = new Thickness(0, 0, 0, 8)
                });
            }
            Paper.Children.Add(new TextBlock
            {
                Text = "保存原文は変更せず、画面上の内部マーカーだけを整えて表示しています。",
                FontSize = 10, Foreground = Brush("#9A8A72"), Margin = new Thickness(0, 0, 0, 30)
            });

            foreach (var message in messages)
            {
                var isUser = message.Role == "user";
                var role = isUser ? "あなた" : message.Role == "assistant" ? "ChatGPT" : message.Role;
                var group = new Border
                {
                    BorderBrush = Brush(isUser ? "#A88758" : "#8D806E"),
                    BorderThickness = new Thickness(2, 0, 0, 0),
                    Padding = new Thickness(18, 0, 0, 0),
                    Margin = new Thickness(0, 0, 0, 34)
                };
                var content = new StackPanel();
                content.Children.Add(new TextBlock
                {
                    Text = role, FontSize = 12, FontWeight = FontWeights.SemiBold,
                    Foreground = Brush(isUser ? "#76562F" : "#6F6251"), Margin = new Thickness(0, 0, 0, 8)
                });
                content.Children.Add(BuildSelectableParagraph(DisplaySanitizer.Clean(message.RawText), 16, "#403A32", 30, new Thickness(0)));
                group.Child = content;
                Paper.Children.Add(group);
            }

            var returnButton = SmallButton("← この断章へ戻る", "#66523A");
            returnButton.FontSize = 14;
            returnButton.Padding = new Thickness(12, 10, 12, 10);
            returnButton.HorizontalAlignment = HorizontalAlignment.Left;
            returnButton.Click += CloseRaw_Click;
            Paper.Children.Add(returnButton);
            Status($"原文  {messages.Count:N0}発言");
        }
        catch (Exception ex)
        {
            Status("原文を開けませんでした: " + ex.Message);
            await CloseRawModeAsync();
        }
    }

    private async void CloseRaw_Click(object sender, RoutedEventArgs e) => await CloseRawModeAsync();

    private async Task CloseRawModeAsync()
    {
        if (!_rawMode) return;
        if (_draggingScrollIndicator)
        {
            ScrollIndicatorLayer.ReleaseMouseCapture();
            _draggingScrollIndicator = false;
        }
        var state = _rawReturnState;
        _rawMode = false;
        _rawReturnState = null;
        SearchBox.IsEnabled = true;
        RawHeader.Visibility = Visibility.Collapsed;
        PaperBorder.MaxWidth = 790;
        if (state is not null) await RestoreStateAsync(state, restoreExactOffset: true);
        UpdateReaderJumpControls();
    }

    private void BuildNavigation(IReadOnlyList<MonthCount> months)
    {
        NavContent.Children.Clear();
        NavContent.Children.Add(new TextBlock { Text = "年月の目次", Foreground = Brush("#C8BBA5"), FontSize = 16, Margin = new Thickness(0, 0, 0, 15) });
        var activeYear = (_currentMonth ?? _monthBeforeSearch ?? months.FirstOrDefault()?.Month)?[..4];
        if (activeYear is not null) _expandedYears.Add(activeYear);
        foreach (var year in months.GroupBy(item => item.Month[..4]))
        {
            var expanded = _expandedYears.Contains(year.Key);
            var yearButton = SmallButton($"{year.Key}  {(expanded ? "▼" : "▶")}", year.Key == activeYear ? "#D7C29A" : "#8F826F");
            yearButton.HorizontalContentAlignment = HorizontalAlignment.Left;
            yearButton.FontSize = 13;
            yearButton.FontWeight = year.Key == activeYear ? FontWeights.SemiBold : FontWeights.Normal;
            yearButton.Margin = new Thickness(0, 8, 0, 3);
            var monthPanel = new StackPanel { Visibility = expanded ? Visibility.Visible : Visibility.Collapsed, Margin = new Thickness(7, 0, 0, 2) };
            foreach (var item in year)
            {
                var month = int.Parse(item.Month[5..7], CultureInfo.InvariantCulture);
                var button = SmallButton($"{month}月   {item.Count:N0}", item.Month == _currentMonth && string.IsNullOrWhiteSpace(_searchText) ? "#E0C99D" : "#BBAE98");
                button.HorizontalContentAlignment = HorizontalAlignment.Left;
                button.FontWeight = item.Month == _currentMonth && string.IsNullOrWhiteSpace(_searchText) ? FontWeights.Bold : FontWeights.Normal;
                button.Click += async (_, _) => await SelectMonthAsync(item.Month, true);
                monthPanel.Children.Add(button);
            }
            yearButton.Click += (_, _) =>
            {
                var opening = monthPanel.Visibility != Visibility.Visible;
                monthPanel.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
                yearButton.Content = $"{year.Key}  {(opening ? "▼" : "▶")}";
                if (opening) _expandedYears.Add(year.Key); else _expandedYears.Remove(year.Key);
            };
            NavContent.Children.Add(yearButton);
            NavContent.Children.Add(monthPanel);
        }
    }

    private void BuildContext(IReadOnlyList<FacetCount> facets, int total)
    {
        BuildContextFacetActions(facets);
        ContextContent.Children.Clear();
        ContextContent.Children.Add(new TextBlock { Text = "探索", Foreground = Brush("#D6C8AE"), FontSize = 18, Margin = new Thickness(0, 0, 0, 4) });
        var scope = !string.IsNullOrWhiteSpace(_searchText)
            ? $"探索範囲：検索結果 {total:N0}{(_sectionType == FragmentSectionType.Reference ? "資料" : "断章")}"
            : _bookmarksOnly
                ? $"探索範囲：しおり {total:N0}断章"
            : _sectionType == FragmentSectionType.Reference
                ? $"探索範囲：資料 {total:N0}資料"
                : $"探索範囲：全記録 {total:N0}断章";
        ContextContent.Children.Add(new TextBlock { Text = scope, Foreground = Brush("#938775"), FontSize = 10, Margin = new Thickness(0, 0, 0, 16), TextWrapping = TextWrapping.Wrap });
        if (_bookmarksOnly) AddFolderSection(CollectionFolderKind.Bookmark, _bookmarkFolders);
        else if (_sectionType == FragmentSectionType.Reference) AddFolderSection(CollectionFolderKind.Reference, _referenceFolders);
        AddFacetSection("Category", "category");
        AddFacetSection("Tag", "tag");
        AddFacetSection("頻出語", "index");

        if (!_database.IsReadOnly && _facetKind == "index" && _facetValue is not null)
        {
            var promote = SmallButton(_selectedIndexPromoted ? "Index Word扱いへ戻す" : "Tagとして使う", "#E8CEA0");
            promote.Background = Brush("#4A3D2D");
            promote.BorderBrush = Brush("#806844");
            promote.BorderThickness = new Thickness(1);
            promote.Margin = new Thickness(0, 9, 0, 7);
            promote.ToolTip = _selectedIndexPromoted
                ? "昇格で付いたUSER Tagだけを外し、元のIndex Wordは残します"
                : "このIndex Wordを持つ断章へUSER Tagとして追加します";
            promote.Click += async (_, _) =>
            {
                try
                {
                    promote.IsEnabled = false;
                    var demoting = _selectedIndexPromoted;
                    var count = demoting
                        ? await RunDatabaseWriteAsync(() => _database.DemoteIndexWordTag(_facetValue))
                        : await RunDatabaseWriteAsync(() => _database.PromoteIndexWordToTag(_facetValue));
                    await ReloadAsync(restoreOffset: ReaderScroll.VerticalOffset);
                    Status(demoting
                        ? $"{_facetValue}をIndex Word扱いへ戻しました"
                        : $"{_facetValue}をUSER Tagとして使います（{count:N0}断章）");
                }
                catch (Exception ex) { promote.IsEnabled = true; Status("Index Wordを変更できませんでした: " + ex.Message); }
            };
            ContextContent.Children.Add(promote);
        }

        void AddFacetSection(string heading, string kind)
        {
            var isOpen = FacetPanelOpen(kind);
            var header = SmallButton($"{heading}  {(isOpen ? "▼" : "▶")}", "#AA9C86");
            header.HorizontalContentAlignment = HorizontalAlignment.Left;
            header.Margin = new Thickness(0, 10, 0, 3);
            header.FontSize = 12;
            ContextContent.Children.Add(header);
            var panel = new StackPanel { Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed };
            header.Click += async (_, _) =>
            {
                var opening = panel.Visibility != Visibility.Visible;
                panel.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
                header.Content = $"{heading}  {(opening ? "▼" : "▶")}";
                SetFacetPanelOpen(kind, opening);
                header.IsEnabled = false;
                try { await SaveViewPreferenceAsync(() => _database.SetMeta($"facet_{kind}_open", opening ? "1" : "0")); }
                catch (Exception ex) { Status("探索パネルの状態を保存できませんでした: " + ex.Message); }
                finally { header.IsEnabled = true; }
            };
            var values = facets.Where(item => item.Kind == kind).ToList();
            var progressive = kind is "tag" or "index";
            var allValuesLoaded = _allFacetValuesLoaded.Contains(kind);
            var visibleLimit = _visibleFacetCounts.TryGetValue(kind, out var requestedVisible)
                ? requestedVisible
                : CollapsedFacetLimit;
            if (progressive)
            {
                var includeSingles = _includeSingleCountFacets.Contains(kind);
                var includeSinglesButton = SmallButton(includeSingles ? "1件だけの語を隠す" : "1件だけの語も表示", "#756A58");
                includeSinglesButton.HorizontalAlignment = HorizontalAlignment.Left;
                includeSinglesButton.FontSize = 10;
                includeSinglesButton.Margin = new Thickness(0, 1, 0, 4);
                includeSinglesButton.Click += async (_, _) =>
                {
                    if (includeSingles) _includeSingleCountFacets.Remove(kind);
                    else _includeSingleCountFacets.Add(kind);
                    _visibleFacetCounts[kind] = CollapsedFacetLimit;
                    _allFacetValuesLoaded.Add(kind);
                    await ReloadAsync(restoreOffset: ReaderScroll.VerticalOffset);
                };
                panel.Children.Add(includeSinglesButton);
                if (allValuesLoaded)
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = $"{Math.Min(visibleLimit, values.Count):N0} / {values.Count:N0}件を表示",
                        Foreground = Brush("#938775"), FontSize = 10, Margin = new Thickness(0, 0, 0, 4)
                    });
                }
            }
            if (values.Count == 0)
            {
                panel.Children.Add(new TextBlock { Text = "—", Foreground = Brush("#655E54"), FontSize = 11 });
                ContextContent.Children.Add(panel);
                return;
            }
            var showingAll = _showAllFacets.Contains(kind);
            var displayed = showingAll ? values : values.Take(allValuesLoaded ? visibleLimit : CollapsedFacetLimit).ToList();
            var selected = values.FirstOrDefault(item => IsSelectedFacet(kind, item.Value));
            if (!showingAll && selected is not null && !displayed.Any(item => string.Equals(item.Value, selected.Value, StringComparison.OrdinalIgnoreCase)))
            {
                var maxDisplayed = allValuesLoaded ? visibleLimit : CollapsedFacetLimit;
                if (displayed.Count == maxDisplayed) displayed.RemoveAt(displayed.Count - 1);
                displayed.Add(selected);
            }
            foreach (var item in displayed)
            {
                var active = IsSelectedFacet(kind, item.Value);
                var button = SmallButton($"{(active ? "✦ " : string.Empty)}{item.Value}   {item.Count:N0}", active ? "#FFF1D2" : "#BBAE98");
                button.HorizontalContentAlignment = HorizontalAlignment.Left;
                button.FontSize = 11;
                button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
                button.Background = active ? Brush("#4A3D2D") : Brushes.Transparent;
                button.BorderBrush = active ? Brush("#9D8055") : Brushes.Transparent;
                button.BorderThickness = new Thickness(1);
                button.Click += async (_, _) => await ApplyFacetAsync(kind, item.Value);
                panel.Children.Add(button);
            }
            var hasMoreProgressive = progressive
                && (allValuesLoaded ? displayed.Count < values.Count : values.Count > CollapsedFacetLimit);
            if (progressive && hasMoreProgressive)
            {
                var more = SmallButton($"次の{FacetPageSize}件を見る", "#D0B987");
                more.HorizontalAlignment = HorizontalAlignment.Left;
                more.FontSize = 10;
                more.Margin = new Thickness(0, 4, 0, 0);
                more.Click += async (_, _) =>
                {
                    _visibleFacetCounts[kind] = visibleLimit + FacetPageSize;
                    _allFacetValuesLoaded.Add(kind);
                    await ReloadAsync(restoreOffset: ReaderScroll.VerticalOffset);
                };
                panel.Children.Add(more);
            }
            else if (!progressive && values.Count > CollapsedFacetLimit)
            {
                var more = SmallButton(showingAll ? "表示を戻す" : "もっと見る", "#D0B987");
                more.HorizontalAlignment = HorizontalAlignment.Left;
                more.FontSize = 10;
                more.Margin = new Thickness(0, 4, 0, 0);
                more.Click += async (_, _) =>
                {
                    if (showingAll) _showAllFacets.Remove(kind); else _showAllFacets.Add(kind);
                    await ReloadAsync(restoreOffset: ReaderScroll.VerticalOffset);
                };
                panel.Children.Add(more);
            }
            ContextContent.Children.Add(panel);
        }

        void AddFolderSection(CollectionFolderKind kind, CollectionFolderOverview overview)
        {
            var bookmark = kind == CollectionFolderKind.Bookmark;
            ContextContent.Children.Add(new TextBlock
            {
                Text = bookmark ? "しおりフォルダ" : "資料フォルダ", Foreground = Brush("#C5B79E"),
                FontSize = 13, Margin = new Thickness(0, 2, 0, 7)
            });
            AddFolderButton("すべて", null, overview.TotalCount);
            AddFolderButton("未分類", -1, overview.UnfiledCount);
            foreach (var folder in overview.Folders) AddFolderButton(folder.Name, folder.Id, folder.Count);

            var createRow = new WrapPanel { Margin = new Thickness(0, 7, 0, 10) };
            var input = new TextBox
            {
                Width = 145, Padding = new Thickness(6, 3, 6, 3), Background = Brush("#302B25"),
                Foreground = Brush("#D8CCB4"), BorderBrush = Brush("#5B5144"),
                ToolTip = "新しいフォルダ名"
            };
            var add = SmallButton("＋", "#D5BE94");
            input.IsEnabled = add.IsEnabled = !_database.IsReadOnly;
            async Task CreateAsync()
            {
                var name = input.Text.Trim();
                if (name.Length == 0) return;
                try
                {
                    add.IsEnabled = false;
                    await RunDatabaseWriteAsync(() => _database.CreateFolder(kind, name));
                    await ReloadAsync();
                    Status($"{name}フォルダを作りました");
                }
                catch (Exception ex) { add.IsEnabled = true; Status("フォルダを作成できませんでした: " + ex.Message); }
            }
            add.Click += async (_, _) => await CreateAsync();
            input.PreviewKeyDown += async (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                await CreateAsync();
            };
            createRow.Children.Add(input);
            createRow.Children.Add(add);
            ContextContent.Children.Add(createRow);

            if (!_database.IsReadOnly && _folderId is > 0)
            {
                var delete = SmallButton("このフォルダを削除", "#9F8B71");
                delete.FontSize = 10;
                delete.ToolTip = $"フォルダだけを削除します。{(bookmark ? "しおり" : "資料")}と断章は残ります";
                delete.Click += async (_, _) =>
                {
                    try
                    {
                        var id = _folderId.Value;
                        await RunDatabaseWriteAsync(() => _database.DeleteFolder(kind, id));
                        _folderId = null;
                        _pageOffset = 0;
                        await ReloadAsync();
                        Status("フォルダだけを削除しました。断章は残っています");
                    }
                    catch (Exception ex) { Status("フォルダを削除できませんでした: " + ex.Message); }
                };
                ContextContent.Children.Add(delete);
            }

            void AddFolderButton(string name, long? id, int count)
            {
                var active = _folderId == id;
                var button = SmallButton($"{(active ? "✦ " : string.Empty)}{name}   {count:N0}", active ? "#FFF1D2" : "#BBAE98");
                button.HorizontalContentAlignment = HorizontalAlignment.Left;
                button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
                button.Background = active ? Brush("#4A3D2D") : Brushes.Transparent;
                button.BorderBrush = active ? Brush("#9D8055") : Brushes.Transparent;
                button.BorderThickness = new Thickness(1);
                button.Click += async (_, _) => await ApplyFolderFilterAsync(id);
                ContextContent.Children.Add(button);
            }
        }
    }

    private void BuildContextFacetActions(IReadOnlyList<FacetCount> facets)
    {
        ContextFacetActions.Children.Clear();
        foreach (var kind in new[] { "tag", "index" })
        {
            if (!_allFacetValuesLoaded.Contains(kind)) continue;
            var heading = kind == "tag" ? "Tag" : "頻出語";
            var visible = _visibleFacetCounts.TryGetValue(kind, out var count) ? count : CollapsedFacetLimit;
            var total = facets.Count(item => item.Kind == kind);
            var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 5) };
            row.Children.Add(new TextBlock
            {
                Text = $"{heading} {Math.Min(visible, total):N0}/{total:N0}　",
                Foreground = Brush("#B8AA94"), FontSize = 10, VerticalAlignment = VerticalAlignment.Center
            });

            var next = SmallButton($"次の{FacetPageSize}件", "#D0B987");
            next.FontSize = 10;
            next.Margin = new Thickness(0, 0, 4, 0);
            next.IsEnabled = visible < total;
            next.Click += async (_, _) =>
            {
                _visibleFacetCounts[kind] = visible + FacetPageSize;
                await ReloadAsync(restoreOffset: ReaderScroll.VerticalOffset);
            };
            row.Children.Add(next);

            var reset = SmallButton("上位10件へ", "#756A58");
            reset.FontSize = 10;
            reset.Click += async (_, _) =>
            {
                _visibleFacetCounts.Remove(kind);
                _allFacetValuesLoaded.Remove(kind);
                await ReloadAsync(restoreOffset: ReaderScroll.VerticalOffset);
            };
            row.Children.Add(reset);
            ContextFacetActions.Children.Add(row);
        }
    }

    private bool FacetPanelOpen(string kind) => kind switch
    {
        "category" => _categoryFacetOpen,
        "tag" => _tagFacetOpen,
        _ => _indexFacetOpen
    };

    private void SetFacetPanelOpen(string kind, bool open)
    {
        switch (kind)
        {
            case "category": _categoryFacetOpen = open; break;
            case "tag": _tagFacetOpen = open; break;
            default: _indexFacetOpen = open; break;
        }
    }

    private async Task ApplySearchAsync()
    {
        var next = SearchBox.Text.Trim();
        if (next == _searchText) return;
        PreserveNormalReadingState();
        RememberCurrent();
        if (string.IsNullOrWhiteSpace(_searchText) && next.Length > 0) _monthBeforeSearch = _currentMonth;
        _searchText = next.Length == 0 ? null : next;
        _currentMonth = _searchText is null && !_bookmarksOnly && _sectionType == FragmentSectionType.Main && _folderId is null
            ? (_monthBeforeSearch ?? _currentMonth ?? await Task.Run(() => _database.GetLatestMonth()))
            : null;
        _facetKind = null;
        _facetValue = null;
        _pageOffset = 0;
        ClearSearchButton.Visibility = _searchText is null ? Visibility.Collapsed : Visibility.Visible;
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        await ReloadAsync();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearSearchButton.Visibility = SearchBox.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e) => SearchBox.Clear();

    private async Task ApplyFacetAsync(string kind, string value)
    {
        if (_rawMode) await CloseRawModeAsync();
        PreserveNormalReadingState();
        RememberCurrent();
        if (_facetKind == kind && _facetValue == value) { _facetKind = null; _facetValue = null; }
        else
        {
            _facetKind = kind;
            _facetValue = value;
            _currentMonth = null;
        }
        _pageOffset = 0;
        await ReloadAsync();
    }

    private async Task ApplyFolderFilterAsync(long? folderId)
    {
        RememberCurrent();
        _folderId = folderId;
        _facetKind = null;
        _facetValue = null;
        _pageOffset = 0;
        await ReloadAsync();
    }

    private async Task ChangeSortAsync(bool newest)
    {
        if (_newestFirst == newest) return;
        RememberCurrent();
        _newestFirst = newest;
        _pageOffset = 0;
        await SaveViewPreferenceAsync(() => _database.SetMeta("sort_order", newest ? "newest" : "oldest"));
        await ReloadAsync();
    }

    private async Task ChangePageSizeAsync(int pageSize)
    {
        if (!AllowedPageSizes.Contains(pageSize) || pageSize == _pageSize) return;
        RememberCurrent();
        var absolute = _pageOffset;
        _pageSize = pageSize;
        _pageOffset = (absolute / _pageSize) * _pageSize;
        await SaveViewPreferenceAsync(() => _database.SetMeta("page_size", _pageSize.ToString(CultureInfo.InvariantCulture)));
        await ReloadAsync();
    }

    private async Task SelectMonthAsync(string month, bool remember)
    {
        if (_rawMode) await CloseRawModeAsync();
        if (remember) RememberCurrent();
        _sectionType = FragmentSectionType.Main;
        SectionButton.Content = "資料";
        _folderId = null;
        _currentMonth = month;
        _monthBeforeSearch = month;
        _searchText = null;
        if (SearchBox.Text.Length > 0) SearchBox.Clear();
        _facetKind = null;
        _facetValue = null;
        _bookmarksOnly = false;
        _pageOffset = 0;
        await ReloadAsync();
        _normalReadingState = CaptureState();
    }

    private async Task JumpToAsync(string fragmentId, bool remember)
    {
        if (remember) RememberCurrent();
        var month = await Task.Run(() => _database.GetFragmentMonth(fragmentId));
        if (month is null) return;
        _sectionType = FragmentSectionType.Main;
        SectionButton.Content = "資料";
        _folderId = null;
        _currentMonth = month;
        _monthBeforeSearch = month;
        _searchText = null;
        _facetKind = null;
        _facetValue = null;
        _bookmarksOnly = false;
        if (SearchBox.Text.Length > 0) SearchBox.Clear();
        var query = CurrentQuery() with { Offset = 0 };
        _pageOffset = await Task.Run(() => (_database.GetFragmentOffset(fragmentId, query) / _pageSize) * _pageSize);
        _currentFragmentId = fragmentId;
        await ReloadAsync(fragmentId);
        _normalReadingState = CaptureState();
        await SaveViewPreferenceAsync(() => _database.SetMeta("last_fragment_id", fragmentId));
    }

    private void RememberCurrent()
    {
        _back.Push(CaptureState());
        _forward.Clear();
        UpdateHistoryButtons();
    }

    private ReaderViewState CaptureState() => new(
        _currentMonth, _searchText, _facetKind, _facetValue, _newestFirst, _bookmarksOnly,
        _pageOffset, _currentFragmentId, ReaderScroll.VerticalOffset, _pageSize, _sectionType, _folderId);

    private async Task RestoreStateAsync(ReaderViewState state, bool restoreExactOffset = false)
    {
        _currentMonth = state.Month;
        _searchText = state.Search;
        _facetKind = state.FacetKind;
        _facetValue = state.FacetValue;
        _newestFirst = state.NewestFirst;
        _bookmarksOnly = state.BookmarksOnly;
        _pageOffset = state.Offset;
        _currentFragmentId = state.FragmentId;
        _pageSize = state.PageSize;
        _sectionType = state.SectionType;
        _folderId = state.FolderId;
        SectionButton.Content = _sectionType == FragmentSectionType.Main ? "資料" : "本編";
        if (SearchBox.Text != (_searchText ?? string.Empty)) SearchBox.Text = _searchText ?? string.Empty;
        await ReloadAsync(_currentFragmentId, restoreExactOffset ? state.ScrollOffset : null);
        if (IsNormalReading()) _normalReadingState = CaptureState();
        UpdateHistoryButtons();
    }

    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_rawMode) { await CloseRawModeAsync(); return; }
        if (_back.Count == 0) return;
        _forward.Push(CaptureState());
        await RestoreStateAsync(_back.Pop());
    }

    private async void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (_rawMode) { await CloseRawModeAsync(); return; }
        if (_forward.Count == 0) return;
        _back.Push(CaptureState());
        await RestoreStateAsync(_forward.Pop());
    }

    private void UpdateHistoryButtons()
    {
        BackButton.IsEnabled = _back.Count > 0;
        ForwardButton.IsEnabled = _forward.Count > 0;
    }

    private async void ToggleNav_Click(object sender, RoutedEventArgs e)
    {
        _navOpen = !_navOpen;
        ApplyPaneVisibility();
        try { await SaveViewPreferenceAsync(() => _database.SetMeta("nav_open", _navOpen ? "1" : "0")); }
        catch (Exception ex) { Status("年月ペインの状態を保存できませんでした: " + ex.Message); }
    }

    private async void ToggleContext_Click(object sender, RoutedEventArgs e)
    {
        _contextOpen = !_contextOpen;
        ApplyPaneVisibility();
        try { await SaveViewPreferenceAsync(() => _database.SetMeta("context_open", _contextOpen ? "1" : "0")); }
        catch (Exception ex) { Status("探索ペインの状態を保存できませんでした: " + ex.Message); }
    }

    private void ApplyPaneVisibility()
    {
        NavPane.Visibility = _navOpen ? Visibility.Visible : Visibility.Collapsed;
        NavColumn.Width = _navOpen ? new GridLength(220) : new GridLength(0);
        ContextPane.Visibility = _contextOpen ? Visibility.Visible : Visibility.Collapsed;
        ContextColumn.Width = _contextOpen ? new GridLength(260) : new GridLength(0);
    }

    private async void ToggleSection_Click(object sender, RoutedEventArgs e)
    {
        if (_rawMode) await CloseRawModeAsync();
        PreserveNormalReadingState();
        RememberCurrent();
        _sectionType = _sectionType == FragmentSectionType.Main ? FragmentSectionType.Reference : FragmentSectionType.Main;
        SectionButton.Content = _sectionType == FragmentSectionType.Main ? "資料" : "本編";
        _currentMonth = _sectionType == FragmentSectionType.Main ? (_monthBeforeSearch ?? await Task.Run(() => _database.GetLatestMonth())) : null;
        _searchText = null;
        _facetKind = null;
        _facetValue = null;
        _bookmarksOnly = false;
        _folderId = null;
        _pageOffset = 0;
        if (SearchBox.Text.Length > 0) SearchBox.Clear();
        await ReloadAsync();
    }

    private void RecordMenu_Click(object sender, RoutedEventArgs e)
    {
        if (RecordButton.ContextMenu is null) return;
        RecordButton.ContextMenu.PlacementTarget = RecordButton;
        RecordButton.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        RecordButton.ContextMenu.IsOpen = true;
    }

    private async void Bookmarks_Click(object sender, RoutedEventArgs e)
    {
        if (_rawMode) await CloseRawModeAsync();
        PreserveNormalReadingState();
        RememberCurrent();
        _sectionType = FragmentSectionType.Main;
        SectionButton.Content = "資料";
        _bookmarksOnly = true;
        _currentMonth = null;
        _searchText = null;
        _facetKind = null;
        _facetValue = null;
        _folderId = null;
        _pageOffset = 0;
        if (SearchBox.Text.Length > 0) SearchBox.Clear();
        await ReloadAsync();
    }

    private async void Home_Click(object sender, RoutedEventArgs e)
    {
        if (_rawMode) await CloseRawModeAsync();
        if (IsNormalReading()) return;
        RememberCurrent();
        var target = _normalReadingState;
        if (target is null)
        {
            var latest = await Task.Run(() => _database.GetLatestMonth());
            target = new ReaderViewState(latest, null, null, null, _newestFirst, false, 0, null, 0, _pageSize, FragmentSectionType.Main, null);
        }
        target = target with
        {
            Search = null, FacetKind = null, FacetValue = null, BookmarksOnly = false,
            SectionType = FragmentSectionType.Main, FolderId = null
        };
        await RestoreStateAsync(target, restoreExactOffset: true);
        Status("通常の読書へ戻りました");
    }

    private bool IsNormalReading() =>
        !_workspaceActive && _sectionType == FragmentSectionType.Main && !_bookmarksOnly
        && string.IsNullOrWhiteSpace(_searchText) && _facetValue is null && _folderId is null;

    private void PreserveNormalReadingState()
    {
        if (!IsNormalReading()) return;
        _normalReadingState = CaptureState();
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_importInProgress) return;
        if (_rawMode) await CloseRawModeAsync();
        var dialog = new OpenFileDialog
        {
            Title = "ChatGPT Exportを読み込む", Filter = "ChatGPT Export (*.zip;*.json)|*.zip;*.json|すべてのファイル (*.*)|*.*",
            Multiselect = true, CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        var files = dialog.FileNames.Where(path =>
            !Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)
            || ChatGptExportImporter.IsConversationJsonName(Path.GetFileName(path))).ToList();
        if (files.Count == 0) { Status("conversations.json または conversations-*.json を選んでください"); return; }
        await RunImportAsync(files);
    }

    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_importInProgress) return;
        if (_rawMode) await CloseRawModeAsync();
        var dialog = new OpenFolderDialog { Title = "ChatGPT Exportのフォルダを選ぶ", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        var files = ChatGptExportImporter.DiscoverConversationFiles(dialog.FolderName);
        if (files.Count == 0) { Status("このフォルダに conversations.json または conversations-*.json がありません"); return; }
        await RunImportAsync(files);
    }

    private async void RetryImport_Click(object sender, RoutedEventArgs e)
    {
        if (_importInProgress || _closingInProgress) return;
        if (_rawMode) await CloseRawModeAsync();
        if (_retryImportPaths.Count == 0 && _workspace is not null)
        {
            SetImportUiBusy(true);
            try
            {
                if (!await FlushPendingEditsAsync()) { Status("未保存の編集を保存できないため、再試行を開始しませんでした"); return; }
                var result = await RunDatabaseWriteAsync(() =>
                {
                    using var lane = _workspace.BeginImport();
                    var retried = _workspace.AfterImport(new ImportResult(0,0,0,0) { ChangedConversationIds = WorkspacePending() });
                    AcknowledgeWorkspaceImport(retried);
                    return retried;
                });
                _workspace.Refresh(); Status(result.Summary);
            }
            catch (Exception ex) { Status("拡張処理を再試行できませんでした: " + ex.Message); }
            finally { await RefreshRetryStateAsync(); SetImportUiBusy(false); }
            return;
        }
        await ShowImportErrorsAsync();
    }

    private async void ImportErrors_Click(object sender, RoutedEventArgs e) => await ShowImportErrorsAsync();

    private async Task ShowImportErrorsAsync()
    {
        if (_importInProgress) return;
        var errors = await Task.Run(() => _database.GetImportErrors());
        if (errors.Count == 0) { Status("取込エラーはありません"); return; }
        var dialog = new ImportErrorsWindow(errors, _database.IsReadOnly) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (dialog.ErrorToDismiss is { } dismissed)
        {
            try
            {
                await RunDatabaseWriteAsync(() => _database.DismissImportError(dismissed));
                await RefreshRetryStateAsync();
                Status($"{Path.GetFileName(dismissed.Path)}の通知を閉じました。失敗履歴は残っています");
            }
            catch (Exception ex) { Status("通知を閉じられませんでした: " + ex.Message); }
        }
        else if (dialog.ErrorToRetry is { } retried)
            await RunImportAsync([retried.Path]);
    }

    private void SetImportUiBusy(bool busy)
    {
        _importInProgress = busy;
        RecordButton.IsEnabled = !busy && !_database.IsReadOnly;
        RetryImportButton.IsEnabled = !busy && !_database.IsReadOnly;
        BackupButton.IsEnabled = !busy && !_database.IsReadOnly;
        if (!busy && _closeAfterImport) { _closeAfterImport = false; Close(); }
    }

    private async Task RunImportAsync(IEnumerable<string> paths)
    {
        if (_importInProgress || _closingInProgress) return;
        var importPaths = paths.ToList();
        if (importPaths.Count == 0) return;
        SetImportUiBusy(true);
        var acceptProgress = true;
        try
        {
            if (!await FlushPendingEditsAsync()) { Status("未保存の編集を保存できないため、取込を開始しませんでした"); return; }
            Status($"0 / {importPaths.Count}  ファイル確認中");
            var progress = new Progress<ImportProgress>(item =>
            {
                if (!acceptProgress) return;
                var stage = item.Stage switch
                {
                    ImportStage.FileCheck => "ファイル確認中",
                    ImportStage.ZipCheck => "ZIP確認中",
                    ImportStage.JsonDetection => "conversations JSON検出中",
                    ImportStage.JsonParsing => "JSON解析中",
                    ImportStage.Database => "DB反映中",
                    ImportStage.Failed => "読み込み失敗",
                    _ => "完了"
                };
                var count = item.Stage == ImportStage.Database && item.TotalConversations > 0
                    ? $"{item.CompletedConversations} / {item.TotalConversations} 対話"
                    : item.Stage == ImportStage.JsonParsing && item.TotalJson > 1
                    ? $"{item.CurrentJson} / {item.TotalJson} JSON"
                    : $"{item.CompletedFiles} / {item.TotalFiles}";
                var name = string.IsNullOrEmpty(item.CurrentPath) ? "" : $"  {Path.GetFileName(item.CurrentPath)}";
                var percent = item.Stage == ImportStage.FileCheck && item.TotalBytes > 0
                    ? $" {100.0 * item.ProcessedBytes / item.TotalBytes:0}%" : "";
                Status($"{count}  {stage}{percent}{name}");
            });
            ImportResult? imported = null;
            WorkspaceImportOutcome? processed = null;
            var completion = await RunDatabaseWriteAsync(() =>
            {
                using var lane = _workspace?.BeginImport();
                imported ??= _importer.Import(importPaths, progress);
                if (_workspace is null)
                {
                    if (_workspaceLoadError is null) return (Import: imported, Summary: "");
                    RememberWorkspaceImport(imported);
                    return (Import: imported, Summary: "設定済みの拡張を読み込めないため処理を保留しました。復旧・再起動後に再試行できます");
                }
                RememberWorkspaceImport(imported);
                try
                {
                    processed ??= _workspace.AfterImport(imported with { ChangedConversationIds = WorkspacePending() });
                    AcknowledgeWorkspaceImport(processed);
                    return (Import: imported, Summary: processed.Summary);
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6) { throw; }
                catch (Exception ex) { return (Import: imported, Summary: "拡張処理は失敗しました（再試行可能）: " + ex.Message); }
            });
            acceptProgress = false;
            var result = completion.Import;
            _workspace?.Refresh();
            _currentMonth = await Task.Run(() => _database.GetLatestMonth());
            _pageOffset = 0;
            await ReloadAsync();
            await RefreshRetryStateAsync();
            var importSummary = result.FilesFailed > 0
                ? result.FilesFailed == 1 && result.Failures[0].Message.Contains("ZIPファイルを読み取れませんでした", StringComparison.Ordinal)
                    ? "ZIPファイルを読み取れませんでした。取込エラーで詳細を確認してください"
                    : $"{result.FilesSeen}件中{result.FilesSucceeded}件を完了。取込エラー {result.FilesFailed}件は内容を確認できます"
                : result.MessagesAdded == 0 ? "すでに取り込み済みです。原文と編集内容はそのままです"
                : $"{result.MessagesAdded:N0}件の発言を保存し、{result.FragmentsAdded:N0}件の断章を編みました";
            Status(importSummary + (completion.Summary.Length == 0 ? "" : " / " + completion.Summary));
        }
        catch (Exception ex) { acceptProgress = false; Status("読み込みできませんでした: " + ex.Message); await RefreshRetryStateAsync(); }
        finally { acceptProgress = false; SetImportUiBusy(false); }
    }

    private async Task RefreshRetryStateAsync()
    {
        _retryImportPaths = await Task.Run(() => _database.GetRetryableImportFiles());
        var pendingWorkspace = await Task.Run(WorkspaceHasPending);
        RetryImportButton.Visibility = _retryImportPaths.Count > 0 || pendingWorkspace ? Visibility.Visible : Visibility.Collapsed;
        RetryImportButton.Content = _retryImportPaths.Count == 0 && _workspace is not null ? $"{_workspace.Title}を再試行" :
            $"取込エラー ({_retryImportPaths.Count})";
    }

    private async Task RunBackgroundMaintenanceAsync()
    {
        try
        {
            var needsMigration = await Task.Run(() => _database.NeedsPhase01DerivedMigration());
            if (needsMigration)
            {
                Status("既存の原文を守りながら分類と検索索引を更新しています…");
                var acceptProgress = true;
                var progress = new Progress<(int Completed, int Total)>(item =>
                {
                    if (acceptProgress && (item.Completed == item.Total || item.Completed % 50 == 0))
                        Status($"分類と検索索引を更新中  {item.Completed:N0} / {item.Total:N0}");
                });
                await RunDatabaseWriteAsync(() => _database.MigratePhase01DerivedData(new HeuristicAnalysisProvider(), progress));
                acceptProgress = false;
                if (!_rawMode && !_workspaceActive) await ReloadAsync(_currentFragmentId);
                Status("既存データの分類と検索索引を安全に更新しました");
            }
            var needsSummaryMigration = await Task.Run(() => _database.NeedsPhase03SummaryMigration());
            if (needsSummaryMigration)
            {
                Status("原文を守りながら、要約を中立的な1文へ整えています…");
                var acceptProgress = true;
                var progress = new Progress<(int Completed, int Total)>(item =>
                {
                    if (acceptProgress && (item.Completed == item.Total || item.Completed % 100 == 0))
                        Status($"要約を整えています  {item.Completed:N0} / {item.Total:N0}");
                });
                await RunDatabaseWriteAsync(() => _database.MigratePhase03Summaries(new HeuristicAnalysisProvider(), progress));
                acceptProgress = false;
                if (!_rawMode && !_workspaceActive) await ReloadAsync(_currentFragmentId, ReaderScroll.VerticalOffset);
                Status("要約と検索索引を安全に更新しました");
            }
            await RunDatabaseWriteAsync(() => _database.AutoBackupIfDue());
        }
        catch (Exception ex) { Status("保全処理を後で再試行します: " + ex.Message); }
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        try { await RunDatabaseWriteAsync(() => _database.CreateBackup()); Status("現在の魔導書を世代バックアップしました"); }
        catch (Exception ex) { Status("バックアップできませんでした: " + ex.Message); }
    }

    private void BeginInlineEdit(StackPanel parent, FrameworkElement label, string originalText, Action<string> updateLabel, string fragmentId, string field, bool multiline)
    {
        if (_importInProgress) { Status("取込中は編集を開始できません"); return; }
        var index = parent.Children.IndexOf(label);
        if (index < 0) return;
        var editor = new TextBox
        {
            Text = originalText, FontSize = label is Control control ? control.FontSize : 14, FontWeight = label is Control styled ? styled.FontWeight : FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap, AcceptsReturn = multiline,
            MinHeight = multiline ? 86 : 0, Margin = label.Margin
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        var saved = false;
        Task<bool>? saveTask = null;
        Func<Task<bool>>? pendingSave = null;
        Task<bool> Save()
        {
            if (saved) return Task.FromResult(true);
            if (saveTask is not null) return saveTask;
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            saveTask = completion.Task;
            _ = SaveCoreAsync(completion);
            return completion.Task;
        }
        async Task SaveCoreAsync(TaskCompletionSource<bool> completion)
        {
            var success = false;
            try
            {
                timer.Stop();
                editor.IsReadOnly = true;
                var value = editor.Text.Trim();
                if (value.Length == 0) value = originalText;
                await RunDatabaseWriteAsync(() => _database.SaveFragmentText(fragmentId, field, value));
                saved = true;
                if (pendingSave is not null) _pendingEdits.Remove(pendingSave);
                updateLabel(value);
                Status("自動保存しました");
                var position = parent.Children.IndexOf(editor);
                if (position >= 0) { parent.Children.RemoveAt(position); parent.Children.Insert(position, label); }
                success = true;
            }
            catch (Exception ex) { Status("保存できませんでした: " + ex.Message); }
            finally { editor.IsReadOnly = false; saveTask = null; completion.TrySetResult(success); }
        }
        pendingSave = Save;
        _pendingEdits.Add(pendingSave);
        timer.Tick += async (_, _) => await Save();
        editor.TextChanged += (_, _) => { timer.Stop(); timer.Start(); };
        editor.LostKeyboardFocus += async (_, _) => await Save();
        editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && saveTask is null)
            {
                saved = true; timer.Stop();
                if (pendingSave is not null) _pendingEdits.Remove(pendingSave);
                var position = parent.Children.IndexOf(editor);
                if (position >= 0) { parent.Children.RemoveAt(position); parent.Children.Insert(position, label); }
            }
            if (!multiline && e.Key == Key.Enter) { e.Handled = true; _ = Save(); }
        };
        parent.Children.RemoveAt(index);
        parent.Children.Insert(index, editor);
        editor.Focus();
        editor.SelectAll();
    }

    private void ReaderScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateScrollIndicator();
        UpdateReaderJumpControls();
        if (_restoring) return;
        if (_rawMode) return;
        _positionTimer.Stop();
        _positionTimer.Start();
        var center = PointFromScreen(new Point(Left + ActualWidth / 2, Top + ActualHeight / 2));
        var hit = InputHitTest(center) as DependencyObject;
        while (hit is not null)
        {
            if (hit is Border border && border.Tag is string id) { _currentFragmentId = id; break; }
            hit = ReaderHitParent(hit);
        }
    }

    internal static DependencyObject? ReaderHitParent(DependencyObject hit) => hit is FrameworkContentElement content
        ? content.Parent : VisualTreeHelper.GetParent(hit);

    private void UpdateReaderJumpControls()
    {
        var available = !_rawMode && !_workspaceActive && ReaderScroll.ScrollableHeight > 1;
        ReaderJumpControls.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        JumpToTopButton.IsEnabled = available && ReaderScroll.VerticalOffset > 1;
        JumpToBottomButton.IsEnabled = available && ReaderScroll.VerticalOffset < ReaderScroll.ScrollableHeight - 1;
    }

    private void JumpToTop_Click(object sender, RoutedEventArgs e) => JumpReaderScroll(toTop: true);
    private void JumpToBottom_Click(object sender, RoutedEventArgs e) => JumpReaderScroll(toTop: false);

    private void JumpReaderScroll(bool toTop)
    {
        if (_rawMode || _workspaceActive || ReaderScroll.ScrollableHeight <= 1) return;
        if (toTop) ReaderScroll.ScrollToTop();
        else ReaderScroll.ScrollToBottom();
    }

    private void UpdateScrollIndicator()
    {
        if (ReaderScroll.ScrollableHeight <= 0 || ScrollIndicatorLayer.ActualHeight <= 0)
        {
            ScrollIndicatorThumb.Height = 0;
            ScrollIndicatorLayer.Opacity = _rawMode ? 0.28 : 0;
            return;
        }
        var layerHeight = ScrollIndicatorLayer.ActualHeight;
        var extent = Math.Max(ReaderScroll.ExtentHeight, 1);
        var minimum = Math.Min(28, layerHeight);
        var thumbHeight = Math.Clamp(layerHeight * ReaderScroll.ViewportHeight / extent, minimum, Math.Min(96, layerHeight));
        ScrollIndicatorThumb.Height = thumbHeight;
        var progress = ReaderScroll.VerticalOffset / ReaderScroll.ScrollableHeight;
        Canvas.SetTop(ScrollIndicatorThumb, Math.Clamp(progress * (layerHeight - thumbHeight), 0, Math.Max(0, layerHeight - thumbHeight)));
        ScrollIndicatorLayer.Opacity = _draggingScrollIndicator ? 0.9 : 0.62;
        _scrollFadeTimer.Stop();
        if (!_draggingScrollIndicator) _scrollFadeTimer.Start();
    }

    private void ScrollIndicator_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_rawMode || ReaderScroll.ScrollableHeight <= 0) return;
        _draggingScrollIndicator = true;
        _scrollFadeTimer.Stop();
        ScrollIndicatorLayer.CaptureMouse();
        ScrollIndicatorLayer.Opacity = 0.9;
        SetReaderScrollFromIndicator(e.GetPosition(ScrollIndicatorLayer).Y);
        e.Handled = true;
    }

    private void ScrollIndicator_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_draggingScrollIndicator) return;
        SetReaderScrollFromIndicator(e.GetPosition(ScrollIndicatorLayer).Y);
        e.Handled = true;
    }

    private void ScrollIndicator_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingScrollIndicator) return;
        SetReaderScrollFromIndicator(e.GetPosition(ScrollIndicatorLayer).Y);
        ScrollIndicatorLayer.ReleaseMouseCapture();
        _draggingScrollIndicator = false;
        ScrollIndicatorLayer.Opacity = 0.62;
        _scrollFadeTimer.Start();
        e.Handled = true;
    }

    private void ScrollIndicator_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_draggingScrollIndicator || !_rawMode) return;
        _scrollFadeTimer.Stop();
        _scrollFadeTimer.Start();
    }

    private void SetReaderScrollFromIndicator(double pointerY)
    {
        var layerHeight = ScrollIndicatorLayer.ActualHeight;
        var thumbHeight = ScrollIndicatorThumb.ActualHeight;
        var travel = layerHeight - thumbHeight;
        if (travel <= 0 || ReaderScroll.ScrollableHeight <= 0) return;
        var top = Math.Clamp(pointerY - thumbHeight / 2, 0, travel);
        ReaderScroll.ScrollToVerticalOffset(top / travel * ReaderScroll.ScrollableHeight);
    }

    private void QueueReadingPositionSave()
    {
        _positionTimer.Stop();
        if (!IsNormalReading()) return;
        _normalReadingState = CaptureState();
        var state = _normalReadingState;
        _ = SaveReadingPositionAsync(state);
    }

    private async Task SaveReadingPositionAsync(ReaderViewState state)
    {
        try
        {
            await SaveViewPreferenceAsync(() => _database.SaveReaderState(state.Month, state.NewestFirst, state.FragmentId, state.ScrollOffset));
        }
        catch (Exception ex)
        {
            Status("読書位置を保存できませんでした: " + ex.Message);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_workspaceActive) return;
        if (_rawMode && (e.Key == Key.Escape || ((Keyboard.Modifiers & ModifierKeys.Alt) != 0 && (e.Key == Key.Left || e.SystemKey == Key.Left))))
        {
            _ = CloseRawModeAsync();
            e.Handled = true;
            return;
        }
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.Left or Key.BrowserBack) { Back_Click(sender, e); e.Handled = true; }
            if (key is Key.Right or Key.BrowserForward) { Forward_Click(sender, e); e.Handled = true; }
            if (key == Key.Home) { Home_Click(sender, e); e.Handled = true; }
        }
        if (!_rawMode && Keyboard.Modifiers == ModifierKeys.None
            && Keyboard.FocusedElement is not TextBoxBase and not PasswordBox
            && ReaderScroll.ScrollableHeight > 1)
        {
            if (e.Key == Key.Home) { JumpReaderScroll(toTop: true); e.Handled = true; return; }
            if (e.Key == Key.End) { JumpReaderScroll(toTop: false); e.Handled = true; return; }
        }
        if ((e.Key == Key.PageDown || e.Key == Key.PageUp)
            && Keyboard.FocusedElement is TextBoxBase
            && (Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) != 0) return;
        if (e.Key == Key.PageDown) ReaderScroll.PageDown();
        if (e.Key == Key.PageUp) ReaderScroll.PageUp();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose || _database.IsReadOnly) return;
        e.Cancel = true;
        if (_importInProgress)
        {
            _closeAfterImport = true;
            Status("取込完了後に閉じます");
            return;
        }
        if (_closingInProgress) return;
        _ = CompleteCloseAsync();
    }

    private async Task CompleteCloseAsync()
    {
        _closingInProgress = true;
        if (Content is UIElement content) content.IsEnabled = false;
        try
        {
            if (!await FlushPendingEditsAsync())
            {
                Status("未保存の編集を保存できないため、終了を取りやめました");
                return;
            }
            var current = _rawMode && _rawReturnState is not null ? _rawReturnState :
                _workspaceActive && _workspaceReaderState is not null ? _workspaceReaderState : CaptureState();
            var state = current.SectionType == FragmentSectionType.Main && !current.BookmarksOnly
                        && string.IsNullOrWhiteSpace(current.Search) && current.FacetValue is null && current.FolderId is null
                ? current
                : _normalReadingState ?? current;
            await RunDatabaseWriteAsync(() => _database.SaveReaderState(state.Month, state.NewestFirst, state.FragmentId, state.ScrollOffset));
            _allowClose = true;
            Close();
        }
        catch (Exception ex)
        {
            _allowClose = false;
            Status("読書位置を保存できないため、終了を取りやめました: " + ex.Message);
        }
        finally
        {
            if (!_allowClose && Content is UIElement activeContent) activeContent.IsEnabled = true;
            _closingInProgress = false;
        }
    }

    private async Task<bool> FlushPendingEditsAsync()
    {
        if (_database.IsReadOnly) return true;
        foreach (var save in _pendingEdits.ToList())
            if (!await save()) return false;
        return true;
    }

    private void Status(string text) => StatusText.Text = text;

    private static string FormatMonth(string? value)
    {
        if (value is null || value.Length < 7) return "すべての記録";
        return $"{value[..4]}年 {int.Parse(value[5..7], CultureInfo.InvariantCulture)}月";
    }

    private static string FacetLabel(string? kind) => kind switch { "category" => "Category", "tag" => "Tag", _ => "頻出語" };

    private string? SelectedFolderName()
    {
        if (_folderId is null) return null;
        if (_folderId == -1) return "未分類";
        var overview = _bookmarksOnly ? _bookmarkFolders : _referenceFolders;
        return overview.Folders.FirstOrDefault(folder => folder.Id == _folderId)?.Name;
    }

    private static Button SmallButton(string text, string color) => new()
    {
        Content = text, Foreground = Brush(color), Background = Brushes.Transparent,
        Padding = new Thickness(7, 4, 7, 4), FontSize = 12
    };

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    private sealed record ReaderViewState(
        string? Month, string? Search, string? FacetKind, string? FacetValue,
        bool NewestFirst, bool BookmarksOnly, int Offset, string? FragmentId,
        double ScrollOffset, int PageSize, FragmentSectionType SectionType, long? FolderId);
}
