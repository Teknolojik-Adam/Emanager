using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Emanager.Core.FileOperations;
using Emanager.Core.Watchers;
using Emanager.Core.Hashing;
using Emanager.Core.Duplicates;
using Emanager.Core.Synchronization;
using Emanager.Core.Preview;
using Emanager.Core.Metadata;

namespace Emanager.App;

public enum ClipboardMode { Copy, Cut }

public sealed record NavigationResult(bool Succeeded, string? ErrorMessage = null)
{
    public static NavigationResult Success() => new(true);
    public static NavigationResult Failure(string message) => new(false, message);
}

/// <summary>
/// One browsable location view. Each tab owns a left pane and, in dual-pane
/// mode, a right pane. Navigation state (history, entries, filter) is per pane;
/// sort/view preferences are shared through the owning window.
/// </summary>
public sealed class FilePane : UserControl
{
    private readonly MainWindow win;
    private readonly ListBox list = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0), SelectionMode = SelectionMode.Multiple };
    private readonly NavigationHistory history = new();
    private readonly TextBlock title = new() { FontSize = 30, FontWeight = FontWeight.Bold };
    private readonly TextBlock pathHint = new() { FontSize = 11, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock countLabel = new() { FontSize = 12 };
    private readonly StackPanel crumbs = new() { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox viewBox = new() { Width = 151 };
    private readonly ComboBox sortBox = new() { Width = 143 };
    private readonly CheckBox descendingBox = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox hiddenBox = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid columns = new();
    private readonly Grid contentArea = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
    private readonly Border previewPanel = new() { Width = 260, Padding = new Thickness(14), Margin = new Thickness(8, 0, 0, 0), CornerRadius = new CornerRadius(12) };
    private readonly StackPanel previewContents = new() { Spacing = 12 };
    private readonly PreviewRegistry previewRegistry = new();
    private PreviewCoordinator previewCoordinator = null!;
    private CancellationTokenSource? previewCts;
    private string? previewedPath;
    private readonly Dictionary<string, Bitmap> thumbnails = new(StringComparer.Ordinal);
    private readonly HashSet<string> pendingThumbs = new(StringComparer.Ordinal);
    private const int MaxThumbnailCache = 160;
    private Point? dragStart;
    private bool dragInProgress;
    private const string InternalPathsFormat = "application/x-emanager-internal-ticket";
    private readonly TextBlock rootWarning = new()
    {
        Text = UiText.T("⚠ KIO-ADMIN — TÜM PANELLERDE YÖNETİCİ YETKİSİ AKTİF"),
        Foreground = Brushes.OrangeRed, FontSize = 12, FontWeight = FontWeight.Bold,
        IsVisible = false
    };

    public void RefreshAdminIndicator() => rootWarning.IsVisible = KioAdmin.Enabled;


    private ControlTheme? wrapGridTheme;
    private IReadOnlyList<FileEntry> entries = [];
    private string searchFilter = "";
    private CancellationTokenSource? scanCts;
    private FileSystemWatchService? folderWatcher;
    private int generation;
    private readonly Dictionary<string, long?> folderSizes = new(StringComparer.Ordinal);

    public string Path { get; private set; } = "";
    public event Action<FilePane>? PathChanged;
    public event Action<FilePane, string>? StatusText;

    public FilePane(MainWindow owner)
    {
        win = owner;
        previewRegistry.Register(new TextPreviewProvider());
        previewRegistry.Register(new ArchivePreviewProvider());
        previewRegistry.Register(new ImagePreviewProvider());
        previewRegistry.Register(new ExternalPreviewProvider());
        previewCoordinator = new PreviewCoordinator(previewRegistry);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        Build();
        DetachedFromVisualTree += (_, _) => folderWatcher?.Dispose();
        AddHandler(KeyDownEvent, OnPaneKeyDown, RoutingStrategies.Tunnel);
        // Clicking inside a pane must give it keyboard focus even when the click
        // lands on empty area, so file chords act on the intended pane.
        AddHandler(PointerPressedEvent, (_, _) =>
        {
            win.SetActivePane(this);
            list.Focus();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    public NavigationHistory History => history;
    public string SearchFilter { get => searchFilter; set { searchFilter = value; RebuildList(); } }

    private void Build()
    {
        var body = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"),
            Margin = new Thickness(24, 17, 24, 20)
        };
        Content = body;

        var heading = new StackPanel { Spacing = 10 };
        pathHint.Foreground = MainWindow.TextMuted;
        title.Foreground = MainWindow.TextPrimary;
        countLabel.Foreground = MainWindow.TextMuted;
        heading.Children.Add(new Border
        {
            Background = MainWindow.SurfaceBrush,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 5),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = pathHint
        });
        heading.Children.Add(title);
        heading.Children.Add(rootWarning);
        RefreshAdminIndicator();
        heading.Children.Add(new ScrollViewer
        {
            Content = crumbs,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 40
        });
        heading.Children.Add(new Border
        {
            Background = MainWindow.SurfaceBrush,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 5),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = countLabel
        });
        heading.Margin = new Thickness(0, 0, 0, 20);
        body.Children.Add(heading);

        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        actions.Margin = new Thickness(0, 0, 0, 14);
        Grid.SetRow(actions, 1);
        body.Children.Add(actions);
        var primary = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        primary.Children.Add(win.MakeButton("⌂  Ana klasör", () => _ = NavigateAsync(MainWindow.HomePath())));
        primary.Children.Add(win.MakeButton("☆  Sabitle", PinCurrent, "Konumu Hızlı Erişim'e sabitle"));
        actions.Children.Add(primary);
        var options = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(options, 1);
        actions.Children.Add(options);
        var previewToggle = win.MakeButton("◧  Önizleme", () =>
        {
            win.Prefs.ShowPreview = !win.Prefs.ShowPreview;
            win.Prefs.Save();
            previewPanel.IsVisible = win.Prefs.ShowPreview;
            UpdatePreview();
        }, "Toggle Windows-style file preview panel");
        options.Children.Add(previewToggle);
        options.Children.Add(win.MakeCaption("GÖRÜNÜM"));
        viewBox.ItemsSource = UiText.ViewModes.Select(UiText.T).ToArray();
        viewBox.SelectedItem = UiText.T(win.Prefs.ViewMode);
        viewBox.SelectionChanged += (_, _) =>
        {
            win.Prefs.ViewMode = UiText.Canonical(viewBox.SelectedItem as string ?? "Detaylar");
            win.ApplyViewPrefsToAllPanes(); RebuildList(); win.Prefs.Save();
        };
        options.Children.Add(viewBox);
        options.Children.Add(win.MakeCaption("SIRALA"));
        sortBox.ItemsSource = UiText.SortModes.Select(UiText.T).ToArray();
        sortBox.SelectedItem = UiText.T(win.Prefs.SortBy);
        sortBox.SelectionChanged += (_, _) =>
        {
            win.Prefs.SortBy = UiText.Canonical(sortBox.SelectedItem as string ?? "Ad");
            win.ApplyViewPrefsToAllPanes(); RebuildList(); win.Prefs.Save();
        };
        options.Children.Add(sortBox);
        descendingBox.Content = "↓";
        descendingBox.IsChecked = win.Prefs.Descending;
        descendingBox.Click += (_, _) =>
        {
            win.Prefs.Descending = descendingBox.IsChecked == true;
            win.ApplyViewPrefsToAllPanes(); RebuildList(); win.Prefs.Save();
        };
        ToolTip.SetTip(descendingBox, UiText.T("Azalan sıra"));
        options.Children.Add(descendingBox);
        hiddenBox.Content = UiText.T("Gizli");
        hiddenBox.IsChecked = win.Prefs.ShowHidden;
        hiddenBox.Click += (_, _) =>
        {
            win.Prefs.ShowHidden = hiddenBox.IsChecked == true;
            win.ApplyViewPrefsToAllPanes(); RebuildList(); win.Prefs.Save();
        };
        options.Children.Add(hiddenBox);

        columns.ColumnDefinitions = new ColumnDefinitions("44,*,110,155,120");
        columns.Margin = new Thickness(16, 7);
        columns.MinHeight = 36;
        void AddColumn(string name, int col)
        {
            var text = win.MakeCaption(name);
            Grid.SetColumn(text, col);
            columns.Children.Add(text);
        }
        AddColumn("AD", 1);
        AddColumn("BOYUT", 2);
        AddColumn("DEĞİŞTİRİLME", 3);
        AddColumn("TÜR", 4);
        Grid.SetRow(columns, 2);
        body.Children.Add(columns);

        var surface = new Border
        {
            Background = MainWindow.SurfaceBrush,
            BorderBrush = MainWindow.StrokeBrush, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(20), Padding = new Thickness(12)
        };
        Grid.SetRow(contentArea, 3);
        body.Children.Add(contentArea);
        contentArea.Children.Add(surface);
        surface.Child = list;
        previewPanel.Background = MainWindow.SidebarBrush;
        previewPanel.BorderBrush = MainWindow.StrokeBrush;
        previewPanel.BorderThickness = new Thickness(1);
        previewPanel.IsVisible = win.Prefs.ShowPreview;
        previewPanel.Child = new ScrollViewer { Content = previewContents, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(previewPanel, 1);
        contentArea.Children.Add(previewPanel);
        list.DoubleTapped += (_, _) => OpenSelection();
        list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { OpenSelection(); e.Handled = true; }
        };
        list.SelectionChanged += (_, _) => { RaiseStatus(); UpdatePreview(); };
        // Right-click the item under the pointer, not a previously selected file.
        // Preserve multi-selection when clicking an already selected item.
        list.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(list).Properties.IsRightButtonPressed) return;
            var row = (e.Source as Control)?.GetSelfAndVisualAncestors()
                .OfType<ListBoxItem>().FirstOrDefault();
            if (row?.DataContext is FileEntry entry &&
                !(list.SelectedItems?.Contains(entry) ?? false))
                list.SelectedItem = entry;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        list.ContextMenu = MakeContextMenu();
        DragDrop.SetAllowDrop(list, true);
        list.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        list.AddHandler(DragDrop.DropEvent, OnDrop);
        list.AddHandler(PointerPressedEvent, OnDragPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        list.AddHandler(PointerMovedEvent, OnDragPointerMoved, RoutingStrategies.Bubble);
        list.AddHandler(PointerReleasedEvent, (_, _) => dragStart = null, RoutingStrategies.Bubble);
        ApplyViewMode(win.Prefs.ViewMode);
    }

    public void ApplyViewMode(string mode)
    {
        columns.IsVisible = mode == "Detaylar";
        if (mode == "Büyük simge")
        {
            wrapGridTheme ??= BuildWrapGridTheme();
            list.Theme = wrapGridTheme;
        }
        else list.Theme = null;
        viewBox.SelectedItem = UiText.T(mode);
        RebuildList();
    }

    public void ApplySortMode()
    {
        sortBox.SelectedItem = UiText.T(win.Prefs.SortBy);
        descendingBox.IsChecked = win.Prefs.Descending;
        hiddenBox.IsChecked = win.Prefs.ShowHidden;
        RebuildList();
    }

    private ControlTheme BuildWrapGridTheme()
    {
        var panelTemplate = new FuncTemplate<Panel>(() => new WrapPanel { ItemWidth = 148, ItemHeight = 124 });
        var scrollTemplate = new FuncControlTemplate<ListBox>((host, ns) =>
        {
            var presenter = new ItemsPresenter { Name = "PART_ItemsPresenter", Margin = new Thickness(6) };
            presenter.Bind(ItemsPresenter.ItemsPanelProperty, new Binding(nameof(ItemsControl.ItemsPanel))
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
            });
            return new ScrollViewer
            {
                Name = "PART_ScrollViewer",
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = presenter
            };
        });
        return new ControlTheme(typeof(ListBox))
        {
            Setters =
            {
                new Setter(ItemsControl.TemplateProperty, scrollTemplate),
                new Setter(ItemsControl.ItemsPanelProperty, panelTemplate)
            }
        };
    }

    private void OnDragPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        dragStart = e.GetCurrentPoint(list).Properties.IsLeftButtonPressed ? e.GetPosition(list) : null;
    }

    private async void OnDragPointerMoved(object? sender, PointerEventArgs e)
    {
        if (dragStart is not Point start || dragInProgress || !e.GetCurrentPoint(list).Properties.IsLeftButtonPressed) return;
        var current = e.GetPosition(list);
        if (Math.Abs(current.X - start.X) < 8 && Math.Abs(current.Y - start.Y) < 8) return;
        dragStart = null;
        var paths = SelectedPaths();
        if (paths.Count == 0) return;
        dragInProgress = true;
        string ticket = InternalDragRegistry.Begin(paths);
        try
        {
            var data = new DataObject();
            data.Set(InternalPathsFormat, ticket);
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy | DragDropEffects.Move);
        }
        finally { InternalDragRegistry.Revoke(ticket); dragInProgress = false; }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (!InternalDragRegistry.IsPending(e.Data.Get(InternalPathsFormat)) || string.IsNullOrEmpty(Path))
        { e.DragEffects = DragDropEffects.None; return; }
        e.DragEffects = e.KeyModifiers.HasFlag(KeyModifiers.Control) ? DragDropEffects.Copy : DragDropEffects.Move;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var paths = InternalDragRegistry.Consume(e.Data.Get(InternalPathsFormat));
        if (paths is null || paths.Count == 0) { e.DragEffects = DragDropEffects.None; return; }
        e.Handled = true;
        var copy = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var destination = Path;
        var target = (e.Source as Control)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext as FileEntry;
        if (target is { IsDirectory: true, IsLink: false }) destination = target.FullPath;
        var admin = KioAdmin.Enabled;
        if (!admin && !Directory.Exists(destination)) return;
        if (admin)
        {
            if (!await ConfirmRootMutationAsync(copy ? "Sürükleyerek kopyala" : "Sürükleyerek taşı", paths, destination)) return;
            if (!KioAdmin.Enabled) return;
        }
        var undo = new List<FileUndo.Step>();
        var errors = new List<string>();
        ConflictAction? applyToAll = null;
        foreach (var source in paths)
        {
            try
            {
                if (string.Equals(System.IO.Path.GetDirectoryName(source), destination, StringComparison.Ordinal)) continue;
                if (admin)
                {
                    if (copy) await KioAdmin.CopyAsync(source, destination);
                    else await KioAdmin.MoveAsync(source, destination);
                }
                else
                {
                    var targetPath = System.IO.Path.Combine(destination, System.IO.Path.GetFileName(source));
                    ConflictAction? conflict = applyToAll;
                    if (conflict is null && (File.Exists(targetPath) || Directory.Exists(targetPath)))
                    {
                        var sourceInfo = new FileInfo(source); var targetInfo = new FileInfo(targetPath);
                        var choice = await ConflictDialog.ShowAsync(this, new FileConflict(source, targetPath, sourceInfo.Exists ? sourceInfo.Length : 0, targetInfo.Exists ? targetInfo.Length : 0, sourceInfo.Exists ? new DateTimeOffset(sourceInfo.LastWriteTimeUtc) : DateTimeOffset.MinValue, targetInfo.Exists ? new DateTimeOffset(targetInfo.LastWriteTimeUtc) : DateTimeOffset.MinValue));
                        if (choice.Action is null) continue;
                        conflict = choice.Action; if (choice.ApplyToAll) applyToAll = conflict;
                    }
                    if (conflict == ConflictAction.Skip) continue;
                    var result = await win.RunManagedOperationAsync(copy ? FileOperationKind.Copy : FileOperationKind.Move, source, destination, conflict);
                    if (result.State != FileOperationState.Completed)
                        throw result.Error ?? new IOException("Dosya işlemi tamamlanamadı.");
                }
                undo.Add(new FileUndo.Step(copy ? FileUndo.Kind.Copy : FileUndo.Kind.Move,
                    source, System.IO.Path.Combine(destination, System.IO.Path.GetFileName(source))));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or TimeoutException or OperationCanceledException)
            { errors.Add(System.IO.Path.GetFileName(source) + ": " + ex.Message); }
        }
        if (!admin) win.UndoHistory.Record(undo);
        await RefreshAsync();
        if (errors.Count > 0) await MessageDialog.ShowAsync(this, UiText.T("Sürükle-bırak"), string.Join("\n", errors));
        else ReportStatus(UiText.F("{0} öğe {1}.", undo.Count, UiText.T(copy ? "kopyalandı" : "taşındı")));
    }

    private ContextMenu MakeContextMenu()
    {
        var menu = new ContextMenu();
        MenuItem Make(string label, Action action)
        {
            var item = new MenuItem { Header = UiText.T(label) };
            item.Click += (_, _) => action();
            return item;
        }
        menu.Items.Add(Make("Aç", OpenSelection));
        var openWith = new MenuItem { Header = UiText.T("Birlikte Aç") };
        openWith.Items.Add(Make("Uygulama seç…  (Ctrl+Shift+O)", () => _ = OpenWithSelectedAsync()));
        menu.Items.Add(openWith);
        menu.Items.Add(new Separator());
        menu.Items.Add(Make("Kopyala  (Ctrl+C)", () => _ = win.SetClipboardAsync(SelectedPaths(), ClipboardMode.Copy, this)));
        menu.Items.Add(Make("Kes  (Ctrl+X)", () => _ = win.SetClipboardAsync(SelectedPaths(), ClipboardMode.Cut, this)));
        menu.Items.Add(Make("Yapıştır  (Ctrl+V)", () => _ = PasteAsync()));
        menu.Items.Add(Make("Son işlemi geri al  (Ctrl+Z)", () => _ = UndoLastAsync()));
        menu.Items.Add(new Separator());

        // A single entry adapts to selection size: one item uses the classic
        // rename dialog, multiple items open the bulk-rename preview.
        menu.Items.Add(Make("Yeniden adlandır  (F2)", () =>
        {
            if (SelectedPaths().Count > 1) _ = BulkRenameSelectedAsync();
            else _ = RenameSelectedAsync();
        }));

        // Native Avalonia MenuItem submenus open sideways on hover.
        var archive = new MenuItem { Header = UiText.T("Arşiv işlemleri") };
        var compress = new MenuItem { Header = UiText.T("Sıkıştır") };
        compress.Items.Add(Make("ZIP (.zip)", () => _ = CompressAsync("zip")));
        compress.Items.Add(Make("7z (.7z)", () => _ = CompressAsync("7z")));
        compress.Items.Add(Make("TAR.GZ (.tar.gz)", () => _ = CompressAsync("tar.gz")));
        compress.Items.Add(new Separator());
        compress.Items.Add(Make("Ark ile sıkıştır…", CompressWithArk));
        archive.Items.Add(compress);
        archive.Items.Add(new Separator());
        archive.Items.Add(Make("Ark ile aç", OpenWithArk));
        archive.Items.Add(Make("Ark ile buraya çıkar…", ExtractWithArk));
        menu.Items.Add(archive);

        menu.Items.Add(Make("Çöp kutusuna taşı  (Del)", () => _ = TrashSelectedAsync()));
        menu.Items.Add(Make("Özellikler", () => _ = ShowPropertiesAsync()));
        var other = new MenuItem { Header = UiText.T("Diğer işlemler") };
        other.Items.Add(Make("Dosya yolunu kopyala", CopySelectedPathToClipboard));
        other.Items.Add(Make("Hash hesapla…", () => _ = HashSelectedAsync()));
        other.Items.Add(Make("Etiketler…", () => _ = EditTagsAsync()));
        other.Items.Add(Make("Tekrarlanan dosyaları bul…", () => _ = DuplicateFinderDialog.ShowAsync(this, Path)));
        other.Items.Add(Make("Klasörleri karşılaştır…", () => _ = CompareFoldersAsync()));
        other.Items.Add(Make("İzinler (chmod)…", () => _ = ChmodSelectedAsync()));
        other.Items.Add(Make("Bu klasörü sabitle", PinCurrent));
        other.Items.Add(Make("Terminali burada aç", OpenTerminal));
        menu.Items.Add(other);
        return menu;
    }

    public async Task ChmodSelectedAsync()
    {
        var paths = SelectedPaths();
        if (paths.Count == 0) { ReportStatus(UiText.T("İzinlerini değiştirmek için öğe seçin.")); return; }
        if (KioAdmin.Enabled)
        {
            await MessageDialog.ShowAsync(this, UiText.T("İzinler"),
                UiText.T("Yönetici modunda izin değiştirme desteklenmiyor."));
            return;
        }
        if (await PermissionsDialog.ShowAsync(win, paths))
        {
            await RefreshAsync();
            ReportStatus(UiText.T("İzinler güncellendi."));
        }
        list.Focus();
    }

    private async Task HashSelectedAsync()
    {
        var file = SelectedPaths().FirstOrDefault();
        if (file is null || Directory.Exists(file)) { ReportStatus(UiText.T("Hash hesaplamak için bir dosya seçin.")); return; }
        try
        {
            ReportStatus(UiText.T("Hash hesaplanıyor…"));
            var service = new FileHashService();
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var sha256 = await service.ComputeAsync(stream, HashAlgorithmKind.Sha256, new Progress<long>(bytes => ReportStatus($"SHA-256: {bytes / 1024 / 1024:0.0} MB")));
            stream.Position = 0;
            var sha512 = await service.ComputeAsync(stream, HashAlgorithmKind.Sha512);
            await HashResultDialog.ShowAsync(this, file, sha256.Hex, sha512.Hex, value => win.SetTextToSystemClipboardAsync(value));
            ReportStatus(UiText.T("Hash hesaplandı."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        { await MessageDialog.ShowAsync(this, UiText.T("Hash"), ex.Message); }
    }

    private async Task EditTagsAsync()
    {
        var file = SelectedPaths().FirstOrDefault();
        if (file is null || Directory.Exists(file)) { ReportStatus(UiText.T("Etiketlemek için bir dosya seçin.")); return; }
        try
        {
            var identity = await win.MetadataStore.GetIdentityAsync(file);
            if (identity is null) return;
            var current = await win.MetadataStore.GetTagsAsync(identity);
            await TagDialog.ShowAsync(this, identity, current, async tags => await win.MetadataStore.SetTagsAsync(identity, tags));
            ReportStatus(UiText.T("Etiketler kaydedildi."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        { await MessageDialog.ShowAsync(this, UiText.T("Etiketler"), ex.Message); }
    }

    private async Task CompareFoldersAsync()
    {
        var other = await InputDialog.ShowAsync(this, UiText.T("Klasör karşılaştır"), Path, UiText.T("Karşılaştırılacak ikinci klasör yolu:"));
        if (string.IsNullOrWhiteSpace(other) || !Directory.Exists(other)) { ReportStatus(UiText.T("Geçerli bir klasör seçilmedi.")); return; }
        await FolderCompareDialog.ShowAsync(this, Path, other);
    }

    public async Task<NavigationResult> NavigateAsync(string path, bool record = true, int historyDelta = 0, bool resolveRelative = false)
    {
        if (string.IsNullOrWhiteSpace(path)) return NavigationResult.Failure(UiText.T("Konum boş."));
        long requestVersion = generation;
        try
        {
            path = path.Trim().Trim('"');
            if (resolveRelative && !System.IO.Path.IsPathRooted(path) && !path.StartsWith('~'))
                path = System.IO.Path.Combine(Path, path);
            if (path.StartsWith("~/", StringComparison.Ordinal) || path == "~")
                path = System.IO.Path.Combine(MainWindow.HomePath(), path == "~" ? "" : path[2..]);
            path = System.IO.Path.GetFullPath(path);
            if (!KioAdmin.Enabled && !Directory.Exists(path))
            {
                var message = UiText.F("Klasör bulunamadı: {0}", path);
                if (requestVersion == generation) ReportStatus(message);
                return NavigationResult.Failure(message);
            }
            scanCts?.Cancel(); scanCts?.Dispose();
            scanCts = new CancellationTokenSource();
            var token = scanCts.Token;
            var version = ++generation;
            ReportStatus(UiText.T("Yükleniyor…"));
            var items = await FileSystem.ListAsync(path, token);
            if (version != generation || token.IsCancellationRequested)
                return NavigationResult.Failure(UiText.T("Konum yenilemesi iptal edildi."));
            // Do not display a history target unless the expected travel can be committed.
            if (historyDelta != 0 && !history.CommitTravel(historyDelta, path))
                return NavigationResult.Failure(UiText.T("Gezinme geçmişi güncellenemedi."));
            Path = path;
            folderWatcher?.Dispose();
            folderWatcher = null;
            try
            {
                if (Directory.Exists(path))
                {
                    folderWatcher = new FileSystemWatchService(path);
                    folderWatcher.Changes += (_, changes) => Dispatcher.UIThread.Post(() =>
                    {
                        if (changes.Count > 0) _ = RefreshAsync();
                    });
                }
            }
            catch (IOException) { folderWatcher = null; }
            catch (UnauthorizedAccessException) { folderWatcher = null; }
            entries = items;
            RenderCrumbs();
            title.Text = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : path;
            pathHint.Text = UiText.T(path == MainWindow.HomePath() ? "ANA DİZİN" : "DOSYA GEZGİNİ");
            if (record) history.RecordSuccess(path);
            win.Prefs.LastDirectory = path;
            folderSizes.Clear();
            previewCts?.Cancel();
            previewedPath = null;
            previewContents.Children.Clear();
            RebuildList();
            _ = LoadFolderSizesAsync(items, version, token);
            win.Prefs.Save();
            PathChanged?.Invoke(this);
            return NavigationResult.Success();
        }
        catch (OperationCanceledException)
        {
            return NavigationResult.Failure(UiText.T("KIO erişimi iptal edildi."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or TimeoutException or OperationCanceledException)
        {
            var message = UiText.F("Konum açılamadı: {0}", ex.Message);
            if (requestVersion == generation) ReportStatus(message);
            return NavigationResult.Failure(message);
        }
        catch (Exception ex)
        {
            var message = UiText.F("Beklenmeyen hata: {0}: {1}", ex.GetType().Name, ex.Message);
            if (requestVersion == generation) ReportStatus(message);
            return NavigationResult.Failure(message);
        }
    }

    public async Task TravelAsync(int delta)
    {
        var target = history.Peek(delta);
        if (target is not null) await NavigateAsync(target, false, delta);
    }

    public async Task GoParentAsync()
    {
        var parent = Directory.GetParent(Path);
        if (parent is not null) await NavigateAsync(parent.FullName);
    }

    public async Task<NavigationResult> RefreshAsync() => await NavigateAsync(Path, false);

    public void FocusList() => list.Focus();

    private static bool IsArchive(string file)
    {
        var lower = file.ToLowerInvariant();
        return new[] { ".zip", ".rar", ".7z", ".tar", ".tar.gz", ".tgz", ".gz", ".tar.xz", ".tar.bz2", ".bz2", ".xz" }
            .Any(lower.EndsWith);
    }

    private void StartArk(params string[] args)
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(TrustedTools.Require("ark")) { UseShellExecute = false };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            TrustedTools.SecurePath(start);
            System.Diagnostics.Process.Start(start);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { ReportStatus(UiText.T("Ark başlatılamadı: ") + ex.Message); }
    }

    private void OpenWithArk()
    {
        var path = SelectedPaths().FirstOrDefault(IsArchive);
        if (path is not null) StartArk(path);
        else ReportStatus(UiText.T("Arşiv açmak için bir arşiv seçin.") + " Ark.");
    }

    private void CompressWithArk()
    {
        var paths = SelectedPaths();
        if (paths.Count == 0) { ReportStatus(UiText.T("Sıkıştırılacak dosyaları seçin.")); return; }
        StartArk(new[] { "--add" }.Concat(paths).ToArray());
    }

    private void ExtractWithArk()
    {
        var path = SelectedPaths().FirstOrDefault(IsArchive);
        if (path is null) { ReportStatus(UiText.T("Çıkarılacak bir arşiv seçin.")); return; }
        // Ark handles format detection, encrypted archives and extraction dialogs.
        StartArk("--extract-to", Path, path);
    }

    private async Task CompressAsync(string format)
    {
        var paths = SelectedPaths();
        if (paths.Count == 0) { ReportStatus(UiText.T("Sıkıştırılacak dosyaları seçin.")); return; }
        try
        {
            ReportStatus(UiText.T("Arşiv oluşturuluyor…"));
            var archive = await SafeArchiveOperations.CreateAsync(paths, Path, format);
            await RefreshAsync();
            ReportStatus(UiText.T("Arşiv oluşturuldu: ") + System.IO.Path.GetFileName(archive));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or TimeoutException or ArgumentException or OperationCanceledException)
        { await MessageDialog.ShowAsync(this, UiText.T("Arşiv oluşturma hatası"), ex.Message); }
    }

    private async void OpenSelection()
    {
        if (list.SelectedItem is not FileEntry entry) return;
        if (entry.IsDirectory) _ = NavigateAsync(entry.FullPath);
        else
        {
            try
            {
                if (OperatingSystem.IsLinux() && UntrustedLauncher.IsPotentiallyExecutable(entry.FullPath))
                {
                    var description = UntrustedLauncher.Describe(entry.FullPath);
                    if (!await ConfirmDialog.ShowAsync(this, UiText.T("Çalıştırılabilir dosya uyarısı"),
                        UiText.F("Bu dosya komut çalıştırabilir:\n{0}\n\n{1}\n\nKaynağına güveniyorsanız açın.", entry.FullPath, description),
                        UiText.T("Riski kabul et ve aç"), dangerous: true)) return;
                }
                if (OperatingSystem.IsLinux())
                {
                    var opener = new System.Diagnostics.ProcessStartInfo(TrustedTools.Require("xdg-open"))
                    { ArgumentList = { entry.FullPath }, UseShellExecute = false };
                    TrustedTools.SecurePath(opener);
                    System.Diagnostics.Process.Start(opener);
                }
                else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(entry.FullPath) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
            { ReportStatus(UiText.F("Dosya açılamadı: {0}", ex.Message)); }
        }
    }

    public async Task OpenWithSelectedAsync()
    {
        if (list.SelectedItem is not FileEntry entry || entry.IsDirectory)
        {
            ReportStatus(UiText.T("Bir dosya seçin; klasörler için uygulama seçimi desteklenmiyor."));
            return;
        }
        var file = entry.FullPath;
        try
        {
            var choice = await OpenWithDialog.ShowAsync(win, file);
            if (choice is null) return;
            if (UntrustedLauncher.IsPotentiallyExecutable(file))
            {
                var description = UntrustedLauncher.Describe(file);
                if (!await ConfirmDialog.ShowAsync(this, UiText.T("Güvenlik: çalıştırılabilir dosya"),
                    $"{file}\n\n{description}\n\n{UiText.T("Bu dosya komut çalıştırabilir.")} {UiText.T("Seçilen uygulama:")} {choice.Application.Name}",
                    UiText.T("Riski kabul et ve aç"), dangerous: true)) return;
            }
            if (choice.MakeDefault)
                await OpenWithService.SetDefaultAsync(choice.MimeType, choice.Application);
            await OpenWithService.LaunchAsync(choice.Application, file);
            ReportStatus($"{System.IO.Path.GetFileName(file)} → {choice.Application.Name}" +
                (choice.MakeDefault ? "  · " + UiText.T("Varsayılan uygulama güncellendi") : ""));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or TimeoutException or ArgumentException or OperationCanceledException)
        {
            await MessageDialog.ShowAsync(this, UiText.T("Birlikte Aç"), ex.Message);
        }
    }

    public IReadOnlyList<string> SelectedPaths()
    {
        var paths = (list.SelectedItems as IEnumerable<object>)?.OfType<FileEntry>().Select(x => x.FullPath).ToList();
        if (paths is { Count: > 0 }) return paths;
        if (list.SelectedItem is FileEntry single) return new[] { single.FullPath };
        return Array.Empty<string>();
    }

    private void OnPaneKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox) return;
        switch (e.Key)
        {
            case Key.C when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                _ = win.SetClipboardAsync(SelectedPaths(), ClipboardMode.Copy, this); e.Handled = true; break;
            case Key.X when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                _ = win.SetClipboardAsync(SelectedPaths(), ClipboardMode.Cut, this); e.Handled = true; break;
            case Key.V when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                _ = PasteAsync(); e.Handled = true; break;
            case Key.O when e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                _ = OpenWithSelectedAsync(); e.Handled = true; break;
            case Key.F2 when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                _ = BulkRenameSelectedAsync(); e.Handled = true; break;
            case Key.F2:
                _ = RenameSelectedAsync(); e.Handled = true; break;
            case Key.Delete:
                _ = TrashSelectedAsync(); e.Handled = true; break;
            case Key.A when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                list.SelectAll(); e.Handled = true; break;
        }
    }

    public async Task UndoLastAsync()
    {
        if (!win.UndoHistory.CanUndo) { ReportStatus(UiText.T("Geri alınacak dosya işlemi yok.")); return; }
        if (!await ConfirmDialog.ShowAsync(this, UiText.T("Dosya işlemini geri al"),
            UiText.T("Son dosya işlemi geri alınacak. Kopyalanan dosyalar güvenlik için Emanager kurtarma klasörüne taşınır."), UiText.T("Geri al"))) return;
        var (restored, error) = await win.UndoHistory.UndoAsync();
        await RefreshAsync();
        if (error is not null) await MessageDialog.ShowAsync(this, UiText.T("Geri al"), UiText.F("Dosya işlemleri geri alındı. Kalan işlem başarısız: {0}", error));
        else ReportStatus(UiText.F("{0} dosya işlemi geri alındı.", restored));
    }

    private Task<bool> ConfirmRootMutationAsync(string operation, IReadOnlyList<string> paths, string destination)
    {
        var sourcePreview = string.Join("\n", paths.Take(8).Select(x => "• " + x));
        if (paths.Count > 8) sourcePreview += "\n… " + UiText.F("{0} diğer öğe", paths.Count - 8);
        return ConfirmDialog.ShowAsync(this, "KIO-ADMIN: " + UiText.T(operation),
            UiText.F("ROOT yetkisiyle {0} işlemi uygulanacak.\n\nKaynaklar:\n{1}\n\nHedef: {2}\n\nBu işlem sistem dosyalarını değiştirebilir.", UiText.T(operation.ToLowerInvariant()), sourcePreview, destination),
            UiText.T("Root işlemini uygula"), dangerous: true);
    }

    public async Task PasteAsync()
    {
        if (!win.HasClipboard || string.IsNullOrEmpty(Path)) return;
        var clipboard = win.ClipboardPaths.ToList();
        var admin = KioAdmin.Enabled;
        if (admin)
        {
            if (!await ConfirmRootMutationAsync(clipboard.Any(x => x.Mode == ClipboardMode.Cut) ? "Taşı" : "Kopyala",
                clipboard.Select(x => x.Path).ToList(), Path)) return;
            if (!KioAdmin.Enabled) return;
        }
        var total = clipboard.Count;
        var done = 0;
        var failures = new List<string>();
        var undoSteps = new List<FileUndo.Step>();
        ConflictAction? applyToAll = null;
        try
        {
            foreach (var (source, mode) in clipboard)
            {
                ReportStatus(UiText.F("Yapıştırılıyor… {0}/{1}", done + 1, total));
                try
                {
                    if (admin)
                    {
                        if (mode == ClipboardMode.Cut) await KioAdmin.MoveAsync(source, Path);
                        else await KioAdmin.CopyAsync(source, Path);
                    }
                    else
                    {
                        var operation = mode == ClipboardMode.Cut ? FileOperationKind.Move : FileOperationKind.Copy;
                        var targetPath = System.IO.Path.Combine(Path, System.IO.Path.GetFileName(source));
                        ConflictAction? conflict = applyToAll;
                        if (conflict is null && (File.Exists(targetPath) || Directory.Exists(targetPath)))
                        {
                            var sourceInfo = new FileInfo(source); var targetInfo = new FileInfo(targetPath);
                            var choice = await ConflictDialog.ShowAsync(this, new FileConflict(source, targetPath, sourceInfo.Exists ? sourceInfo.Length : 0, targetInfo.Exists ? targetInfo.Length : 0, sourceInfo.Exists ? new DateTimeOffset(sourceInfo.LastWriteTimeUtc) : DateTimeOffset.MinValue, targetInfo.Exists ? new DateTimeOffset(targetInfo.LastWriteTimeUtc) : DateTimeOffset.MinValue));
                            if (choice.Action is null) break;
                            conflict = choice.Action; if (choice.ApplyToAll) applyToAll = conflict;
                        }
                        if (conflict == ConflictAction.Skip) { done++; continue; }
                        var result = await win.RunManagedOperationAsync(operation, source, Path, conflict);
                        if (result.State != FileOperationState.Completed)
                            throw result.Error ?? new IOException("Dosya işlemi tamamlanamadı.");
                    }
                    undoSteps.Add(new FileUndo.Step(mode == ClipboardMode.Cut ? FileUndo.Kind.Move : FileUndo.Kind.Copy,
                        source, System.IO.Path.Combine(Path, System.IO.Path.GetFileName(source))));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or TimeoutException or OperationCanceledException)
                {
                    failures.Add($"{System.IO.Path.GetFileName(source)}: {ex.Message}");
                }
                done++;
            }
            if (!admin) win.UndoHistory.Record(undoSteps);
            if (win.ClipboardMode == ClipboardMode.Cut) win.ClearClipboard();
            await RefreshAsync();
            if (failures.Count > 0)
                await MessageDialog.ShowAsync(this, "Yapıştırma", UiText.F("{0}/{1} öğe aktarıldı.\n\nAtlananlar:\n{2}", done - failures.Count, total, string.Join("\n", failures)));
            list.Focus();
        }
        catch (Exception ex)
        {
            ReportStatus(UiText.F("Beklenmeyen yapıştırma hatası: {0}: {1}", ex.GetType().Name, ex.Message));
        }
    }

    public async Task BulkRenameSelectedAsync()
    {
        var paths = SelectedPaths();
        if (paths.Count < 2) { ReportStatus(UiText.T("En az iki öğe seçerek toplu yeniden adlandırma yapın.")); return; }
        if (KioAdmin.Enabled)
        {
            await MessageDialog.ShowAsync(this, UiText.T("KIO-Admin"), UiText.T("Yönetici modunda toplu yeniden adlandırma henüz desteklenmiyor."));
            return;
        }
        if (await BulkRename.ShowAsync(win, paths))
        {
            await RefreshAsync();
            ReportStatus(UiText.F("{0} öğe için toplu yeniden adlandırma tamamlandı.", paths.Count));
        }
    }

    public async Task RenameSelectedAsync()
    {
        if (list.SelectedItem is not FileEntry entry) return;
        var text = await InputDialog.ShowAsync(this, UiText.T("Yeniden adlandır"), System.IO.Path.GetFileName(entry.FullPath),
            UiText.T("Yeni ad: (yol ayraçları ve mevcut adlar kullanılamaz)"));
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            if (KioAdmin.Enabled)
            {
                if (!await ConfirmRootMutationAsync("Yeniden adlandır", [entry.FullPath],
                    System.IO.Path.Combine(System.IO.Path.GetDirectoryName(entry.FullPath)!, text))) return;
                if (!KioAdmin.Enabled) return;
                await KioAdmin.RenameAsync(entry.FullPath, text);
            }
            else
            {
                var target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(entry.FullPath)!, text);
                var result = await win.RunManagedOperationAsync(FileOperationKind.Rename, entry.FullPath, target);
                if (result.State != FileOperationState.Completed)
                    throw result.Error ?? new IOException("Yeniden adlandırma tamamlanamadı.");
                win.UndoHistory.Record([new FileUndo.Step(FileUndo.Kind.Rename, entry.FullPath,
                    System.IO.Path.Combine(System.IO.Path.GetDirectoryName(entry.FullPath)!, text))]);
            }
            await RefreshAsync();
            ReportStatus(UiText.T("Yeniden adlandırıldı."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or TimeoutException or OperationCanceledException)
        { await MessageDialog.ShowAsync(this, UiText.T("Yeniden adlandırma"), ex.Message); }
        list.Focus();
    }

    public async Task TrashSelectedAsync()
    {
        var items = SelectedPaths();
        if (items.Count == 0) return;
        var names = items.Select(System.IO.Path.GetFileName).ToList();
        var preview = string.Join(", ", names.Take(6)) + (names.Count > 6 ? $" … +{names.Count - 6}" : "");
        var admin = KioAdmin.Enabled;
        if (!await ConfirmDialog.ShowAsync(this, admin ? UiText.T("KIO-Admin: Kalıcı sil") : UiText.T("Çöp kutusuna taşı"),
            admin ? UiText.F("{0} öğe KALICI olarak silinecek (çöp kutusu yok):\n{1}", names.Count, preview) :
            UiText.F("{0} öğe çöp kutusuna taşınacak:\n{1}\n\nÇöp kutusundan geri alınabilir.", names.Count, preview),
            admin ? UiText.T("Kalıcı sil") : UiText.T("Çöp kutusuna taşı"), dangerous: admin)) return;
        if (admin && !KioAdmin.Enabled) return;
        var failures = new List<string>();
        foreach (var item in items)
        {
            try { if (admin) await KioAdmin.RemoveAsync(item); else SafeFileOperations.Trash(item); }
            catch (TrashUnavailableException ex)
            {
                failures.Add($"{System.IO.Path.GetFileName(item)}: {ex.Message} {UiText.T("Dosya korunmuştur.")}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or OperationCanceledException)
            { failures.Add($"{System.IO.Path.GetFileName(item)}: {ex.Message}"); }
        }
        await RefreshAsync();
        win.RefreshTrashView();
        if (failures.Count > 0) await MessageDialog.ShowAsync(this, UiText.T("Çöp kutusu"), string.Join("\n", failures));
        else ReportStatus(admin ? UiText.F("{0} öğe kalıcı olarak silindi.", names.Count) :
            UiText.F("{0} öğe çöp kutusuna taşındı.", names.Count));
        list.Focus();
    }

    private void CopySelectedPathToClipboard()
    {
        if (list.SelectedItem is not FileEntry entry) return;
        _ = win.SetTextToSystemClipboardAsync(entry.FullPath);
    }

    private async Task ShowPropertiesAsync()
    {
        if (list.SelectedItem is not FileEntry entry) return;
        await MessageDialog.ShowAsync(this, "Özellikler",
            UiText.F("Ad: {0}\nYol: {1}\nTür: {2}\nBoyut: {3}\nDeğiştirilme: {4}\nUnix izinleri: {5}\nSembolik bağlantı: {6}",
                entry.Name, entry.FullPath, entry.Kind, entry.SizeLabel,
                entry.ModifiedLabel, entry.Permissions, UiText.T(entry.IsLink ? "Evet" : "Hayır")));
    }

    private void PinCurrent() => win.PinPath(Path);

    private void OpenTerminal() => win.OpenTerminalAt(Path);

    private void RenderCrumbs()
    {
        crumbs.Children.Clear();
        var root = System.IO.Path.GetPathRoot(Path) ?? "/";
        var accumulated = root;
        var rootButton = win.MakeButton("⌂  Bilgisayar", () => _ = NavigateAsync(root), root);
        rootButton.Padding = new Thickness(9, 5);
        rootButton.MinHeight = 28;
        crumbs.Children.Add(rootButton);
        var parts = Path.Substring(root.Length)
            .Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        foreach (string segment in parts)
        {
            crumbs.Children.Add(new TextBlock
            {
                Text = "›", Foreground = MainWindow.TextMuted, FontSize = 17,
                VerticalAlignment = VerticalAlignment.Center
            });
            accumulated = System.IO.Path.Combine(accumulated, segment);
            string destination = accumulated;
            var button = win.MakeButton(segment, () => _ = NavigateAsync(destination), destination);
            button.Padding = new Thickness(9, 5);
            button.MinHeight = 28;
            button.Background = string.Equals(destination, Path, StringComparison.Ordinal)
                ? MainWindow.SurfaceBrush : Brushes.Transparent;
            button.BorderThickness = new Thickness(0);
            crumbs.Children.Add(button);
        }
    }

    // Calculate directory sizes off the UI thread. Never follow symbolic links,
    // and abandon stale results when the user navigates elsewhere.
    private async Task LoadFolderSizesAsync(IReadOnlyList<FileEntry> items, int version, CancellationToken token)
    {
        try
        {
            foreach (var entry in items.Where(e => e.IsDirectory && !e.IsLink))
            {
                token.ThrowIfCancellationRequested();
                long? size = await Task.Run(() => FolderMeasure.Measure(entry.FullPath, token), token);
                if (version != generation || token.IsCancellationRequested) return;
                folderSizes[entry.FullPath] = size;
                // Re-render the visible list without re-reading the filesystem.
                RebuildList();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void RebuildList()
    {
        IEnumerable<FileEntry> query = entries;
        if (!win.Prefs.ShowHidden) query = query.Where(x => !FileSystem.Hidden(x.FullPath));
        if (!string.IsNullOrWhiteSpace(searchFilter)) query = query.Where(x => x.Name.Contains(searchFilter, StringComparison.CurrentCultureIgnoreCase));
        query = (win.Prefs.SortBy switch
        {
            "Boyut" => win.Prefs.Descending ? query.OrderByDescending(x => x.Length) : query.OrderBy(x => x.Length),
            "Tür" => win.Prefs.Descending
                ? query.OrderByDescending(x => x.Kind, StringComparer.CurrentCultureIgnoreCase)
                : query.OrderBy(x => x.Kind, StringComparer.CurrentCultureIgnoreCase),
            "Değiştirilme" => win.Prefs.Descending ? query.OrderByDescending(x => x.Modified) : query.OrderBy(x => x.Modified),
            _ => win.Prefs.Descending
                ? query.OrderByDescending(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                : query.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
        });
        if (win.Prefs.FoldersFirst) query = query.OrderByDescending(x => x.IsDirectory);
        list.ItemTemplate = win.Prefs.ViewMode switch
        {
            "Büyük simge" => TileTemplate(),
            "Liste" => RowTemplate(false),
            _ => RowTemplate(true)
        };
        list.ItemsSource = query.ToList();
        UpdatePreview();
        countLabel.Text = UiText.F("{0} öğe görüntüleniyor", query.Count());
        RaiseStatus();
    }

    private void RaiseStatus()
    {
        var count = (list.ItemsSource as IEnumerable<FileEntry>)?.Count() ?? 0;
        var selected = list.SelectedItems?.Count ?? (list.SelectedItem == null ? 0 : 1);
        StatusText?.Invoke(this, UiText.F("{0} öğe  •  {1} seçili  •  {2}", count, selected, Path));
    }

    private void ReportStatus(string text) => StatusText?.Invoke(this, text);

    private static bool IsImage(string path) => new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" }
        .Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private void AttachThumbnail(FileEntry entry, Panel host, Control fallback, int width)
    {
        if (entry.IsDirectory || entry.IsLink || !IsImage(entry.FullPath)) return;
        if (thumbnails.TryGetValue(entry.FullPath, out var bitmap))
        {
            fallback.IsVisible = false;
            host.Children.Add(new Image { Source = bitmap, Width = width, Height = width,
                Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center });
            return;
        }
        if (!pendingThumbs.Add(entry.FullPath)) return;
        var path = entry.FullPath;
        _ = Task.Run(() =>
        {
            Bitmap? decoded = null;
            try
            {
                if (new FileInfo(path).Length <= 80 * 1024 * 1024)
                    using (var stream = File.OpenRead(path)) decoded = ImageDecode.TryDecodeToWidth(stream, 160);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or TimeoutException or OperationCanceledException) { }
            Dispatcher.UIThread.Post(() =>
            {
                pendingThumbs.Remove(path);
                if (decoded is null) return;
                if (thumbnails.Count >= MaxThumbnailCache)
                {
                    // Avoid disposing bitmaps still bound to recycled Avalonia controls.
                    decoded.Dispose();
                    return;
                }
                thumbnails[path] = decoded;
                // Update the visible list when an image is ready.
                RebuildList();
            });
        });
    }

    private void UpdatePreview()
    {
        if (!previewPanel.IsVisible) { previewedPath = null; previewCts?.Cancel(); return; }
        var selectedPath = (list.SelectedItem as FileEntry)?.FullPath;
        if (selectedPath == previewedPath && previewContents.Children.Count > 0) return;
        previewCts?.Cancel();
        previewCts?.Dispose();
        previewCts = new CancellationTokenSource();
        var token = previewCts.Token;
        previewedPath = selectedPath;
        previewContents.Children.Clear();
        if (list.SelectedItem is not FileEntry item)
        {
            previewContents.Children.Add(new TextBlock { Text = UiText.T("Önizlemek için bir dosya seçin."),
                TextWrapping = TextWrapping.Wrap, Foreground = MainWindow.TextMuted });
            return;
        }
        previewContents.Children.Add(new TextBlock { Text = item.Name, FontSize = 17,
            FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
            Foreground = MainWindow.TextPrimary });
        previewContents.Children.Add(new TextBlock { Text = UiText.F("Tür: {0}\nBoyut: {1}\nDeğiştirilme: {2}", item.Kind, item.SizeLabel, item.ModifiedLabel),
            TextWrapping = TextWrapping.Wrap, Foreground = MainWindow.TextMuted });
        if (item.IsDirectory || item.IsLink) return;
        if (System.IO.Path.GetExtension(item.FullPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var archiveText = new TextBlock { Text = UiText.T("Arşiv yükleniyor…"), TextWrapping = TextWrapping.Wrap, Foreground = MainWindow.TextPrimary };
            previewContents.Children.Add(archiveText);
            _ = Task.Run(async () =>
            {
                var preview = await previewCoordinator.PreviewAsync(item.FullPath, "application/zip", token);
                Dispatcher.UIThread.Post(() =>
                {
                    if (token.IsCancellationRequested || preview?.Value is not IReadOnlyList<string> names) return;
                    archiveText.Text = string.Join("\n", names.Take(200)) + (preview.IsPartial ? "\n…" : "");
                });
            });
            return;
        }
        if (IsImage(item.FullPath))
        {
            var picture = new Image { MaxWidth = 230, MaxHeight = 310, Stretch = Stretch.Uniform };
            previewContents.Children.Add(picture);
            var file = item.FullPath;
            _ = Task.Run(() =>
            {
                Bitmap? image = null;
                try
                {
                    if (new FileInfo(file).Length <= 80 * 1024 * 1024)
                        using (var stream = File.OpenRead(file)) image = ImageDecode.TryDecodeToWidth(stream, 600);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or TimeoutException or OperationCanceledException) { }
                Dispatcher.UIThread.Post(() =>
                {
                    if (token.IsCancellationRequested) { image?.Dispose(); return; }
                    picture.Source = image;
                });
            });
        }
        else if (new[] { ".txt", ".md", ".json", ".xml", ".csv", ".log", ".cs", ".py", ".sh" }
            .Contains(System.IO.Path.GetExtension(item.FullPath), StringComparer.OrdinalIgnoreCase))
        {
            var snippet = new TextBlock { Text = UiText.T("Metin yükleniyor…"), TextWrapping = TextWrapping.Wrap,
                FontSize = 12, Foreground = MainWindow.TextPrimary };
            previewContents.Children.Add(snippet);
            _ = Task.Run(() =>
            {
                string text;
                try
                {
                    using var stream = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    var buffer = new char[4000];
                    int count = reader.ReadBlock(buffer, 0, buffer.Length);
                    text = new string(buffer, 0, count).Replace("\0", "");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { text = UiText.T("Önizleme okunamadı."); }
                Dispatcher.UIThread.Post(() => { if (!token.IsCancellationRequested) snippet.Text = text; });
            });
        }
    }

    private FuncDataTemplate<FileEntry> RowTemplate(bool details) => new((item, _) =>
    {
        // Avalonia can rebuild recycled containers with a null item during
        // virtualization; returning an empty placeholder prevents an NRE that
        // would abort the list update mid-refresh.
        if (item is null) return new Panel { MinHeight = 43 };
        var row = new Grid
        {
            ColumnDefinitions = details
                ? new ColumnDefinitions("44,*,110,155,120")
                : new ColumnDefinitions("44,*"),
            Margin = new Thickness(3), MinHeight = 43
        };
        string glyph = item.IsLink ? "↗" : item.IsDirectory ? "📁" : "📄";
        var ico = new TextBlock
        {
            Text = glyph, FontSize = 21,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        row.Children.Add(ico);
        AttachThumbnail(item, row, ico, 36);
        var label = new TextBlock
        {
            Text = item.Name, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = MainWindow.TextPrimary,
            FontSize = 13, FontWeight = item.IsDirectory ? FontWeight.SemiBold : FontWeight.Normal,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 4)
        };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        if (details)
        {
            var sizeLabel = item.IsDirectory && !item.IsLink
                ? folderSizes.TryGetValue(item.FullPath, out var folderSize)
                    ? folderSize.HasValue ? FileSystem.HumanSize(folderSize.Value) : "—"
                    : "Calculating…"
                : item.SizeLabel;
            var fields = new[] { sizeLabel, item.ModifiedLabel, item.Kind };
            for (int i = 0; i < fields.Length; i++)
            {
                var cell = new TextBlock
                {
                    Text = fields[i], FontSize = 12, Foreground = MainWindow.TextMuted,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(cell, i + 2);
                row.Children.Add(cell);
            }
        }
        return row;
    });

    private FuncDataTemplate<FileEntry> TileTemplate() => new((item, _) =>
    {
        if (item is null) return new Panel { Width = 140, MinHeight = 120 };
        string glyph = item.IsLink ? "↗" : item.IsDirectory ? "📁" : "📄";
        var content = new StackPanel { Spacing = 6 };
        var tileIcon = new Grid { Height = 76, Width = 116, Margin = new Thickness(0, 6, 0, 0) };
        var tileGlyph = new TextBlock { Text = glyph, FontSize = 44,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        tileIcon.Children.Add(tileGlyph);
        AttachThumbnail(item, tileIcon, tileGlyph, 108);
        content.Children.Add(tileIcon);
        content.Children.Add(new TextBlock
        {
            Text = item.Name, FontSize = 12, Foreground = MainWindow.TextPrimary,
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(4, 0)
        });
        // Selection highlight is provided by the default ListBoxItem container style.
        var tile = new Border
        {
            Width = 140, MinHeight = 116, CornerRadius = new CornerRadius(10),
            Child = content
        };
        return tile;
    });
}

/// <summary>Simple modal used for properties, notices and confirmations.</summary>
public sealed record ConflictDialogResult(ConflictAction? Action, bool ApplyToAll);

public static class ConflictDialog
{
    public static async Task<ConflictDialogResult> ShowAsync(Control owner, FileConflict conflict)
    {
        var window = owner as Window ?? TopLevel.GetTopLevel(owner) as Window;
        if (window is null) return new(null, false);
        var result = new ConflictDialogResult(null, false);
        var dialog = new Window { Title = UiText.T("Dosya çakışması"), Width = 650, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = MainWindow.SidebarBrush, CanResize = false };
        var all = new CheckBox { Content = UiText.T("Bu kararı tüm çakışmalara uygula"), Margin = new Thickness(20, 0, 20, 12) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 0, 20, 18) };
        void Add(string label, ConflictAction? action, bool primary = false) { var button = new Button { Content = label, Padding = new Thickness(12, 7), Background = primary ? MainWindow.AccentBrush : MainWindow.SurfaceBrush, Foreground = primary ? MainWindow.CanvasBrush : MainWindow.TextPrimary }; button.Click += (_, _) => { result = new(action, all.IsChecked == true); dialog.Close(); }; buttons.Children.Add(button); }
        Add(UiText.T("Atla"), ConflictAction.Skip); Add(UiText.T("Daha yenisini koru"), ConflictAction.NewerOnly); Add(UiText.T("Üzerine yaz"), ConflictAction.Overwrite, true); Add(UiText.T("Vazgeç"), null);
        var text = new TextBlock { Text = UiText.T("Aynı ada sahip bir dosya zaten var.") + "\n\n" + UiText.F("Kaynak: {0}\n{1:N0} bayt · {2:g}\n\nHedef: {3}\n{4:N0} bayt · {5:g}", conflict.Source, conflict.SourceLength, conflict.SourceModified, conflict.Destination, conflict.DestinationLength, conflict.DestinationModified), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20), Foreground = MainWindow.TextPrimary };
        var stack = new StackPanel(); stack.Children.Add(text); stack.Children.Add(all); stack.Children.Add(buttons); dialog.Content = stack; await dialog.ShowDialog(window); window.Focus(); return result;
    }
}

public static class DuplicateFinderDialog
{
    public static async Task ShowAsync(Control owner, string root)
    {
        var window = owner as Window ?? TopLevel.GetTopLevel(owner) as Window;
        if (window is null) return;
        var dialog = new Window { Title = UiText.T("Tekrarlanan dosyalar"), Width = 820, Height = 620, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = MainWindow.SidebarBrush };
        var results = new ListBox { SelectionMode = SelectionMode.Multiple, Margin = new Thickness(16) };
        var status = new TextBlock { Text = UiText.T("Taranıyor…"), Margin = new Thickness(16, 12, 16, 0), Foreground = MainWindow.TextMuted };
        var trash = new Button { Content = UiText.T("Seçilenleri Çöp Kutusuna Taşı"), IsEnabled = false, Padding = new Thickness(14, 7) };
        var close = new Button { Content = UiText.T("Kapat"), Padding = new Thickness(14, 7) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 0, 16, 16) };
        buttons.Children.Add(trash); buttons.Children.Add(close);
        var layout = new DockPanel(); DockPanel.SetDock(status, Dock.Top); DockPanel.SetDock(buttons, Dock.Bottom); layout.Children.Add(status); layout.Children.Add(buttons); layout.Children.Add(results); dialog.Content = layout;
        close.Click += (_, _) => dialog.Close();
        var groups = await Task.Run(() => new DuplicateFinder().FindAsync(root));
        var paths = groups.SelectMany(x => x.Files.Skip(1).Select(file => file.Path)).ToArray();
        results.ItemsSource = paths; status.Text = UiText.F("{0} yinelenen grup · {1} kaldırılabilir aday", groups.Count, paths.Length); trash.IsEnabled = paths.Length > 0;
        trash.Click += (_, _) =>
        {
            var selected = results.SelectedItems?.OfType<string>().ToArray() ?? Array.Empty<string>();
            foreach (var path in selected) { try { SafeFileOperations.Trash(path); } catch { } }
            status.Text = UiText.F("{0} öğe Çöp Kutusuna taşındı. Kalıcı silme yapılmadı.", selected.Length);
            results.ItemsSource = paths.Except(selected, StringComparer.Ordinal).ToArray();
        };
        await dialog.ShowDialog(window); window.Focus();
    }
}

public static class FolderCompareDialog
{
    public static async Task ShowAsync(Control owner, string left, string right)
    {
        var window = owner as Window ?? TopLevel.GetTopLevel(owner) as Window;
        if (window is null) return;
        var dialog = new Window { Title = UiText.T("Klasör karşılaştırması"), Width = 900, Height = 620, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = MainWindow.SidebarBrush };
        var list = new ListBox { Margin = new Thickness(16) }; var status = new TextBlock { Text = UiText.T("Karşılaştırılıyor…"), Margin = new Thickness(16, 12, 16, 0), Foreground = MainWindow.TextMuted };
        var direction = new ComboBox { ItemsSource = new[] { UiText.T("Sol → Sağ"), UiText.T("Sağ → Sol"), UiText.T("İki yönlü") }, SelectedIndex = 0, Width = 150 };
        var apply = new Button { Content = UiText.T("Planı uygula"), Padding = new Thickness(14, 7), IsEnabled = false };
        var close = new Button { Content = UiText.T("Kapat"), Padding = new Thickness(14, 7) }; close.Click += (_, _) => dialog.Close();
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(16, 0, 16, 12) }; controls.Children.Add(new TextBlock { Text = UiText.T("Yön:"), Foreground = MainWindow.TextMuted, VerticalAlignment = VerticalAlignment.Center }); controls.Children.Add(direction); controls.Children.Add(apply); controls.Children.Add(close);
        var layout = new DockPanel(); DockPanel.SetDock(status, Dock.Top); DockPanel.SetDock(controls, Dock.Bottom); layout.Children.Add(status); layout.Children.Add(controls); layout.Children.Add(list); dialog.Content = layout;
        var entries = await new FolderComparisonService().CompareAsync(left, right);
        void RefreshPlan()
        {
            var selected = direction.SelectedIndex switch { 1 => SyncDirection.RightToLeft, 2 => SyncDirection.TwoWay, _ => SyncDirection.LeftToRight };
            var plan = new FolderSyncPlanner().Plan(entries, selected);
            list.ItemsSource = plan.Actions.Select(x => $"{x.Kind}: {x.Source}  →  {x.Destination}{(x.RequiresConfirmation ? "  [confirmation]" : "")}").ToArray();
            status.Text = UiText.F("{0} planlı işlem · Planı uygula düğmesine basılana kadar değişiklik yok", plan.Actions.Count); apply.IsEnabled = plan.Actions.Count > 0;
            apply.Tag = plan;
        }
        RefreshPlan(); direction.SelectionChanged += (_, _) => RefreshPlan();
        apply.Click += async (_, _) =>
        {
            if (apply.Tag is not SyncPlan plan) return;
            if (!await ConfirmDialog.ShowAsync(dialog, UiText.T("Eşitleme"), UiText.T("Dosyalar kopyalanabilir veya üzerine yazılabilir. Yalnızca plan doğruysa devam edin."), UiText.T("Uygula"), dangerous: true)) return;
            try { var completed = await new FolderSyncExecutor().ExecuteAsync(plan, true); status.Text = UiText.F("{0} işlem uygulandı.", completed.Count); apply.IsEnabled = false; }
            catch (Exception ex) { status.Text = UiText.T("Eşitleme başarısız: ") + ex.Message; }
        };
        await dialog.ShowDialog(window); window.Focus();
    }
}

public static class HashResultDialog
{
    public static async Task ShowAsync(Control owner, string path, string sha256, string sha512, Func<string, Task> copy)
    {
        var window = owner as Window ?? TopLevel.GetTopLevel(owner) as Window;
        if (window is null) return;
        var dialog = new Window { Title = UiText.T("Dosya hash değeri"), Width = 760, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = MainWindow.SidebarBrush, CanResize = false };
        var stack = new StackPanel { Spacing = 10, Margin = new Thickness(20) };
        stack.Children.Add(new TextBlock { Text = System.IO.Path.GetFileName(path), FontWeight = FontWeight.SemiBold, Foreground = MainWindow.TextPrimary });
        void Add(string label, string value)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10 };
            row.Children.Add(new TextBlock { Text = label, Foreground = MainWindow.TextMuted, VerticalAlignment = VerticalAlignment.Center });
            var text = new TextBox { Text = value, IsReadOnly = true, FontFamily = new FontFamily("monospace"), TextWrapping = TextWrapping.Wrap, Foreground = MainWindow.TextPrimary };
            Grid.SetColumn(text, 1); row.Children.Add(text);
            var button = new Button { Content = UiText.T("Panoya kopyala"), Padding = new Thickness(10, 5) };
            button.Click += async (_, _) => await copy(value);
            Grid.SetColumn(button, 2); row.Children.Add(button); stack.Children.Add(row);
        }
        Add("SHA-256", sha256); Add("SHA-512", sha512);
        var close = new Button { Content = UiText.T("Kapat"), HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(16, 7) };
        close.Click += (_, _) => dialog.Close(); stack.Children.Add(close); dialog.Content = stack; await dialog.ShowDialog(window); window.Focus();
    }
}

public static class MessageDialog
{
    private static Window? OwnerWindow(Control owner) => owner as Window
        ?? TopLevel.GetTopLevel(owner) as Window
        ?? owner.GetVisualAncestors().OfType<Window>().FirstOrDefault();

    public static async Task ShowAsync(Control owner, string title, string message)
    {
        var window = OwnerWindow(owner);
        if (window is null) return;
        var dialog = new Window
        {
            Title = UiText.T(title), Width = 520, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = MainWindow.SidebarBrush, CanResize = false
        };
        var text = new TextBlock
        {
            Text = UiText.T(message), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24, 20, 24, 10),
            Foreground = MainWindow.TextPrimary, MaxWidth = 460
        };
        var ok = new Button
        {
            Content = UiText.T("Tamam"), Padding = new Thickness(20, 8), Margin = new Thickness(0, 0, 24, 18),
            HorizontalAlignment = HorizontalAlignment.Right, Background = MainWindow.SurfaceBrush,
            Foreground = MainWindow.TextPrimary, BorderBrush = MainWindow.StrokeBrush, BorderThickness = new Thickness(1)
        };
        ok.Click += (_, _) => dialog.Close();
        dialog.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter) { dialog.Close(); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        var panel = new StackPanel();
        panel.Children.Add(text);
        panel.Children.Add(ok);
        dialog.Content = panel;
        await dialog.ShowDialog(window);
        // Without a window manager the owner can be left unfocused after a
        // modal closes; hand keyboard focus back explicitly.
        window.Focus();
    }
}

public static class ConfirmDialog
{
    private static Window? OwnerWindow(Control owner) => owner as Window ?? owner.GetVisualAncestors().OfType<Window>().FirstOrDefault();

    public static async Task<bool> ShowAsync(Control owner, string title, string message, string acceptText, bool dangerous = false)
    {
        var window = OwnerWindow(owner);
        if (window is null) return false;
        var result = false;
        var dialog = new Window
        {
            Title = UiText.T(title), Width = 520, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = MainWindow.SidebarBrush, CanResize = false
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 24, 18)
        };
        var accept = new Button
        {
            Content = UiText.T(acceptText), Padding = new Thickness(18, 8),
            Background = MainWindow.AccentBrush, Foreground = MainWindow.CanvasBrush,
            IsDefault = !dangerous
        };
        accept.Click += (_, _) => { result = true; dialog.Close(); };
        var cancel = new Button
        {
            Content = UiText.T("Vazgeç"), Padding = new Thickness(18, 8),
            Background = MainWindow.SurfaceBrush, Foreground = MainWindow.TextPrimary,
            BorderBrush = MainWindow.StrokeBrush, BorderThickness = new Thickness(1),
            IsDefault = dangerous
        };
        cancel.Click += (_, _) => dialog.Close();
        buttons.Children.Add(cancel);
        buttons.Children.Add(accept);
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = UiText.T(message), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24, 20, 24, 12),
            Foreground = MainWindow.TextPrimary, MaxWidth = 460
        });
        panel.Children.Add(buttons);
        dialog.Content = panel;
        // Destructive actions need a deliberate button click; Enter never accepts.
        dialog.Opened += (_, _) => cancel.Focus();
        dialog.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                if (!dangerous) result = true;
                dialog.Close(); e.Handled = true;
            }
            else if (e.Key == Key.Escape) { dialog.Close(); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        await dialog.ShowDialog(window);
        window.Focus();
        return result;
    }
}

public static class InputDialog
{
    public static async Task<string?> ShowAsync(Control owner, string title, string initial, string prompt)
    {
        var window = OwnerWindow(owner);
        if (window is null) return null;
        string? result = null;
        var dialog = new Window
        {
            Title = UiText.T(title), Width = 480, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = MainWindow.SidebarBrush, CanResize = false
        };
        var input = new TextBox { Text = initial, Margin = new Thickness(24, 4, 24, 10), Background = MainWindow.SurfaceBrush, Foreground = MainWindow.TextPrimary };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 24, 18)
        };
        var accept = new Button
        {
            Content = UiText.T("Tamam"), Padding = new Thickness(18, 8),
            Background = MainWindow.AccentBrush, Foreground = MainWindow.CanvasBrush,
            IsDefault = true
        };
        accept.Click += (_, _) => { result = input.Text; dialog.Close(); };
        var cancel = new Button
        {
            Content = UiText.T("Vazgeç"), Padding = new Thickness(18, 8),
            Background = MainWindow.SurfaceBrush, Foreground = MainWindow.TextPrimary,
            BorderBrush = MainWindow.StrokeBrush, BorderThickness = new Thickness(1)
        };
        cancel.Click += (_, _) => dialog.Close();
        buttons.Children.Add(cancel);
        buttons.Children.Add(accept);
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = UiText.T(prompt), Margin = new Thickness(24, 18, 24, 6), Foreground = MainWindow.TextMuted, FontSize = 12
        });
        panel.Children.Add(input);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { result = input.Text; dialog.Close(); e.Handled = true; } };
        // Standard modal keyboard contract: Enter accepts, Escape cancels.
        dialog.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter) { result = input.Text; dialog.Close(); e.Handled = true; }
            else if (e.Key == Key.Escape) { dialog.Close(); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        input.SelectAll();
        await dialog.ShowDialog(window);
        window.Focus();
        return result;
    }

    private static Window? OwnerWindow(Control owner) => owner as Window
        ?? TopLevel.GetTopLevel(owner) as Window
        ?? owner.GetVisualAncestors().OfType<Window>().FirstOrDefault();
}
