using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Input;

namespace Emanager.App;

internal sealed record OpenWithSelection(DesktopApplication Application, string MimeType, bool MakeDefault);

/// <summary>Windows-style application picker with MIME-aware suggestions,
/// search, every installed program, and a persistent default choice.</summary>
internal static class OpenWithDialog
{
    internal static async Task<OpenWithSelection?> ShowAsync(Window owner, string filePath)
    {
        var dialog = new Window
        {
            Title = UiText.T("Birlikte Aç") + " — " + Path.GetFileName(filePath),
            Width = 700, Height = 665, MinWidth = 560, MinHeight = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = MainWindow.SidebarBrush
        };

        var content = new Grid
        {
            Margin = new Thickness(23, 19, 23, 19),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto,Auto")
        };
        dialog.Content = content;

        var header = new StackPanel { Spacing = 4 };
        header.Children.Add(new TextBlock
        {
            Text = UiText.T("Bu dosyayı hangi uygulamayla açmak istiyorsun?"),
            FontSize = 20, FontWeight = FontWeight.SemiBold, Foreground = MainWindow.TextPrimary,
            TextWrapping = TextWrapping.Wrap
        });
        header.Children.Add(new TextBlock
        {
            Text = Path.GetFileName(filePath), FontSize = 13,
            Foreground = MainWindow.TextMuted, TextTrimming = TextTrimming.CharacterEllipsis
        });
        content.Children.Add(header);

        var search = new TextBox
        {
            Watermark = UiText.T("🔎  Uygulama ara (ör. VLC, Kate, Firefox…)"),
            Foreground = MainWindow.TextPrimary, Background = MainWindow.SurfaceBrush,
            Height = 42, VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 10)
        };
        Grid.SetRow(search, 1);
        content.Children.Add(search);

        var filters = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var showAll = new CheckBox
        {
            Content = UiText.T("Tüm kurulu uygulamaları göster"), IsChecked = false,
            Foreground = MainWindow.TextPrimary, VerticalAlignment = VerticalAlignment.Center
        };
        filters.Children.Add(showAll);
        var mimeLabel = new TextBlock
        {
            Text = UiText.T("Dosya türü belirleniyor…"), Foreground = MainWindow.TextMuted,
            VerticalAlignment = VerticalAlignment.Center, FontSize = 11
        };
        Grid.SetColumn(mimeLabel, 1);
        filters.Children.Add(mimeLabel);
        Grid.SetRow(filters, 2);
        content.Children.Add(filters);

        var list = new ListBox
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            SelectionMode = SelectionMode.Single
        };
        list.ItemTemplate = new FuncDataTemplate<DesktopApplication>((app, _) =>
        {
            var panel = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("46,*"),
                Margin = new Thickness(3, 2), MinHeight = 54
            };
            panel.Children.Add(new Border
            {
                Width = 36, Height = 36, CornerRadius = new CornerRadius(10),
                Background = MainWindow.SurfaceBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = app.Name.Length > 0 ? app.Name[..1].ToUpperInvariant() : "▣",
                    Foreground = MainWindow.AccentBrush,
                    FontWeight = FontWeight.Bold, FontSize = 19,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
            var labels = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            labels.Children.Add(new TextBlock
            {
                Text = app.Name, Foreground = MainWindow.TextPrimary,
                FontWeight = FontWeight.SemiBold, FontSize = 14,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            labels.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(app.Description) ? app.DesktopId : app.Description,
                Foreground = MainWindow.TextMuted, FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetColumn(labels, 1);
            panel.Children.Add(labels);
            return panel;
        });
        var listFrame = new Border
        {
            Margin = new Thickness(0, 10, 0, 10),
            CornerRadius = new CornerRadius(12), BorderBrush = MainWindow.StrokeBrush,
            BorderThickness = new Thickness(1), Background = MainWindow.CanvasBrush,
            Padding = new Thickness(6), Child = list
        };
        Grid.SetRow(listFrame, 3);
        content.Children.Add(listFrame);

        var lower = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 12) };
        var selectedInfo = new TextBlock
        {
            Text = UiText.T("Uygulamalar yükleniyor…"), TextWrapping = TextWrapping.Wrap,
            Foreground = MainWindow.TextMuted, FontSize = 12
        };
        lower.Children.Add(selectedInfo);
        var saveDefault = new CheckBox
        {
            Content = UiText.T("Her zaman bu dosya türünü seçilen uygulamayla aç"),
            Foreground = MainWindow.TextPrimary, IsEnabled = false
        };
        lower.Children.Add(saveDefault);
        Grid.SetRow(lower, 4);
        content.Children.Add(lower);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var cancel = new Button
        {
            Content = UiText.T("Vazgeç"), Padding = new Thickness(22, 10),
            Background = MainWindow.SurfaceBrush, Foreground = MainWindow.TextPrimary
        };
        var open = new Button
        {
            Content = UiText.T("Birlikte Aç"), Padding = new Thickness(22, 10),
            IsEnabled = false, IsDefault = true,
            Background = MainWindow.AccentBrush, Foreground = MainWindow.CanvasBrush
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(open);
        Grid.SetRow(buttons, 5);
        content.Children.Add(buttons);

        OpenWithChoices? choices = null;
        IReadOnlyList<DesktopApplication> allApps = [];
        bool loading = true;
        var selectedId = "";
        void UpdateDetails()
        {
            if (list.SelectedItem is not DesktopApplication app)
            {
                open.IsEnabled = false;
                if (!loading) selectedInfo.Text = UiText.T("Bir program seçin.");
                return;
            }
            selectedId = app.DesktopId;
            open.IsEnabled = true;
            if (choices is null) return;
            var currentDefault = app.DesktopId == choices.DefaultId ? "  ·  " + UiText.T("Şu anki varsayılan") : "";
            var isCompatible = app.Supports(choices.MimeType) ||
                choices.RecommendedIds.Contains(app.DesktopId) || app.DesktopId == choices.DefaultId;
            selectedInfo.Text = app.Name + currentDefault + "\n" + app.DesktopId +
                (isCompatible ? "" : "\n" + UiText.T("Bu uygulama bu dosya türü için kayıtlı değil; dosyayı açamayabilir."));
        }
        void Filter()
        {
            if (loading) return;
            var query = (search.Text ?? "").Trim();
            var suggestions = choices is null ? Array.Empty<DesktopApplication>() : allApps.Where(app =>
                app.DesktopId == choices.DefaultId || choices.RecommendedIds.Contains(app.DesktopId) ||
                app.Supports(choices.MimeType)).ToArray();
            var showEveryApp = showAll.IsChecked == true || query.Length > 0 || suggestions.Length == 0;
            // ShowAll deliberately includes NoDisplay helpers: users explicitly
            // requested every installed program, not only application-menu items.
            var apps = (showEveryApp ? allApps : suggestions.AsEnumerable())
                .Where(app => query.Length == 0 || app.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                    app.DesktopId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    app.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .OrderByDescending(app => app.DesktopId == choices?.DefaultId)
                .ThenByDescending(app => choices?.RecommendedIds.Contains(app.DesktopId) == true)
                .ThenBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            list.ItemsSource = apps;
            list.SelectedItem = apps.FirstOrDefault(app => app.DesktopId == selectedId) ?? apps.FirstOrDefault();
            if (apps.Count == 0)
            {
                selectedInfo.Text = UiText.T("Eşleşen uygulama bulunamadı. 'Tüm kurulu uygulamalar' seçeneğini deneyin.");
                open.IsEnabled = false;
            }
            else UpdateDetails();
        }
        list.SelectionChanged += (_, _) => UpdateDetails();
        search.TextChanged += (_, _) => Filter();
        showAll.IsCheckedChanged += (_, _) => Filter();
        cancel.Click += (_, _) => dialog.Close((OpenWithSelection?)null);
        void OpenSelected()
        {
            if (list.SelectedItem is not DesktopApplication app) return;
            dialog.Close(new OpenWithSelection(app, choices?.MimeType ?? "", saveDefault.IsChecked == true));
        }
        open.Click += (_, _) => OpenSelected();
        list.DoubleTapped += (_, _) => OpenSelected();
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { dialog.Close((OpenWithSelection?)null); e.Handled = true; }
        };
        using var cancellation = new CancellationTokenSource();
        dialog.Closed += (_, _) => cancellation.Cancel();
        dialog.Opened += async (_, _) =>
        {
            try
            {
                choices = await OpenWithService.LoadChoicesAsync(filePath, cancellation.Token);
                allApps = choices.Applications;
                mimeLabel.Text = choices.MimeType;
                selectedId = choices.DefaultId ?? "";
                // application/octet-stream covers unrelated unknown files.
                // Never change every unknown file's default unintentionally.
                saveDefault.IsEnabled = choices.MimeType != "application/octet-stream";
                if (!saveDefault.IsEnabled)
                    saveDefault.Content = UiText.T("Bilinmeyen dosya türü için varsayılan atama yapılamaz");
                selectedInfo.Text = $"{allApps.Count} uygulama bulundu. Önerilen programlardan birini seçin.";
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or ArgumentException)
            {
                // MIME detection can fail for unreadable files; opening with a
                // selected installed application still works when permissions allow.
                allApps = await Task.Run(() => OpenWithService.DiscoverApplications());
                mimeLabel.Text = UiText.T("MIME belirlenemedi");
                showAll.IsChecked = true;
                selectedInfo.Text = UiText.T("Dosya türü okunamadı: ") + ex.Message;
                saveDefault.IsEnabled = false;
            }
            loading = false;
            Filter();
            search.Focus();
        };
        var result = await dialog.ShowDialog<OpenWithSelection?>(owner);
        owner.Focus();
        return result;
    }
}
