using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace Emanager.App;

/// <summary>In-app view for the normal user's Freedesktop trash entries.</summary>
public sealed class TrashView : UserControl
{
    private readonly MainWindow win;
    private readonly ListBox list = new() { SelectionMode = SelectionMode.Multiple, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
    private readonly TextBlock status = new() { Foreground = MainWindow.TextMuted, TextWrapping = TextWrapping.Wrap };
    private IReadOnlyList<TrashEntry> entries = [];
    private bool busy;
    private bool reloadPending;
    private bool operationBusy;
    private readonly List<Button> actionButtons = [];

    public TrashView(MainWindow owner)
    {
        win = owner;
        Margin = new Thickness(24, 17, 24, 20);
        Content = Build();
        AttachedToVisualTree += (_, _) => Reload();
        Reload();
    }

    private Control Build()
    {
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*") };
        body.Children.Add(new TextBlock
        {
            Text = UiText.T("🗑️  Çöp Kutusu"), FontSize = 30, FontWeight = FontWeight.Bold,
            Foreground = MainWindow.TextPrimary
        });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        Grid.SetRow(actions, 1);
        actions.Children.Add(Action(UiText.T("↩  Geri Yükle"), RestoreSelectedAsync));
        actions.Children.Add(Action(UiText.T("Kalıcı Sil"), DeleteSelectedAsync));
        actions.Children.Add(Action(UiText.T("Çöp Kutusunu Boşalt"), EmptyAsync));
        body.Children.Add(actions);

        Grid.SetRow(status, 2);
        body.Children.Add(status);
        list.ItemTemplate = new FuncDataTemplate<TrashEntry>((entry, _) =>
        {
            // Avalonia may invoke the template with null when a recycled
            // ListBoxItem is detached after deleting the last trash entry.
            if (entry is null)
                return new Border { IsVisible = false };
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("2*,3*,1.2*,1*")
            };
            AddCell(grid, entry.Name, 0, MainWindow.TextPrimary);
            AddCell(grid, string.IsNullOrEmpty(entry.OriginalPath) ? UiText.T("(özgün konum bilinmiyor)") : entry.OriginalPath,
                1, MainWindow.TextMuted);
            AddCell(grid, entry.DeletedAt?.ToString("g") ?? "—", 2, MainWindow.TextMuted);
            AddCell(grid, FileSystem.HumanSize(entry.Size), 3, MainWindow.TextMuted);
            return new Border
            {
                Padding = new Thickness(12, 10), Margin = new Thickness(0, 2),
                Background = MainWindow.SurfaceBrush, CornerRadius = new CornerRadius(8), Child = grid
            };
        });
        list.DoubleTapped += (_, _) => _ = RestoreSelectedAsync();
        Grid.SetRow(list, 3);
        body.Children.Add(list);
        return body;
    }

    private static void AddCell(Grid grid, string text, int column, IBrush foreground)
    {
        var cell = new TextBlock
        {
            Text = text, Foreground = foreground, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0)
        };
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    private Button Action(string label, Func<Task> action)
    {
        var button = win.MakeButton(label, () => _ = RunOperationAsync(action));
        button.Background = MainWindow.SurfaceBrush;
        actionButtons.Add(button);
        return button;
    }

    private async Task RunOperationAsync(Func<Task> action)
    {
        if (operationBusy) return;
        operationBusy = true;
        foreach (var button in actionButtons) button.IsEnabled = false;
        TrashDiagnostics.Event("operation-start");
        try { await action(); }
        catch (Exception ex)
        {
            TrashDiagnostics.Event("operation-exception", exception: ex);
            await MessageDialog.ShowAsync(this, UiText.T("Çöp kutusu işlemi başarısız"), ex.Message);
        }
        finally
        {
            foreach (var button in actionButtons) button.IsEnabled = true;
            operationBusy = false;
            TrashDiagnostics.Event("operation-finished");
        }
    }

    public void Reload() => _ = ReloadAsync();

    private async Task ReloadAsync()
    {
        // A refresh requested during an earlier refresh must not be lost.
        // Otherwise a completed delete can leave stale entries visible.
        reloadPending = true;
        if (busy) return;
        busy = true;
        try
        {
            do
            {
                reloadPending = false;
                try
                {
                    TrashDiagnostics.Event("reload-start");
                    var updated = await Task.Run(TrashService.ListAll);
                    entries = updated;
                    list.ItemsSource = updated;
                    status.Text = updated.Count == 0
                        ? UiText.T("Çöp kutusu boş. Silinen dosyalar burada görünecek.")
                        : UiText.F("{0} öğe · Ad · Özgün konum · Silinme tarihi · Boyut", updated.Count);
                    TrashDiagnostics.Event("reload-finished", $"count={updated.Count}");
                }
                catch (Exception ex)
                {
                    TrashDiagnostics.Event("reload-exception", exception: ex);
                    status.Text = UiText.T("Çöp kutusu okunamadı: ") + ex.Message;
                }
            } while (reloadPending);
        }
        finally { busy = false; }
    }

    private IReadOnlyList<TrashEntry> Selected()
    {
        return (list.SelectedItems as IEnumerable<object>)?.OfType<TrashEntry>().ToList() ?? [];
    }

    private async Task RestoreSelectedAsync()
    {
        foreach (var entry in Selected())
        {
            try
            {
                string? alternate = null;
                var parent = string.IsNullOrWhiteSpace(entry.OriginalPath) ? null : Directory.GetParent(entry.OriginalPath)?.FullName;
                if (parent is null || !Directory.Exists(parent))
                {
                    alternate = await InputDialog.ShowAsync(this, UiText.T("Geri yükleme konumu"),
                        Path.Combine(MainWindow.HomePath(), entry.Name),
                        UiText.T("Özgün klasör bulunamadı. Yeni hedef yolu girin:"));
                    if (string.IsNullOrWhiteSpace(alternate)) continue;
                }
                TrashService.Restore(entry, alternate);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                await MessageDialog.ShowAsync(this, UiText.T("Geri yükleme başarısız"), ex.Message);
            }
        }
        Reload();
    }

    private async Task DeleteSelectedAsync()
    {
        var selected = Selected();
        TrashDiagnostics.Event("delete-click", $"selected={selected.Count}");
        if (selected.Count == 0) { status.Text = UiText.T("Kalıcı silmek için en az bir öğe seçin."); return; }
        TrashDiagnostics.Event("confirm-open", $"operation=delete count={selected.Count}");
        if (!await ConfirmDialog.ShowAsync(this, UiText.T("Kalıcı sil"),
            UiText.F("{0} öğe kalıcı olarak silinecek. Bu işlem geri alınamaz.", selected.Count), UiText.T("Kalıcı Sil"), dangerous: true))
        {
            TrashDiagnostics.Event("confirm-result", "operation=delete accepted=false");
            return;
        }
        TrashDiagnostics.Event("confirm-result", "operation=delete accepted=true");
        var result = await Task.Run(() => TrashService.DeleteEntries(selected));
        TrashDiagnostics.Event("delete-result", $"found={selected.Count} deleted={result.DeletedCount} failed={result.Failures.Count}");
        await ReloadAsync();
        if (result.Failures.Count > 0)
        {
            var details = string.Join("\n", result.Failures.Select(x => $"{x.Name}: {x.Message}"));
            await MessageDialog.ShowAsync(this, UiText.T("Kalıcı silme kısmen başarısız"),
                UiText.F("{0} öğe silindi; {1} öğe silinemedi:\n{2}", result.DeletedCount, result.Failures.Count, details));
        }
    }

    private async Task EmptyAsync()
    {
        // Refresh before acting: entries shown on screen may already be gone.
        await ReloadAsync();
        TrashDiagnostics.Event("empty-click", $"count={entries.Count}");
        if (entries.Count == 0)
        {
            status.Text = UiText.T("Çöp kutusu boş. Silinecek öğe bulunamadı.");
            return;
        }
        TrashDiagnostics.Event("confirm-open", $"operation=empty count={entries.Count}");
        if (!await ConfirmDialog.ShowAsync(this, UiText.T("Çöp Kutusunu Boşalt"),
            UiText.F("Çöp kutusundaki {0} öğe kalıcı olarak silinecek. Bu işlem geri alınamaz.", entries.Count), UiText.T("Çöp Kutusunu Boşalt"), dangerous: true))
        {
            TrashDiagnostics.Event("confirm-result", "operation=empty accepted=false");
            return;
        }
        TrashDiagnostics.Event("confirm-result", "operation=empty accepted=true");
        try
        {
            var result = await Task.Run(TrashService.EmptyAll);
            TrashDiagnostics.Event("empty-result", $"found={entries.Count} deleted={result.DeletedCount} failed={result.Failures.Count}");
            if (result.Failures.Count > 0)
            {
                var details = string.Join("\n", result.Failures.Select(x => $"{x.Name}: {x.Message}"));
                await MessageDialog.ShowAsync(this, UiText.T("Çöp kutusu kısmen boşaltıldı"),
                    UiText.F("{0} öğe silindi; {1} öğe silinemedi:\n{2}", result.DeletedCount, result.Failures.Count, details));
            }
        }
        catch (Exception ex)
        { TrashDiagnostics.Event("empty-exception", exception: ex); throw; }
        finally
        {
            await ReloadAsync();
            TrashDiagnostics.Event("empty-reload-finished", $"remaining={entries.Count}");
        }
    }
}
