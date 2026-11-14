using System.Diagnostics;

namespace Emanager.App;

/// <summary>
/// Read-only network mount discovery plus explicit user-initiated GVfs connections.
/// Credentials are never saved, logged, or passed as shell commands.
/// </summary>
public static class NetworkLocations
{
    public static bool TryNormalize(string value, out string normalized)
    {
        normalized = "";
        // Detect traversal before System.Uri canonicalizes /a/../b into /b.
        if (value is null || value.Split('/').Any(part =>
            Uri.UnescapeDataString(part.Split('?', '#')[0]) is "." or "..")) return false;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != "smb" && uri.Scheme != "sftp" && uri.Scheme != "ftp" && uri.Scheme != "ftps" && uri.Scheme != "dav" && uri.Scheme != "davs") ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) && Uri.UnescapeDataString(uri.UserInfo).Contains(':') ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return false;
        if (uri.Scheme == "smb" && uri.AbsolutePath.Trim('/').Length == 0) return false;
        if (uri.Scheme == "smb" && uri.Port != -1) return false;
        var segments = Uri.UnescapeDataString(uri.AbsolutePath).Split('/');
        if (segments.Any(x => x == "." || x == "..")) return false;
        normalized = uri.AbsoluteUri;
        return true;
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    public static string MountRoot
    {
        get
        {
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (!string.IsNullOrEmpty(runtime) && Path.IsPathFullyQualified(runtime))
                return Path.Combine(runtime, "gvfs");
            return OperatingSystem.IsLinux()
                ? $"/run/user/{GetEffectiveUserId()}/gvfs"
                : Path.Combine(Path.GetTempPath(), "emanager-gvfs-unavailable");
        }
    }

    public static IReadOnlyList<(string Label, string Path)> Mounted()
    {
        var items = new List<(string Label, string Path)>();
        try
        {
            if (!Directory.Exists(MountRoot)) return items;
            foreach (var folder in Directory.EnumerateDirectories(MountRoot))
            {
                var name = Path.GetFileName(folder);
                if (!name.StartsWith("smb-share:", StringComparison.Ordinal) &&
                    !name.StartsWith("sftp:", StringComparison.Ordinal) &&
                    !name.StartsWith("ftp:", StringComparison.Ordinal) &&
                    !name.StartsWith("dav:", StringComparison.Ordinal) &&
                    !name.StartsWith("davs:", StringComparison.Ordinal)) continue;
                items.Add((name.StartsWith("sftp:", StringComparison.Ordinal)
                    ? "SFTP · " + Attribute(name, "host")
                    : name.StartsWith("smb-share:", StringComparison.Ordinal)
                        ? "SMB · " + Attribute(name, "share") + " @ " + Attribute(name, "server")
                        : name[..name.IndexOf(':')] + " · " + Attribute(name, "host"), folder));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return items.OrderBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string Attribute(string name, string key)
    {
        var start = name.IndexOf(':');
        if (start < 0) return "";
        foreach (var cell in name[(start + 1)..].Split(','))
        {
            var pair = cell.Split('=', 2);
            if (pair.Length == 2 && pair[0] == key)
                return Uri.UnescapeDataString(pair[1]);
        }
        return "";
    }

    public static string? FindMount(string address)
    {
        if (!TryNormalize(address, out var valid)) return null;
        var uri = new Uri(valid);
        foreach (var (_, path) in Mounted())
        {
            var name = Path.GetFileName(path);
            if (uri.Scheme == "smb" && !name.StartsWith("smb-share:", StringComparison.Ordinal)) continue;
            if (uri.Scheme == "sftp" && !name.StartsWith("sftp:", StringComparison.Ordinal)) continue;
            if (uri.Scheme is "ftp" or "ftps" or "dav" or "davs" && !name.StartsWith(uri.Scheme + ":", StringComparison.Ordinal)) continue;
            var host = Attribute(name, uri.Scheme == "smb" ? "server" : "host");
            if (string.IsNullOrWhiteSpace(host) && uri.Scheme is not "smb" and not "sftp") host = name;
            if (!host.Equals(uri.Host, StringComparison.OrdinalIgnoreCase)) continue;
            if (uri.Scheme == "smb" &&
                !Attribute(name, "share").Equals(Uri.UnescapeDataString(uri.AbsolutePath.Trim('/').Split('/')[0]), StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrEmpty(uri.UserInfo) &&
                !Attribute(name, "user").Equals(Uri.UnescapeDataString(uri.UserInfo), StringComparison.Ordinal))
                continue;
            if (uri.Scheme == "sftp" && uri.Port > 0 && uri.Port != 22 &&
                Attribute(name, "port") != uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)) continue;
            var extra = uri.Scheme == "smb"
                ? Uri.UnescapeDataString(string.Join("/", uri.AbsolutePath.Trim('/').Split('/').Skip(1)))
                : Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
            if (extra.Split('/').Any(x => x == "." || x == "..")) return null;
            var destination = Path.GetFullPath(Path.Combine(path, extra));
            if (!FileSystem.IsSubpath(path, destination)) return null;
            return Directory.Exists(destination) ? destination : null;
        }
        return null;
    }

    public static bool HasSmbBackend => File.Exists("/usr/libexec/gvfsd-smb");

    public static async Task UnmountAsync(string mountPath, CancellationToken token = default)
    {
        if (!Mounted().Any(m => string.Equals(m.Path, mountPath, StringComparison.Ordinal)))
            throw new ArgumentException("Geçerli bir SMB/SFTP bağlantısı seçin.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = new Process { StartInfo = new ProcessStartInfo("/usr/bin/gio")
        {
            UseShellExecute = false, RedirectStandardError = true,
            RedirectStandardOutput = true, CreateNoWindow = true
        } };
        TrustedTools.SecurePath(process.StartInfo);
        process.StartInfo.ArgumentList.Add("mount");
        process.StartInfo.ArgumentList.Add("-u");
        process.StartInfo.ArgumentList.Add(mountPath);
        if (!process.Start()) throw new IOException("gio başlatılamadı.");
        try
        {
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(errorTask, outputTask);
            if (process.ExitCode != 0) throw new IOException("Ağ bağlantısı kesilemedi: " + errorTask.Result.Trim());
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new TimeoutException("Ağ bağlantısını kesme işlemi zaman aşımına uğradı.");
        }
    }

    public static bool HasGvfsFuse => Directory.Exists(MountRoot);

    public static async Task<string> MountAsync(string address, CancellationToken token = default)
    {
        if (!TryNormalize(address, out var normalized))
            throw new ArgumentException(UiText.T("SMB için smb://sunucu/paylasim veya SFTP için sftp://kullanici@sunucu/ adresi girin."));
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException(UiText.T("GVfs Linux gerektirir."));
        if (FindMount(normalized) is { } existing) return existing;
        if (new Uri(normalized).Scheme == "smb" && !HasSmbBackend)
            throw new IOException("SMB desteği kurulu değil. Fedora terminalinde: sudo dnf install gvfs-smb gvfs-fuse");
        if (!File.Exists("/usr/libexec/gvfsd-fuse") && !HasGvfsFuse)
            throw new IOException("GVfs FUSE desteği kurulu değil. Fedora terminalinde: sudo dnf install gvfs-fuse");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("/usr/bin/gio")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        TrustedTools.SecurePath(process.StartInfo);
        process.StartInfo.ArgumentList.Add("mount");
        process.StartInfo.ArgumentList.Add(normalized);
        if (!process.Start()) throw new IOException(UiText.T("gio başlatılamadı."));
        try
        {
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(stderr, stdout);
            if (process.ExitCode != 0)
                throw new IOException(UiText.T("GVfs bağlantıyı kuramadı. Sunucuyu, yetkiyi ve ağ bağlantısını kontrol edin."));
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new TimeoutException(UiText.T("Ağ bağlantısı zaman aşımına uğradı."));
        }
        // GVfs is allowed a short interval to publish its FUSE mount.
        for (int i = 0; i < 12; i++)
        {
            if (FindMount(normalized) is { } path) return path;
            await Task.Delay(250, token);
        }
        throw new IOException(UiText.T("GVfs bağlandı ancak FUSE dizini görünmüyor. gvfs-fuse etkin olmalı."));
    }
}
