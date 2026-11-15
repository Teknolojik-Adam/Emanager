using System.Diagnostics;
using System.Text.RegularExpressions;
using Emanager.Core.Network;

namespace Emanager.Core.Storage;

/// <summary>GIO-based Android MTP backend. It never invokes a shell or guesses a USB device path.</summary>
public sealed class GvfsMtpProvider : IMtpProvider
{
    public bool IsAvailable => File.Exists("/usr/bin/gio") &&
        (File.Exists("/usr/libexec/gvfsd-mtp") || File.Exists("/usr/lib/gvfs/gvfsd-mtp") ||
         File.Exists("/usr/lib64/gvfs/gvfsd-mtp"));

    public async Task<IReadOnlyList<MtpDevice>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return [];
        var output = await RunAsync(["mount", "-li"], cancellationToken);
        var devices = new Dictionary<string, MtpDevice>(StringComparer.Ordinal);
        var name = "Android";
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (Regex.IsMatch(line, @"^(Volume|Mount)\(\d+\):"))
            {
                var sep = line.IndexOf(": ", StringComparison.Ordinal);
                if (sep >= 0) name = line[(sep + 2)..].Split(" -> ")[0].Trim();
            }
            var match = Regex.Match(line, @"mtp://[^\s'""<>]+", RegexOptions.IgnoreCase);
            if (!match.Success) continue;
            var uri = match.Value.TrimEnd(',', ')', '.').TrimEnd('/') + "/";
            if (!ValidUri(uri)) continue;
            devices[uri] = new MtpDevice(uri, name, true);
        }
        return devices.Values.ToArray();
    }

    public async IAsyncEnumerable<NetworkEntry> ListAsync(string deviceId, string path,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) throw new PlatformNotSupportedException("GVfs MTP backend is unavailable.");
        var uri = Resolve(deviceId, path);
        var output = await RunAsync(["list", "-lu", uri], cancellationToken);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fields = line.TrimEnd('\r').Split('\t');
            if (fields.Length < 3 || !ValidUri(fields[0])) continue;
            var item = fields[0];
            if (!item.StartsWith(uri.TrimEnd('/') + "/", StringComparison.Ordinal)) continue;
            var name = Uri.UnescapeDataString(item.TrimEnd('/').Split('/').Last());
            if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
                name.IndexOfAny(['/', '\\', '\0', '\r', '\n']) >= 0) continue;
            yield return new NetworkEntry(name, item,
                fields[2].Contains("directory", StringComparison.OrdinalIgnoreCase),
                long.TryParse(fields[1], out var size) ? size : null);
        }
    }

    public async Task CopyToDeviceAsync(string localPath, string deviceId, string remotePath,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) throw new PlatformNotSupportedException("GVfs MTP backend is unavailable.");
        if (!File.Exists(localPath) || File.GetAttributes(localPath).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Only regular local files can be uploaded.");
        var target = Resolve(deviceId, remotePath);
        var name = Path.GetFileName(localPath);
        if (remotePath.EndsWith('/')) target += Uri.EscapeDataString(name);
        // GIO's -i safeguards against replacing existing phone files without confirmation.
        await RunAsync(["copy", "-i", "-T", localPath, target], cancellationToken, denyOverwrite: true);
        progress?.Report(new FileInfo(localPath).Length);
    }

    private static bool ValidUri(string uri) => uri.StartsWith("mtp://", StringComparison.OrdinalIgnoreCase) &&
        !uri.Contains('\0') && !uri.Contains('\r') && !uri.Contains('\n');

    private static string Resolve(string deviceId, string relative)
    {
        if (!ValidUri(deviceId)) throw new ArgumentException("MTP device ID must be a GIO MTP URI.");
        if (relative.StartsWith("mtp://", StringComparison.OrdinalIgnoreCase))
        {
            if (!relative.StartsWith(deviceId.TrimEnd('/') + "/", StringComparison.Ordinal))
                throw new ArgumentException("MTP path is outside the selected phone.");
            return relative;
        }
        return deviceId.TrimEnd('/') + "/" + relative.TrimStart('/');
    }

    private static async Task<string> RunAsync(string[] args, CancellationToken token, bool denyOverwrite = false)
    {
        var info = new ProcessStartInfo("/usr/bin/gio")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        info.Environment["LC_ALL"] = "C";
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("GIO failed to start.");
        process.StandardInput.Close();
        using var registration = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var outputTask = process.StandardOutput.ReadToEndAsync(token);
        var errorTask = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        var output = await outputTask;
        var error = await errorTask;
        if (denyOverwrite && Regex.IsMatch(output + error, @"\boverwrite\b", RegexOptions.IgnoreCase))
            throw new IOException("Phone target already exists; no overwrite performed.");
        if (process.ExitCode != 0) throw new IOException(string.IsNullOrWhiteSpace(error)
            ? "GIO operation failed." : error.Trim());
        return output;
    }
}
