namespace Emanager.App;

/// <summary>Only launch known command names from system tool directories, never
/// from a relative PATH entry or the currently browsed directory.</summary>
internal static class TrustedTools
{
    internal static string? Resolve(string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." ||
            name.Contains('/') || name.Contains('\\') || name.Contains('\0')) return null;
        foreach (var dir in new[] { "/usr/bin", "/bin" })
        {
            var file = Path.Combine(dir, name);
            if (File.Exists(file)) return file;
        }
        return null;
    }

    internal static string Require(string name) => Resolve(name)
        ?? throw new IOException("Güvenilir sistem aracı bulunamadı: " + name);

    internal static void SecurePath(System.Diagnostics.ProcessStartInfo process)
    {
        ArgumentNullException.ThrowIfNull(process);
        // Exclude relative PATH components and world-writable directories.
        // Preserve legitimate absolute user application directories.
        var directories = new List<string> { "/usr/bin", "/bin" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir) || !Path.IsPathFullyQualified(dir)) continue;
            try
            {
                var fullPath = Path.GetFullPath(dir);
                if (!Directory.Exists(fullPath)) continue;
                if (OperatingSystem.IsLinux())
                {
                    var mode = File.GetUnixFileMode(fullPath);
                    if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0) continue;
                }
                directories.Add(fullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        process.Environment["PATH"] = string.Join(Path.PathSeparator, directories.Distinct(StringComparer.Ordinal));
    }
}
