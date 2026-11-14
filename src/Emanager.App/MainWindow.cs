using Avalonia;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Avalonia.Controls.ApplicationLifetimes;
using Emanager.App.Infrastructure;
using Emanager.Core.FileOperations;
using Emanager.Core.Metadata;
using Emanager.Core.Storage;

namespace Emanager.App;

public sealed class MainWindow : Window
{
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
    private static bool IsRoot => OperatingSystem.IsLinux() && GetEffectiveUserId() == 0;
    private readonly Preferences prefs = Preferences.Load();
    internal FileUndo UndoHistory { get; } = new();
    private sealed record WindowStateSnapshot(
        List<(string Left, string? Right)> Tabs,
        int ActiveIndex, bool ActiveRight,
        List<(string Path, ClipboardMode Mode)> Clipboard);
    private readonly WindowStateSnapshot? restoreState;
    private static readonly Dictionary<string, string> DarkPalette = new()
    {
        ["canvas"] = "#10151F", ["sidebar"] = "#192232", ["surface"] = "#243247",
        ["stroke"] = "#34465E", ["accent"] = "#83C5FF", ["text"] = "#F5F7FB", ["muted"] = "#AABBD1"
    };
    private static readonly Dictionary<string, string> LightPalette = new()
    {
        ["canvas"] = "#F5F7FC", ["sidebar"] = "#EAF0F8", ["surface"] = "#FFFFFF",
        ["stroke"] = "#D4DFEB", ["accent"] = "#1F6FEB", ["text"] = "#1B2430", ["muted"] = "#5B6879"
    };
    // Shared mutable brushes keep all existing tab/pane visuals in sync on theme changes.
    private static readonly Dictionary<string, SolidColorBrush> ThemeBrushes =
        DarkPalette.ToDictionary(x => x.Key, x => new SolidColorBrush(Color.Parse(x.Value)));
    private static IBrush Brush(string key) => ThemeBrushes[key];
    internal static IBrush CanvasBrush => Brush("canvas");
    internal static IBrush SidebarBrush => Brush("sidebar");
    internal static IBrush SurfaceBrush => Brush("surface");
    internal static IBrush StrokeBrush => Brush("stroke");
    internal static IBrush AccentBrush => Brush("accent");
    internal static IBrush TextPrimary => Brush("text");
    internal static IBrush TextMuted => Brush("muted");

    private readonly StackPanel sidebar = new() { Spacing = 6, Margin = new Thickness(14, 14) };
    private readonly DispatcherTimer deviceTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer rootIdleTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private bool rootIdleClosing;
    private StorageDiscoverySnapshot? storageSnapshot;
    private readonly IBlockDeviceDiscovery storageDiscovery = new LinuxBlockDeviceDiscovery();
    private readonly IDiskMountService diskOperationService;
    private string deviceSnapshot = "";
    private bool deviceScanBusy;
    private readonly TextBox pathBox = new() { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox searchBox = new() { Width = 216 };
    private readonly Border terminalPanel = new() { IsVisible = false, Background = SidebarBrush, Padding = new Thickness(12, 6) };
    private readonly TextBox terminalOutput = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("monospace"), FontSize = 12 };
    private readonly TextBox terminalInput = new();
    private string terminalDirectory = HomePath();
    private bool terminalBusy;
    private readonly TextBlock status = new() { Text = UiText.T("Hazır"), FontSize = 12 };
    private readonly FileOperationManager operationManager = new(
        new SafeFileOperationsAdapter(),
        new OperationJournal(Path.Combine(UserFolders.ConfigDirectory, "operations.jsonl")),
        maxConcurrency: 2);
    private readonly Button themeBox = new();
    private Button rootModeButton = null!;
    private readonly StackPanel tabStripPanel = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly Grid paneArea = new();
    private readonly List<FilePane> panes = [];
    private TrashView? trashView;
    private bool trashVisible;
    private FilePane? active;
    private IDisposable? searchSubscription;
    private List<(string Path, ClipboardMode Mode)> clipboardFiles = [];

    public MainWindow() : this(null) { }

    private MainWindow(WindowStateSnapshot? restore)
    {
        restoreState = restore;
        UiText.Use(prefs.Language);
        diskOperationService = new PolicyEnforcedDiskMountService(
            storageDiscovery,
            new DiskOperationPolicy(new SystemDiskProtectionService()),
            new UDisks2DiskOperationBackend());
        terminalInput.Watermark = UiText.T("Komut yazıp Enter'a basın (bash)");
        ApplyThemeVariant();
        status.Foreground = TextMuted;
        pathBox.Watermark = UiText.T("Konum yazın veya Ctrl+L");
        searchBox.Watermark = UiText.T("⌕  Bu klasörde ara");
        status.Text = UiText.T("Hazır");
        Title = IsRoot ? "Emanager — Linux [ROOT]" : "Emanager — Linux";
        Width = 1320; Height = 820;
        MinWidth = 850; MinHeight = 520;
        Background = CanvasBrush;
        BuildUi();
        operationManager.ProgressChanged += ManagedOperationProgress;
        AddHandler(KeyDownEvent, OnKeyPressed, RoutingStrategies.Tunnel);
        Opened += async (_, _) =>
        {
            var launchArg = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault();
            string? requestedFolder = null;
            if (!string.IsNullOrWhiteSpace(launchArg))
            {
                try
                {
                    var candidate = launchArg.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                        ? new Uri(launchArg).LocalPath : launchArg;
                    if (Directory.Exists(candidate)) requestedFolder = System.IO.Path.GetFullPath(candidate);
                }
                catch (Exception ex) when (ex is UriFormatException or ArgumentException or NotSupportedException) { }
            }
            var start = requestedFolder ?? (Directory.Exists(prefs.LastDirectory) ? prefs.LastDirectory : HomePath());
            if (requestedFolder is not null || restoreState is null)
                await NewTabAsync(start);
            else
                await RestoreTabsAsync(restoreState);
            await RefreshDevicesAsync();
            if (operationManager.Recoverable.Count > 0)
                await OperationRecoveryDialog.ShowAsync(this, operationManager);
            deviceTimer.Start();
        };
        deviceTimer.Tick += (_, _) => _ = RefreshDevicesAsync();
        rootIdleTimer.Tick += async (_, _) =>
        {
            if (!adminMode || rootIdleClosing || !KioAdmin.IsIdleFor(TimeSpan.FromMinutes(5))) return;
            rootIdleClosing = true;
            try { await DisableAdminModeAsync(UiText.T("Root oturumu 5 dakika işlem yapılmadığı için kapatıldı.")); }
            catch (Exception ex) { status.Text = UiText.T("Root oturumu kapatma hatası: ") + ex.Message; }
            finally { rootIdleClosing = false; }
        };
        Closing += async (_, e) =>
        {
            if (safeToClose) return;
            e.Cancel = true;
            if (closingInProgress) return;
            closingInProgress = true;
            deviceTimer.Stop();
            rootIdleTimer.Stop();
            try
            {
                await KioAdmin.DisableAsync();
                prefs.Save();
                safeToClose = true;
                Close();
            }
            catch (Exception ex)
            {
                closingInProgress = false;
                status.Text = UiText.T("Yönetici işlemleri durdurulamadı: ") + ex.Message;
            }
        };
    }

    internal Preferences Prefs => prefs;
    internal IReadOnlyList<(string Path, ClipboardMode Mode)> ClipboardPaths => clipboardFiles;
    internal ClipboardMode ClipboardMode => clipboardFiles.Count > 0 ? clipboardFiles[0].Mode : ClipboardMode.Copy;
    internal bool HasClipboard => clipboardFiles.Count > 0;
    internal FileOperationManager OperationManager => operationManager;
    private SqliteMetadataStore? metadataStore;
    internal SqliteMetadataStore MetadataStore => metadataStore ??= new SqliteMetadataStore(Path.Combine(UserFolders.ConfigDirectory, "metadata.db"));

    private void ManagedOperationProgress(object? sender, (FileOperationJob Job, TransferProgress Progress) value)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var p = value.Progress;
            var percent = p.TotalBytes > 0 ? $"{p.Fraction:P0}" : "…";
            var speed = p.BytesPerSecond > 1024 * 1024 ? $"{p.BytesPerSecond / 1024 / 1024:0.0} MB/s" : $"{p.BytesPerSecond / 1024:0} KB/s";
            status.Text = $"{value.Job.Kind}: {Path.GetFileName(value.Job.Source)} · {percent} · {speed}";
        });
    }

    internal async Task<FileOperationResult> RunManagedOperationAsync(FileOperationKind kind, string source, string destination, ConflictAction? conflict = null, CancellationToken cancellationToken = default)
    {
        var total = File.Exists(source) ? new FileInfo(source).Length : 0;
        var job = new FileOperationJob(Guid.NewGuid(), kind, source, destination, DateTimeOffset.UtcNow, total, conflict);
        var result = await operationManager.EnqueueAsync(job, cancellationToken);
        if (result.State == FileOperationState.Completed) status.Text = UiText.T("İşlem tamamlandı.");
        else if (result.Error is not null) status.Text = result.Error.Message;
        return result;
    }

    public static string HomePath() => UserFolders.Home;

    private void ApplyThemeVariant()
    {
        var colors = prefs.DarkTheme ? DarkPalette : LightPalette;
        foreach (var (key, value) in colors)
            ThemeBrushes[key].Color = Color.Parse(value);
        EmanagerApplication.SetTheme(prefs.DarkTheme);
    }

    private void ToggleTheme()
    {
        prefs.DarkTheme = !prefs.DarkTheme;
        prefs.Save();
        ApplyThemeVariant();
        // Do not tear down attached tabs, panes or controls. All palettes use
        // shared brushes and therefore repaint in place without reparent crashes.
        themeBox.Content = UiText.T(prefs.DarkTheme ? "☀  Açık tema · Ctrl+D" : "☾  Koyu tema · Ctrl+D");
    }

    internal static Button MakeBtn(string label, Action action, string? tip = null)
    {
        var btn = new Button
        {
            Content = UiText.T(label), Padding = new Thickness(13, 9), MinHeight = 38,
            Background = SurfaceBrush, Foreground = TextPrimary,
            BorderBrush = StrokeBrush, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        if (tip != null) ToolTip.SetTip(btn, UiText.T(tip));
        btn.Click += (_, _) => action();
        return btn;
    }

    internal Button MakeButton(string label, Action action, string? tip = null) => MakeBtn(label, action, tip);

    internal TextBlock MakeCaption(string text) => new()
    {
        Text = UiText.T(text), FontSize = 11, FontWeight = FontWeight.SemiBold,
        Foreground = TextMuted, VerticalAlignment = VerticalAlignment.Center
    };

    private void BuildUi()
    {
        var shell = new Grid
        {
            RowDefinitions = new RowDefinitions("86,66,0,*,Auto,42"),
            ColumnDefinitions = new ColumnDefinitions("235,5,*"),
            Background = CanvasBrush
        };
        Content = shell;

        // Brand bar.
        var masthead = new Border
        {
            Background = SidebarBrush, BorderBrush = StrokeBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(28, 0)
        };
        Grid.SetColumnSpan(masthead, 3);
        var mastRow = new Grid { ColumnDefinitions = new ColumnDefinitions("6,*,Auto,Auto") };
        var accentLine = new Border { Background = AccentBrush, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 16, 0, 16) };
        mastRow.Children.Add(accentLine);
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(new Border
        {
            Width = 48, Height = 48, CornerRadius = new CornerRadius(15), Background = AccentBrush,
            Child = new TextBlock
            {
                Text = "E", FontSize = 27, FontWeight = FontWeight.Bold, Foreground = CanvasBrush,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        });
        var brandText = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        brandText.Children.Add(new TextBlock { Text = "Emanager", FontSize = 25, FontWeight = FontWeight.Bold, Foreground = TextPrimary });
        brandText.Children.Add(new TextBlock { Text = UiText.T("LINUX DOSYA YÖNETİCİSİ"), FontSize = 11, LetterSpacing = 1.7, Foreground = AccentBrush });
        brand.Children.Add(brandText);
        Grid.SetColumn(brand, 1);
        brand.Margin = new Thickness(18, 0, 0, 0);
        mastRow.Children.Add(brand);
        var headerTag = new Border
        {
            Background = SurfaceBrush, CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 6), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = UiText.T("●  YEREL DOSYALAR"), FontSize = 10, FontWeight = FontWeight.SemiBold, Foreground = AccentBrush }
        };
        Grid.SetColumn(headerTag, 2);
        mastRow.Children.Add(headerTag);
        var languageSelector = new ComboBox
        {
            ItemsSource = new[] { "Türkçe", "English" },
            SelectedIndex = prefs.Language == "en" ? 1 : 0,
            Width = 126,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            Background = SurfaceBrush, Foreground = TextPrimary
        };
        ToolTip.SetTip(languageSelector, UiText.T("DİL"));
        languageSelector.SelectionChanged += (_, _) =>
            ChangeLanguage(languageSelector.SelectedIndex == 1 ? "en" : "tr");
        Grid.SetColumn(languageSelector, 3);
        mastRow.Children.Add(languageSelector);
        masthead.Child = mastRow;
        shell.Children.Add(masthead);

        // Tab strip.
        var tabRow = new Border { Background = CanvasBrush, BorderBrush = StrokeBrush, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(16, 7) };
        Grid.SetRow(tabRow, 1);
        Grid.SetColumnSpan(tabRow, 3);
        var tabGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("205,*") };
        var tabsArea = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        tabGrid.Children.Add(tabsArea);
        var tabScroll = new ScrollViewer
        {
            Content = tabStripPanel, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        tabsArea.Children.Add(tabScroll);
        var newTab = new Button
        {
            Content = "＋", FontSize = 15, Width = 34, Height = 30, Margin = new Thickness(6, 0, 0, 0),
            Background = Brushes.Transparent, Foreground = AccentBrush, BorderThickness = new Thickness(0)
        };
        ToolTip.SetTip(newTab, UiText.T("Yeni sekme · Ctrl+T"));
        newTab.Click += (_, _) => _ = NewTabAsync(active?.Path ?? HomePath());
        var newTabBox = new Grid();
        Grid.SetColumn(newTabBox, 1);
        newTabBox.Children.Add(newTab);
        tabsArea.Children.Add(newTabBox);
        tabRow.Child = tabGrid;
        shell.Children.Add(tabRow);

        // Toolbar: navigation, address, search.
        var toolbar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(8, 2, 2, 2)
        };
        Grid.SetColumn(toolbar, 1);
        tabGrid.Children.Add(toolbar);
        var nav = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        nav.Children.Add(MakeBtn("←", () => _ = ActivePane()?.TravelAsync(-1), "Geri · Alt+Sol"));
        nav.Children.Add(MakeBtn("→", () => _ = ActivePane()?.TravelAsync(1), "İleri · Alt+Sağ"));
        nav.Children.Add(MakeBtn("↑", () => _ = ActivePane()?.GoParentAsync(), "Üst klasör · Backspace"));
        nav.Children.Add(MakeBtn("⟳", () => _ = ActivePane()?.RefreshAsync(), "Yenile · F5"));
        nav.Children.Add(MakeBtn("▥", ToggleDualPane, "Çift panel · F9"));
        var networkButton = MakeBtn("🌐 Ağ", () => _ = ConnectNetworkAsync(), "SMB veya SFTP paylaşımına bağlan");
        networkButton.Background = AccentBrush;
        networkButton.Foreground = CanvasBrush;
        nav.Children.Add(networkButton);
        nav.Children.Add(MakeBtn("⌨ Terminal", () => OpenTerminalAt(ActivePane()?.Path ?? HomePath()), "Terminali geçerli klasörde aç"));
        nav.Children.Add(MakeBtn(UiText.T("⌕ Gelişmiş ara"), () => _ = ShowAdvancedSearchAsync(), "İndekssiz alt klasör araması"));
        rootModeButton = MakeBtn("🔒 Root kullanıcı",
            () => _ = ToggleRootModeAsync(), "Polkit ile yönetici dosya bağlantısı aç/kapat");
        nav.Children.Add(rootModeButton);
        toolbar.Children.Add(nav);
        // The permanent address field is intentionally removed to keep this
        // toolbar compact. Ctrl+L still opens a location entry dialog.
        searchBox.VerticalAlignment = VerticalAlignment.Center;
        searchBox.CornerRadius = new CornerRadius(11);
        searchBox.MinHeight = 38;
        searchBox.Width = 180;
        searchSubscription?.Dispose();
        searchSubscription = searchBox.GetObservable(TextBox.TextProperty).Subscribe(new TextObserver(OnSearchTextChanged));
        Grid.SetColumn(searchBox, 2);
        toolbar.Children.Add(searchBox);

        // Sidebar rail.
        var left = new Border
        {
            Background = SidebarBrush, BorderBrush = StrokeBrush,
            BorderThickness = new Thickness(0, 0, 1, 0)
        };
        Grid.SetRow(left, 3);
        shell.Children.Add(left);
        var sidebarSplitter = new GridSplitter
        {
            Width = 5, Background = StrokeBrush,
            ResizeDirection = GridResizeDirection.Columns,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            Cursor = new Cursor(StandardCursorType.SizeWestEast)
        };
        Grid.SetRow(sidebarSplitter, 3);
        Grid.SetColumn(sidebarSplitter, 1);
        shell.Children.Add(sidebarSplitter);
        left.Child = new ScrollViewer
        {
            Content = sidebar, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        RenderSidebar();

        // Pane area.
        Grid.SetRow(paneArea, 3);
        Grid.SetColumn(paneArea, 2);
        shell.Children.Add(paneArea);
        BuildTerminalPanel();
        Grid.SetRow(terminalPanel, 4);
        Grid.SetColumnSpan(terminalPanel, 3);
        shell.Children.Add(terminalPanel);

        // Status bar.
        var bottom = new Border
        {
            Background = SidebarBrush,
            Padding = new Thickness(22, 9),
            BorderBrush = StrokeBrush, BorderThickness = new Thickness(0, 1, 0, 0)
        };
        Grid.SetRow(bottom, 5);
        Grid.SetColumnSpan(bottom, 3);
        bottom.Child = status;
        shell.Children.Add(bottom);
    }

    private FilePane? ActivePane() => active;

    private FilePane CreatePane()
    {
        var pane = new FilePane(this);
        pane.PathChanged += Pane_PathChanged;
        pane.StatusText += Pane_StatusText;
        pane.PointerPressed += (_, _) => SetActivePane(pane);
        panes.Add(pane);
        return pane;
    }

    private void Pane_PathChanged(FilePane pane)
    {
        RenderSidebar();
        if (pane == active) pathBox.Text = pane.Path;
    }

    private void Pane_StatusText(FilePane pane, string text)
    {
        if (pane == active) status.Text = text;
    }

    private void RebuildTabs()
    {
        tabStripPanel.Children.Clear();
        for (int i = 0; i < tabs.Count; i++)
        {
            int index = i;
            var tab = tabs[index];
            var chip = new Button
            {
                Content = $"  {TabTitle(tab)}  ",
                Padding = new Thickness(16, 8), MinHeight = 34,
                Background = index == ActiveTabIndex ? SurfaceBrush : Brushes.Transparent,
                Foreground = index == ActiveTabIndex ? AccentBrush : TextPrimary,
                BorderBrush = index == ActiveTabIndex ? StrokeBrush : Brushes.Transparent,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12)
            };
            chip.Click += (_, _) => SwitchTab(index);
            tabStripPanel.Children.Add(chip);
        }
        RebuildPaneArea();
    }

    private async Task RestoreTabsAsync(WindowStateSnapshot state)
    {
        clipboardFiles = state.Clipboard.ToList();
        foreach (var (leftPath, rightPath) in state.Tabs)
        {
            var left = CreatePane();
            FilePane? right = rightPath is null ? null : CreatePane();
            tabs.Add((left, right));
            ActiveTabIndex = tabs.Count - 1;
            active = left;
            await left.NavigateAsync(Directory.Exists(leftPath) ? leftPath : HomePath());
            if (right is not null)
                await right.NavigateAsync(Directory.Exists(rightPath) ? rightPath : HomePath());
        }
        if (tabs.Count == 0) { await NewTabAsync(HomePath()); return; }
        ActiveTabIndex = Math.Clamp(state.ActiveIndex, 0, tabs.Count - 1);
        var selected = tabs[ActiveTabIndex];
        active = state.ActiveRight && selected.Right is not null ? selected.Right : selected.Left;
        RebuildTabs();
        pathBox.Text = active.Path;
    }

    private void ChangeLanguage(string language)
    {
        if (language == prefs.Language || (language != "tr" && language != "en")) return;
        var state = new WindowStateSnapshot(
            tabs.Select(t => (t.Left.Path, t.Right?.Path)).ToList(),
            ActiveTabIndex,
            ActiveTabIndex < tabs.Count && active == tabs[ActiveTabIndex].Right,
            clipboardFiles.ToList());
        prefs.Language = language;
        prefs.Save();
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Show replacement first so shutting the old main window cannot exit the app.
            var replacement = new MainWindow(state)
            {
                Width = Width,
                Height = Height,
                Position = Position,
                WindowState = WindowState
            };
            desktop.MainWindow = replacement;
            replacement.Show();
            Close();
        }
    }

    // Tab model: each entry is (leftPane, rightPaneOrNull).
    private readonly List<(FilePane Left, FilePane? Right)> tabs = [];
    private int ActiveTabIndex;

    private string TabTitle((FilePane Left, FilePane? Right) tab)
    {
        var path = tab.Left.Path.Length > 0 ? tab.Left.Path : HomePath();
        var name = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? "/" : name;
    }

    private async Task NewTabAsync(string path)
    {
        trashVisible = false;
        var left = CreatePane();
        tabs.Add((left, null));
        ActiveTabIndex = tabs.Count - 1;
        active = left;
        RebuildTabs();
        await left.NavigateAsync(path);
        left.FocusList();
    }

    private void SwitchTab(int index)
    {
        if (index < 0 || index >= tabs.Count || (index == ActiveTabIndex && active == tabs[index].Left)) return;
        trashVisible = false;
        ActiveTabIndex = index;
        var (left, right) = tabs[index];
        active = right ?? left;
        RebuildTabs();
        pathBox.Text = active.Path;
        active.FocusList();
    }

    private async Task CloseTabAsync()
    {
        if (tabs.Count == 0) return;
        var (left, right) = tabs[ActiveTabIndex];
        panes.Remove(left);
        if (right is not null) panes.Remove(right);
        tabs.RemoveAt(ActiveTabIndex);
        if (tabs.Count == 0)
        {
            await NewTabAsync(HomePath());
            return;
        }
        ActiveTabIndex = Math.Min(ActiveTabIndex, tabs.Count - 1);
        var next = tabs[ActiveTabIndex];
        active = next.Right ?? next.Left;
        RebuildTabs();
        pathBox.Text = active.Path;
        await Task.CompletedTask;
    }

    private void ToggleDualPane()
    {
        if (ActiveTabIndex >= tabs.Count) return;
        var (left, right) = tabs[ActiveTabIndex];
        if (right is null)
        {
            var second = CreatePane();
            tabs[ActiveTabIndex] = (left, second);
            active = second;
            _ = second.NavigateAsync(HomePath());
        }
        else
        {
            panes.Remove(right);
            tabs[ActiveTabIndex] = (left, null);
                active = left;
        }
        RebuildTabs();
        if (active is not null) pathBox.Text = active.Path;
    }

    internal void SetActivePane(FilePane pane)
    {
        if (!panes.Contains(pane)) return;
        trashVisible = false;
        active = pane;
        pathBox.Text = pane.Path;
    }

    private void FocusOtherPane()
    {
        var (left, right) = ActiveTabIndex < tabs.Count ? tabs[ActiveTabIndex] : (panes.FirstOrDefault()!, null);
        if (left is null) return;
        var target = active == left ? right : left;
        if (target is not null) SetActivePane(target);
        else SetActivePane(left);
        target?.FocusList();
    }

    private void RebuildPaneArea()
    {
        paneArea.Children.Clear();
        paneArea.ColumnDefinitions = new ColumnDefinitions();
        if (trashVisible)
        {
            paneArea.ColumnDefinitions = new ColumnDefinitions("*");
            trashView ??= new TrashView(this);
            Grid.SetColumn(trashView, 0);
            paneArea.Children.Add(trashView);
            return;
        }
        if (ActiveTabIndex >= tabs.Count) return;
        var (left, right) = tabs[ActiveTabIndex];
        if (right is null)
        {
            paneArea.ColumnDefinitions = new ColumnDefinitions("*");
            Grid.SetColumn(left, 0);
            paneArea.Children.Add(left);
        }
        else
        {
            paneArea.ColumnDefinitions = new ColumnDefinitions("*,6,*");
            var splitter = new GridSplitter
            {
                Width = 6, Background = StrokeBrush,
                ResizeDirection = GridResizeDirection.Columns,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext
            };
            Grid.SetColumn(splitter, 1);
            Grid.SetColumn(left, 0);
            Grid.SetColumn(right, 2);
            paneArea.Children.Add(left);
            paneArea.Children.Add(splitter);
            paneArea.Children.Add(right);
        }
        if (active is not null) pathBox.Text = active.Path;
    }

    private void OnSearchTextChanged()
    {
        if (active is not null) active.SearchFilter = searchBox.Text ?? "";
    }

    private void OnPathBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var pane = ActivePane();
            if (pane is not null) _ = pane.NavigateAsync(pathBox.Text ?? "", resolveRelative: true);
            e.Handled = true;
        }
    }

    internal void PinPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (prefs.Pins.Contains(path, StringComparer.Ordinal)) prefs.Pins.Remove(path);
        else prefs.Pins.Add(path);
        prefs.Save(); RenderSidebar();
    }

    internal async Task SetClipboardAsync(IReadOnlyList<string> paths, ClipboardMode mode, FilePane origin)
    {
        clipboardFiles = paths.Select(p => (p, mode)).ToList();
        SetActivePane(origin);
        if (paths.Count > 0)
            status.Text = UiText.F("{0} öğe {1} · Ctrl+V ile yapıştır", paths.Count, UiText.T(mode == ClipboardMode.Copy ? "kopyalandı" : "kesildi"));
    }

    internal void ClearClipboard() => clipboardFiles = [];

    internal async Task SetTextToSystemClipboardAsync(string text)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null) await clipboard.SetTextAsync(text);
        }
        catch (Exception ex) { status.Text = ex.Message; }
    }

    private void BuildTerminalPanel()
    {
        var panel = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Height = 205 };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        header.Children.Add(new TextBlock { Text = UiText.T("⌨ Komut paneli — bash (etkileşimli TTY değil)"), Foreground = TextPrimary });
        var close = new Button { Content = UiText.T("Kapat") };
        close.Click += (_, _) => ToggleTerminalPanel();
        header.Children.Add(close);
        panel.Children.Add(header);
        terminalOutput.Text = UiText.T("Komutları geçerli klasörde çalıştırır. Tam ekran terminal için 'Terminal aç' seçeneğini kullanın.\n");
        Grid.SetRow(terminalOutput, 1);
        panel.Children.Add(terminalOutput);
        Grid.SetRow(terminalInput, 2);
        panel.Children.Add(terminalInput);
        terminalInput.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !terminalBusy)
            { var command = terminalInput.Text ?? ""; terminalInput.Text = ""; _ = RunTerminalCommandAsync(command); e.Handled = true; }
        };
        terminalPanel.Child = panel;
    }

    private void ToggleTerminalPanel()
    {
        terminalPanel.IsVisible = !terminalPanel.IsVisible;
        if (terminalPanel.IsVisible)
        {
            terminalDirectory = ActivePane()?.Path is { Length: > 0 } p ? p : HomePath();
            terminalInput.Focus();
        }
    }

    private async Task RunTerminalCommandAsync(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        if (command == "clear") { terminalOutput.Text = ""; return; }
        if (command == "cd" || command.StartsWith("cd ", StringComparison.Ordinal))
        {
            var dest = command == "cd" ? HomePath() : command[3..].Trim().Trim('"', '\'');
            if (dest == "~") dest = HomePath();
            else if (dest.StartsWith("~/")) dest = System.IO.Path.Combine(HomePath(), dest[2..]);
            var resolved = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(dest) ? dest : System.IO.Path.Combine(terminalDirectory, dest));
            if (Directory.Exists(resolved)) terminalDirectory = resolved;
            else terminalOutput.Text += "Folder not found: " + resolved + "\n";
            return;
        }
        terminalBusy = true;
        terminalOutput.Text += $"{terminalDirectory}$ {command}\n";
        try
        {
            using var proc = new System.Diagnostics.Process();
            proc.StartInfo = new System.Diagnostics.ProcessStartInfo("/usr/bin/bash")
            { WorkingDirectory = terminalDirectory, RedirectStandardOutput = true,
                RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
            TrustedTools.SecurePath(proc.StartInfo);
            proc.StartInfo.ArgumentList.Add("-c"); proc.StartInfo.ArgumentList.Add(command);
            proc.Start();
            proc.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            try { await proc.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { proc.Kill(entireProcessTree: true); terminalOutput.Text += "[45 second timeout]\n"; }
            await Task.WhenAll(stdout, stderr);
            var text = stdout.Result + stderr.Result;
            terminalOutput.Text += text.Length > 20000 ? text[..20000] + "\n[Output truncated]\n" : text + "\n";
            if (terminalOutput.Text?.Length > 60000) terminalOutput.Text = terminalOutput.Text[^40000..];
        }
        catch (Exception ex) { terminalOutput.Text += "Error: " + ex.Message + "\n"; }
        finally { terminalBusy = false; }
    }

    private async Task ShowLocationDialogAsync()
    {
        var pane = ActivePane();
        if (pane is null) return;
        var location = await InputDialog.ShowAsync(this, UiText.T("Konuma git"), pane.Path,
            UiText.T("Klasör yolunu girin:"));
        if (!string.IsNullOrWhiteSpace(location))
            await pane.NavigateAsync(location, resolveRelative: true);
    }

    private Task ShowAdvancedSearchAsync()
    {
        var pane = ActivePane();
        if (pane is null) return Task.CompletedTask;
        return AdvancedSearch.ShowAsync(this, pane.Path, path => pane.NavigateAsync(path));
    }

    private bool adminMode;
    private bool safeToClose;
    private bool closingInProgress;

    private void RefreshAdminIndicators()
    {
        foreach (var pane in panes) pane.RefreshAdminIndicator();
    }

    private async Task DisableAdminModeAsync(string? notice = null)
    {
        rootModeButton.IsEnabled = false;
        rootIdleTimer.Stop();
        status.Text = UiText.T("KIO-Admin işlemleri durduruluyor…");
        try
        {
            await KioAdmin.DisableAsync();
            adminMode = false;
            rootModeButton.Content = UiText.T("🔒 Root kullanıcı");
            rootModeButton.Background = SurfaceBrush;
            rootModeButton.Foreground = TextPrimary;
            Title = "Emanager — Linux";
            RefreshAdminIndicators();
            foreach (var pane in panes)
            {
                if (!Directory.Exists(pane.Path)) await pane.NavigateAsync(HomePath());
                else await pane.RefreshAsync();
            }
            status.Text = notice ?? UiText.T("KIO-Admin kapatıldı.");
        }
        finally { rootModeButton.IsEnabled = true; }
    }

    private async Task ToggleRootModeAsync()
    {
        var pane = ActivePane();
        if (pane is null)
        {
            await MessageDialog.ShowAsync(this, UiText.T("KIO-Admin"), UiText.T("Önce bir dosya paneli açın."));
            return;
        }
        if (adminMode)
        {
            try { await DisableAdminModeAsync(); }
            catch (Exception ex) { status.Text = UiText.T("KIO-Admin kapanış hatası: ") + ex.Message; }
            return;
        }
        if (!File.Exists("/usr/bin/kioclient") || !File.Exists("/usr/libexec/kf6/kio-admin-helper"))
        {
            await MessageDialog.ShowAsync(this, UiText.T("KIO-Admin"), UiText.T("KDE KIO-Admin altyapısı kurulu değil."));
            return;
        }
        if (!await ConfirmDialog.ShowAsync(this, "KDE KIO-Admin",
            UiText.T("KDE Polkit ile yönetici dosya erişimi açılacak. Sistem dosyalarındaki değişiklikler geri alınamayabilir."),
            UiText.T("Yönetici erişimini aç"), dangerous: true)) return;
        rootModeButton.IsEnabled = false;
        try
        {
            status.Text = UiText.T("KDE KIO-Admin yetkilendiriliyor…");
            // The first real panel refresh is the authorization request. A
            // separate admin:///etc probe immediately followed by this refresh
            // caused two KIO worker launches and could produce two Polkit
            // prompts before the KDE auth_admin_keep cache was warm.
            KioAdmin.Enable();
            var refresh = await pane.RefreshAsync();
            if (!refresh.Succeeded)
            {
                await KioAdmin.DisableAsync();
                adminMode = false;
                RefreshAdminIndicators();
                rootIdleTimer.Stop();
                await MessageDialog.ShowAsync(this, UiText.T("KIO-Admin açılamadı"),
                    refresh.ErrorMessage ?? UiText.T("KIO erişimi doğrulanamadı."));
                return;
            }
            if (closingInProgress || safeToClose) return;
            adminMode = true;
            RefreshAdminIndicators();
            rootIdleTimer.Start();
            rootModeButton.Content = UiText.T("🔓 ROOT AKTİF");
            rootModeButton.Background = new SolidColorBrush(Color.Parse("#A52B39"));
            rootModeButton.Foreground = Brushes.White;
            Title = "Emanager — Linux [KIO-ADMIN]";
            status.Text = UiText.T("KDE KIO-Admin dosya erişimi etkin.");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            await KioAdmin.DisableAsync();
            adminMode = false;
            RefreshAdminIndicators();
            rootIdleTimer.Stop();
            await MessageDialog.ShowAsync(this, UiText.T("KIO-Admin açılamadı"), ex.Message);
        }
        finally { rootModeButton.IsEnabled = true; }
    }

    internal void OpenTerminalAt(string path)
    {
        try
        {
            string? term = new[] { "x-terminal-emulator", "konsole", "gnome-terminal", "xfce4-terminal", "xterm" }
                .Select(TrustedTools.Resolve).FirstOrDefault(cmd => cmd is not null);
            if (term is null) { status.Text = UiText.T("Uygun terminal bulunamadı."); return; }
            var info = new System.Diagnostics.ProcessStartInfo(term) { WorkingDirectory = path, UseShellExecute = false };
            TrustedTools.SecurePath(info);
            System.Diagnostics.Process.Start(info);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        { status.Text = UiText.T("Terminal açılamadı: ") + ex.Message; }
    }

    private async Task RefreshDevicesAsync()
    {
        if (deviceScanBusy) return;
        deviceScanBusy = true;
        try
        {
            var snapshot = await storageDiscovery.DiscoverAsync();
            if (!snapshot.IsComplete)
            {
                storageSnapshot = snapshot;
                status.Text = UiText.T("Disk topolojisi güvenli şekilde çözümlenemedi; disk işlemleri kapatıldı.");
                RenderSidebar();
                return;
            }
            if (snapshot.Fingerprint == deviceSnapshot) return;
            deviceSnapshot = snapshot.Fingerprint;
            storageSnapshot = snapshot;
            RenderSidebar();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        {
            status.Text = UiText.T("Sürücü listesi yenilenemedi: ") + ex.Message;
        }
        finally { deviceScanBusy = false; }
    }

    private IReadOnlyList<StorageNode> UserStorageNodes()
    {
        if (storageSnapshot is null || !storageSnapshot.IsComplete) return [];
        var filesystemKinds = new[] { StorageNodeKind.Partition, StorageNodeKind.Filesystem, StorageNodeKind.CryptMapping,
            StorageNodeKind.LvmVolume, StorageNodeKind.RaidArray, StorageNodeKind.DeviceMapper };
        return storageSnapshot.Nodes
            .Where(x => x.Kind == StorageNodeKind.PhysicalDisk || filesystemKinds.Contains(x.Kind))
            .Where(x => x.Kind == StorageNodeKind.PhysicalDisk ||
                (x.FileSystemType is not ("swap" or "crypto_LUKS" or "LVM2_member" or "linux_raid_member" or "squashfs") &&
                 !x.MountPoints.Any(IsBootOrSystemOnlyPartition)))
            .OrderBy(x => x.Kind == StorageNodeKind.PhysicalDisk ? 0 : 1)
            .ThenBy(x => x.DevicePath, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsBootOrSystemOnlyPartition(string mount) =>
        mount is "/boot" or "/boot/efi";

    private StorageNode? StorageOperationTarget(StorageNode node)
    {
        var filesystemKinds = new[] { StorageNodeKind.Partition, StorageNodeKind.Filesystem,
            StorageNodeKind.CryptMapping, StorageNodeKind.LvmVolume, StorageNodeKind.RaidArray,
            StorageNodeKind.DeviceMapper };
        if (filesystemKinds.Contains(node.Kind) && !string.IsNullOrWhiteSpace(node.FileSystemType))
            return node;
        if (!node.IsPhysicalDisk || storageSnapshot is null) return null;

        // A physical disk is only an operation target when it has exactly one
        // safe filesystem descendant. This avoids guessing on multi-partition
        // disks and always sends UDisks2 the partition object that owns the FS.
        var physicalProtection = new SystemDiskProtectionService().Evaluate(storageSnapshot, node.StableId);
        if (physicalProtection.ProtectionClass == DiskProtectionClass.SystemCritical) return null;
        var descendants = new DeviceDependencyResolver().GetDescendants(storageSnapshot, node.StableId);
        var candidates = storageSnapshot.Nodes
            .Where(x => x.StableId != node.StableId && filesystemKinds.Contains(x.Kind))
            .Where(x => !string.IsNullOrWhiteSpace(x.FileSystemType))
            .Where(x => !x.MountPoints.Any(IsBootOrSystemOnlyPartition))
            .Where(x => descendants.Contains(x.StableId))
            .ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private async Task OpenStorageNodeAsync(StorageNode node)
    {
        try
        {
            var path = node.MountPoints.FirstOrDefault();
            if (path is null && storageSnapshot is not null)
            {
                var descendants = new DeviceDependencyResolver().GetDescendants(storageSnapshot, node.StableId);
                path = storageSnapshot.Nodes.Where(x => descendants.Contains(x.StableId))
                    .SelectMany(x => x.MountPoints).FirstOrDefault();
            }
            if (path is not null)
            {
                if (ActivePane() is { } pane) await pane.NavigateAsync(path);
                return;
            }
            var target = StorageOperationTarget(node);
            var protection = storageSnapshot is null || target is null
                ? null
                : new SystemDiskProtectionService().Evaluate(storageSnapshot, target.StableId);
            if (target is not null && protection?.Capabilities.CanMount == true)
            {
                await ExecuteStorageOperationAsync(target, DiskOperationKind.Mount);
                return;
            }
            await ShowStorageInfoAsync(node);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or
            UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            status.Text = UiText.T("Disk işlemi başarısız.");
            await MessageDialog.ShowAsync(this, UiText.T("Sürücü erişimi"), ex.Message);
            await RefreshDevicesAsync();
        }
    }

    private ContextMenu BuildStorageContextMenu(StorageNode node)
    {
        var menu = new ContextMenu();
        var protection = storageSnapshot is null
            ? null
            : new SystemDiskProtectionService().Evaluate(storageSnapshot, node.StableId);
        var operationTarget = StorageOperationTarget(node);
        var operationProtection = storageSnapshot is null || operationTarget is null
            ? null
            : new SystemDiskProtectionService().Evaluate(storageSnapshot, operationTarget.StableId);
        void Add(string text, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = UiText.T(text), IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        var openPath = node.MountPoints.FirstOrDefault();
        if (openPath is null && storageSnapshot is not null)
        {
            var descendants = new DeviceDependencyResolver().GetDescendants(storageSnapshot, node.StableId);
            openPath = storageSnapshot.Nodes.Where(x => descendants.Contains(x.StableId))
                .SelectMany(x => x.MountPoints).FirstOrDefault();
        }
        Add("Aç", () => _ = ActivePane()?.NavigateAsync(openPath!), openPath is not null);
        if (operationTarget?.IsMounted == true && operationProtection?.Capabilities.CanUnmount == true)
            Add("Ayır", () => _ = ExecuteStorageOperationAsync(operationTarget, DiskOperationKind.Unmount));
        else if (operationTarget is not null && !operationTarget.IsMounted && operationProtection?.Capabilities.CanMount == true)
            Add("Bağla", () => _ = ExecuteStorageOperationAsync(operationTarget, DiskOperationKind.Mount));
        if (protection?.Capabilities.CanEject == true)
            Add("Bu diski çıkar", () => _ = ExecuteStorageOperationAsync(node, DiskOperationKind.Eject));
        menu.Items.Add(new Separator());
        Add("Disk bilgileri", () => _ = ShowStorageInfoAsync(node));
        Add("Yenile", () => _ = RefreshDevicesAsync());
        menu.Items.Add(new Separator());
        var protectionText = protection?.ProtectionClass switch
        {
            DiskProtectionClass.SystemCritical => "🔒 Sistem diski — Korumalı",
            DiskProtectionClass.Protected => "🔒 Fiziksel disk korumalı",
            DiskProtectionClass.Removable => "✓ Çıkarılabilir aygıt",
            DiskProtectionClass.DataDisk => "✓ Veri diski",
            _ => "⚠ Bilinmeyen aygıt — işlemler kapalı"
        };
        var state = new MenuItem { Header = UiText.T(protectionText), IsEnabled = false };
        menu.Items.Add(state);
        return menu;
    }

    private async Task ShowStorageInfoAsync(StorageNode node)
    {
        var protection = storageSnapshot is null
            ? null
            : new SystemDiskProtectionService().Evaluate(storageSnapshot, node.StableId);
        var mounts = node.MountPoints.Count == 0 ? UiText.T("Bağlı değil") : string.Join(", ", node.MountPoints);
        var message = UiText.F("Aygıt: {0}\nTür: {1}\nBoyut: {2}\nDosya sistemi: {3}\nBağlama noktası: {4}\nKoruma: {5}\n\n{6}",
            node.DevicePath ?? node.KernelName,
            node.Kind,
            FileSystem.HumanSize(node.SizeBytes),
            node.FileSystemType ?? UiText.T("Bilinmiyor"),
            mounts,
            protection?.ProtectionClass.ToString() ?? UiText.T("Bilinmiyor"),
            protection?.Reason ?? UiText.T("Güvenlik durumu hesaplanamadı."));
        await MessageDialog.ShowAsync(this, UiText.T("Disk bilgileri"), message);
    }

    private async Task ExecuteStorageOperationAsync(StorageNode node, DiskOperationKind operation)
    {
        if (storageSnapshot is null || !storageSnapshot.IsComplete)
        {
            await MessageDialog.ShowAsync(this, UiText.T("Disk işlemi reddedildi"), UiText.T("Disk topolojisi doğrulanamadı. İşlem güvenlik nedeniyle engellendi."));
            return;
        }
        var protection = new SystemDiskProtectionService().Evaluate(storageSnapshot, node.StableId);
        if (protection.ProtectionClass == DiskProtectionClass.SystemCritical ||
            ((operation is DiskOperationKind.Eject or DiskOperationKind.PowerOff) && protection.ProtectionClass == DiskProtectionClass.Protected))
        {
            await MessageDialog.ShowAsync(this, UiText.T("Sistem diski koruması"), UiText.T("Bu aygıt çalışan Linux sistemiyle ilişkili olduğu için bu işlem engellendi."));
            return;
        }
        if (operation is DiskOperationKind.Unmount or DiskOperationKind.Eject or DiskOperationKind.PowerOff)
        {
            var mount = node.MountPoints.FirstOrDefault() ?? UiText.T("bağlama noktası yok");
            var question = UiText.F("{0} aygıtının {1} işlemi uygulansın mı?\n\nAygıt: {2}\nBağlama noktası: {3}\n\nAçık dosyalar varsa işlem başarısız olabilir.",
                node.Label ?? node.DevicePath ?? node.KernelName, OperationLabel(operation), node.DevicePath ?? node.KernelName, mount);
            if (!await ConfirmDialog.ShowAsync(this, UiText.T("Disk işlemini onayla"), question, UiText.T("Uygula"), dangerous: true)) return;
        }
        status.Text = UiText.F("Disk işlemi uygulanıyor: {0}", OperationLabel(operation));
        var request = new DiskOperationRequest(operation, node.StableId, node.DevicePath, storageSnapshot.Fingerprint);
        var result = await diskOperationService.ExecuteAsync(request);
        if (!result.Succeeded)
        {
            await MessageDialog.ShowAsync(this, UiText.T("Disk işlemi başarısız"), DiskFailureMessage(result));
            return;
        }
        await RefreshDevicesAsync();
        if ((operation is DiskOperationKind.Unmount or DiskOperationKind.Eject or DiskOperationKind.PowerOff) &&
            ActivePane() is { } activePane && node.MountPoints.Any(x => FileSystem.IsSubpath(x, activePane.Path)))
            await activePane.NavigateAsync(HomePath());
        status.Text = UiText.T("Disk işlemi tamamlandı.");
    }

    private static string OperationLabel(DiskOperationKind operation) => operation switch
    {
        DiskOperationKind.Mount => UiText.T("Bağla"),
        DiskOperationKind.Unmount => UiText.T("Ayır"),
        DiskOperationKind.Eject => UiText.T("Bu diski çıkar"),
        DiskOperationKind.PowerOff => UiText.T("Gücü kapat"),
        _ => UiText.T("İşlem")
    };

    private static string DiskFailureMessage(DiskOperationResult result) => result.DenyReason switch
    {
        DiskDenyReason.ProtectedMount or DiskDenyReason.ProtectedSwap or DiskDenyReason.ProtectedPhysicalAncestor => UiText.T("Bu aygıt çalışan Linux sistemiyle ilişkili olduğu için işlem engellendi."),
        DiskDenyReason.TopologyChanged or DiskDenyReason.IdentityChanged => UiText.T("Aygıt bilgileri işlem öncesinde değişti; işlem engellendi."),
        DiskDenyReason.DeviceBusy => UiText.T("Bu sürücü şu anda kullanımda olduğu için ayrılamadı. Dosyaları kullanan uygulamaları kapatıp tekrar deneyin."),
        DiskDenyReason.DiscoveryUnavailable or DiskDenyReason.UnknownTopology => UiText.T("Disk topolojisi doğrulanamadı. İşlem güvenlik nedeniyle engellendi."),
        DiskDenyReason.UnsupportedOperation => UiText.T("Bu işlem aygıt tarafından desteklenmiyor."),
        _ => result.Message
    };

    private static string StorageDisplayLabel(StorageNode node)
    {
        var label = string.IsNullOrWhiteSpace(node.Label) ? node.DevicePath ?? node.KernelName : node.Label;
        var size = FileSystem.HumanSize(node.SizeBytes);
        var suffix = node.IsPhysicalDisk
            ? (node.IsRemovable ? UiText.T("Çıkarılabilir sürücü") : UiText.T("İç sürücü"))
            : node.FileSystemType ?? node.Kind.ToString();
        return $"{label} · {size} · {suffix}";
    }

    private Button CreateStorageSidebarButton(StorageNode node, int depth)
    {
        var protection = storageSnapshot is null
            ? null
            : new SystemDiskProtectionService().Evaluate(storageSnapshot, node.StableId);
        var mount = node.MountPoints.FirstOrDefault();
        var icon = node.IsRemovable ? "▣" : node.IsPhysicalDisk ? "▤" : "▥";
        var prefix = depth == 0 ? icon : "└─";
        var display = StorageDisplayLabel(node);
        var hint = mount ?? protection?.Reason ?? node.DevicePath ?? node.KernelName;
        var button = MakeBtn($"{prefix}    {display}",
            () => _ = OpenStorageNodeAsync(node), hint);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        button.Background = Brushes.Transparent;
        button.BorderThickness = new Thickness(0);
        button.Foreground = protection?.ProtectionClass == DiskProtectionClass.SystemCritical
            ? TextMuted : TextPrimary;
        button.Padding = new Thickness(13, 8);
        button.Margin = new Thickness(depth * 18, 0, 0, 0);
        button.Tag = node.StableId;
        button.ContextMenu = BuildStorageContextMenu(node);
        button.ContextRequested += (_, _) =>
        {
            // Rebuild on every native right-click so the action set uses
            // the current snapshot and the exact node under the pointer.
            var stableId = button.Tag as string;
            var current = storageSnapshot?.Nodes.FirstOrDefault(x => x.StableId == stableId);
            button.ContextMenu = current is null ? null : BuildStorageContextMenu(current);
        };
        return button;
    }

    private IEnumerable<StorageNode> VisibleStorageChildren(StorageNode parent,
        IReadOnlyDictionary<string, StorageNode> visible, HashSet<string> visited)
    {
        if (storageSnapshot is null) yield break;
        foreach (var childId in parent.ChildIds)
        {
            if (!visited.Add(childId)) continue;
            if (visible.TryGetValue(childId, out var child))
            {
                yield return child;
                continue;
            }
            var hidden = storageSnapshot.Nodes.FirstOrDefault(x => x.StableId == childId);
            if (hidden is null) continue;
            foreach (var descendant in VisibleStorageChildren(hidden, visible, visited))
                yield return descendant;
        }
    }

    private void AddStorageTree(StackPanel host, StorageNode node, int depth,
        IReadOnlyDictionary<string, StorageNode> visible, HashSet<string> rendered)
    {
        if (!rendered.Add(node.StableId)) return;
        host.Children.Add(CreateStorageSidebarButton(node, depth));
        var visited = new HashSet<string>(StringComparer.Ordinal) { node.StableId };
        foreach (var child in VisibleStorageChildren(node, visible, visited))
            AddStorageTree(host, child, depth + 1, visible, rendered);
    }

    private void RenderSidebar()
    {
        sidebar.Children.Clear();
        SidebarLabel("KÜTÜPHANE");
        var trashButton = MakeBtn("🗑️    Çöp Kutusu", ShowTrash, "Silinen dosyaları görüntüle ve geri yükle");
        trashButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        trashButton.HorizontalContentAlignment = HorizontalAlignment.Left;
        trashButton.Background = trashVisible ? SurfaceBrush : Brushes.Transparent;
        trashButton.BorderBrush = trashVisible ? AccentBrush : StrokeBrush;
        trashButton.BorderThickness = trashVisible ? new Thickness(1) : new Thickness(0);
        trashButton.CornerRadius = new CornerRadius(9);
        trashButton.Foreground = trashVisible ? AccentBrush : TextPrimary;
        trashButton.Padding = new Thickness(13, 9);
        sidebar.Children.Add(trashButton);
        SidebarLabel("HIZLI ERİŞİM");
        foreach (var (label, path) in UserFolders.Standard()) SidebarEntry("⌂", label, path);
        foreach (var pin in prefs.Pins.Distinct(StringComparer.Ordinal).Where(Directory.Exists))
            SidebarEntry("★", System.IO.Path.GetFileName(pin.TrimEnd('/')) is { Length: > 0 } n ? n : pin, pin);
        var pinButton = MakeBtn("＋   Bu klasörü sabitle", () => { var p = ActivePane(); if (p is not null) PinPath(p.Path); });
        pinButton.Background = Brushes.Transparent;
        pinButton.BorderThickness = new Thickness(0);
        pinButton.Foreground = AccentBrush;
        sidebar.Children.Add(pinButton);
        SidebarLabel("BU BİLGİSAYAR");
        var storageNodes = UserStorageNodes();
        var visibleById = storageNodes.ToDictionary(x => x.StableId, StringComparer.Ordinal);
        var rendered = new HashSet<string>(StringComparer.Ordinal);
        var storageGroup = new StackPanel { Spacing = 1 };
        var hasStorageEntry = false;
        foreach (var disk in storageNodes.Where(x => x.IsPhysicalDisk))
        {
            if (hasStorageEntry)
                storageGroup.Children.Add(new Border
                {
                    Height = 1,
                    Background = StrokeBrush,
                    Margin = new Thickness(8, 4)
                });
            AddStorageTree(storageGroup, disk, 0, visibleById, rendered);
            hasStorageEntry = true;
        }
        foreach (var node in storageNodes.Where(x => !rendered.Contains(x.StableId)))
        {
            AddStorageTree(storageGroup, node, 0, visibleById, rendered);
            hasStorageEntry = true;
        }
        if (hasStorageEntry)
            sidebar.Children.Add(new Border
            {
                Background = SurfaceBrush,
                BorderBrush = StrokeBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(4),
                Child = storageGroup
            });
        SidebarLabel("AĞ");
        var connect = MakeBtn("＋   Ağ konumuna bağlan", () => _ = ConnectNetworkAsync(),
            "SMB: smb://sunucu/paylasim · SFTP: sftp://kullanici@sunucu/");
        connect.Background = SurfaceBrush;
        connect.HorizontalAlignment = HorizontalAlignment.Stretch;
        sidebar.Children.Add(connect);
        foreach (var (label, path) in NetworkLocations.Mounted())
        {
            SidebarEntry("▣", label, path, path);
            var disconnect = MakeBtn("    ⏏   " + label + " · " + UiText.T("Bağlantıyı kes"), () => _ = DisconnectNetworkAsync(path));
            disconnect.HorizontalAlignment = HorizontalAlignment.Stretch;
            disconnect.HorizontalContentAlignment = HorizontalAlignment.Left;
            disconnect.Foreground = TextMuted;
            sidebar.Children.Add(disconnect);
        }
        foreach (var address in (prefs.NetworkBookmarks ?? []).Distinct(StringComparer.Ordinal))
        {
            if (!NetworkLocations.TryNormalize(address, out var valid)) continue;
            var uri = new Uri(valid);
            var label = (uri.Scheme == "smb" ? "SMB · " : "SFTP · ") + uri.Host;
            var button = MakeBtn("☆   " + label, () => _ = OpenNetworkAsync(valid), valid);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.Background = Brushes.Transparent;
            button.BorderThickness = new Thickness(0);
            button.Foreground = TextPrimary;
            sidebar.Children.Add(button);
        }
        SidebarLabel("GÖRÜNÜM");
        themeBox.Content = UiText.T(prefs.DarkTheme ? "☀  Açık tema · Ctrl+D" : "☾  Koyu tema · Ctrl+D");
        themeBox.HorizontalAlignment = HorizontalAlignment.Left;
        themeBox.Click -= OnThemeBoxClick;
        themeBox.Click += OnThemeBoxClick;
        sidebar.Children.Add(themeBox);
    }

    private void ShowTrash()
    {
        trashVisible = true;
        trashView ??= new TrashView(this);
        trashView.Reload();
        RebuildPaneArea();
        RenderSidebar();
    }

    private void ShowPaneArea()
    {
        if (!trashVisible) return;
        trashVisible = false;
        RebuildPaneArea();
        RenderSidebar();
    }

    internal void RefreshTrashView() => trashView?.Reload();

    internal void ShowFileBrowser()
    {
        if (!trashVisible) return;
        trashVisible = false;
        RebuildPaneArea();
        RenderSidebar();
    }

    private async Task DisconnectNetworkAsync(string path)
    {
        try
        {
            await NetworkLocations.UnmountAsync(path);
            if (ActivePane() is { } pane && FileSystem.IsSubpath(path, pane.Path))
                await pane.NavigateAsync(HomePath());
            RenderSidebar();
            status.Text = UiText.T("Ağ bağlantısı kesildi.");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or ArgumentException
            or System.ComponentModel.Win32Exception)
        {
            await MessageDialog.ShowAsync(this, UiText.T("Ağ bağlantısı kesilemedi"), ex.Message);
        }
    }

    private async Task ConnectNetworkAsync()
    {
        var address = await InputDialog.ShowAsync(this, UiText.T("Ağ sunucusuna bağlan"), "smb://",
            UiText.T("SMB, SFTP, FTP/FTPS veya WebDAV adresi girin. Örnek: smb://sunucu/paylasim · sftp://kullanici@sunucu/"));
        if (address is null) return;
        await OpenNetworkAsync(address);
    }

    private async Task OpenNetworkAsync(string address)
    {
        if (!NetworkLocations.TryNormalize(address, out var normalized))
        {
            await MessageDialog.ShowAsync(this, UiText.T("Geçersiz ağ adresi"),
                UiText.T("smb://sunucu/paylasim, sftp://kullanici@sunucu/, ftps://sunucu/ veya davs://sunucu/ kullanın. Parola adres içine yazılmamalıdır."));
            return;
        }
        try
        {
            status.Text = UiText.T("Ağ konumuna bağlanılıyor…");
            var mountedPath = await NetworkLocations.MountAsync(normalized);
            if (prefs.NetworkBookmarks is null) prefs.NetworkBookmarks = [];
            if (!prefs.NetworkBookmarks.Contains(normalized, StringComparer.Ordinal))
            {
                prefs.NetworkBookmarks.Add(normalized);
                prefs.Save();
            }
            RenderSidebar();
            if (ActivePane() is { } pane) await pane.NavigateAsync(mountedPath);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or ArgumentException or PlatformNotSupportedException)
        {
            status.Text = UiText.T("Ağ bağlantısı kurulamadı.");
            await MessageDialog.ShowAsync(this, UiText.T("Ağ bağlantısı"), ex.Message);
        }
    }

    private void OnThemeBoxClick(object? sender, RoutedEventArgs e) => ToggleTheme();

    private void SidebarLabel(string text) => sidebar.Children.Add(new TextBlock
    {
        Text = UiText.T(text), FontSize = 10, FontWeight = FontWeight.Bold,
        Foreground = TextMuted, LetterSpacing = 1.2,
        Margin = new Thickness(13, 17, 0, 6)
    });

    private void SidebarEntry(string symbol, string label, string path, string? tip = null)
    {
        var current = ActivePane()?.Path;
        var btn = MakeBtn($"{symbol}    {label}", () =>
        {
            ShowPaneArea();
            _ = ActivePane()?.NavigateAsync(path);
        }, tip ?? path);
        btn.HorizontalAlignment = HorizontalAlignment.Stretch;
        btn.HorizontalContentAlignment = HorizontalAlignment.Left;
        bool selected = string.Equals(path, current, StringComparison.Ordinal);
        btn.Background = selected ? SurfaceBrush : Brushes.Transparent;
        btn.BorderThickness = selected ? new Thickness(1) : new Thickness(0);
        btn.BorderBrush = selected ? AccentBrush : StrokeBrush;
        btn.CornerRadius = new CornerRadius(9);
        btn.Foreground = selected ? AccentBrush : TextPrimary;
        btn.Padding = new Thickness(13, 9);
        sidebar.Children.Add(btn);
    }

    private void OnKeyPressed(object? sender, KeyEventArgs e)
    {
        // History travel works from anywhere, including while the address or
        // search box has focus: Alt+Left/Right never insert text in these fields.
        if (e.Key == Key.Left && e.KeyModifiers.HasFlag(KeyModifiers.Alt)) { _ = ActivePane()?.TravelAsync(-1); e.Handled = true; return; }
        if (e.Key == Key.Right && e.KeyModifiers.HasFlag(KeyModifiers.Alt)) { _ = ActivePane()?.TravelAsync(1); e.Handled = true; return; }
        // Tab management and global chords also work while text boxes have focus.
        if (e.Key == Key.T && e.KeyModifiers.HasFlag(KeyModifiers.Alt)) { ToggleTerminalPanel(); e.Handled = true; return; }
        if (e.Key == Key.T && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { _ = NewTabAsync(active?.Path ?? HomePath()); e.Handled = true; return; }
        if (e.Key == Key.W && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { _ = CloseTabAsync(); e.Handled = true; return; }
        if (e.Key == Key.Tab && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { CycleTab(); e.Handled = true; return; }
        if (e.Key == Key.L && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { _ = ShowLocationDialogAsync(); e.Handled = true; return; }
        // Paste, trash, rename and the file clipboard act on the active pane even
        // when the address bar still holds focus after navigation; they are not
        // text-editing chords, except copy/cut which stay list-only on purpose.
        if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Source is not TextBox)
        { _ = ActivePane()?.UndoLastAsync(); e.Handled = true; return; }
        if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { _ = ActivePane()?.PasteAsync(); e.Handled = true; return; }
        if (e.Key == Key.Delete && e.Source is not TextBox) { _ = ActivePane()?.TrashSelectedAsync(); e.Handled = true; return; }
        if (e.Key == Key.O && e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
            e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Source is not TextBox)
        { _ = ActivePane()?.OpenWithSelectedAsync(); e.Handled = true; return; }
        if (e.Key == Key.F2 && e.Source is not TextBox) { _ = ActivePane()?.RenameSelectedAsync(); e.Handled = true; return; }
        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Source is not TextBox)
        { var p = ActivePane(); if (p is not null) _ = SetClipboardAsync(p.SelectedPaths(), ClipboardMode.Copy, p); e.Handled = true; return; }
        if (e.Key == Key.X && e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Source is not TextBox)
        { var p = ActivePane(); if (p is not null) _ = SetClipboardAsync(p.SelectedPaths(), ClipboardMode.Cut, p); e.Handled = true; return; }
        if (e.Source is TextBox) return;
        if (e.Key == Key.F5) { _ = ActivePane()?.RefreshAsync(); e.Handled = true; }
        else if (e.Key == Key.Back) { _ = ActivePane()?.GoParentAsync(); e.Handled = true; }
        else if (e.Key == Key.F9) { ToggleDualPane(); e.Handled = true; }
        else if (e.Key == Key.D && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { ToggleTheme(); e.Handled = true; }
        else if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { searchBox.Focus(); searchBox.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.Escape && !string.IsNullOrEmpty(searchBox.Text)) { searchBox.Text = string.Empty; e.Handled = true; }
        else if (e.Key == Key.H && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            prefs.ShowHidden = !prefs.ShowHidden;
            foreach (var pane in panes) pane.ApplySortMode();
            prefs.Save(); e.Handled = true;
        }
    }

    internal void ApplyViewPrefsToAllPanes()
    {
        foreach (var pane in panes)
        {
            pane.ApplyViewMode(prefs.ViewMode);
            pane.ApplySortMode();
        }
    }

    private void CycleTab()
    {
        if (tabs.Count < 2) return;
        SwitchTab((ActiveTabIndex + 1) % tabs.Count);
    }
}

internal sealed class TextObserver(Action action) : IObserver<string?>
{
    public void OnCompleted() { }
    public void OnError(Exception error) { }
    public void OnNext(string? value) => action();
}

internal static class Paths
{
    public static bool ExecutableExists(string command) => TrustedTools.Resolve(command) is not null;
}
