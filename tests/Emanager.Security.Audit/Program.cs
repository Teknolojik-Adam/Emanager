namespace Emanager.App;

// Minimal compile-time surface for KioAdmin's URL-only security checks.
public sealed record FileEntry(string FullPath, string Name, bool IsDirectory, bool IsLink,
    long Length, DateTime Modified, string Kind, string Permissions);

public static class FileSystem
{
    public static FileEntry? ReadEntry(string path) => null;
}

static class SecurityAudit
{
    private static int failures;

    private static void Check(bool condition, string name)
    {
        if (condition) Console.WriteLine($"PASS: {name}");
        else { Console.WriteLine($"FAIL: {name}"); failures++; }
    }

    public static async Task<int> RunAsync()
    {
        // A KIO URL must preserve the filename as data, not let URI syntax
        // reinterpret spaces, '#', '?', '%' or Unicode characters.
        var actual = KioAdmin.AdminUrl("/tmp/a b#c?d%e-ç.txt");
        var expected = "admin:///tmp/a%20b%23c%3Fd%25e-%C3%A7.txt";
        Check(actual == expected, "AdminUrl percent-encodes special and Unicode filename characters");
        Check(!actual.Contains('#') && !actual.Contains('?') && !actual.Contains('%', StringComparison.Ordinal) ||
              actual.Contains("%25", StringComparison.Ordinal),
            "AdminUrl does not expose fragment/query delimiters or raw percent escapes");
        var filePaneSource = Path.Combine(Directory.GetCurrentDirectory(), "src", "Emanager.App", "FilePane.cs");
        var filePaneText = File.ReadAllText(filePaneSource);
        Check(filePaneText.Contains("Task<NavigationResult> NavigateAsync", StringComparison.Ordinal) &&
              filePaneText.Contains("Task<NavigationResult> RefreshAsync", StringComparison.Ordinal),
            "navigation and refresh expose an explicit success result");
        try
        {
            _ = KioAdmin.RenameAsync("/tmp/source", "../outside");
            Check(false, "KIO rename rejects path traversal names");
        }
        catch (ArgumentException)
        {
            Check(true, "KIO rename rejects path traversal names");
        }
        // Isolated test process, not KDE and not a privileged operation.
        KioAdmin.TestClientPath = "/bin/sleep";
        KioAdmin.TestOperationTimeout = TimeSpan.FromMilliseconds(200);
        KioAdmin.Enable();
        Check(!KioAdmin.IsIdleFor(TimeSpan.FromDays(1)), "recent root session is not idle");
        Check(KioAdmin.IsIdleFor(TimeSpan.Zero), "root session idle expiration can be checked");
        try
        {
            await KioAdmin.RunAsync("3");
            Check(false, "KIO operation timeout stops the child process");
        }
        catch (TimeoutException)
        {
            Check(true, "KIO operation timeout stops the child process");
        }
        await KioAdmin.DisableAsync();

        KioAdmin.TestOperationTimeout = TimeSpan.FromSeconds(10);
        KioAdmin.Enable();
        var ongoing = KioAdmin.RunAsync("3");
        await Task.Delay(150);
        await KioAdmin.DisableAsync();
        try
        {
            await ongoing;
            Check(false, "Disabling admin cancels running operations");
        }
        catch (OperationCanceledException)
        {
            Check(true, "Disabling admin cancels running operations");
        }
        Check(!KioAdmin.Enabled, "Admin session is disabled after operation drain");
        try
        {
            await KioAdmin.RunAsync("1");
            Check(false, "Disabled admin refuses new processes");
        }
        catch (OperationCanceledException)
        {
            Check(true, "Disabled admin refuses new processes");
        }
        // Authorization probe must also be tracked by DisableAsync: closing
        // the app while Polkit is open cannot leave a detached kioclient.
        var mock = Path.Combine(Path.GetTempPath(), "emanager-probe-fixture-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(mock, "#!/bin/sh\nexec /bin/sleep 3\n");
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(mock, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            KioAdmin.TestClientPath = mock;
            KioAdmin.TestOperationTimeout = TimeSpan.FromSeconds(10);
            var probe = KioAdmin.ProbeAsync(CancellationToken.None);
            await Task.Delay(150);
            await KioAdmin.DisableAsync();
            try { await probe; Check(false, "closing cancels an in-flight authorization probe"); }
            catch (OperationCanceledException)
            { Check(true, "closing cancels an in-flight authorization probe"); }
        }
        finally { File.Delete(mock); }
        // Simulate KIO's newline-delimited ls output: a fabricated line may
        // not become a privileged entry without an independent matching stat.
        var listingMock = Path.Combine(Path.GetTempPath(), "emanager-kio-list-fixture-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(listingMock,
                "#!/bin/sh\n" +
                "if [ \"$1\" = ls ]; then printf 'legit\\nspoofed\\n'; exit 0; fi\n" +
                "if [ \"$1\" = stat ]; then\n" +
                "  case \"$2\" in\n" +
                "    *legit) printf 'NAME legit\\nFILE_TYPE 0100000\\nSIZE 2\\n'; exit 0 ;;\n" +
                "    *spoofed) printf 'NAME other\\nFILE_TYPE 0100000\\nSIZE 1\\n'; exit 0 ;;\n" +
                "  esac\nfi\nexit 1\n");
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(listingMock, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            KioAdmin.TestClientPath = listingMock;
            KioAdmin.TestOperationTimeout = TimeSpan.FromSeconds(5);
            KioAdmin.Enable();
            var results = await KioAdmin.ListAsync("/tmp", CancellationToken.None);
            Check(results.Count == 1 && results[0].Name == "legit", "KIO ls forged records refused by stat NAME verification");
            await KioAdmin.DisableAsync();
        }
        finally { File.Delete(listingMock); }
        return failures == 0 ? 0 : 1;
    }
}

public static class Program
{
    public static Task<int> Main() => SecurityAudit.RunAsync();
}
