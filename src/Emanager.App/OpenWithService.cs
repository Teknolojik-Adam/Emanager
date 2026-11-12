using System.Diagnostics;
using System.Globalization;

namespace Emanager.App;

/// <summary>Freedesktop application discovery and MIME associations. No shell,
/// administrator privileges, or hand-parsing of desktop Exec commands.</summary>
internal sealed record DesktopApplication(
    string DesktopId, string Name, string DesktopFile, string Description,
    string IconName, IReadOnlyList<string> MimeTypes, bool NoDisplay)
{
    internal bool Supports(string mime) => MimeTypes.Contains(mime, StringComparer.OrdinalIgnoreCase);
    public override string ToString() => Name;
}

internal sealed record OpenWithChoices(
    string MimeType, string? DefaultId, IReadOnlySet<string> RecommendedIds,
    IReadOnlyList<DesktopApplication> Applications);

internal static class OpenWithService
{
    internal static IReadOnlyList<DesktopApplication> DiscoverApplications(
        IEnumerable<string>? dataDirectories = null)
    {
        var roots = dataDirectories ?? XdgDataDirectories();
        var found = new Dictionary<string, DesktopApplication>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            if (!Path.IsPathFullyQualified(root)) continue;
            var apps = Path.Combine(root, "applications");
            if (!Directory.Exists(apps)) continue;
            IEnumerable<string> desktopFiles;
            try { desktopFiles = Directory.EnumerateFiles(apps, "*.desktop", SearchOption.AllDirectories).Take(5000).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in desktopFiles)
            {
                var relative = Path.GetRelativePath(apps, file);
                var id = relative.Replace(Path.DirectorySeparatorChar, '-');
                if (!ValidDesktopId(id) || found.ContainsKey(id)) continue;
                var application = ReadDesktopEntry(file, id);
                if (application is not null) found.Add(id, application);
            }
        }
        return found.Values.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    internal static IEnumerable<string> XdgDataDirectories()
    {
        var home = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(home) || !Path.IsPathFullyQualified(home))
            home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        yield return home;
        var value = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        if (string.IsNullOrWhiteSpace(value)) value = "/usr/local/share:/usr/share";
        foreach (var root in value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (Path.IsPathFullyQualified(root)) yield return root;
    }

    internal static DesktopApplication? ReadDesktopEntry(string file, string desktopId)
    {
        try
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            bool inDesktopEntry = false;
            foreach (var line in File.ReadLines(file).Take(1000))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                {
                    inDesktopEntry = trimmed == "[Desktop Entry]";
                    continue;
                }
                if (!inDesktopEntry || trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
                var separator = line.IndexOf('=');
                if (separator <= 0) continue;
                values.TryAdd(line[..separator].Trim(), line[(separator + 1)..].Trim());
            }
            if (values.GetValueOrDefault("Type") != "Application" ||
                values.GetValueOrDefault("Hidden") == "true" ||
                (!values.ContainsKey("Exec") && values.GetValueOrDefault("DBusActivatable") != "true"))
                return null;
            var name = LocalizedName(values);
            if (string.IsNullOrWhiteSpace(name)) return null;
            var tryExec = values.GetValueOrDefault("TryExec");
            if (!string.IsNullOrWhiteSpace(tryExec) && !TryExecutableExists(tryExec)) return null;
            var desktops = (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "")
                .Split(':', StringSplitOptions.RemoveEmptyEntries);
            var onlyShowIn = SplitList(values.GetValueOrDefault("OnlyShowIn"));
            var notShowIn = SplitList(values.GetValueOrDefault("NotShowIn"));
            if (desktops.Length > 0 &&
                ((onlyShowIn.Length > 0 && !desktops.Any(onlyShowIn.Contains)) || desktops.Any(notShowIn.Contains)))
                return null;
            return new DesktopApplication(desktopId, Unescape(name), Path.GetFullPath(file),
                Unescape(values.GetValueOrDefault("Comment") ?? ""),
                values.GetValueOrDefault("Icon") ?? "",
                SplitList(values.GetValueOrDefault("MimeType")),
                values.GetValueOrDefault("NoDisplay") == "true");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private static string LocalizedName(Dictionary<string, string> values)
    {
        var language = CultureInfo.CurrentUICulture.Name.Replace('-', '_');
        if (values.TryGetValue("Name[" + language + "]", out var name)) return name;
        var neutral = language.Split('_')[0];
        if (values.TryGetValue("Name[" + neutral + "]", out name)) return name;
        return values.GetValueOrDefault("Name") ?? "";
    }

    private static string Unescape(string value) => value.Replace("\\s", " ").Replace("\\n", " ")
        .Replace("\\t", " ").Replace("\\r", " ").Replace("\\\\", "\\");

    private static string[] SplitList(string? value) => (value ?? "")
        .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static bool TryExecutableExists(string name)
    {
        if (Path.IsPathFullyQualified(name)) return File.Exists(name);
        if (name.Contains('/')) return false;
        var paths = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
        return paths.Split(Path.PathSeparator).Any(path => Path.IsPathFullyQualified(path) && File.Exists(Path.Combine(path, name)));
    }

    internal static bool ValidDesktopId(string value) => value.EndsWith(".desktop", StringComparison.Ordinal) &&
        value.Length is > 8 and < 256 && value.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-');

    internal static bool ValidMimeType(string mime)
    {
        if (mime.Length is < 3 or > 200 || mime.Count(c => c == '/') != 1) return false;
        var segments = mime.Split('/');
        return segments.All(x => x.Length > 0 && char.IsAsciiLetterOrDigit(x[0]) &&
            x.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '+' or '.' or '_'));
    }

    internal static async Task<OpenWithChoices> LoadChoicesAsync(string path, CancellationToken ct = default)
    {
        var mime = await GetMimeTypeAsync(path, ct);
        var defaultId = await GetDefaultAsync(mime, ct);
        IReadOnlySet<string> recommended;
        try
        {
            var output = await ExecuteAsync(TrustedTools.Require("gio"), ["mime", mime], ct);
            recommended = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(ValidDesktopId)
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (IOException) { recommended = new HashSet<string>(StringComparer.Ordinal); }
        var apps = await Task.Run(() => DiscoverApplications(), ct);
        return new OpenWithChoices(mime, defaultId, recommended, apps);
    }

    internal static async Task<string> GetMimeTypeAsync(string path, CancellationToken ct = default)
    {
        var fullPath = Path.GetFullPath(path);
        var output = (await ExecuteAsync(TrustedTools.Require("xdg-mime"), ["query", "filetype", fullPath], ct)).Trim();
        if (!ValidMimeType(output)) throw new IOException("Dosya türü (MIME) tespit edilemedi.");
        return output;
    }

    internal static async Task<string?> GetDefaultAsync(string mime, CancellationToken ct = default)
    {
        if (!ValidMimeType(mime)) throw new ArgumentException("Geçersiz MIME türü.", nameof(mime));
        var value = (await ExecuteAsync(TrustedTools.Require("xdg-mime"), ["query", "default", mime], ct)).Trim();
        return ValidDesktopId(value) ? value : null;
    }

    internal static async Task SetDefaultAsync(string mime, DesktopApplication app, CancellationToken ct = default)
    {
        if (!ValidMimeType(mime) || !ValidDesktopId(app.DesktopId) || !File.Exists(app.DesktopFile))
            throw new ArgumentException("Uygulama veya dosya türü geçersiz.");
        await ExecuteAsync(TrustedTools.Require("xdg-mime"), ["default", app.DesktopId, mime], ct);
        var actual = await GetDefaultAsync(mime, ct);
        if (!string.Equals(actual, app.DesktopId, StringComparison.Ordinal))
            throw new IOException("Varsayılan uygulama atanamadı. Masaüstü MIME ayarlarını kontrol edin.");
    }

    internal static ProcessStartInfo BuildLaunchInfo(DesktopApplication app, string file)
    {
        if (!ValidDesktopId(app.DesktopId) || !Path.IsPathFullyQualified(app.DesktopFile) ||
            !File.Exists(app.DesktopFile))
            throw new IOException("Seçilen uygulama artık yüklü değil.");
        var start = new ProcessStartInfo(TrustedTools.Require("gio"))
        {
            UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("launch");
        start.ArgumentList.Add(app.DesktopFile);
        start.ArgumentList.Add(Path.GetFullPath(file));
        TrustedTools.SecurePath(start);
        return start;
    }

    internal static async Task LaunchAsync(DesktopApplication app, string file, CancellationToken ct = default)
    {
        var start = BuildLaunchInfo(app, file);
        await ExecuteProcessAsync(start, ct);
    }

    private static Task<string> ExecuteAsync(string executable, string[] arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        TrustedTools.SecurePath(start);
        return ExecuteProcessAsync(start, ct);
    }

    private static async Task<string> ExecuteProcessAsync(ProcessStartInfo start, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(20));
        using var process = Process.Start(start) ?? throw new IOException("Uygulama başlatılamadı.");
        var stdout = process.StandardOutput.ReadToEndAsync(limit.Token);
        var stderr = process.StandardError.ReadToEndAsync(limit.Token);
        try
        {
            await process.WaitForExitAsync(limit.Token);
            var result = await stdout;
            var errors = await stderr;
            if (process.ExitCode != 0)
                throw new IOException(string.IsNullOrWhiteSpace(errors) ? "İşlem başarısız: " + result.Trim() : errors.Trim());
            return result;
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException("Uygulama/MIME işlemi zaman aşımına uğradı.");
        }
    }
}
