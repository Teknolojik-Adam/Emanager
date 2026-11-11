using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Emanager.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<EmanagerApplication>().UsePlatformDetect().LogToTrace();
    public static void SetTheme(bool dark) => EmanagerApplication.SetTheme(dark);
}

public sealed class EmanagerApplication : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
    }
    public static void SetTheme(bool dark)
    {
        if (Current is { } app)
            app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
    }
    public override void OnFrameworkInitializationCompleted()
    {
        var dispatcher = Avalonia.Threading.Dispatcher.UIThread;
        dispatcher.UnhandledException -= OnDispatcherUnhandled;
        dispatcher.UnhandledException += OnDispatcherUnhandled;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandled;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandled;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
    private static void OnDispatcherUnhandled(object? sender, Avalonia.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the window alive: report the failure to the user via the crash log
        // instead of terminating with unsaved state.
        Report("DISPATCHER", e.Exception);
        e.Handled = true;
    }
    private static void OnDomainUnhandled(object? sender, UnhandledExceptionEventArgs e)
        => Report("DOMAIN", e.ExceptionObject as Exception);
    private static void Report(string stage, Exception? ex)
    {
        Console.Error.WriteLine($"EMANAGER-UNHANDLED-{stage}: {ex}");
        try { File.AppendAllText(Path.Combine(UserFolders.ConfigDirectory, "crash.log"), $"[{DateTime.UtcNow:O}] {stage}: {ex}\n"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
