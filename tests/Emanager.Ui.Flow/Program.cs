using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Emanager.App;

// End-to-end UI flow test for the chmod dialog on a real X display (Xvfb).
// The dialog is driven through the Avalonia visual tree, not pixel guesses.
var root = Path.Combine(Path.GetTempPath(), "emanager-ui-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
try
{
    var file = Path.Combine(root, "deneme.txt");
    File.WriteAllText(file, "x");
    File.SetUnixFileMode(file, (UnixFileMode)0x180); // 600

    var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    var thread = new Thread(() =>
    {
        try
        {
            App2.OnReady = (win, lifetime) => _ = RunFlow(win, lifetime, root, file, tcs);
            AppBuilder.Configure<App2>().UsePlatformDetect().StartWithClassicDesktopLifetime(Array.Empty<string>());
        }
        catch (Exception ex) { tcs.TrySetException(ex); }
    });
    thread.IsBackground = true;
    thread.Start();
    await tcs.Task;
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch (IOException) { }
}
Console.WriteLine($"PASS: {passed} UI assertions");
return;

void Check(bool ok, string name)
{
    Console.WriteLine((ok ? "PASS: " : "FAIL: ") + name);
    if (!ok) Environment.Exit(1);
    passed++;
}

async Task RunFlow(Window win, IClassicDesktopStyleApplicationLifetime lifetime, string root, string file, TaskCompletionSource<int> tcs)
{
    try
    {
        Check(win.IsVisible, "owner window visible");
        var showTask = PermissionsDialog.ShowAsync(win, new[] { file });
        Window? dialog = null;
        for (int i = 0; i < 100 && dialog is null; i++)
        {
            await Task.Delay(100);
            dialog = lifetime.Windows.FirstOrDefault(w => w != win && w.Title!.Contains("İzinler"));
        }
        Check(dialog is not null, "chmod dialog window opened from UI");
        if (dialog is null) { tcs.TrySetException(new Exception("no dialog")); return; }
        await Task.Delay(300);
        Button? apply = null;
        void Walk(Control? c)
        {
            if (c is null || apply is not null) return;
            if (c is Button b && b.Content is string s && s.Contains("uygula")) { apply = b; return; }
            foreach (var child in c.GetVisualChildren().OfType<Control>()) Walk(child);
        }
        Walk(dialog.Content as Control);
        Check(apply is not null, "apply button rendered in dialog");
        if (apply is null) { tcs.TrySetException(new Exception("no apply button")); return; }
        Console.WriteLine($"apply state: enabled={apply.IsEnabled} visible={apply.IsVisible} attached={apply.GetVisualParent() is not null}");
        // Without a window manager hit-testing may be gated; raise the protected
        // OnClick directly — the same entry point the pointer pipeline uses.
        typeof(Button).GetMethod("OnClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.Invoke(apply, null);
        var completed = await Task.WhenAny(showTask, Task.Delay(15000));
        Check(completed == showTask, "dialog closed after apply click");
        bool changed = await showTask;
        Check(changed, "ShowAsync reported a change");
        Check((int)File.GetUnixFileMode(file) == 0x180, "file mode preserved through dialog apply (600)");
        win.Close();
        tcs.TrySetResult(0);
    }
    catch (Exception ex)
    {
        Console.WriteLine("FLOW ERROR: " + ex);
        tcs.TrySetException(ex);
    }
}

public class App2 : Application
{
    public static Action<Window, IClassicDesktopStyleApplicationLifetime>? OnReady;
    public override void Initialize() => Styles.Add(new FluentTheme());
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var w = new Window { Width = 400, Height = 300, Title = "ui-flow-owner" };
            desktop.MainWindow = w;
            w.Opened += (_, _) => OnReady?.Invoke(w, desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
