using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Emanager.App;

/// <summary>
/// Disk-usage analyzer for the pane area (Baobab/WinDirStat style, list form).
/// Scans one folder off the UI thread, lists its top-level entries by size with
/// proportional bars and lets the user drill into subfolders. The scan follows
/// no symbolic links and is fully cancellable.
/// </summary>
public sealed class DiskUseView : UserControl
{
    private readonly MainWindow win;
    private readonly TextBlock title = new() { FontSize = 30, FontWeight = FontWeight.Bold, Foreground = MainWindow.TextPrimary };
    private readonly TextBlock pathLabel = new() { FontSize = 12, Foreground = MainWindow.TextMuted, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock status = new() { FontSize = 12, Foreground = MainWindow.TextMuted, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel rows = new() { Spacing = 3 };
    private readonly ScrollViewer listScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ProgressBar busy = new() { IsIndeterminate = false, IsVisible = false, Height = 4, Minimum = 0, Maximum = 1, Value = 0 };
    private Button upButton = null!;
    private Button rescanButton = null!;
    private Button stopButton = null!;
    private Button openButton = null!;
    private CancellationTokenSource? cts;
    private bool busyScanning;
    private bool restartPending;
    private string scannedPath = "";

    public DiskUseView(MainWindow owner)
    {
        win = owner;
        Margin = new Thickness(24, 17, 24, 20);
        Content = Build();
        DetachedFromVisualTree += (_, _) => cts?.Cancel();
    }

    private Control Build()
    {
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*") };
        body.Children.Add(title);
        title.Text = UiText.T("▦  Disk Kullanımı");

        var head = new StackPanel { Spacing = 4 };
        Grid.SetRow(head, 1);
        head.Margin = new Thickness(0, 4, 0, 10);
        head.Children.Add(pathLabel);
        body.Children.Add(head);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        Grid.SetRow(actions, 2);
        actions.Margin = new Thickness(0, 0, 0, 8);
        upButton = win.MakeButton("↑  Üst klasör", GoUp, "Üst klasörün kullanımını ölç");
        rescanButton = win.MakeButton("⟳  Yeniden tara", () => { if (!string.IsNullOrEmpty(scannedPath)) StartScan(scannedPath); }, "Aramayı yinele");
        stopButton = win.MakeButton("⏹  Durdur", () => cts?.Cancel(), "Taramayı durdur");
        openButton = win.MakeButton("📂  Bu klasörde aç", OpenInPane, "Gezinme panelini bu klasöre döndür");
        foreach (var b in new[] { upButton, rescanButton, stopButton, openButton })
        {
            b.Background = MainWindow.SurfaceBrush;
            actions.Children.Add(b);
        }
        body.Children.Add(actions);

        Grid.SetRow(busy, 3);
        body.Children.Add(busy);

        var listHost = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(listHost, 4);
        var statusWrap = new Border { Child = status, Margin = new Thickness(0, 0, 0, 8) };
        status.Foreground = MainWindow.TextMuted;
        listHost.Children.Add(statusWrap);
        listScroll.Content = rows;
        Grid.SetRow(listScroll, 1);
        listHost.Children.Add(listScroll);
        body.Children.Add(listHost);
        return body;
    }

    public void StartScan(string path)
    {
        restartPending = true;
        if (busyScanning) return;
        _ = ScanLoopAsync(path);
    }

    private async Task ScanLoopAsync(string path)
    {
        busyScanning = true;
        SetButtons(false);
        try
        {
            do
            {
                restartPending = false;
                await ScanOnceAsync(path);
                path = scannedPath;
            } while (restartPending && !string.IsNullOrEmpty(path));
        }
        finally
        {
            busyScanning = false;
            SetButtons(true);
            busy.IsIndeterminate = false;
            busy.IsVisible = false;
        }
    }

    private void SetButtons(bool idle)
    {
        upButton.IsEnabled = idle;
        rescanButton.IsEnabled = idle;
        stopButton.IsEnabled = !idle;
        openButton.IsEnabled = idle;
    }

    private async Task ScanOnceAsync(string path)
    {
        cts?.Cancel();
        cts?.Dispose();
        cts = new CancellationTokenSource();
        var token = cts.Token;
        scannedPath = path;
        title.Text = UiText.T("▦  Disk Kullanımı");
        pathLabel.Text = path;
        rows.Children.Clear();
        busy.IsVisible = true;
        busy.IsIndeterminate = true;
        status.Text = UiText.T("Taranıyor…");
        IDisposable? ticker = null;
        var started = DateTime.UtcNow;
        ticker = DispatcherTimer.Run(() =>
        {
            status.Text = UiText.F("Taranıyor…  {0} sn", (int)(DateTime.UtcNow - started).TotalSeconds);
            return true;
        }, TimeSpan.FromSeconds(1));
        FolderMeasure.Breakdown? result = null;
        string? error = null;
        try
        {
            // Progress callbacks arrive on the UI thread via SynchronizationContext.
            var progress = new Progress<FolderMeasure.Progress>(p =>
                status.Text = UiText.F("Taranıyor…  {0} klasör  ·  {1}", p.DirectoriesScanned, FileSystem.HumanSize(p.BytesSoFar)));
            result = await Task.Run(() => FolderMeasure.TopLevelBreakdown(path, token, progress), token);
        }
        catch (OperationCanceledException)
        {
            status.Text = UiText.T("Tarama durduruldu.");
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = ex.Message;
        }
        finally
        {
            ticker?.Dispose();
        }
        if (error is not null)
        {
            status.Text = UiText.F("Klasör okunamadı: {0}", error);
            return;
        }
        if (result is null) return;
        RenderRows(result);
    }

    private void RenderRows(FolderMeasure.Breakdown data)
    {
        rows.Children.Clear();
        long max = Math.Max(data.Largest, 1);
        if (data.Items.Count == 0)
        {
            status.Text = UiText.T("Klasör boş — gösterilecek öğe yok.");
            return;
        }
        var summary = UiText.F(
            "Toplam {0}  ·  {1} klasör  ·  {2} dosya{3}{4}",
            FileSystem.HumanSize(data.TotalBytes), data.DirectoryCount, data.FileCount,
            data.LinkCount > 0 ? UiText.F("  ·  {0} bağlantı", data.LinkCount) : "",
            data.UnknownCount > 0 ? UiText.F("  ·  {0} öğe ölçülemedi", data.UnknownCount) : "");
        status.Text = summary;
        const int maxRows = 80;
        foreach (var item in data.Items.Take(maxRows))
            rows.Children.Add(BuildRow(item, max, data.TotalBytes));
        if (data.Items.Count > maxRows)
            rows.Children.Add(new TextBlock
            {
                Text = UiText.F("… {0} öğe daha (listede gösterilmedi)", data.Items.Count - maxRows),
                Foreground = MainWindow.TextMuted, FontSize = 11, Margin = new Thickness(6, 6, 0, 0)
            });
    }

    private Control BuildRow(FolderMeasure.BreakdownItem item, long max, long total)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,100,56,150"),
            Margin = new Thickness(0)
        };
        var name = new TextBlock
        {
            Text = item.IsDirectory ? item.Name + "/" : item.Name,
            FontSize = 12,
            FontWeight = item.IsDirectory ? FontWeight.SemiBold : FontWeight.Normal,
            Foreground = item.IsLink ? MainWindow.TextMuted : MainWindow.TextPrimary,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0)
        };
        Grid.SetColumn(name, 0);
        grid.Children.Add(name);
        var size = new TextBlock
        {
            Text = item.IsUnknown ? UiText.T("ölçülemedi") : item.IsLink ? "—" : FileSystem.HumanSize(item.Bytes),
            FontSize = 12, Foreground = MainWindow.TextMuted,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(size, 1);
        grid.Children.Add(size);
        var pct = new TextBlock
        {
            Text = item.IsUnknown || item.IsLink || total <= 0 ? "—" : $"{100.0 * item.Bytes / total:0.#}%",
            FontSize = 11, Foreground = MainWindow.TextMuted,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0)
        };
        Grid.SetColumn(pct, 2);
        grid.Children.Add(pct);
        var barWidth = item.IsUnknown || item.IsLink ? 0 : Math.Max(2.0, 148.0 * item.Bytes / max);
        var bar = new Border
        {
            Height = 10, CornerRadius = new CornerRadius(5),
            Background = item.IsDirectory ? MainWindow.AccentBrush : MainWindow.StrokeBrush,
            Width = barWidth, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(4, 0)
        };
        var barCell = new Border
        {
            Child = bar, Background = item.IsUnknown || item.IsLink ? Brushes.Transparent : MainWindow.CanvasBrush,
            CornerRadius = new CornerRadius(5), VerticalAlignment = VerticalAlignment.Center, Height = 14
        };
        Grid.SetColumn(barCell, 3);
        grid.Children.Add(barCell);

        var rowBorder = new Border
        {
            Child = grid, Padding = new Thickness(6, 4), Margin = new Thickness(0, 1),
            Background = MainWindow.SurfaceBrush, CornerRadius = new CornerRadius(8)
        };
        ToolTip.SetTip(rowBorder, item.IsDirectory
            ? UiText.T("Çift tıkla: bu klasöre in") + "\n" + item.Path
            : item.Path);
        if (item.IsDirectory && !item.IsLink)
            rowBorder.DoubleTapped += (_, _) => StartScan(item.Path);
        else if (!item.IsDirectory && !item.IsLink)
            rowBorder.DoubleTapped += (_, _) => _ = OpenContainingFolder();
        return rowBorder;
    }

    private async Task OpenContainingFolder()
    {
        win.ShowFileBrowser();
        var pane = win.ActivePaneForDiskUse();
        if (pane is not null && !string.IsNullOrEmpty(scannedPath))
            await pane.NavigateAsync(scannedPath);
    }

    private void OpenInPane()
    {
        if (string.IsNullOrEmpty(scannedPath)) return;
        win.ShowFileBrowser();
        _ = win.ActivePaneForDiskUse()?.NavigateAsync(scannedPath);
    }

    private void GoUp()
    {
        if (string.IsNullOrEmpty(scannedPath)) return;
        var parent = Path.GetDirectoryName(scannedPath.TrimEnd('/'));
        if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent)) StartScan(parent);
    }
}
