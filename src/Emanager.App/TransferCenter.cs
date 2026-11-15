using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Emanager.Core.FileOperations;

namespace Emanager.App;

internal sealed record TransferRow(Guid Id, string Label, string Source, string Destination,
    FileOperationState State, long Bytes, long Total, double? Percent, double Speed,
    TimeSpan? Remaining, string? Error, bool Pausable, bool Remote);

/// <summary>Tracks both safe local copies and external GIO transfers. Does not own the UI window.</summary>
internal sealed class TransferCenter
{
    private readonly FileOperationManager manager;
    private readonly object gate = new();
    private readonly Dictionary<Guid, TransferRow> jobs = [];
    private readonly Dictionary<Guid, CancellationTokenSource> external = [];

    public TransferCenter(FileOperationManager manager)
    {
        this.manager = manager;
        manager.ProgressChanged += (_, value) =>
        {
            lock (gate)
                if (jobs.TryGetValue(value.Job.Id, out var row) && row.State is not (FileOperationState.Completed or FileOperationState.Cancelled or FileOperationState.Failed or FileOperationState.Skipped))
                    jobs[value.Job.Id] = row with
                    {
                        State = row.State == FileOperationState.Paused ? FileOperationState.Paused : FileOperationState.Running,
                        Bytes = value.Progress.BytesTransferred,
                        Total = value.Progress.TotalBytes,
                        Percent = value.Progress.TotalBytes > 0 ? value.Progress.Fraction * 100 : null,
                        Speed = value.Progress.BytesPerSecond,
                        Remaining = value.Progress.Remaining
                    };
        };
    }

    public void RegisterLocal(FileOperationJob job)
    {
        lock (gate) jobs[job.Id] = new TransferRow(job.Id, job.Kind.ToString(), job.Source,
            job.Destination, FileOperationState.Queued, 0, job.TotalBytes, null, 0, null, null,
            job.Kind != FileOperationKind.Rename, false);
    }

    public void FinishLocal(FileOperationResult result)
    {
        lock (gate)
            if (jobs.TryGetValue(result.JobId, out var row))
                jobs[result.JobId] = row with { State = result.State, Error = result.Error?.Message,
                    Percent = result.State == FileOperationState.Completed ? 100 : row.Percent };
    }

    public (Guid Id, CancellationToken Token) RegisterExternal(string label, string source, string destination, long total = 0)
    {
        var id = Guid.NewGuid();
        var cts = new CancellationTokenSource();
        lock (gate)
        {
            external[id] = cts;
            jobs[id] = new TransferRow(id, label, source, destination, FileOperationState.Queued,
                0, total, null, 0, null, null, false, true);
        }
        return (id, cts.Token);
    }

    public void ExternalStarted(Guid id)
    {
        lock (gate)
            if (jobs.TryGetValue(id, out var row)) jobs[id] = row with { State = FileOperationState.Running };
    }

    public void ExternalProgress(Guid id, double percent)
    {
        lock (gate)
            if (jobs.TryGetValue(id, out var row))
                jobs[id] = row with { State = row.State == FileOperationState.Paused ? FileOperationState.Paused : FileOperationState.Running,
                    Percent = Math.Clamp(percent, 0, 100), Bytes = row.Total > 0
                        ? (long)(row.Total * Math.Clamp(percent, 0, 100) / 100) : 0 };
    }

    public void FinishExternal(Guid id, Exception? error = null, bool cancelled = false)
    {
        lock (gate)
        {
            if (jobs.TryGetValue(id, out var row))
                jobs[id] = row with { State = cancelled ? FileOperationState.Cancelled :
                    error is null ? FileOperationState.Completed : FileOperationState.Failed,
                    Error = error?.Message, Percent = error is null && !cancelled ? 100 : row.Percent };
            if (external.Remove(id, out var token)) token.Dispose();
        }
    }

    public IReadOnlyList<TransferRow> Snapshot()
    {
        lock (gate) return jobs.Values.Reverse().Take(150).ToArray();
    }

    public void Pause(Guid id)
    {
        lock (gate)
            if (jobs.TryGetValue(id, out var job) && job.Pausable &&
                job.State is FileOperationState.Running or FileOperationState.Queued)
            {
                if (!job.Remote) manager.Pause(id);
                jobs[id] = job with { State = FileOperationState.Paused };
            }
    }

    public void Resume(Guid id)
    {
        lock (gate)
            if (jobs.TryGetValue(id, out var job) && job.Pausable && job.State == FileOperationState.Paused)
            {
                if (!job.Remote) manager.Resume(id);
                jobs[id] = job with { State = FileOperationState.Running };
            }
    }

    public void Cancel(Guid id)
    {
        lock (gate)
        {
            if (!jobs.TryGetValue(id, out var row) ||
                row.State is FileOperationState.Completed or FileOperationState.Cancelled or
                    FileOperationState.Skipped or FileOperationState.Failed) return;
            if (row.Remote)
            {
                if (external.TryGetValue(id, out var cts)) cts.Cancel();
            }
            else manager.Cancel(id);
        }
    }

    public void ClearFinished()
    {
        lock (gate)
            foreach (var id in jobs.Where(x => x.Value.State is FileOperationState.Completed or
                FileOperationState.Cancelled or FileOperationState.Skipped or FileOperationState.Failed)
                .Select(x => x.Key).ToArray()) jobs.Remove(id);
    }
}

internal sealed class TransferCenterWindow : Window
{
    private readonly TransferCenter center;
    private readonly StackPanel records = new() { Spacing = 9 };
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(550) };
    private string previous = "<initial>";

    public TransferCenterWindow(TransferCenter center)
    {
        this.center = center;
        Title = "Emanager — Transferler / Transfers";
        Width = 900; Height = 600; MinWidth = 610; MinHeight = 370;
        Background = MainWindow.CanvasBrush;
        var clear = new Button { Content = "✓ Bitenleri temizle / Clear finished", Padding = new Thickness(12, 7) };
        clear.Click += (_, _) => { center.ClearFinished(); previous = "<initial>"; Render(); };
        var header = new DockPanel { Margin = new Thickness(18, 16, 18, 10) };
        DockPanel.SetDock(clear, Dock.Right); header.Children.Add(clear);
        header.Children.Add(new TextBlock { Text = "⇅  Dosya aktarım yöneticisi / Transfers", FontSize = 19,
            FontWeight = FontWeight.Bold, Foreground = MainWindow.TextPrimary, VerticalAlignment = VerticalAlignment.Center });
        var body = new DockPanel();
        DockPanel.SetDock(header, Dock.Top); body.Children.Add(header);
        body.Children.Add(new ScrollViewer { Content = records, Margin = new Thickness(18, 0, 18, 18),
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        Content = body;
        refresh.Tick += (_, _) => Render();
        Opened += (_, _) => { Render(); refresh.Start(); };
        Closed += (_, _) => refresh.Stop();
    }

    private static string StateText(FileOperationState state) => state switch
    {
        FileOperationState.Queued => "Kuyrukta / Queued",
        FileOperationState.Running => "Aktarılıyor / Transferring",
        FileOperationState.Paused => "Duraklatıldı / Paused",
        FileOperationState.Completed => "Tamamlandı / Completed",
        FileOperationState.Cancelled => "İptal edildi / Cancelled",
        FileOperationState.Skipped => "Atlandı / Skipped",
        FileOperationState.Failed => "Hata / Failed",
        _ => state.ToString()
    };

    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / 1024d / 1024 / 1024:0.0} GiB"
        : bytes >= 1024L * 1024 ? $"{bytes / 1024d / 1024:0.0} MiB"
        : bytes >= 1024 ? $"{bytes / 1024d:0} KiB" : $"{bytes} B";

    private void Render()
    {
        var jobs = center.Snapshot();
        var fingerprint = string.Join("|", jobs.Select(x => $"{x.Id:N},{x.State},{x.Percent:0.0},{x.Bytes},{x.Total},{x.Speed:0},{x.Error}"));
        if (previous == fingerprint) return;
        previous = fingerprint;
        records.Children.Clear();
        if (jobs.Count == 0)
        {
            records.Children.Add(new TextBlock { Text = "Henüz aktarım yok. / No transfers yet.",
                Foreground = MainWindow.TextMuted, Margin = new Thickness(12) });
            return;
        }
        foreach (var row in jobs)
        {
            var pane = new StackPanel { Spacing = 5 };
            pane.Children.Add(new TextBlock { Text = $"{row.Label} · {StateText(row.State)}",
                FontWeight = FontWeight.SemiBold, Foreground = MainWindow.TextPrimary });
            pane.Children.Add(new TextBlock { Text = row.Source + "  →  " + row.Destination,
                TextTrimming = TextTrimming.CharacterEllipsis, Foreground = MainWindow.TextMuted,
                MaxWidth = 760, FontSize = 12 });
            var detail = row.Percent.HasValue ? $"{row.Percent.Value:0.0}%" : "…";
            if (row.Total > 0) detail = $"{FormatBytes(row.Bytes)} / {FormatBytes(row.Total)}  ·  " + detail;
            if (row.Speed > 0) detail += $"  ·  {row.Speed / 1024 / 1024:0.0} MiB/s";
            if (row.Remaining is { } remaining && remaining.TotalSeconds >= 0)
                detail += $"  ·  {remaining:mm\\:ss}";
            if (!string.IsNullOrWhiteSpace(row.Error)) detail += "  ·  " + row.Error;
            pane.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100,
                Value = row.Percent ?? 0, Height = 9, IsIndeterminate = !row.Percent.HasValue &&
                    row.State is FileOperationState.Running or FileOperationState.Queued });
            pane.Children.Add(new TextBlock { Text = detail, Foreground = MainWindow.TextMuted, FontSize = 12,
                TextWrapping = TextWrapping.Wrap });
            if (row.State is FileOperationState.Running or FileOperationState.Queued or FileOperationState.Paused)
            {
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
                if (row.Pausable)
                {
                    var pause = new Button { Content = row.State == FileOperationState.Paused
                        ? "▶ Devam / Resume" : "Ⅱ Duraklat / Pause" };
                    pause.Click += (_, _) => { if (row.State == FileOperationState.Paused) center.Resume(row.Id);
                        else center.Pause(row.Id); previous = ""; Render(); };
                    buttons.Children.Add(pause);
                }
                var cancel = new Button { Content = "✕ İptal / Cancel" };
                cancel.Click += (_, _) => { center.Cancel(row.Id); cancel.IsEnabled = false; };
                buttons.Children.Add(cancel);
                pane.Children.Add(buttons);
            }
            records.Children.Add(new Border { Background = MainWindow.SurfaceBrush,
                BorderBrush = MainWindow.StrokeBrush, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9), Padding = new Thickness(12), Child = pane });
        }
    }
}
