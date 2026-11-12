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

    public TrashView(MainWindow owner)
    {
        win = owner;
        Margin = new Thickness(24, 17, 24, 20);
        Content = Build();
        Reload();
    }

    private Control Build()
    {
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*") };
        body.Children.Add(new TextBlock
        {
            Text = "🗑️  Çöp Kutusu", FontSize = 30, FontWeight = FontWeight.Bold,
            Foreground = MainWindow.TextPrimary
        });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        Grid.SetRow(actions, 1);
        actions.Children.Add(Action("↩  Geri Yükle", () => _ = RestoreSelectedAsync()));
        actions.Children.Add(Action("Kalıcı Sil", () => _ = DeleteSelectedAsync()));
        actions.Children.Add(Action("Çöp Kutusunu Boşalt", () => _ = EmptyAsync()));
        body.Children.Add(actions);

        Grid.SetRow(status, 2);
        body.Children.Add(status);
        list.ItemTemplate = new FuncDataTemplate<TrashEntry>((entry, _) =>
        {
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("2*,3*,1.2*,1*")
            };
            AddCell(grid, entry.Name, 0, MainWindow.TextPrimary);
            AddCell(grid, string.IsNullOrEmpty(entry.OriginalPath) ? "(özgün konum bilinmiyor)" : entry.OriginalPath,
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

    private Button Action(string label, Action action)
    {
        var button = win.MakeButton(label, action);
        button.Background = MainWindow.SurfaceBrush;
        return button;
    }

    public void Reload() => _ = ReloadAsync();

    private async Task ReloadAsync()
    {
        if (busy) return;
        busy = true;
        try
        {
            entries = await Task.Run(TrashService.ListAll);
            list.ItemsSource = entries;
            status.Text = entries.Count == 0
                ? "Çöp kutusu boş. Silinen dosyalar burada görünecek."
                : $"{entries.Count} öğe · Ad · Özgün konum · Silinme tarihi · Boyut";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            status.Text = "Çöp kutusu okunamadı: " + ex.Message;
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
                    alternate = await InputDialog.ShowAsync(this, "Geri yükleme konumu",
                        Path.Combine(MainWindow.HomePath(), entry.Name),
                        "Özgün klasör bulunamadı. Yeni hedef yolu girin:");
                    if (string.IsNullOrWhiteSpace(alternate)) continue;
                }
                TrashService.Restore(entry, alternate);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                await MessageDialog.ShowAsync(this, "Geri yükleme başarısız", ex.Message);
            }
        }
        Reload();
    }

    private async Task DeleteSelectedAsync()
    {
        var selected = Selected();
        if (selected.Count == 0) return;
        if (!await ConfirmDialog.ShowAsync(this, "Kalıcı sil",
            $"{selected.Count} öğe kalıcı olarak silinecek. Bu işlem geri alınamaz.", "Kalıcı Sil", dangerous: true)) return;
        try
        {
            foreach (var entry in selected) TrashService.PermanentlyDelete(entry);
            Reload();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { await MessageDialog.ShowAsync(this, "Kalıcı silme başarısız", ex.Message); }
    }

    private async Task EmptyAsync()
    {
        if (entries.Count == 0) return;
        if (!await ConfirmDialog.ShowAsync(this, "Çöp Kutusunu Boşalt",
            $"Çöp kutusundaki {entries.Count} öğe kalıcı olarak silinecek. Bu işlem geri alınamaz.", "Çöp Kutusunu Boşalt", dangerous: true)) return;
        try
        {
            await Task.Run(TrashService.EmptyAll);
            Reload();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { await MessageDialog.ShowAsync(this, "Çöp kutusu boşaltılamadı", ex.Message); }
    }
}
