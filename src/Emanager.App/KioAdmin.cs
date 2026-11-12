using System.Diagnostics;
using System.Globalization;

namespace Emanager.App;

/// <summary>KDE's installed KIO admin worker via kioclient. Never launches the UI as root.</summary>
internal static class KioAdmin
{
    private static readonly object sessionGate = new();
    private static CancellationTokenSource sessionCancellation = new();
    private static TaskCompletionSource allOperationsFinished = CompletedSignal();
    private static int activeOperations;
    private static bool enabled;
    private static DateTime lastActivityUtc = DateTime.UtcNow;

    internal static bool IsIdleFor(TimeSpan duration)
    {
        lock (sessionGate) return enabled && activeOperations == 0 &&
            DateTime.UtcNow - lastActivityUtc >= duration;
    }
#if EMANAGER_SECURITY_TEST
    internal static string TestClientPath = "/usr/bin/kioclient";
    internal static TimeSpan? TestOperationTimeout;
#endif

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }

    internal static bool Enabled { get { lock (sessionGate) return enabled; } }

    internal static void Enable()
    {
        lock (sessionGate)
        {
            if (enabled) return;
            lastActivityUtc = DateTime.UtcNow;
            enabled = true;
        }
    }

    // Disabling is not just a UI flag: block new work, cancel in-flight clients,
    // and wait until each child process has been reaped before showing normal mode.
    internal static async Task DisableAsync()
    {
        CancellationTokenSource oldSession;
        Task drain;
        lock (sessionGate)
        {
            enabled = false;
            oldSession = sessionCancellation;
            sessionCancellation = new CancellationTokenSource();
            drain = allOperationsFinished.Task;
        }
        oldSession.Cancel();
        try { await drain; }
        finally { oldSession.Dispose(); }
    }

    internal static string AdminUrl(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var segments = fullPath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString);
        return "admin:///" + string.Join('/', segments);
    }
    internal static string FileUrl(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;
    internal static string Url(string path) => Enabled ? AdminUrl(path) : FileUrl(path);

    internal static Task<string> RunAsync(string verb, CancellationToken ct = default, params string[] args) =>
        RunCoreAsync(verb, false, ct, args);

    // Only the initial authorization probe is allowed before a session exists.
    internal static Task<string> ProbeAsync(CancellationToken ct) =>
        RunCoreAsync("ls", true, ct, "admin:///etc");

    private static async Task<string> RunCoreAsync(string verb, bool authorizationProbe,
        CancellationToken ct, params string[] args)
    {
        CancellationToken sessionToken;
        lock (sessionGate)
        {
            if (!authorizationProbe && !enabled) throw new OperationCanceledException("KIO-Admin oturumu kapalı.");
            sessionToken = sessionCancellation.Token;
            lastActivityUtc = DateTime.UtcNow;
            if (activeOperations++ == 0)
                allOperationsFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, sessionToken);
            var maximumDuration = authorizationProbe ? TimeSpan.FromSeconds(120) :
                verb is "copy" or "move" or "remove" ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(60);
#if EMANAGER_SECURITY_TEST
            maximumDuration = TestOperationTimeout ?? maximumDuration;
            var psi = new ProcessStartInfo(TestClientPath)
#else
            var psi = new ProcessStartInfo("/usr/bin/kioclient")
#endif
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            timeout.CancelAfter(maximumDuration);
            TrustedTools.SecurePath(psi);
            psi.ArgumentList.Add(verb);
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi) ?? throw new IOException("KDE KIO istemcisi başlatılamadı.");
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                var output = await stdout.WaitAsync(timeout.Token);
                var error = await stderr.WaitAsync(timeout.Token);
                if (process.ExitCode != 0)
                    throw new IOException($"KIO {verb} başarısız: " +
                        (string.IsNullOrWhiteSpace(error) ? output : error).Trim());
                return output;
            }
            catch (OperationCanceledException)
            {
                // Killing the client is best effort: the privileged KDE worker is
                // a separate service, so partial writes must still be reported.
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception ex) when (ex is InvalidOperationException or
                    System.ComponentModel.Win32Exception or TimeoutException)
                {
                    throw new IOException("KIO istemcisi iptal edildi fakat süreç kapanışı doğrulanamadı.", ex);
                }
                if (ct.IsCancellationRequested || sessionToken.IsCancellationRequested)
                    throw new OperationCanceledException("KIO işlemi iptal edildi; kısmi dosyaları kontrol edin.");
                throw new TimeoutException($"KIO {verb} zaman aşımı; kısmi dosyaları kontrol edin.");
            }
        }
        finally
        {
            lock (sessionGate)
            {
                lastActivityUtc = DateTime.UtcNow;
                if (--activeOperations == 0) allOperationsFinished.TrySetResult();
            }
        }
    }

    internal static async Task<IReadOnlyList<FileEntry>> ListAsync(string directory, CancellationToken ct)
    {
        var output = await RunAsync("ls", ct, AdminUrl(directory));
        var results = new List<FileEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            ct.ThrowIfCancellationRequested();
            var itemName = name.TrimEnd('\r');
            if (itemName is "." or ".." || itemName.Contains('/') || itemName.Contains('\0') ||
                itemName.Contains('\r') || !seen.Add(itemName)) continue;
            var path = Path.Combine(directory, itemName);
            // Locally accessible entries can be verified cheaply by the OS.
            // Only privileged/unreadable entries need a KIO stat round-trip.
            // A newline-spoofed ls record that resolves locally is a real,
            // locally visible path, not an arbitrary privileged path.
            var local = FileSystem.ReadEntry(path);
            if (local is not null) { results.Add(local); continue; }
            // For privileged paths, the newline-delimited ls output is not
            // authoritative. Stat the exact candidate and verify its NAME.
            string stat;
            try { stat = await RunAsync("stat", ct, AdminUrl(path)); }
            catch (IOException) { continue; }
            var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in stat.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = line.IndexOf(' ');
                if (separator > 0 && !attributes.ContainsKey(line[..separator]))
                    attributes.Add(line[..separator], line[(separator + 1)..].TrimEnd('\r'));
            }
            if (attributes.GetValueOrDefault("NAME") != itemName ||
                !attributes.ContainsKey("FILE_TYPE")) continue;
            var mode = attributes.GetValueOrDefault("FILE_TYPE", "");
            var isDirectory = mode == "040000" || mode == "0040000" || mode.StartsWith("04", StringComparison.Ordinal);
            var isLink = mode.StartsWith("012", StringComparison.Ordinal);
            _ = long.TryParse(attributes.GetValueOrDefault("SIZE"), CultureInfo.InvariantCulture, out var size);
            var kind = isDirectory ? "Klasör" : isLink ? "Bağlantı" : "Dosya";
            results.Add(new FileEntry(path, itemName, isDirectory, isLink, size,
                DateTime.MinValue, kind, attributes.GetValueOrDefault("ACCESS", "")));
        }
        return results;
    }

    internal static Task<string> CopyAsync(string source, string directory) =>
        RunAsync("copy", default, Url(source), AdminUrl(Path.Combine(directory, Path.GetFileName(source))));
    internal static Task<string> MoveAsync(string source, string directory) =>
        RunAsync("move", default, Url(source), AdminUrl(Path.Combine(directory, Path.GetFileName(source))));
    internal static Task<string> RenameAsync(string source, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName is "." or ".." ||
            newName.Contains('/') || newName.Contains('\\') || newName.Contains('\0') ||
            Path.GetFileName(newName) != newName)
            throw new ArgumentException("Yeni ad yalnızca tek bir dosya adı olmalıdır.", nameof(newName));
        var parent = Path.GetDirectoryName(Path.GetFullPath(source))
            ?? throw new IOException("Kaynak üst klasörü çözümlenemedi.");
        return RunAsync("move", default, AdminUrl(source), AdminUrl(Path.Combine(parent, newName)));
    }
    internal static Task<string> RemoveAsync(string source) => RunAsync("remove", default, AdminUrl(source));
}
