using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Emanager.Core.FileOperations;

namespace Emanager.App.Ui;

/// <summary>
/// A compact, reusable transfer queue for the MTP side panel. Pass the existing
/// transfer center; the view only reads snapshots and invokes supported controls.
/// </summary>
internal sealed class MtpTransferQueueView : UserControl
{
    private readonly TransferCenter transfers;
    private readonly StackPanel rows = new() { Spacing = MtpUiTokens.Space8 };
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private string lastFingerprint = "<initial>";

    public MtpTransferQueueView(TransferCenter transfers)
    {
        this.transfers = transfers;
        var root = new StackPanel { Spacing = MtpUiTokens.Space12 };
        root.Children.Add(new TextBlock
        {
            Text = "Aktarımlar",
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Foreground = MainWindow.TextPrimary
        });
        root.Children.Add(new ScrollViewer
        {
            Content = rows,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        });
        Content = root;
        refresh.Tick += (_, _) => Render();
        AttachedToVisualTree += (_, _) => { Render(); refresh.Start(); };
        DetachedFromVisualTree += (_, _) => refresh.Stop();
    }

    private void Render()
    {
        var snapshot = transfers.Snapshot();
        var fingerprint = string.Join("|", snapshot.Select(row =>
            $"{row.Id:N},{row.State},{row.Percent:0.0},{row.Speed:0.0},{row.Error}"));
        if (fingerprint == lastFingerprint) return;
        lastFingerprint = fingerprint;
        rows.Children.Clear();
        if (snapshot.Count == 0)
        {
            rows.Children.Add(new TextBlock
            {
                Text = "Henüz aktarım yok",
                Foreground = MtpUiTokens.Muted,
                Margin = new Thickness(10, 6)
            });
            return;
        }

        foreach (var row in snapshot)
        {
            var content = new StackPanel { Spacing = 5 };
            var direction = IsMtp(row.Source) ? "↓ İndir" : IsMtp(row.Destination) ? "↑ Yükle" : "⇄ Aktar";
            content.Children.Add(new TextBlock
            {
                Text = $"{direction}  ·  {row.Label}  ·  {StateLabel(row.State)}",
                FontWeight = FontWeight.SemiBold,
                Foreground = row.State == FileOperationState.Paused ? MtpUiTokens.Warning : MtpUiTokens.Text,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            content.Children.Add(new TextBlock
            {
                Text = IsMtp(row.Source) ? row.Source : row.Destination,
                FontSize = 11,
                Foreground = MtpUiTokens.Muted,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            content.Children.Add(new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = Math.Clamp(row.Percent ?? 0, 0, 100),
                Height = 7,
                Foreground = MtpUiTokens.Flow,
                IsIndeterminate = row.Percent is null && row.State is FileOperationState.Queued or FileOperationState.Running
            });
            var detail = row.Percent is { } percent ? $"{percent:0}%" : "İlerleme bekleniyor";
            if (row.Speed > 0) detail += $"  ·  {FormatSpeed(row.Speed)}";
            if (row.Remaining is { } remaining && remaining.TotalSeconds >= 0)
                detail += $"  ·  {remaining:mm\\:ss} kaldı";
            if (!string.IsNullOrWhiteSpace(row.Error)) detail += $"  ·  {row.Error}";
            content.Children.Add(new TextBlock { Text = detail, FontSize = 11, Foreground = MtpUiTokens.Muted,
                TextWrapping = TextWrapping.Wrap });

            if (row.State is FileOperationState.Queued or FileOperationState.Running or FileOperationState.Paused)
            {
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                if (row.Pausable)
                {
                    var pause = new Button { Content = row.State == FileOperationState.Paused ? "Sürdür" : "Duraklat",
                        Padding = new Thickness(8, 4) };
                    pause.Click += (_, _) =>
                    {
                        if (row.State == FileOperationState.Paused) transfers.Resume(row.Id);
                        else transfers.Pause(row.Id);
                        lastFingerprint = "";
                        Render();
                    };
                    actions.Children.Add(pause);
                }
                var cancel = new Button { Content = "İptal", Padding = new Thickness(8, 4) };
                cancel.IsEnabled = row.Remote || row.State is FileOperationState.Queued or FileOperationState.Running or FileOperationState.Paused;
                cancel.Click += (_, _) => { transfers.Cancel(row.Id); cancel.IsEnabled = false; };
                actions.Children.Add(cancel);
                content.Children.Add(actions);
            }

            rows.Children.Add(new Border
            {
                Background = MtpUiTokens.Surface,
                BorderBrush = MtpUiTokens.Stroke,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                Child = content
            });
        }
    }

    private static bool IsMtp(string value) => GioMtpSupport.IsMtpUri(value);
    private static string StateLabel(FileOperationState state) => state switch
    {
        FileOperationState.Queued => MtpUiTokens.PairingLabel + " · Kuyrukta",
        FileOperationState.Running => "Sürüyor",
        FileOperationState.Paused => MtpUiTokens.WarningLabel + " · Duraklatıldı",
        FileOperationState.Completed => MtpUiTokens.DoneLabel + " · Tamamlandı",
        FileOperationState.Failed => MtpUiTokens.ErrorLabel + " · Hata",
        FileOperationState.Cancelled => "İptal edildi",
        FileOperationState.Skipped => "Atlandı",
        _ => state.ToString()
    };
    private static string FormatSpeed(double bytesPerSecond) => bytesPerSecond >= 1024 * 1024
        ? $"{bytesPerSecond / 1024 / 1024:0.0} MiB/s"
        : $"{bytesPerSecond / 1024:0} KiB/s";
}

/// <summary>Selection toolbar. Unsupported filesystem operations remain visibly disabled.</summary>
internal sealed class MtpSelectionActionBar : UserControl
{
    private readonly TextBlock selectionSummary = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button download = MakeButton("↓ İndir");
    private readonly Button upload = MakeButton("↑ Yükle");
    private readonly Button rename = MakeButton("Adlandır");
    private readonly Button newFolder = MakeButton("Yeni klasör");
    private readonly Button delete = MakeButton("Sil");

    public event EventHandler? DownloadRequested;
    public event EventHandler? UploadRequested;
    public event EventHandler? RenameRequested;
    public event EventHandler? NewFolderRequested;
    public event EventHandler? DeleteRequested;

    public MtpSelectionActionBar()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        bar.Children.Add(selectionSummary);
        bar.Children.Add(download);
        bar.Children.Add(upload);
        bar.Children.Add(rename);
        bar.Children.Add(newFolder);
        bar.Children.Add(delete);
        Content = new Border { BorderBrush = MainWindow.StrokeBrush, BorderThickness = new Thickness(0, 1),
            Padding = new Thickness(8, 7), Child = bar };
        download.Click += (_, _) => DownloadRequested?.Invoke(this, EventArgs.Empty);
        upload.Click += (_, _) => UploadRequested?.Invoke(this, EventArgs.Empty);
        rename.Click += (_, _) => RenameRequested?.Invoke(this, EventArgs.Empty);
        newFolder.Click += (_, _) => NewFolderRequested?.Invoke(this, EventArgs.Empty);
        delete.Click += (_, _) => DeleteRequested?.Invoke(this, EventArgs.Empty);
        SetSelection(0);
    }

    /// <summary>Updates the selection count and explicit per-operation availability.</summary>
    public void SetSelection(int selectedCount, bool canDownload = true, bool canUpload = true,
        bool canRename = false, bool canCreateFolder = false, bool canDelete = false)
    {
        selectionSummary.Text = selectedCount == 1 ? "1 seçili" : $"{selectedCount} seçili";
        download.IsEnabled = selectedCount > 0 && canDownload;
        upload.IsEnabled = canUpload;
        rename.IsEnabled = selectedCount == 1 && canRename;
        newFolder.IsEnabled = canCreateFolder;
        delete.IsEnabled = selectedCount > 0 && canDelete;
    }

    private static Button MakeButton(string text) => new() { Content = text, Padding = new Thickness(9, 6), MinHeight = MtpUiTokens.MinimumHeight };
}

/// <summary>Connection and aggregate transfer status for an MTP pane.</summary>
internal sealed class MtpConnectionStatus : UserControl
{
    private readonly TransferCenter transfers;
    private readonly Func<bool> isConnected;
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly TextBlock connection = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock transferSummary = new() { VerticalAlignment = VerticalAlignment.Center };

    public MtpConnectionStatus(TransferCenter transfers, Func<bool> isConnected)
    {
        this.transfers = transfers;
        this.isConnected = isConnected;
        var layout = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        layout.Children.Add(connection);
        layout.Children.Add(new Border { Width = 1, Height = 18, Background = MainWindow.StrokeBrush,
            VerticalAlignment = VerticalAlignment.Center });
        layout.Children.Add(transferSummary);
        Content = new Border { Background = MainWindow.SurfaceBrush, CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10, 7), Child = layout };
        refresh.Tick += (_, _) => Render();
        AttachedToVisualTree += (_, _) => { Render(); refresh.Start(); };
        DetachedFromVisualTree += (_, _) => refresh.Stop();
    }

    private void Render()
    {
        var connected = isConnected();
        connection.Text = connected ? MtpUiTokens.ConnectedLabel + " · Bağlı" : MtpUiTokens.OfflineLabel + " · Bağlı değil";
        connection.Foreground = connected ? MtpUiTokens.Connected : MtpUiTokens.Error;
        var active = transfers.Snapshot().Where(x => x.State is FileOperationState.Queued or
            FileOperationState.Running or FileOperationState.Paused).ToArray();
        var speed = active.Sum(x => x.Speed);
        transferSummary.Text = $"{active.Length} etkin  ·  {MtpTransferQueueViewSpeed(speed)}";
        transferSummary.Foreground = active.Length > 0 ? MtpUiTokens.Accent : MtpUiTokens.Muted;
    }

    private static string MtpTransferQueueViewSpeed(double bytesPerSecond) => bytesPerSecond >= 1024 * 1024
        ? $"{bytesPerSecond / 1024 / 1024:0.0} MiB/s"
        : $"{bytesPerSecond / 1024:0} KiB/s";
}
