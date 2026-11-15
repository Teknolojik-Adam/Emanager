using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Media;
using Emanager.Core.Storage;

namespace Emanager.App;

internal sealed class AndroidAndUsbWindow : Window
{
    private readonly MainWindow owner;
    private readonly TransferCenter transfers;
    private readonly ComboBox phones = new() { MinWidth = 320 };
    private readonly TextBlock phoneStatus = new() { Foreground = MainWindow.TextMuted, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock remotePath = new() { Foreground = MainWindow.TextMuted, TextWrapping = TextWrapping.Wrap };
    private readonly ListBox remoteList = new() { MinHeight = 190, Height = 240 };
    private readonly TextBox localFolder = new() { MinWidth = 260 };
    private readonly StackPanel usbRows = new() { Spacing = 6 };
    private IReadOnlyList<AndroidLocation> locations = [];
    private IReadOnlyList<AndroidFile> entries = [];
    private AndroidLocation? selectedDevice;
    private string? currentUri;
    private bool loading;

    public AndroidAndUsbWindow(MainWindow owner, TransferCenter transfers)
    {
        this.owner = owner;
        this.transfers = transfers;
        Title = "Emanager — Android ve USB / Android & USB";
        Width = 970; Height = 790; MinWidth = 700; MinHeight = 480;
        Background = MainWindow.CanvasBrush;
        localFolder.Text = owner.CurrentDirectory;
        var root = new StackPanel { Spacing = 13, Margin = new Thickness(20) };
        root.Children.Add(Heading("▣  Android MTP telefonlar"));
        root.Children.Add(phoneStatus);
        var choose = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        choose.Children.Add(phones);
        choose.Children.Add(Action("↻ Telefonları tara", () => _ = ScanPhonesAsync()));
        choose.Children.Add(Action("▶ Aç / Bağla", () => _ = OpenPhoneAsync()));
        root.Children.Add(choose);
        root.Children.Add(remotePath);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(Action("↑ Üst klasör", () => _ = GoParentAsync()));
        buttons.Children.Add(Action("▣ Klasörü aç", () => _ = OpenSelectedFolderAsync()));
        buttons.Children.Add(Action("↻ Yenile", () => _ = ListPhoneAsync()));
        root.Children.Add(buttons);
        remoteList.DoubleTapped += (_, _) => _ = OpenSelectedFolderAsync();
        root.Children.Add(new Border { Background = MainWindow.SurfaceBrush,
            BorderBrush = MainWindow.StrokeBrush, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(8), Child = remoteList });
        root.Children.Add(new TextBlock { Text = "PC hedef klasörü / PC destination:", Foreground = MainWindow.TextPrimary });
        root.Children.Add(localFolder);
        var transfersRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        transfersRow.Children.Add(Action("↓ Telefonda seçileni indir", () => _ = DownloadAsync()));
        transfersRow.Children.Add(Action("↑ PC dosyasını telefona gönder", () => _ = UploadAsync()));
        transfersRow.Children.Add(Action("⇅ Transferler", owner.ShowTransfers));
        root.Children.Add(transfersRow);
        root.Children.Add(new Border { Height = 1, Background = MainWindow.StrokeBrush, Margin = new Thickness(0, 8) });
        root.Children.Add(Heading("▣  USB bellekler / USB drives"));
        root.Children.Add(new TextBlock { Text = "USB diskler güvenli disk politikasıyla bağlanır. Çıkarmadan önce tüm aktarımı bitirin.",
            TextWrapping = TextWrapping.Wrap, Foreground = MainWindow.TextMuted });
        root.Children.Add(Action("↻ USB yenile", () => _ = RefreshUsbAsync()));
        root.Children.Add(usbRows);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Opened += (_, _) => { _ = ScanPhonesAsync(); _ = RefreshUsbAsync(); };
    }

    private static TextBlock Heading(string label) => new()
    {
        Text = label, FontWeight = FontWeight.Bold, FontSize = 19, Foreground = MainWindow.TextPrimary
    };

    private static Button Action(string title, Action act)
    {
        var button = new Button { Content = title, Padding = new Thickness(11, 7) };
        button.Click += (_, _) => act();
        return button;
    }

    private async Task ScanPhonesAsync()
    {
        if (loading) return;
        loading = true;
        try
        {
            if (!GioMtpSupport.HasBackend)
            {
                phoneStatus.Text = "Android MTP desteği yüklü değil. Fedora: sudo dnf install gvfs-mtp  ·  " +
                    "Debian/Pardus: sudo apt install gvfs-backends. Telefonu USB dosya aktarımı moduna alın.";
                phones.ItemsSource = Array.Empty<string>();
                remoteList.ItemsSource = Array.Empty<string>();
                locations = []; selectedDevice = null; currentUri = null; entries = [];
                return;
            }
            phoneStatus.Text = "Android telefonlar taranıyor…";
            locations = await GioMtpSupport.DiscoverAsync();
            phones.ItemsSource = locations.Select(x => $"{x.Name} — {x.Uri}").ToArray();
            phones.SelectedIndex = locations.Count > 0 ? 0 : -1;
            phoneStatus.Text = locations.Count == 0
                ? "MTP telefonu bulunamadı. USB kablosunu, kilit açmayı ve telefonda 'Dosya aktarımı' modunu kontrol edin."
                : $"{locations.Count} MTP konumu bulundu. Aç / Bağla düğmesini kullanın.";
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
        { phoneStatus.Text = "Telefon taraması başarısız: " + ex.Message; }
        finally { loading = false; }
    }

    private async Task OpenPhoneAsync()
    {
        if (phones.SelectedIndex < 0 || phones.SelectedIndex >= locations.Count) return;
        selectedDevice = locations[phones.SelectedIndex];
        currentUri = selectedDevice.Uri;
        try
        {
            phoneStatus.Text = "Telefon açılıyor…";
            if (!selectedDevice.IsMounted) await GioMtpSupport.MountAsync(currentUri);
            await ListPhoneAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { phoneStatus.Text = "Android bağlantısı başarısız: " + ex.Message; }
    }

    private async Task ListPhoneAsync()
    {
        if (selectedDevice is null || currentUri is null) return;
        try
        {
            phoneStatus.Text = "Telefon dosyaları okunuyor…";
            entries = await GioMtpSupport.ListAsync(currentUri);
            remotePath.Text = "Telefon: " + currentUri;
            remoteList.ItemsSource = entries.Select(x => (x.IsDirectory ? "📁 " : "📄 ") + x.Name +
                (x.Size is { } size && !x.IsDirectory ? $" ({size / 1024d / 1024d:0.0} MiB)" : "")).ToArray();
            phoneStatus.Text = $"{entries.Count} öğe · Telefon hazır";
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        { phoneStatus.Text = "MTP klasörü açılamadı: " + ex.Message; }
    }

    private async Task OpenSelectedFolderAsync()
    {
        if (remoteList.SelectedIndex < 0 || remoteList.SelectedIndex >= entries.Count) return;
        var item = entries[remoteList.SelectedIndex];
        if (!item.IsDirectory) { phoneStatus.Text = "Dosyayı indirmek için İndir düğmesini kullanın."; return; }
        currentUri = item.Uri.TrimEnd('/') + "/";
        await ListPhoneAsync();
    }

    private async Task GoParentAsync()
    {
        if (selectedDevice is null || currentUri is null) return;
        var root = selectedDevice.Uri.TrimEnd('/') + "/";
        if (currentUri == root) return;
        var path = currentUri.TrimEnd('/');
        var slash = path.LastIndexOf('/');
        currentUri = slash < root.Length ? root : path[..slash] + "/";
        await ListPhoneAsync();
    }

    private async Task DownloadAsync()
    {
        if (remoteList.SelectedIndex < 0 || remoteList.SelectedIndex >= entries.Count) return;
        var entry = entries[remoteList.SelectedIndex];
        if (entry.IsDirectory) { phoneStatus.Text = "Önce bir dosya seçin (klasör toplu aktarımı bu sürümde yok)."; return; }
        var targetDir = localFolder.Text?.Trim() ?? "";
        if (!Directory.Exists(targetDir)) { phoneStatus.Text = "PC hedef klasörü bulunamadı."; return; }
        if (!GioMtpSupport.IsSafeLeafName(entry.Name))
        { phoneStatus.Text = "Telefondan gelen dosya adı güvenli değil; aktarım reddedildi."; return; }
        var targetFile = Path.Combine(targetDir, entry.Name);
        if (File.Exists(targetFile) || Directory.Exists(targetFile))
        { phoneStatus.Text = "Hedefte aynı isimde dosya var. Mevcut dosya korunuyor: " + targetFile; return; }
        var tempFile = Path.Combine(targetDir, ".emanager-mtp-" + Guid.NewGuid().ToString("N") + ".part");
        var (id, token) = transfers.RegisterExternal("Android → PC", entry.Uri, targetFile, entry.Size ?? 0);
        owner.ShowTransfers();
        _ = Task.Run(async () =>
        {
            try
            {
                transfers.ExternalStarted(id);
                await GioMtpSupport.CopyAsync(entry.Uri, tempFile, p => transfers.ExternalProgress(id, p), token);
                token.ThrowIfCancellationRequested();
                File.Move(tempFile, targetFile, false);
                transfers.FinishExternal(id);
            }
            catch (OperationCanceledException) { transfers.FinishExternal(id, cancelled: true); }
            catch (Exception ex) { transfers.FinishExternal(id, error: token.IsCancellationRequested ? null : ex,
                cancelled: token.IsCancellationRequested); }
            finally { try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch (IOException) { } }
        });
    }

    private async Task UploadAsync()
    {
        if (currentUri is null || selectedDevice is null) return;
        var selected = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Telefonuna gönderilecek dosyayı seç",
            AllowMultiple = false
        });
        var file = selected.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(file)) return;
        if (!File.Exists(file) || File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
        { phoneStatus.Text = "Yalnızca normal yerel dosyalar gönderilebilir."; return; }
        var target = GioMtpSupport.JoinUri(currentUri, Path.GetFileName(file));
        try
        {
            if (await GioMtpSupport.ExistsAsync(target))
            { phoneStatus.Text = "Telefonda aynı dosya adı var. Güvenlik için üzerine yazılmadı."; return; }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        { phoneStatus.Text = "Telefon hedefi kontrol edilemedi: " + ex.Message; return; }
        var (id, token) = transfers.RegisterExternal("PC → Android", file, target, new FileInfo(file).Length);
        owner.ShowTransfers();
        _ = Task.Run(async () =>
        {
            try
            {
                transfers.ExternalStarted(id);
                await GioMtpSupport.CopyAsync(file, target, p => transfers.ExternalProgress(id, p), token);
                token.ThrowIfCancellationRequested();
                transfers.FinishExternal(id);
            }
            catch (OperationCanceledException) { transfers.FinishExternal(id, cancelled: true); }
            catch (Exception ex) { transfers.FinishExternal(id, error: token.IsCancellationRequested ? null : ex,
                cancelled: token.IsCancellationRequested); }
        });
    }

    private async Task RefreshUsbAsync()
    {
        usbRows.Children.Clear();
        usbRows.Children.Add(new TextBlock { Text = "USB sürücüler taranıyor…", Foreground = MainWindow.TextMuted });
        await owner.RefreshUsbSnapshotAsync();
        usbRows.Children.Clear();
        var nodes = owner.RemovableStorageNodes();
        if (nodes.Count == 0)
        {
            usbRows.Children.Add(new TextBlock { Text = "Takılı USB bellek algılanmadı.", Foreground = MainWindow.TextMuted });
            return;
        }
        foreach (var node in nodes)
        {
            var group = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
            var name = node.Label ?? node.Model ?? node.DevicePath ?? node.KernelName;
            group.Children.Add(new TextBlock { Text = $"▣ {name} · {node.SizeBytes / 1024d / 1024d / 1024d:0.0} GiB",
                Foreground = MainWindow.TextPrimary, VerticalAlignment = VerticalAlignment.Center, Width = 420,
                TextTrimming = TextTrimming.CharacterEllipsis });
            group.Children.Add(Action("Aç / Bağla", () => _ = owner.OpenRemovableAsync(node.StableId)));
            group.Children.Add(Action("Güvenli çıkar", () => _ = owner.EjectRemovableAsync(node.StableId)));
            usbRows.Children.Add(group);
        }
    }
}
