using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Emanager.App.Ui;
using Emanager.Core.Storage;

namespace Emanager.App;

/// <summary>Small injectable boundary around GVfs so the embedded view can be exercised without a phone.</summary>
public interface IAndroidMtpService
{
    bool HasBackend { get; }
    Task<IReadOnlyList<AndroidLocation>> DiscoverAsync(CancellationToken cancellationToken = default);
    Task MountAsync(string uri, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AndroidFile>> ListAsync(string uri, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(string uri, CancellationToken cancellationToken = default);
    Task CopyAsync(string source, string destination, Action<double>? progress, CancellationToken cancellationToken = default);
}

internal sealed class GioAndroidMtpService : IAndroidMtpService
{
    public bool HasBackend => GioMtpSupport.HasBackend;
    public Task<IReadOnlyList<AndroidLocation>> DiscoverAsync(CancellationToken cancellationToken = default) => GioMtpSupport.DiscoverAsync(cancellationToken);
    public Task MountAsync(string uri, CancellationToken cancellationToken = default) => GioMtpSupport.MountAsync(uri, cancellationToken);
    public Task<IReadOnlyList<AndroidFile>> ListAsync(string uri, CancellationToken cancellationToken = default) => GioMtpSupport.ListAsync(uri, cancellationToken);
    public Task<bool> ExistsAsync(string uri, CancellationToken cancellationToken = default) => GioMtpSupport.ExistsAsync(uri, cancellationToken);
    public Task CopyAsync(string source, string destination, Action<double>? progress, CancellationToken cancellationToken = default) => GioMtpSupport.CopyAsync(source, destination, progress, cancellationToken);
}

/// <summary>An MTP browser rendered directly in MainWindow's pane area.</summary>
internal sealed class AndroidMtpBrowser : UserControl
{
    private readonly MainWindow owner;
    private readonly TransferCenter transfers;
    private readonly IAndroidMtpService service;
    private readonly ComboBox devices = new() { MinWidth = 180, MinHeight = 44 };
    private readonly TextBlock state = new() { TextWrapping = TextWrapping.Wrap, Foreground = MainWindow.TextMuted };
    private readonly TextBlock path = new() { Foreground = MainWindow.TextMuted };
    private readonly ListBox listing = new() { MinHeight = 180, Background = Brushes.Transparent };
    private readonly TextBox destination = new() { MinWidth = 180 };
    private readonly TextBox searchBox = new() { Width = 190, Watermark = "Telefonda ara" };
    private readonly Grid shell = new();
    private readonly Grid body = new();
    private readonly Border leftPanel = new();
    private readonly Border rightPanel = new();
    private readonly Border leftDrawer = new();
    private readonly Border rightDrawer = new();
    private readonly Button leftToggle = new();
    private readonly Button rightToggle = new();
    private readonly StackPanel viewToggle = new() { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4) };
    private readonly Button openButton = new();
    private readonly Button parentButton = new();
    private readonly MtpSelectionActionBar selectionActionBar = new();
    private readonly MtpConnectionStatus connectionStatus;
    private readonly MtpTransferQueueView desktopQueue;
    private readonly MtpTransferQueueView drawerQueue;
    private readonly TextBlock breadcrumb = new();
    private readonly TextBlock listingMessage = new() { IsVisible = false, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16), Foreground = MainWindow.TextMuted };
    private readonly StackPanel drawerDeviceList = new() { Spacing = 6 };
    private bool leftDrawerOpen;
    private bool rightDrawerOpen;
    private IReadOnlyList<AndroidLocation> locations = [];
    private IReadOnlyList<AndroidFile> entries = [];
    private IReadOnlyList<AndroidFile> visibleEntries = [];
    private bool detailedView;
    private AndroidLocation? selectedDevice;
    private string? currentUri;
    private bool busy;
    private long selectionGeneration;
    private CancellationTokenSource? selectedDeviceLifetime;
    internal string? SelectedDeviceUri => selectedDevice?.Uri;
    internal string StatusText => state.Text ?? "";

    public AndroidMtpBrowser(MainWindow owner, TransferCenter transfers, IAndroidMtpService service)
    {
        this.owner = owner; this.transfers = transfers; this.service = service;
        connectionStatus = new MtpConnectionStatus(transfers, () => currentUri is not null && selectedDevice is not null);
        desktopQueue = new MtpTransferQueueView(transfers);
        drawerQueue = new MtpTransferQueueView(transfers);
        Background = MtpUiTokens.Canvas;
        destination.Text = owner.CurrentDirectory;
        BuildWorkspace();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        ApplyResponsiveLayout();
    }

    private void BuildWorkspace()
    {
        shell.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        shell.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
        shell.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        shell.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var toolbar = new Grid { Background = MtpUiTokens.Surface, ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(1, GridUnitType.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto) } };
        toolbar.Height = 56;
        leftToggle.Content = "☰ Cihazlar"; leftToggle.Margin = new Thickness(12, 9); leftToggle.Click += (_, _) => ToggleDrawer(true);
        rightToggle.Content = "☷ Kuyruk"; rightToggle.Margin = new Thickness(12, 9); rightToggle.Click += (_, _) => ToggleDrawer(false);
        Grid.SetColumn(leftToggle, 0); Grid.SetColumn(rightToggle, 4);
        var crumbs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = MtpUiTokens.Space8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(MtpUiTokens.Space12, MtpUiTokens.Space4) };
        crumbs.Children.Add(new TextBlock { Text = "Android MTP", FontWeight = FontWeight.Bold, Foreground = MainWindow.TextPrimary, VerticalAlignment = VerticalAlignment.Center });
        crumbs.Children.Add(new TextBlock { Text = "›", Foreground = MainWindow.TextMuted, VerticalAlignment = VerticalAlignment.Center });
        breadcrumb.Text = "Cihaz seçin"; breadcrumb.Foreground = MainWindow.TextMuted; breadcrumb.VerticalAlignment = VerticalAlignment.Center;
        crumbs.Children.Add(breadcrumb); Grid.SetColumn(crumbs, 1);
        searchBox.Margin = new Thickness(8); searchBox.VerticalAlignment = VerticalAlignment.Center;
        searchBox.TextChanged += (_, _) => RenderListing(); Grid.SetColumn(searchBox, 2);
        var listView = Action("☷ Liste", () => { detailedView = false; RenderListing(); });
        var detailView = Action("☰ Ayrıntı", () => { detailedView = true; RenderListing(); });
        viewToggle.Children.Add(listView); viewToggle.Children.Add(detailView); Grid.SetColumn(viewToggle, 3);
        toolbar.Children.Add(leftToggle); toolbar.Children.Add(crumbs); toolbar.Children.Add(searchBox); toolbar.Children.Add(viewToggle); toolbar.Children.Add(rightToggle);
        Grid.SetRow(toolbar, 0); shell.Children.Add(toolbar);
        body.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(280)));
        body.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 420 });
        body.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(340)));
        leftPanel.Child = BuildDevicePanel(); leftPanel.Background = MainWindow.SidebarBrush;
        rightPanel.Child = BuildQueuePanel(); rightPanel.Background = MainWindow.SidebarBrush;
        Grid.SetColumn(leftPanel, 0); body.Children.Add(leftPanel);
        var center = BuildFileBrowser(); Grid.SetColumn(center, 1); body.Children.Add(center);
        Grid.SetColumn(rightPanel, 2); body.Children.Add(rightPanel);
        Grid.SetRow(body, 1); shell.Children.Add(body);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 8) };
        parentButton.Content = "↑ Üst klasör"; parentButton.Click += (_, _) => _ = GoParentAsync();
        openButton.Content = "Klasörü aç"; openButton.Click += (_, _) => _ = OpenFolderAsync();
        actions.Children.Add(parentButton); actions.Children.Add(openButton); actions.Children.Add(selectionActionBar);
        selectionActionBar.DownloadRequested += (_, _) => _ = DownloadAsync();
        selectionActionBar.UploadRequested += (_, _) => _ = UploadAsync();
        var actionBar = new Border { Background = MainWindow.SurfaceBrush, BorderBrush = MainWindow.StrokeBrush, BorderThickness = new Thickness(0, 1), Child = actions };
        Grid.SetRow(actionBar, 2); shell.Children.Add(actionBar);
        var statusLayout = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        statusLayout.Children.Add(connectionStatus); statusLayout.Children.Add(state);
        var statusBar = new Border { Background = MainWindow.CanvasBrush, Padding = new Thickness(14, 6), Child = statusLayout };
        Grid.SetRow(statusBar, 3); shell.Children.Add(statusBar);
        leftDrawer.Child = BuildDeviceDrawer(); leftDrawer.Background = MainWindow.SidebarBrush;
        rightDrawer.Child = BuildQueueDrawer(); rightDrawer.Background = MainWindow.SidebarBrush;
        leftDrawer.HorizontalAlignment = HorizontalAlignment.Left;
        rightDrawer.HorizontalAlignment = HorizontalAlignment.Right;
        leftDrawer.IsVisible = rightDrawer.IsVisible = false;
        var overlays = new Grid { IsHitTestVisible = false };
        leftDrawer.IsHitTestVisible = rightDrawer.IsHitTestVisible = true;
        overlays.Children.Add(leftDrawer); overlays.Children.Add(rightDrawer);
        Grid.SetRow(overlays, 1); shell.Children.Add(overlays);
        Content = shell;
    }

    private Control BuildDevicePanel()
    {
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "Cihazlar ve oturum", FontSize = 17, FontWeight = FontWeight.Bold, Foreground = MainWindow.TextPrimary });
        panel.Children.Add(new TextBlock { Text = "Telefonu bağlayıp kilidini açın ve USB modundan Dosya Aktarımı'nı seçin.", TextWrapping = TextWrapping.Wrap, Foreground = MainWindow.TextMuted });
        devices.HorizontalAlignment = HorizontalAlignment.Stretch; panel.Children.Add(devices);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var openDeviceButton = Action("Aç / Bağla", () => _ = OpenDeviceAsync());
        openDeviceButton.IsEnabled = false;
        devices.SelectionChanged += (_, _) => openDeviceButton.IsEnabled = devices.SelectedIndex >= 0;
        row.Children.Add(Action("↻ Tara", () => _ = owner.RefreshMtpDevicesAsync())); row.Children.Add(openDeviceButton); panel.Children.Add(row);
        panel.Children.Add(new Border { Height = 1, Background = MainWindow.StrokeBrush, Margin = new Thickness(0, 4) });
        panel.Children.Add(new TextBlock { Text = "Oturum", FontWeight = FontWeight.SemiBold, Foreground = MainWindow.TextPrimary });
        panel.Children.Add(new TextBlock { Text = "Yalnızca otomatik algılanan cihazlar burada görünür.", TextWrapping = TextWrapping.Wrap, Foreground = MainWindow.TextMuted });
        return panel;
    }

    private Control BuildFileBrowser()
    {
        var content = new Grid { Margin = new Thickness(18), RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(1, GridUnitType.Star) } };
        var head = new StackPanel { Spacing = 5, Margin = new Thickness(0, 0, 0, 12) };
        head.Children.Add(new TextBlock { Text = "Telefon dosyaları", FontSize = 21, FontWeight = FontWeight.Bold, Foreground = MainWindow.TextPrimary }); head.Children.Add(path);
        Grid.SetRow(head, 0); content.Children.Add(head);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 10) };
        controls.Children.Add(Action("↻ Yenile", () => _ = RefreshListingAsync())); controls.Children.Add(Action("Klasörü aç", () => _ = OpenFolderAsync()));
        Grid.SetRow(controls, 1); content.Children.Add(controls);
        listing.DoubleTapped += (_, _) => _ = OpenFolderAsync();
        listing.SelectionChanged += (_, _) =>
        {
            var valid = listing.SelectedIndex >= 0 && listing.SelectedIndex < visibleEntries.Count;
            selectionActionBar.SetSelection(valid ? 1 : 0, canDownload: valid && !visibleEntries[listing.SelectedIndex].IsDirectory,
                canUpload: currentUri is not null && selectedDevice is not null);
        };
        string[] TableColumns = ["selection", "name", "type", "size", "date"];
        var header = new Grid { Height = 40, ColumnDefinitions = { new ColumnDefinition(new GridLength(34)), new ColumnDefinition(1, GridUnitType.Star), new ColumnDefinition(new GridLength(92)), new ColumnDefinition(new GridLength(92)), new ColumnDefinition(new GridLength(150)) } };
        foreach (var pair in new[] { ("", 0), ("Ad", 1), ("Tür", 2), ("Boyut", 3), ("Tarih", 4) }) { var label = new TextBlock { Text = pair.Item1, Foreground = MtpUiTokens.Muted, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(label, pair.Item2); header.Children.Add(label); }
        var table = new Grid { RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(1, GridUnitType.Star) } };
        Grid.SetRow(header, 0); table.Children.Add(header); Grid.SetRow(listing, 1); table.Children.Add(listing);
        var listContent = new StackPanel { Spacing = MtpUiTokens.Space8 };
        listContent.Children.Add(table); listContent.Children.Add(listingMessage);
        var listFrame = new Border { Background = MtpUiTokens.Surface, BorderBrush = MtpUiTokens.Stroke, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(MtpUiTokens.CardRadius), Padding = new Thickness(8), Child = listContent };
        Grid.SetRow(listFrame, 2); content.Children.Add(listFrame); return content;
    }

    private Control BuildQueuePanel()
    {
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "Aktarım kuyruğu", FontSize = 17, FontWeight = FontWeight.Bold, Foreground = MainWindow.TextPrimary });
        panel.Children.Add(new TextBlock { Text = "Aktarımlar ortak kuyrukta çalışır. Mevcut dosyalar korunur.", TextWrapping = TextWrapping.Wrap, Foreground = MainWindow.TextMuted });
        panel.Children.Add(Action("⇅ Aktarım merkezini aç", owner.ShowTransfers));
        panel.Children.Add(desktopQueue);
        panel.Children.Add(new Border { Height = 1, Background = MainWindow.StrokeBrush, Margin = new Thickness(0, 4) });
        panel.Children.Add(new TextBlock { Text = "Bilgisayar hedefi", FontWeight = FontWeight.SemiBold, Foreground = MainWindow.TextPrimary });
        panel.Children.Add(destination); panel.Children.Add(Action("Hedef klasörü seç", () => _ = ChooseDestinationAsync())); panel.Children.Add(Action("↑ Dosya gönder", () => _ = UploadAsync())); return panel;
    }

    private Control BuildDeviceDrawer()
    {
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "Cihazlar ve oturum", FontSize = 17, FontWeight = FontWeight.Bold, Foreground = MainWindow.TextPrimary });
        panel.Children.Add(Action("↻ Tara", () => _ = owner.RefreshMtpDevicesAsync()));
        panel.Children.Add(drawerDeviceList);
        panel.Children.Add(new TextBlock { Text = "Telefonu bağlayın, kilidi açın ve Dosya Aktarımı modunu seçin.", TextWrapping = TextWrapping.Wrap, Foreground = MainWindow.TextMuted });
        return panel;
    }

    private Control BuildQueueDrawer()
    {
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "Aktarım kuyruğu", FontSize = 17, FontWeight = FontWeight.Bold, Foreground = MainWindow.TextPrimary });
        panel.Children.Add(Action("⇅ Aktarım merkezini aç", owner.ShowTransfers));
        panel.Children.Add(drawerQueue);
        panel.Children.Add(Action("↑ Dosya gönder", () => _ = UploadAsync()));
        panel.Children.Add(new TextBlock { Text = "Bilgisayar hedefi", Foreground = MainWindow.TextPrimary });
        panel.Children.Add(new TextBlock { Text = destination.Text, TextWrapping = TextWrapping.Wrap, Foreground = MainWindow.TextMuted });
        panel.Children.Add(Action("Hedef klasörü seç", () => _ = ChooseDestinationAsync()));
        return panel;
    }

    private async Task ChooseDestinationAsync()
    {
        var folders = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose PC destination folder", AllowMultiple = false });
        var folder = folders.FirstOrDefault()?.TryGetLocalPath(); if (!string.IsNullOrWhiteSpace(folder)) destination.Text = folder;
    }

    private void ApplyResponsiveLayout()
    {
        var width = Bounds.Width; var narrow = width < 720; var compact = width < 980;
        leftDrawer.Width = Math.Min(280, Math.Max(240, width * .86));
        rightDrawer.Width = Math.Min(340, Math.Max(260, width * .88));
        body.ColumnDefinitions[0].Width = new GridLength(narrow ? 0 : 280);
        body.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star); body.ColumnDefinitions[1].MinWidth = narrow ? 0 : 420;
        body.ColumnDefinitions[2].Width = new GridLength(compact ? 0 : 340);
        leftPanel.IsVisible = !narrow; rightPanel.IsVisible = !compact;
        leftToggle.IsVisible = narrow; rightToggle.IsVisible = compact;
        searchBox.IsVisible = !narrow; viewToggle.IsVisible = !narrow;
        leftDrawer.IsVisible = narrow && leftDrawerOpen; rightDrawer.IsVisible = compact && rightDrawerOpen;
        parentButton.IsVisible = openButton.IsVisible = !narrow;
    }

    private void ToggleDrawer(bool left)
    {
        if (left) { leftDrawerOpen = !leftDrawerOpen; if (leftDrawerOpen) rightDrawerOpen = false; }
        else { rightDrawerOpen = !rightDrawerOpen; if (rightDrawerOpen) leftDrawerOpen = false; }
        ApplyResponsiveLayout();
    }

    private static Button Action(string label, Action action) { var b = new Button { Content = label, Padding = new Thickness(11, 7), MinHeight = MtpUiTokens.MinimumHeight, CornerRadius = new CornerRadius(MtpUiTokens.ButtonRadius) }; b.Click += (_, _) => action(); return b; }
    private void SetState(string message) => state.Text = message;

    internal void UpdateDevices(IReadOnlyList<AndroidLocation> snapshot)
    {
        var previous = selectedDevice?.Uri;
        var previousName = selectedDevice?.Name;
        locations = snapshot;
        devices.ItemsSource = locations.Select(x => $"{x.Name} — {x.Uri}").ToArray();
        drawerDeviceList.Children.Clear();
        foreach (var location in locations)
        {
            var deviceRow = Action("● " + location.Name + " · USB · MTP", () => _ = OpenLocationAsync(location.Uri));
            deviceRow.Height = 56;
            drawerDeviceList.Children.Add(deviceRow);
        }
        if (locations.Count == 0) drawerDeviceList.Children.Add(new TextBlock { Text = "Bağlı telefon yok. Telefonu takın, kilidi açın ve Dosya Aktarımı'nı seçin.", TextWrapping = TextWrapping.Wrap, Foreground = MainWindow.TextMuted });
        var index = previous is null ? -1 : locations.ToList().FindIndex(x => x.Uri == previous);
        if (index >= 0)
        {
            if (locations[index].Name != previousName)
            {
                InvalidateSelectedDevice();
                selectedDevice = null; currentUri = null; entries = [];
                entries = []; visibleEntries = []; listing.ItemsSource = Array.Empty<string>(); path.Text = ""; breadcrumb.Text = "Cihaz seçin";
                devices.SelectedIndex = index;
                SetState("Telefon değişti. Devam etmek için yeniden seçin.");
                return;
            }
            selectedDevice = locations[index];
            devices.SelectedIndex = index;
            return;
        }
        if (previous is not null)
        {
            InvalidateSelectedDevice();
            selectedDevice = null; currentUri = null; entries = [];
            entries = []; visibleEntries = []; listing.ItemsSource = Array.Empty<string>(); path.Text = ""; breadcrumb.Text = "Cihaz seçin";
            SetState("Telefon bağlantısı kesildi (disconnected). Yeniden bağlayıp tarayın.");
        }
        else if (locations.Count == 0)
        {
            devices.SelectedIndex = -1;
            if (!service.HasBackend) SetState("Android MTP desteği kurulu değil. gvfs-mtp ve gvfs-backends kurup telefonu Dosya Aktarımı modunda yeniden bağlayın.");
            else SetState("MTP telefonu bulunamadı. Kabloyu, kilidi ve Dosya Aktarımı modunu kontrol edin.");
        }
        else devices.SelectedIndex = 0;
    }

    internal async Task OpenLocationAsync(string uri)
    {
        var index = locations.ToList().FindIndex(x => x.Uri == uri);
        if (index < 0) return;
        devices.SelectedIndex = index;
        await OpenDeviceAsync();
    }

    public async Task RefreshDevicesAsync()
    {
        if (busy) return;
        busy = true;
        try
        {
            SetState("Telefonlar taranıyor…");
            if (!service.HasBackend) { locations = []; devices.ItemsSource = Array.Empty<string>(); SetState("Android MTP desteği kurulu değil. gvfs-mtp ve gvfs-backends kurun."); return; }
            UpdateDevices(await service.DiscoverAsync());
            SetState(locations.Count == 0 ? "MTP telefonu bulunamadı. Telefonu bağlayıp Dosya Aktarımı modunu seçin." : $"{locations.Count} MTP konumu bulundu. Açmak için seçin.");
        }
        catch (Exception ex) { SetState("Telefon taraması başarısız: " + ex.Message); }
        finally { busy = false; }
    }

    private async Task OpenDeviceAsync()
    {
        if (devices.SelectedIndex < 0 || devices.SelectedIndex >= locations.Count) return;
        InvalidateSelectedDevice();
        selectedDeviceLifetime = new CancellationTokenSource();
        var generation = selectionGeneration;
        var cancellationToken = selectedDeviceLifetime.Token;
        selectedDevice = locations[devices.SelectedIndex];
        var deviceUri = selectedDevice.Uri;
        currentUri = deviceUri;
        try
        {
            SetState("Telefon açılıyor…");
            if (!selectedDevice.IsMounted) await service.MountAsync(deviceUri, cancellationToken);
            if (!IsCurrentSelection(generation, deviceUri)) return;
            await LoadListingAsync(deviceUri, generation, cancellationToken);
        }
        catch (OperationCanceledException) when (!IsCurrentSelection(generation, deviceUri)) { }
        catch (Exception ex) { if (IsCurrentSelection(generation, deviceUri)) SetState("Telefon bağlantısı başarısız: " + ex.Message); }
    }

    private async Task RefreshListingAsync()
    {
        if (currentUri is null) { SetState("Dosyalara göz atmak için otomatik bulunan telefonu seçin."); return; }
        await LoadListingAsync(currentUri, selectionGeneration, selectedDeviceLifetime?.Token ?? CancellationToken.None);
    }

    private async Task LoadListingAsync(string uri, long generation, CancellationToken cancellationToken)
    {
        try
        {
            SetState("Telefon klasörü okunuyor…");
            var result = await service.ListAsync(uri, cancellationToken);
            if (!IsCurrentSelection(generation, selectedDevice?.Uri) || currentUri != uri) return;
            entries = result;
            path.Text = "Telefon: " + uri;
            breadcrumb.Text = selectedDevice?.Name + " › " + uri.TrimEnd('/').Split('/').LastOrDefault();
            RenderListing();
            SetState(entries.Count == 0 ? "This folder is empty." : $"{entries.Count} item(s) · Phone ready");
        }
        catch (OperationCanceledException) when (!IsCurrentSelection(generation, selectedDevice?.Uri)) { }
        catch (Exception ex) { if (IsCurrentSelection(generation, selectedDevice?.Uri)) SetState("MTP klasörü okunamadı: " + ex.Message); }
    }

    private bool IsCurrentSelection(long generation, string? uri) =>
        generation == selectionGeneration && uri is not null && selectedDevice?.Uri == uri;

    private void RenderListing()
    {
        var query = searchBox.Text?.Trim();
        visibleEntries = string.IsNullOrEmpty(query) ? entries : entries.Where(x => x.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        listingMessage.IsVisible = visibleEntries.Count == 0;
        listingMessage.Text = entries.Count == 0
            ? "Bu klasör boş. Telefonda dosya bulunamadı."
            : "Aramanızla eşleşen dosya yok. Arama metnini değiştirin.";
        listing.ItemsSource = visibleEntries.Select(x =>
        {
            var type = x.IsDirectory ? "Klasör" : Path.GetExtension(x.Name).TrimStart('.').ToUpperInvariant();
            var size = x.IsDirectory || x.Size is not { } n ? "—" : detailedView ? $"{n:N0} B" : $"{n / 1048576d:0.0} MiB";
            var row = new Grid { Height = 40, ColumnDefinitions = { new ColumnDefinition(new GridLength(34)), new ColumnDefinition(1, GridUnitType.Star), new ColumnDefinition(new GridLength(92)), new ColumnDefinition(new GridLength(92)), new ColumnDefinition(new GridLength(150)) } };
            foreach (var cell in new[] { (x.IsDirectory ? "📁  " + x.Name : "📄  " + x.Name, 1), (type, 2), (size, 3), ("—", 4) })
            {
                var text = new TextBlock { Text = cell.Item1, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Foreground = cell.Item2 == 1 ? MtpUiTokens.Text : MtpUiTokens.Muted };
                Grid.SetColumn(text, cell.Item2); row.Children.Add(text);
            }
            return new Border { CornerRadius = new CornerRadius(MtpUiTokens.RowRadius), Child = row };
        }).ToArray();
        selectionActionBar.SetSelection(0, canUpload: currentUri is not null && selectedDevice is not null);
    }

    private void InvalidateSelectedDevice()
    {
        selectionGeneration++;
        var previous = selectedDeviceLifetime;
        selectedDeviceLifetime = null;
        if (previous is null) return;
        try { previous.Cancel(); } catch (AggregateException) { }
        previous.Dispose();
    }

    private async Task OpenFolderAsync()
    {
        var i = listing.SelectedIndex;
        if (i < 0 || i >= visibleEntries.Count) return;
        var item = visibleEntries[i];
        if (!item.IsDirectory) { SetState("Select a file and choose Download."); return; }
        currentUri = item.Uri.TrimEnd('/') + "/"; await RefreshListingAsync();
    }

    private async Task GoParentAsync()
    {
        if (selectedDevice is null || currentUri is null) return;
        var root = selectedDevice.Uri.TrimEnd('/') + "/";
        if (currentUri == root) return;
        var p = currentUri.TrimEnd('/'); var slash = p.LastIndexOf('/');
        currentUri = slash < root.Length ? root : p[..slash] + "/";
        await RefreshListingAsync();
    }

    private async Task DownloadAsync()
    {
        var i = listing.SelectedIndex;
        if (i < 0 || i >= visibleEntries.Count) return;
        var entry = visibleEntries[i]; var folder = destination.Text?.Trim() ?? "";
        if (entry.IsDirectory) { SetState("Folder download is not available; choose a file."); return; }
        if (!Directory.Exists(folder)) { SetState("PC destination folder does not exist."); return; }
        if (!GioMtpSupport.IsSafeLeafName(entry.Name)) { SetState("Unsafe phone filename; transfer refused."); return; }
        var target = System.IO.Path.Combine(folder, entry.Name);
        if (File.Exists(target) || Directory.Exists(target)) { SetState("Target already exists; existing file preserved: " + target); return; }
        var temp = System.IO.Path.Combine(folder, ".emanager-mtp-" + Guid.NewGuid().ToString("N") + ".part");
        var (id, token) = transfers.RegisterExternal("Android → PC", entry.Uri, target, entry.Size ?? 0); owner.ShowTransfers();
        _ = Task.Run(async () => { try { transfers.ExternalStarted(id); await service.CopyAsync(entry.Uri, temp, p => transfers.ExternalProgress(id, p), token); token.ThrowIfCancellationRequested(); File.Move(temp, target, false); transfers.FinishExternal(id); } catch (OperationCanceledException) { transfers.FinishExternal(id, cancelled: true); } catch (Exception ex) { transfers.FinishExternal(id, error: token.IsCancellationRequested ? null : ex, cancelled: token.IsCancellationRequested); } finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } } });
    }

    private async Task UploadAsync()
    {
        if (currentUri is null || selectedDevice is null) return;
        var picked = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions { Title = "Choose a file to send to the phone", AllowMultiple = false });
        var file = picked.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(file)) return;
        if (!File.Exists(file) || File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) { SetState("Only ordinary local files can be uploaded."); return; }
        var target = GioMtpSupport.JoinUri(currentUri, System.IO.Path.GetFileName(file));
        try { if (await service.ExistsAsync(target)) { SetState("That filename already exists on the phone; it was not overwritten."); return; } }
        catch (Exception ex) { SetState("Could not check the phone destination: " + ex.Message); return; }
        var (id, token) = transfers.RegisterExternal("PC → Android", file, target, new FileInfo(file).Length); owner.ShowTransfers();
        _ = Task.Run(async () => { try { transfers.ExternalStarted(id); await service.CopyAsync(file, target, p => transfers.ExternalProgress(id, p), token); token.ThrowIfCancellationRequested(); transfers.FinishExternal(id); } catch (OperationCanceledException) { transfers.FinishExternal(id, cancelled: true); } catch (Exception ex) { transfers.FinishExternal(id, error: token.IsCancellationRequested ? null : ex, cancelled: token.IsCancellationRequested); } });
    }
}
