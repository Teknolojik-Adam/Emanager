using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace Emanager.App;

/// <summary>
/// Discover partition volumes independently of mount status, excluding virtual
/// devices and boot partitions. No automatic mount/privileged action is performed.
/// </summary>
public static class StorageDevices
{
    public static IReadOnlyList<MountedVolume>? Scan()
    {
        if (!OperatingSystem.IsLinux()) return [];
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo("/usr/bin/lsblk")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            TrustedTools.SecurePath(process.StartInfo);
            foreach (var arg in new[] { "--json", "--bytes", "--output",
                "NAME,PATH,TYPE,FSTYPE,LABEL,SIZE,MOUNTPOINTS,RM,TRAN,PARTTYPE" })
                process.StartInfo.ArgumentList.Add(arg);
            if (!process.Start()) return null;
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(3000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
            return process.ExitCode == 0 ? Parse(output) : null;
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    public static IReadOnlyList<MountedVolume> Parse(string json)
    {
        var volumes = new List<MountedVolume>();
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("blockdevices", out var nodes) ||
            nodes.ValueKind != JsonValueKind.Array) return volumes;
        foreach (var node in nodes.EnumerateArray())
            Walk(node, false, false, volumes);

        return volumes
            .GroupBy(v => v.Device, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(v => v.MountPoint == "/" ? 0 : v.IsMounted ? 1 : 2)
            .ThenBy(v => v.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static void Walk(JsonElement node, bool parentRemovable, bool parentUsb,
        List<MountedVolume> output)
    {
        var name = String(node, "name");
        var path = String(node, "path");
        var type = String(node, "type");
        var fs = String(node, "fstype");
        bool removable = parentRemovable || Bool(node, "rm");
        bool usb = parentUsb || String(node, "tran").Equals("usb", StringComparison.OrdinalIgnoreCase);

        if ((type is "part" or "disk" or "crypt" or "lvm") &&
            !string.IsNullOrWhiteSpace(fs) && !string.IsNullOrWhiteSpace(name) &&
            !string.IsNullOrWhiteSpace(path) && path.StartsWith("/dev/", StringComparison.Ordinal) &&
            fs is not ("swap" or "squashfs" or "crypto_LUKS" or "LVM2_member" or
                "linux_raid_member" or "zfs_member" or "BitLocker") &&
            String(node, "parttype") != "c12a7328-f81f-11d2-ba4b-00a0c93ec93b")
        {
            var mounts = new List<string>();
            if (node.TryGetProperty("mountpoints", out var points) && points.ValueKind == JsonValueKind.Array)
                foreach (var point in points.EnumerateArray())
                    if (point.ValueKind == JsonValueKind.String && point.GetString() is { Length: > 0 } mount)
                        mounts.Add(mount);
            // Partition may be mounted more than once (e.g. Btrfs / and /home).
            var mountPoint = mounts.Contains("/") ? "/" :
                mounts.FirstOrDefault(m => FileSystem.IsUserStorageMount(m)) ??
                mounts.FirstOrDefault(m => m == "/home") ?? "";
            // The boot partitions aren't user-facing data volumes.
            if (mounts.Count == 0 || !string.IsNullOrEmpty(mountPoint))
            {
                long size = Long(node, "size");
                long free = 0;
                if (!string.IsNullOrEmpty(mountPoint))
                {
                    try
                    {
                        var drive = new DriveInfo(mountPoint);
                        if (drive.IsReady)
                        {
                            size = drive.TotalSize;
                            free = drive.AvailableFreeSpace;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
                }
                string label = String(node, "label");
                if (string.IsNullOrWhiteSpace(label))
                    label = mountPoint == "/" ? UiText.T("Sistem diski") :
                        UiText.F(removable || usb ? "{0} Çıkarılabilir sürücü ({1})" :
                            "{0} İç sürücü ({1})", FileSystem.HumanSize(size), name);
                output.Add(new MountedVolume(mountPoint, path, label, size, free, fs, removable || usb));
            }
        }

        if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray())
                Walk(child, removable, usb, output);
    }

    private static string String(JsonElement node, string key) =>
        node.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    private static long Long(JsonElement node, string key) =>
        node.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var result) ? result : 0;

    private static bool Bool(JsonElement node, string key) =>
        node.TryGetProperty(key, out var value) &&
        (value.ValueKind == JsonValueKind.True ||
         (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int n) && n != 0));

    /// <summary>Mount only after the user clicks an explicitly discovered device.</summary>
    public static async Task<string> MountAsync(string device, CancellationToken token = default)
    {
        var volume = FileSystem.Volumes().FirstOrDefault(v => v.Device == device);
        if (volume is null) throw new IOException(UiText.T("Aygıt artık bağlı değil."));
        if (volume.IsMounted) return volume.MountPoint;

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("/usr/bin/udisksctl")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        TrustedTools.SecurePath(process.StartInfo);
        process.StartInfo.ArgumentList.Add("mount");
        process.StartInfo.ArgumentList.Add("--block-device");
        process.StartInfo.ArgumentList.Add(device);

        if (!process.Start()) throw new IOException(UiText.T("udisksctl başlatılamadı."));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(stdout, stderr);
            if (process.ExitCode != 0)
                throw new IOException(UiText.T("Disk bağlanamadı: ") + (await stderr).Trim());
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new TimeoutException(UiText.T("Disk bağlama işlemi zaman aşımına uğradı."));
        }
        for (int i = 0; i < 12; i++)
        {
            var mounted = FileSystem.Volumes().FirstOrDefault(v => v.Device == device);
            if (mounted?.IsMounted == true) return mounted.MountPoint;
            await Task.Delay(250, token);
        }
        throw new IOException(UiText.T("Disk bağlandı ancak erişilebilir bağlama noktası bulunamadı."));
    }
}
