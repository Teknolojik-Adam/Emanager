using Avalonia;
using System.Runtime.InteropServices;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Emanager.App;

internal static class Program
{
    [STAThread]
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    public static void Main(string[] args)
    {
        if (OperatingSystem.IsLinux() && GetEffectiveUserId() == 0)
        {
            Console.Error.WriteLine("Emanager root olarak başlatılamaz. Normal kullanıcı olarak açıp KIO-Admin/Polkit kullanın.");
            Environment.ExitCode = 1;
            return;
        }
        // A sandbox, SSH session or background runner may have no graphical session.
        // Avalonia otherwise throws XOpenDisplay failed and .NET aborts (SIGABRT).
        if (OperatingSystem.IsLinux() &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")) &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            Console.Error.WriteLine("Emanager: Grafik oturumu bulunamadı (DISPLAY/WAYLAND_DISPLAY). GNOME uygulama listesinden açın.");
            Environment.ExitCode = 2;
            return;
        }
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Emanager başlatılamadı: {ex.GetType().Name}: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }
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
        // A faulted task nobody awaited used to terminate the process with
        // SIGABRT (fail-fast). Log it and mark it observed instead; the UI keeps
        // serving the user and the failure stays diagnosable in crash.log.
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException -= OnUnobservedTask;
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTask;
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
    private static void OnUnobservedTask(object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
    {
        Report("TASK", e.Exception);
        e.SetObserved();
    }
    private static string Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "";
        var text = message.Replace('\r', ' ').Replace('\n', ' ');
        var home = UserFolders.Home;
        if (!string.IsNullOrEmpty(home)) text = text.Replace(home, "~", StringComparison.Ordinal);
        return text.Length > 160 ? text[..160] + "…" : text;
    }
    private static string Describe(Exception? ex)
    {
        // AggregateException hides the real fault from callers; unwrap chains.
        var parts = new List<string>();
        while (ex is not null)
        {
            if (ex is AggregateException agg && agg.InnerExceptions.Count > 0)
            {
                parts.Add(agg.GetType().Name);
                ex = agg.InnerException;
                continue;
            }
            parts.Add(ex.GetType().FullName + ": " + Sanitize(ex.Message));
            ex = ex.InnerException;
        }
        return string.Join(" <- ", parts);
    }
    private static void Report(string stage, Exception? ex)
    {
        // Type + frames give the diagnosis; the message is sanitized because it
        // can embed user paths. Credentials never reach this text (they are not
        // part of exception messages thrown by this code base).
        // AggregateException carries no stack of its own; log the deepest frames.
        Exception? frameSource = ex;
        while (frameSource?.InnerException is { } inner) frameSource = inner;
        var line = $"[{DateTime.UtcNow:O}] {stage}: {Describe(ex)}" +
                   (frameSource is null ? "" : $" frames={TrashDiagnostics.Frames(frameSource)}") + "\n";
        Console.Error.Write(line);
        try
        {
            Directory.CreateDirectory(UserFolders.ConfigDirectory);
            var path = Path.Combine(UserFolders.ConfigDirectory, "crash.log");
            if (OperatingSystem.IsLinux())
            {
                try { if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) return; }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
            var options = new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None
            };
            if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var output = new FileStream(path, options);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            if (output.Length > 1024 * 1024) output.SetLength(0);
            output.Seek(0, SeekOrigin.End);
            using var writer = new StreamWriter(output);
            writer.Write(line);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
