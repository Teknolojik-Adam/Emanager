using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Emanager.App;

public sealed record FileEntry(string FullPath, string Name, bool IsDirectory, bool IsLink, long Length,
    DateTime Modified, string Kind, string Permissions)
{
    public string SizeLabel => IsDirectory ? "—" : FileSystem.HumanSize(Length);
    public string ModifiedLabel => Modified.ToString("g", CultureInfo.CurrentCulture);
}

public sealed record MountedVolume(string MountPoint, string Device, string Label, long Total, long Free,
    string FileSystemType = "", bool Removable = false)
{
    public bool IsMounted => !string.IsNullOrEmpty(MountPoint);
    public string SizeLabel => !IsMounted
        ? $"{UiText.T("Bağlı değil")} · {FileSystem.HumanSize(Total)} · {FileSystemType}"
        : Total > 0 ? $"{FileSystem.HumanSize(Free)} {UiText.T("boş")} / {FileSystem.HumanSize(Total)}" : MountPoint;
}

public static class FileSystem
{
    public static bool Hidden(string path) => Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal);
    public static string HumanSize(long size)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double n = size;
        int i = 0;
        while (n >= 1024 && i < units.Length - 1) { n /= 1024; i++; }
        return $"{n:0.#} {units[i]}";
    }

    // A broken symbolic link is still a directory entry and must remain visible.
    // Query LinkTarget before dereferencing the target; File.GetAttributes can fail
    // when the target no longer exists on some filesystems/platforms.
    public static FileEntry? ReadEntry(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            var dirInfo = new DirectoryInfo(path);
            bool isLink = fileInfo.LinkTarget is not null || dirInfo.LinkTarget is not null;
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) when (isLink) { attributes = FileAttributes.ReparsePoint; }
            catch (DirectoryNotFoundException) when (isLink) { attributes = FileAttributes.ReparsePoint; }

            bool isDirectory = attributes.HasFlag(FileAttributes.Directory);
            // Link targets may be broken. Report them without trying to inspect their content.
            long length = isLink || isDirectory ? 0 : fileInfo.Length;
            FileSystemInfo info = isDirectory ? dirInfo : fileInfo;
            DateTime modified;
            try { modified = info.LastWriteTime; }
            catch (IOException) when (isLink) { modified = DateTime.MinValue; }

            string permissions = "";
            if (OperatingSystem.IsLinux() && !isLink)
            {
                try { permissions = Convert.ToString((int)File.GetUnixFileMode(path) & 0x1ff, 8)!.PadLeft(3, '0'); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            string kind = isLink ? UiText.T("Bağlantı") : isDirectory ? UiText.T("Klasör") :
                (Path.GetExtension(path).TrimStart('.').ToUpperInvariant() is { Length: > 0 } ext
                    ? ext + " " + UiText.T("dosyası") : UiText.T("Dosya"));
            return new FileEntry(path, Path.GetFileName(path), isDirectory, isLink, length, modified, kind, permissions);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken ct) =>
        KioAdmin.Enabled ? KioAdmin.ListAsync(path, ct) : Task.Run(() =>
    {
        var results = new List<FileEntry>();
        foreach (string item in Directory.EnumerateFileSystemEntries(path))
        {
            ct.ThrowIfCancellationRequested();
            var entry = ReadEntry(item);
            if (entry is not null) results.Add(entry);
        }
        return (IReadOnlyList<FileEntry>)results;
    }, ct);

    // Sidebar storage: physical/real block-backed mounted filesystems only.
    // Never list proc, sysfs, tmpfs, snap loop mounts or swap as disks.
    // Each storage source appears once: Btrfs / and /home often share one partition.
    public static IReadOnlyList<MountedVolume> Volumes()
    {
        if (!OperatingSystem.IsLinux()) return Array.Empty<MountedVolume>();
        var detected = StorageDevices.Scan();
        if (detected is not null) return detected;
        var result = new List<MountedVolume>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var line in File.ReadLines("/proc/self/mountinfo"))
            {
                var separator = line.IndexOf(" - ", StringComparison.Ordinal);
                if (separator < 0) continue;
                var before = line[..separator].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var after = line[(separator + 3)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (before.Length < 5 || after.Length < 2) continue;
                var mount = UnescapeMountField(before[4]);
                var source = UnescapeMountField(after[1]);
                if (!source.StartsWith("/dev/", StringComparison.Ordinal)) continue;
                if (!IsUserStorageMount(mount) || !IsBlockStorageDevice(source)) continue;
                // Prefer root over the same partition mounted at /home or a subvolume.
                if (!seen.Add(source)) continue;
                try
                {
                    var drive = new DriveInfo(mount);
                    if (!drive.IsReady) continue;
                    var label = mount == "/" ? UiText.T("Sistem diski") :
                        System.IO.Path.GetFileName(mount.TrimEnd('/'));
                    result.Add(new MountedVolume(mount, source, label, drive.TotalSize, drive.AvailableFreeSpace));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return result.OrderBy(x => x.MountPoint == "/" ? 0 : 1)
            .ThenBy(x => x.MountPoint, StringComparer.Ordinal).ToList();
    }

    internal static string UnescapeMountField(string text)
    {
        return text.Replace("\\040", " ", StringComparison.Ordinal)
            .Replace("\\011", "\t", StringComparison.Ordinal)
            .Replace("\\012", "\n", StringComparison.Ordinal)
            .Replace("\\134", "\\", StringComparison.Ordinal);
    }

    internal static bool IsUserStorageMount(string mount) =>
        mount == "/" || mount.StartsWith("/run/media/", StringComparison.Ordinal) ||
        mount.StartsWith("/media/", StringComparison.Ordinal) ||
        mount.StartsWith("/mnt/", StringComparison.Ordinal);

    private static bool IsBlockStorageDevice(string source)
    {
        var name = System.IO.Path.GetFileName(source);
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith("loop", StringComparison.Ordinal) ||
            name.StartsWith("zram", StringComparison.Ordinal)) return false;
        // /dev/mapper/ volumes are block devices too; support encrypted disks.
        if (!source.StartsWith("/dev/mapper/", StringComparison.Ordinal) &&
            !File.Exists("/sys/class/block/" + name) && !Directory.Exists("/sys/class/block/" + name))
            return false;
        return true;
    }

    public static bool IsSubpath(string parent, string child)
    {
        var p = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var c = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return string.Equals(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal)
            || c.StartsWith(p, StringComparison.Ordinal);
    }
}

public static class UserFolders
{
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string ConfigDirectory => Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } cfg && Path.IsPathFullyQualified(cfg)
        ? cfg : Path.Combine(Home, ".config"), "Emanager");
    public static IEnumerable<(string Label, string Path)> Standard()
    {
        yield return (UiText.T("Ana klasör"), Home);
        foreach (var (label, key, fallback) in new[] {
            ("Masaüstü", "XDG_DESKTOP_DIR", "Desktop"), ("İndirilenler", "XDG_DOWNLOAD_DIR", "Downloads"),
            ("Belgeler", "XDG_DOCUMENTS_DIR", "Documents"), ("Resimler", "XDG_PICTURES_DIR", "Pictures"),
            ("Müzik", "XDG_MUSIC_DIR", "Music"), ("Videolar", "XDG_VIDEOS_DIR", "Videos")})
        {
            var path = ReadXdgUserDir(key) ?? Path.Combine(Home, fallback);
            if (Directory.Exists(path)) yield return (UiText.T(label), path);
        }
    }
    private static string? ReadXdgUserDir(string key)
    {
        var cfgHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var path = Path.Combine(!string.IsNullOrWhiteSpace(cfgHome) && Path.IsPathFullyQualified(cfgHome) ? cfgHome : Path.Combine(Home, ".config"), "user-dirs.dirs");
        if (!File.Exists(path)) return null;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith(key + "=", StringComparison.Ordinal)) continue;
                var value = trimmed[(key.Length + 1)..].Trim().Trim('"');
                value = value.Replace("$HOME", Home, StringComparison.Ordinal);
                return Path.IsPathFullyQualified(value) ? value : null;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }
}
