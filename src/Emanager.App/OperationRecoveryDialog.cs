using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Emanager.Core.FileOperations;

namespace Emanager.App;

public static class OperationRecoveryDialog
{
    public static async Task ShowAsync(Window owner, FileOperationManager manager)
    {
        var dialog = new Window { Title = UiText.T("Yarım kalan işlemler"), Width = 720, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = MainWindow.SidebarBrush, CanResize = false };
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(20) };
        var entries = manager.Recoverable;
        panel.Children.Add(new TextBlock { Text = UiText.T("Önceki oturumda yarım kalan dosya işlemleri var. İşlemler son bayt konumundan sürdürülmez; baştan denenir. Hedef zaten varsa her çakışma için yeni bir karar istenir. İptal edilen işlem daha sonra yeniden denenebilir."), TextWrapping = Avalonia.Media.TextWrapping.Wrap, Foreground = MainWindow.TextMuted });
        panel.Children.Add(new ListBox { ItemsSource = entries.Select(x => $"{x.Kind}: {x.Source} → {x.Destination} ({x.State})").ToArray(), MaxHeight = 260 });
        var status = new TextBlock { Foreground = MainWindow.TextMuted };
        var resume = new Button { Content = UiText.T("Baştan dene"), Padding = new Thickness(14, 7), Background = MainWindow.AccentBrush, Foreground = MainWindow.CanvasBrush, IsEnabled = entries.Count > 0 };
        var skip = new Button { Content = UiText.T("Atla"), Padding = new Thickness(14, 7) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right }; buttons.Children.Add(skip); buttons.Children.Add(resume); panel.Children.Add(status); panel.Children.Add(buttons); dialog.Content = panel;
        skip.Click += (_, _) => dialog.Close();
        async Task<ConflictAction?> ResolveConflictAsync(FileConflict conflict, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var choice = await ConflictDialog.ShowAsync(owner, conflict, allowApplyToAll: false);
            return choice.Action;
        }
        resume.Click += async (_, _) =>
        {
            try
            {
                resume.IsEnabled = false;
                var results = await manager.RetryRecoverableAsync(ResolveConflictAsync);
                var completed = results.Count(x => x.State == FileOperationState.Completed);
                var skipped = results.Count(x => x.State == FileOperationState.Skipped);
                var cancelled = results.Count(x => x.State == FileOperationState.Cancelled);
                var failed = results.Count(x => x.State == FileOperationState.Failed);
                status.Text = UiText.F("{0} tamamlandı · {1} atlandı · {2} iptal edildi · {3} başarısız", completed, skipped, cancelled, failed);
                var errors = results.Where(x => x.Error is not null)
                    .Select(result => $"{entries.FirstOrDefault(entry => entry.JobId == result.JobId)?.Source ?? result.JobId.ToString()}: {result.Error!.Message}")
                    .ToArray();
                if (errors.Length > 0) status.Text += "\n" + string.Join("\n", errors);
                resume.IsEnabled = manager.Recoverable.Count > 0;
            }
            catch (Exception ex) { status.Text = ex.Message; resume.IsEnabled = manager.Recoverable.Count > 0; }
        };
        await dialog.ShowDialog(owner); owner.Focus();
    }
}
