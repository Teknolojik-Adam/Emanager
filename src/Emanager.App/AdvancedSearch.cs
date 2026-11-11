using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Emanager.App;

public static class AdvancedSearch
{
    public static async Task ShowAsync(Window owner, string root, Func<string, Task> navigate)
    {
        var window = new Window { Title = "Advanced file search (no index)", Width = 780, Height = 580,
            MinWidth = 560, MinHeight = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var layout = new DockPanel { Margin = new Thickness(14) };
        var top = new StackPanel { Spacing = 8 };
        DockPanel.SetDock(top, Dock.Top);
        layout.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Search by name — no disk index or database is created." });
        var query = new TextBox { Watermark = "File or folder name (e.g. report)" };
        top.Children.Add(query);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var subfolders = new CheckBox { Content = "Subfolders", IsChecked = true };
        var hidden = new CheckBox { Content = "Hidden folders", IsChecked = false };
        var start = new Button { Content = "Search" };
        var stop = new Button { Content = "Stop", IsEnabled = false };
        row.Children.Add(subfolders); row.Children.Add(hidden); row.Children.Add(start); row.Children.Add(stop);
        top.Children.Add(row);
        var status = new TextBlock { Text = "Search location: " + root, TextWrapping = TextWrapping.Wrap };
        top.Children.Add(status);
        var results = new ListBox();
        layout.Children.Add(results);
        window.Content = layout;
        CancellationTokenSource? cts = null;
        start.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(query.Text)) { status.Text = "Enter a name to search for."; return; }
            cts?.Cancel(); cts?.Dispose(); cts = new CancellationTokenSource();
            var token = cts.Token;
            var term = query.Text!;
            var includeSubfolders = subfolders.IsChecked == true;
            var includeHidden = hidden.IsChecked == true;
            start.IsEnabled = false; stop.IsEnabled = true;
            var matches = new List<string>();
            results.ItemsSource = matches.ToArray();
            var searched = 0;
            try
            {
                await Task.Run(() =>
                {
                    var folders = new Stack<string>();
                    folders.Push(root);
                    while (folders.Count > 0 && searched < 100000 && matches.Count < 3000)
                    {
                        token.ThrowIfCancellationRequested();
                        var folder = folders.Pop();
                        try
                        {
                            foreach (var item in Directory.EnumerateFileSystemEntries(folder))
                            {
                                token.ThrowIfCancellationRequested();
                                searched++;
                                var name = Path.GetFileName(item);
                                if (name.Contains(term, StringComparison.OrdinalIgnoreCase)) matches.Add(item);
                                if (includeSubfolders && (includeHidden || !name.StartsWith('.')))
                                {
                                    var attrs = File.GetAttributes(item);
                                    if (attrs.HasFlag(FileAttributes.Directory) && !attrs.HasFlag(FileAttributes.ReparsePoint)) folders.Push(item);
                                }
                                if (searched >= 100000 || matches.Count >= 3000) break;
                            }
                        }
                        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
                    }
                }, token);
                status.Text = $"{matches.Count} results · {searched} items scanned" + (searched >= 100000 ? " · scan limit reached" : "");
            }
            catch (OperationCanceledException) { status.Text = $"Stopuldu · {matches.Count} results"; }
            finally { results.ItemsSource = matches.ToArray(); start.IsEnabled = true; stop.IsEnabled = false; }
        };
        stop.Click += (_, _) => cts?.Cancel();
        window.Closed += (_, _) => { cts?.Cancel(); cts?.Dispose(); };
        results.DoubleTapped += async (_, _) =>
        {
            if (results.SelectedItem is not string selected) return;
            await navigate(Directory.Exists(selected) ? selected : Path.GetDirectoryName(selected)!);
            window.Close();
        };
        await window.ShowDialog(owner);
    }
}
