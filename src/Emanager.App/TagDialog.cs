using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Emanager.Core.Metadata;

namespace Emanager.App;

public static class TagDialog
{
    public static async Task ShowAsync(Control owner, FileIdentity identity, IReadOnlyList<FileTag> current, Func<IReadOnlyList<FileTag>, Task> save)
    {
        var window = owner as Window ?? TopLevel.GetTopLevel(owner) as Window;
        if (window is null) return;
        var dialog = new Window { Title = UiText.T("Dosya etiketleri"), Width = 520, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = MainWindow.SidebarBrush, CanResize = false };
        var tags = new TextBox { Text = string.Join(", ", current.Select(x => x.Tag)), Watermark = UiText.T("iş, önemli, arşiv") };
        var color = new TextBox { Text = current.FirstOrDefault()?.Color ?? "", Watermark = UiText.T("Renk (isteğe bağlı), ör. #83C5FF") };
        var status = new TextBlock { Foreground = MainWindow.TextMuted, TextWrapping = TextWrapping.Wrap };
        var saveButton = new Button { Content = UiText.T("Kaydet"), Padding = new Thickness(14, 7), Background = MainWindow.AccentBrush, Foreground = MainWindow.CanvasBrush };
        var cancel = new Button { Content = UiText.T("Vazgeç"), Padding = new Thickness(14, 7) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right }; buttons.Children.Add(cancel); buttons.Children.Add(saveButton);
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = identity.Path, TextWrapping = TextWrapping.Wrap, Foreground = MainWindow.TextPrimary }); panel.Children.Add(new TextBlock { Text = UiText.T("Etiketler"), Foreground = MainWindow.TextMuted }); panel.Children.Add(tags); panel.Children.Add(new TextBlock { Text = UiText.T("Renk"), Foreground = MainWindow.TextMuted }); panel.Children.Add(color); panel.Children.Add(status); panel.Children.Add(buttons); dialog.Content = panel;
        cancel.Click += (_, _) => dialog.Close();
        saveButton.Click += async (_, _) =>
        {
            var values = (tags.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).Select(x => new FileTag(x, string.IsNullOrWhiteSpace(color.Text) ? null : color.Text.Trim())).ToArray();
            try { saveButton.IsEnabled = false; await save(values); dialog.Close(); } catch (Exception ex) { saveButton.IsEnabled = true; status.Text = ex.Message; }
        };
        await dialog.ShowDialog(window); window.Focus();
    }
}
