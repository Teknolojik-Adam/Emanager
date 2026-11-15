using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Emanager.App;

public sealed record AndroidLocation(string Name, string Uri, bool IsMounted);
public sealed record AndroidFile(string Name, string Uri, bool IsDirectory, long? Size);

/// <summary>GIO/GVfs MTP bridge; intentionally never runs ADB or executes a shell.</summary>
public static class GioMtpSupport
{
    public static bool HasBackend => File.Exists("/usr/libexec/gvfsd-mtp") ||
                                     File.Exists("/usr/lib/gvfs/gvfsd-mtp") ||
                                     File.Exists("/usr/lib64/gvfs/gvfsd-mtp");

    public static IReadOnlyList<AndroidLocation> ParseMounts(string output)
    {
        var devices = new Dictionary<string, AndroidLocation>(StringComparer.Ordinal);
        var label = "Android";
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (Regex.IsMatch(line, @"^(Volume|Mount)\(\d+\):"))
            {
                var sep = line.IndexOf(": ", StringComparison.Ordinal);
                if (sep >= 0) label = line[(sep + 2)..].Split(" -> ")[0].Trim();
            }
            var match = Regex.Match(line, @"mtp://[^\s'\""<>]+", RegexOptions.IgnoreCase);
            if (!match.Success) continue;
            var uri = match.Value.TrimEnd(',', ')', '.');
            // Mount roots may include a trailing / and must preserve usb:port identifiers.
            uri = uri.TrimEnd('/') + "/";
            if (!IsMtpUri(uri)) continue;
            var mounted = line.StartsWith("Mount(", StringComparison.Ordinal) ||
                          line.Contains("default_location=", StringComparison.OrdinalIgnoreCase);
            if (!devices.TryGetValue(uri, out var current) || (mounted && !current.IsMounted))
                devices[uri] = new AndroidLocation(string.IsNullOrWhiteSpace(label) ? "Android" : label, uri, mounted);
        }
        return devices.Values.ToArray();
    }

    public static async Task<IReadOnlyList<AndroidLocation>> DiscoverAsync(CancellationToken ct = default)
    {
        if (!HasBackend) return [];
        return ParseMounts(await RunTextAsync(["mount", "-li"], ct));
    }

    public static bool IsMtpUri(string uri) =>
        !string.IsNullOrWhiteSpace(uri) && uri.StartsWith("mtp://", StringComparison.OrdinalIgnoreCase) &&
        !uri.Contains('\n') && !uri.Contains('\r') && !uri.Contains('\0');

    public static bool IsSafeLeafName(string fileName) => !string.IsNullOrWhiteSpace(fileName) &&
        fileName is not "." and not ".." &&
        fileName.IndexOfAny(['/', '\\', '\0', '\r', '\n']) < 0;

    public static string JoinUri(string directoryUri, string fileName)
    {
        if (!IsMtpUri(directoryUri) || !IsSafeLeafName(fileName))
            throw new ArgumentException("Invalid MTP path.");
        return directoryUri.TrimEnd('/') + "/" + Uri.EscapeDataString(fileName);
    }

    public static async Task MountAsync(string uri, CancellationToken ct = default)
    {
        if (!IsMtpUri(uri)) throw new ArgumentException("Not an MTP device.");
        await RunTextAsync(["mount", uri], ct);
    }

    public static async Task<IReadOnlyList<AndroidFile>> ListAsync(string uri, CancellationToken ct = default)
    {
        if (!IsMtpUri(uri)) throw new ArgumentException("Not an MTP location.");
        var output = await RunTextAsync(["list", "-lu", uri], ct);
        return ParseList(uri, output);
    }

    public static IReadOnlyList<AndroidFile> ParseList(string uri, string output)
    {
        if (!IsMtpUri(uri)) throw new ArgumentException("Not an MTP location.");
        var result = new List<AndroidFile>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.TrimEnd('\r').Split('\t');
            if (fields.Length < 3 || !IsMtpUri(fields[0])) continue;
            var itemUri = fields[0];
            if (!itemUri.StartsWith(uri.TrimEnd('/') + "/", StringComparison.Ordinal)) continue;
            var name = Uri.UnescapeDataString(itemUri.TrimEnd('/').Split('/').Last());
            if (!IsSafeLeafName(name)) continue;
            var directory = fields[2].Contains("directory", StringComparison.OrdinalIgnoreCase);
            result.Add(new AndroidFile(name, itemUri, directory,
                long.TryParse(fields[1], out var length) ? length : null));
        }
        return result.OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static async Task<bool> ExistsAsync(string uri, CancellationToken ct = default)
    {
        if (!IsMtpUri(uri)) throw new ArgumentException("Not an MTP URI.");
        try { await RunTextAsync(["info", uri], ct); return true; }
        catch (IOException) { return false; }
    }

    public static async Task CopyAsync(string source, string destination, Action<double>? report,
        CancellationToken ct = default)
    {
        if (!IsMtpUri(source) && !IsMtpUri(destination))
            throw new ArgumentException("A phone source or target is required.");
        if (!File.Exists("/usr/bin/gio")) throw new IOException("GIO is not installed.");
        var start = new ProcessStartInfo("/usr/bin/gio")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, CreateNoWindow = true
        };
        start.Environment["LC_ALL"] = "C";
        foreach (var arg in new[] { "copy", "-p", "-i", "-T", source, destination }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Failed to start gio.");
        // Never answer overwrite prompts affirmatively. Fails closed even if target appears mid-copy.
        process.StandardInput.Close();
        using var cancellation = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var errorTask = process.StandardError.ReadToEndAsync();
        var buffer = new char[1024];
        var last = new StringBuilder();
        try
        {
            while (true)
            {
                var count = await process.StandardOutput.ReadAsync(buffer.AsMemory(), ct);
                if (count == 0) break;
                last.Append(buffer, 0, count);
                if (last.Length > 4096) last.Remove(0, last.Length - 2048);
                // GIO emits CR-delimited progress using a localized / locale-independent message.
                foreach (var segment in last.ToString().Split('\r', '\n').TakeLast(3))
                {
                    var pct = ParseProgress(segment);
                    if (pct is { } progress) report?.Invoke(progress);
                }
            }
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            throw;
        }
        var error = await errorTask;
        if (Regex.IsMatch(last.ToString() + error, @"\boverwrite\b", RegexOptions.IgnoreCase))
            throw new IOException("Destination already exists; overwrite was refused.");
        if (process.ExitCode != 0) throw new IOException(string.IsNullOrWhiteSpace(error)
            ? "GIO file transfer failed." : error.Trim());
    }

    public static double? ParseProgress(string output)
    {
        var m = Regex.Match(output, @"([\d.]+)\s*([kMGT]?B)\s+(?:out of|/)\s+([\d.]+)\s*([kMGT]?B)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!m.Success) return null;
        var left = InBytes(m.Groups[1].Value, m.Groups[2].Value);
        var right = InBytes(m.Groups[3].Value, m.Groups[4].Value);
        return left is not null && right > 0 ? Math.Clamp(left.Value / right.Value * 100, 0, 100) : null;
    }

    private static double? InBytes(string amount, string unit)
    {
        if (!double.TryParse(amount, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return null;
        var factor = unit[0] switch { 'k' or 'K' => 1e3, 'M' => 1e6, 'G' => 1e9, 'T' => 1e12, _ => 1 };
        return value * factor;
    }

    private static async Task<string> RunTextAsync(string[] args, CancellationToken ct)
    {
        if (!File.Exists("/usr/bin/gio")) throw new IOException("GIO is not installed.");
        var start = new ProcessStartInfo("/usr/bin/gio")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("GIO could not start.");
        using var cancellation = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0) throw new IOException((await error).Trim());
        return await output;
    }
}
