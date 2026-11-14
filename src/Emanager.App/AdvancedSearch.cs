using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Emanager.Core.Search;

namespace Emanager.App;

public static class AdvancedSearch
{
    public static async Task ShowAsync(Window owner, string root, Func<string, Task> navigate)
    {
        var window = new Window { Title = UiText.T("Gelişmiş dosya arama (indekssiz)"), Width = 900, Height = 650,
            MinWidth = 560, MinHeight = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var layout = new DockPanel { Margin = new Thickness(14) };
        var top = new StackPanel { Spacing = 8 };
        DockPanel.SetDock(top, Dock.Top);
        layout.Children.Add(top);
        top.Children.Add(new TextBlock { Text = UiText.T("Core arama — disk üzerinde indeks veya veritabanı oluşturulmaz.") });
        var query = new TextBox { Watermark = UiText.T("Dosya veya klasör adı (örnek: rapor)") };
        top.Children.Add(query);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var subfolders = new CheckBox { Content = UiText.T("Alt klasörler"), IsChecked = true };
        var hidden = new CheckBox { Content = UiText.T("Gizli klasörler"), IsChecked = false };
        var contents = new CheckBox { Content = UiText.T("İçerik ara"), IsChecked = false };
        var mime = new TextBox { Watermark = UiText.T("MIME (isteğe bağlı)"), Width = 150 };
        var start = new Button { Content = UiText.T("Ara") };
        var stop = new Button { Content = UiText.T("Durdur"), IsEnabled = false };
        row.Children.Add(subfolders); row.Children.Add(hidden); row.Children.Add(contents); row.Children.Add(mime); row.Children.Add(start); row.Children.Add(stop);
        top.Children.Add(row);
        var status = new TextBlock { Text = UiText.F("Arama konumu: {0}", root), TextWrapping = TextWrapping.Wrap };
        top.Children.Add(status);
        var results = new ListBox();
        layout.Children.Add(results);
        window.Content = layout;
        CancellationTokenSource? cts = null;
        start.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(query.Text)) { status.Text = UiText.T("Aranacak adı yazın."); return; }
            cts?.Cancel(); cts?.Dispose(); cts = new CancellationTokenSource();
            var token = cts.Token;
            var term = query.Text!;
            var includeSubfolders = subfolders.IsChecked == true;
            var includeHidden = hidden.IsChecked == true;
            var searchContents = contents.IsChecked == true;
            var mimeFilter = string.IsNullOrWhiteSpace(mime.Text) ? null : mime.Text.Trim();
            start.IsEnabled = false; stop.IsEnabled = true;
            var matches = new List<SearchResult>();
            results.ItemsSource = matches.ToArray();
            var searched = 0L;
            try
            {
                var progress = new Progress<SearchProgress>(p => { searched = p.ItemsScanned; status.Text = UiText.F("{0} sonuç · {1} öğe tarandı", p.ResultsFound, p.ItemsScanned); });
                IProgress<SearchResult> resultProgress = new Progress<SearchResult>(result => { if (matches.Count < 3000) matches.Add(result); results.ItemsSource = matches.ToArray(); });
                await Task.Run(() => new FileSearchEngine().SearchAsync(root,
                    new SearchQuery { NameContains = term, IncludeSubdirectories = includeSubfolders, IncludeHidden = includeHidden, SearchContents = searchContents, ContentContains = searchContents ? term : null, MimeType = mimeFilter },
                    result => resultProgress.Report(result), token, progress), token);
                status.Text = UiText.F("{0} sonuç · {1} öğe tarandı", matches.Count, searched) + (searched >= 100000 ? " · " + UiText.T("tarama sınırına ulaşıldı") : "");
            }
            catch (OperationCanceledException) { status.Text = UiText.F("Durduruldu · {0} sonuç", matches.Count); }
            finally { results.ItemsSource = matches.ToArray(); start.IsEnabled = true; stop.IsEnabled = false; }
        };
        stop.Click += (_, _) => cts?.Cancel();
        window.Closed += (_, _) => { cts?.Cancel(); cts?.Dispose(); };
        results.DoubleTapped += async (_, _) =>
        {
            if (results.SelectedItem is not SearchResult selected) return;
            await navigate(Directory.Exists(selected.FullPath) ? selected.FullPath : Path.GetDirectoryName(selected.FullPath)!);
            window.Close();
        };
        await window.ShowDialog(owner);
    }
}
