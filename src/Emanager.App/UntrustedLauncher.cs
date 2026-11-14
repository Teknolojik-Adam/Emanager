namespace Emanager.App;

internal static class UntrustedLauncher
{
    private static string ResolveLocalLink(string path)
    {
        try
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                return new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return path;
    }

    private static bool DangerousExtension(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".desktop" or ".application" or ".sh";

    internal static bool IsPotentiallyExecutable(string path)
    {
        var resolved = ResolveLocalLink(path);
        if (DangerousExtension(path) || DangerousExtension(resolved)) return true;
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            var mode = File.GetUnixFileMode(resolved);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    internal static string Describe(string path)
    {
        var actual = ResolveLocalLink(path);
        if (!Path.GetExtension(actual).Equals(".desktop", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetExtension(path).Equals(".desktop", StringComparison.OrdinalIgnoreCase))
            return UiText.T("Çalıştırılabilir dosya veya betik.");
        try
        {
            foreach (var line in File.ReadLines(actual).Take(120))
                if (line.StartsWith("Exec=", StringComparison.Ordinal))
                    return UiText.T("Masaüstü başlatıcısı komutu: ") + line[5..].Replace('\n', ' ').Replace('\r', ' ')[..Math.Min(240, line.Length - 5)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return UiText.T("Bu masaüstü başlatıcısı bir komut çalıştırabilir.");
    }
}
