using System.Globalization;

namespace Emanager.App;

internal sealed record TrashEntry(
    string Name,
    string OriginalPath,
    DateTime? DeletedAt,
    long Size,
    bool IsDirectory,
    string TrashPath,
    string InfoPath,
    string TrashRoot);

/// <summary>Freedesktop Trash metadata and lifecycle operations.</summary>
internal static class TrashService
{
    public static IReadOnlyList<TrashEntry> ListAll()
    {
        var roots = new List<string> { HomeTrashRoot() };
        try
        {
            foreach (var volume in FileSystem.Volumes())
                if (!string.IsNullOrWhiteSpace(volume.MountPoint))
                    roots.Add(Path.Combine(volume.MountPoint, $".Trash-{CurrentUid()}"));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return roots.Distinct(StringComparer.Ordinal)
            .SelectMany(List)
            .GroupBy(x => x.TrashPath, StringComparer.Ordinal)
            .Select(x => x.First())
            .OrderByDescending(x => x.DeletedAt ?? DateTime.MinValue)
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<TrashEntry> List(string trashRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trashRoot);
        var root = Path.GetFullPath(trashRoot);
        var files = Path.Combine(root, "files");
        var info = Path.Combine(root, "info");
        if (!Directory.Exists(files) || !Directory.Exists(info)) return [];
        var result = new List<TrashEntry>();
        foreach (var item in Directory.EnumerateFileSystemEntries(files))
        {
            var name = Path.GetFileName(item);
            if (string.IsNullOrEmpty(name)) continue;
            var infoPath = Path.Combine(info, name + ".trashinfo");
            var metadata = ReadInfo(infoPath);
            result.Add(new TrashEntry(name, metadata.OriginalPath ?? "", metadata.DeletedAt,
                Measure(item), IsDirectory(item), item, infoPath, root));
        }
        return result.OrderByDescending(x => x.DeletedAt ?? DateTime.MinValue)
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static void Restore(TrashEntry entry, string? alternatePath = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var destination = Path.GetFullPath(string.IsNullOrWhiteSpace(alternatePath)
            ? entry.OriginalPath
            : alternatePath);
        if (string.IsNullOrWhiteSpace(entry.OriginalPath) && string.IsNullOrWhiteSpace(alternatePath))
            throw new IOException("Çöp kutusu girdisinin özgün konumu yok.");
        if (File.Exists(destination) || Directory.Exists(destination) || IsLink(destination))
            throw new IOException("Geri yükleme hedefi zaten mevcut.");
        var parent = Directory.GetParent(destination)?.FullName
            ?? throw new IOException("Geri yükleme üst klasörü bulunamadı.");
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);
        if (!File.Exists(entry.TrashPath) && !Directory.Exists(entry.TrashPath) && !IsLink(entry.TrashPath))
            throw new FileNotFoundException("Çöp kutusu girdisi bulunamadı.", entry.TrashPath);
        if (Directory.Exists(entry.TrashPath) && !IsLink(entry.TrashPath)) Directory.Move(entry.TrashPath, destination);
        else File.Move(entry.TrashPath, destination, overwrite: false);
        TryDeleteInfo(entry.InfoPath);
    }

    public static void PermanentlyDelete(TrashEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        DeleteEntry(entry.TrashPath);
        TryDeleteInfo(entry.InfoPath);
    }

    public static int Empty(string trashRoot)
    {
        var entries = List(trashRoot);
        foreach (var entry in entries) PermanentlyDelete(entry);
        return entries.Count;
    }

    public static int EmptyAll()
    {
        var count = 0;
        foreach (var entry in ListAll())
        {
            PermanentlyDelete(entry);
            count++;
        }
        return count;
    }

    internal static string HomeTrashRoot() => Path.Combine(UserFolders.Home, ".local", "share", "Trash");

    private static string CurrentUid()
    {
        if (!OperatingSystem.IsLinux()) return Environment.UserName;
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo("/usr/bin/id")
            {
                UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
            };
            TrustedTools.SecurePath(process.StartInfo);
            process.StartInfo.ArgumentList.Add("-u");
            if (process.Start())
            {
                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(1000);
                if (uint.TryParse(output, NumberStyles.None, CultureInfo.InvariantCulture, out var uid)) return uid.ToString(CultureInfo.InvariantCulture);
            }
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { }
        return Environment.UserName;
    }

    private static (string? OriginalPath, DateTime? DeletedAt) ReadInfo(string path)
    {
        string? original = null;
        DateTime? deleted = null;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0) continue;
                var key = line[..separator];
                var value = line[(separator + 1)..];
                if (key == "Path") original = Uri.UnescapeDataString(value);
                else if (key == "DeletionDate" && DateTime.TryParseExact(value, "yyyy-MM-ddTHH:mm:ss",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date)) deleted = date;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return (original, deleted);
    }

    private static long Measure(string path)
    {
        try
        {
            if (IsLink(path)) return 0;
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (!Directory.Exists(path)) return 0;
            long total = 0;
            foreach (var child in Directory.EnumerateFileSystemEntries(path)) total += Measure(child);
            return total;
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    private static bool IsDirectory(string path) => Directory.Exists(path) && !IsLink(path);

    private static bool IsLink(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void DeleteEntry(string path)
    {
        if (IsLink(path) || File.Exists(path)) { File.Delete(path); return; }
        if (!Directory.Exists(path)) return;
        foreach (var child in Directory.EnumerateFileSystemEntries(path)) DeleteEntry(child);
        Directory.Delete(path);
    }

    private static void TryDeleteInfo(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
