using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Emanager.Core.FileOperations;

namespace Emanager.App;

public static class OperationRecoveryDialog
{
    public static async Task ShowAsync(Window owner, FileOperationManager manager)
    {
        var dialog = new Window { Title = UiText.T("Geri alınamayan işlemler"), Width = 720, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = MainWindow.SidebarBrush, CanResize = false };
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(20) };
        var entries = manager.Recoverable;
        panel.Children.Add(new TextBlock { Text = UiText.T("Önceki oturumda yarım kalan dosya işlemleri var. Kaynak ve hedef hâlâ erişilebiliyorsa devam edin."), TextWrapping = Avalonia.Media.TextWrapping.Wrap, Foreground = MainWindow.TextMuted });
        panel.Children.Add(new ListBox { ItemsSource = entries.Select(x => $"{x.Kind}: {x.Source} → {x.Destination} ({x.State})").ToArray(), MaxHeight = 260 });
        var status = new TextBlock { Foreground = MainWindow.TextMuted };
        var resume = new Button { Content = UiText.T("Devam et"), Padding = new Thickness(14, 7), Background = MainWindow.AccentBrush, Foreground = MainWindow.CanvasBrush };
        var skip = new Button { Content = UiText.T("Atla"), Padding = new Thickness(14, 7) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right }; buttons.Children.Add(skip); buttons.Children.Add(resume); panel.Children.Add(status); panel.Children.Add(buttons); dialog.Content = panel;
        skip.Click += (_, _) => dialog.Close();
        resume.Click += async (_, _) => { try { resume.IsEnabled = false; var results = await manager.ResumeRecoverableAsync(); status.Text = UiText.F("{0} işlem devam ettirildi.", results.Count(x => x.State == FileOperationState.Completed)); } catch (Exception ex) { status.Text = ex.Message; resume.IsEnabled = true; } };
        await dialog.ShowDialog(owner); owner.Focus();
    }
}
