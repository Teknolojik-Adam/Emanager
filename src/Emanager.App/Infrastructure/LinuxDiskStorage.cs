using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Emanager.Core.Storage;

namespace Emanager.App;

/// <summary>
/// Read-only Linux block-device snapshot. The snapshot combines lsblk topology,
/// findmnt live mounts, /proc/swaps and UDisks2 capability probes. It never
/// mounts or changes a device during discovery.
/// </summary>
public sealed class LinuxBlockDeviceDiscovery : IBlockDeviceDiscovery
{
    private sealed record RawNode(
        string KernelName, string? DevicePath, string Type, string? FileSystem,
        string? Uuid, string? PartUuid, string? Wwn, string? Serial, string? Label,
        string? Transport, string? Model, long Size, bool Removable, bool ReadOnly,
        string? ParentKernel, List<string> MountPoints);

    public async Task<StorageDiscoverySnapshot> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux()) return StorageDiscoverySnapshot.Unavailable("Linux block-device discovery is unavailable on this platform.");
        try
        {
            var lsblk = await RunAsync("lsblk", ["--json", "--bytes", "--output",
                "NAME,KNAME,PATH,TYPE,FSTYPE,LABEL,UUID,PARTUUID,WWN,SERIAL,SIZE,RM,RO,TRAN,MOUNTPOINTS,PKNAME,MODEL"], cancellationToken);
            using var document = JsonDocument.Parse(lsblk);
            if (!document.RootElement.TryGetProperty("blockdevices", out var root) || root.ValueKind != JsonValueKind.Array)
                return StorageDiscoverySnapshot.Unavailable("lsblk returned no block-device graph.");

            var raw = new List<RawNode>();
            foreach (var item in root.EnumerateArray()) Walk(item, null, false, null, raw);
            if (raw.Count == 0) return StorageDiscoverySnapshot.Unavailable("No block devices were discovered.");
            var ids = raw.ToDictionary(x => x.KernelName, StableId, StringComparer.Ordinal);
            var pathToId = raw.Where(x => x.DevicePath is not null)
                .ToDictionary(x => x.DevicePath!, x => ids[x.KernelName], StringComparer.Ordinal);
            var nodeBuilders = raw.ToDictionary(x => ids[x.KernelName], x => x, StringComparer.Ordinal);
            var mounts = new List<StorageMount>();
            foreach (var item in raw)
            {
                var id = ids[item.KernelName];
                foreach (var mount in item.MountPoints.Distinct(StringComparer.Ordinal))
                    mounts.Add(new StorageMount(id, mount, item.DevicePath ?? item.KernelName, item.FileSystem ?? "", item.ReadOnly, Array.Empty<string>()));
            }

            var findmntSucceeded = await TryReadFindMntAsync(pathToId, mounts, cancellationToken);
            var mountInfoSucceeded = await TryReadMountInfoAsync(pathToId, mounts, cancellationToken);
            var swaps = await ReadSwapsAsync(pathToId, cancellationToken);
            var swapTopologyComplete = swaps.All(x => !x.IsActive || x.StableDeviceId is not null);
            var nodes = new List<StorageNode>();
            foreach (var item in raw)
            {
                var id = ids[item.KernelName];
                var parents = item.ParentKernel is { Length: > 0 } parent && ids.TryGetValue(parent, out var parentId)
                    ? new[] { parentId } : Array.Empty<string>();
                var kind = Kind(item.Type, item.FileSystem);
                nodes.Add(new StorageNode(id, kind, item.KernelName, item.DevicePath, item.Size,
                    item.Removable, item.ReadOnly, item.FileSystem, item.Uuid ?? item.PartUuid, item.Label,
                    item.Transport, parents, Array.Empty<string>(), item.MountPoints.Distinct(StringComparer.Ordinal).ToArray(),
                    Model: item.Model, Serial: item.Serial));
            }
            var mountTargets = mounts.GroupBy(x => x.StableDeviceId, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.Select(y => y.Target).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
            nodes = nodes.Select(x => mountTargets.TryGetValue(x.StableId, out var targets)
                ? x with { MountPoints = targets }
                : x).ToList();
            nodes = LinkChildren(nodes);
            nodes = await ProbeDriveCapabilitiesAsync(nodes, cancellationToken);
            var fingerprint = Fingerprint(nodes, mounts, swaps);
            var complete = findmntSucceeded && mountInfoSucceeded && swapTopologyComplete;
            return new StorageDiscoverySnapshot(nodes, mounts, swaps, complete, fingerprint, DateTimeOffset.UtcNow,
                complete ? null : "Live mount, mountinfo, or swap topology could not be resolved completely.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
            return StorageDiscoverySnapshot.Unavailable(ex.Message);
        }
    }

    private static void Walk(JsonElement item, string? parentKernel, bool parentRemovable,
        string? parentTransport, List<RawNode> output)
    {
        var kernel = String(item, "kname");
        if (string.IsNullOrWhiteSpace(kernel)) kernel = String(item, "name");
        if (string.IsNullOrWhiteSpace(kernel)) return;
        var transport = NullString(item, "tran") ?? parentTransport;
        var removable = Bool(item, "rm") || parentRemovable ||
            string.Equals(transport, "usb", StringComparison.OrdinalIgnoreCase);
        var mounts = new List<string>();
        if (item.TryGetProperty("mountpoints", out var points) && points.ValueKind == JsonValueKind.Array)
            foreach (var point in points.EnumerateArray())
                if (point.ValueKind == JsonValueKind.String && point.GetString() is { Length: > 0 } value)
                    mounts.Add(value);
        output.Add(new RawNode(kernel, NullString(item, "path"), String(item, "type"), NullString(item, "fstype"),
            NullString(item, "uuid"), NullString(item, "partuuid"), NullString(item, "wwn"), NullString(item, "serial"),
            NullString(item, "label"), transport, NullString(item, "model"), Long(item, "size"),
            removable, Bool(item, "ro"), NullString(item, "pkname") ?? parentKernel, mounts));
        if (item.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray()) Walk(child, kernel, removable, transport, output);
    }

    private static List<StorageNode> LinkChildren(List<StorageNode> nodes)
    {
        var children = nodes.ToDictionary(x => x.StableId, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var node in nodes)
            foreach (var parent in node.ParentIds)
                if (children.TryGetValue(parent, out var list)) list.Add(node.StableId);
        return nodes.Select(x => x with { ChildIds = children[x.StableId] }).ToList();
    }

    private static async Task<List<StorageNode>> ProbeDriveCapabilitiesAsync(List<StorageNode> nodes, CancellationToken token)
    {
        var result = new List<StorageNode>(nodes.Count);
        foreach (var node in nodes)
        {
            if (node.Kind != StorageNodeKind.PhysicalDisk || !node.IsRemovable || node.DevicePath is null)
            {
                result.Add(node);
                continue;
            }
            try
            {
                var info = await RunAsync("udisksctl", ["info", "--block-device", node.DevicePath], token);
                var ejectable = info.Split('\n').Any(x => x.Trim().EndsWith("Ejectable: true", StringComparison.OrdinalIgnoreCase));
                var powerOff = info.Split('\n').Any(x => x.Trim().EndsWith("CanPowerOff: true", StringComparison.OrdinalIgnoreCase));
                result.Add(node with { CanEject = ejectable, CanPowerOff = powerOff });
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            { result.Add(node); }
        }
        return result;
    }

    private static async Task<bool> TryReadFindMntAsync(Dictionary<string, string> pathToId, List<StorageMount> mounts, CancellationToken token)
    {
        try
        {
            var json = await RunAsync("findmnt", ["--real", "--json", "--output", "SOURCE,TARGET,FSTYPE,OPTIONS"], token);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("filesystems", out var filesystems)) return false;
            WalkMounts(filesystems, pathToId, mounts);
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        { return false; }
    }

    private static void WalkMounts(JsonElement items, Dictionary<string, string> pathToId, List<StorageMount> mounts)
    {
        if (items.ValueKind != JsonValueKind.Array) return;
        foreach (var item in items.EnumerateArray())
        {
            var source = NullString(item, "source");
            var target = NullString(item, "target");
            if (source is not null && target is not null)
            {
                var bracket = source.IndexOf('[', StringComparison.Ordinal);
                if (bracket >= 0) source = source[..bracket];
                if (pathToId.TryGetValue(source, out var id))
                {
                    var fs = NullString(item, "fstype") ?? "";
                    var options = (NullString(item, "options") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var readOnly = options.Contains("ro", StringComparer.OrdinalIgnoreCase);
                    if (!mounts.Any(x => x.StableDeviceId == id && x.Target == target))
                        mounts.Add(new StorageMount(id, target, source, fs, readOnly, options));
                }
            }
            if (item.TryGetProperty("children", out var children)) WalkMounts(children, pathToId, mounts);
        }
    }

    private static async Task<bool> TryReadMountInfoAsync(Dictionary<string, string> pathToId,
        List<StorageMount> mounts, CancellationToken token)
    {
        try
        {
            var lines = await File.ReadAllLinesAsync("/proc/self/mountinfo", token);
            foreach (var line in lines)
            {
                var separator = line.IndexOf(" - ", StringComparison.Ordinal);
                if (separator < 0) continue;
                var left = line[..separator].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var right = line[(separator + 3)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (left.Length < 6 || right.Length < 2) continue;
                var target = DecodeMountInfoField(left[4]);
                var source = DecodeMountInfoField(right[1]);
                if (!pathToId.TryGetValue(source, out var id)) continue;
                var fs = right[0];
                var options = left[5].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var readOnly = options.Contains("ro", StringComparer.OrdinalIgnoreCase);
                if (!mounts.Any(x => x.StableDeviceId == id && x.Target == target))
                    mounts.Add(new StorageMount(id, target, source, fs, readOnly, options));
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return false; }
    }

    private static string DecodeMountInfoField(string value)
    {
        return Regex.Replace(value, @"\\([0-7]{3})", match =>
            ((char)Convert.ToInt32(match.Groups[1].Value, 8)).ToString());
    }

    private static async Task<List<SwapEntry>> ReadSwapsAsync(Dictionary<string, string> pathToId, CancellationToken token)
    {
        var swaps = new List<SwapEntry>();
        if (!File.Exists("/proc/swaps")) return swaps;
        foreach (var line in await File.ReadAllLinesAsync("/proc/swaps", token))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("Filename", StringComparison.Ordinal)) continue;
            var path = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (path is null) continue;
            if (pathToId.TryGetValue(path, out var deviceId)) swaps.Add(new SwapEntry(path, deviceId, true));
            else if (path.StartsWith("/", StringComparison.Ordinal))
            {
                try
                {
                    var source = (await RunAsync("findmnt", ["--target", path, "--noheadings", "--output", "SOURCE"], token)).Trim();
                    var bracket = source.IndexOf('[', StringComparison.Ordinal);
                    if (bracket >= 0) source = source[..bracket];
                    pathToId.TryGetValue(source, out deviceId);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) { }
                swaps.Add(new SwapEntry(path, deviceId, true));
            }
        }
        return swaps;
    }

    private static string StableId(RawNode node)
    {
        if (!string.IsNullOrWhiteSpace(node.Uuid)) return "uuid:" + node.Uuid;
        if (!string.IsNullOrWhiteSpace(node.PartUuid)) return "partuuid:" + node.PartUuid;
        if (!string.IsNullOrWhiteSpace(node.Wwn)) return "wwn:" + node.Wwn;
        if (!string.IsNullOrWhiteSpace(node.Serial)) return "serial:" + node.Serial;
        var sysfs = Path.Combine("/sys/class/block", node.KernelName);
        try { if (Directory.Exists(sysfs) || File.Exists(sysfs)) return "sysfs:" + Path.GetFullPath(sysfs); } catch { }
        return "path:" + (node.DevicePath ?? node.KernelName);
    }

    private static StorageNodeKind Kind(string type, string? fs) =>
        fs switch
        {
            "crypto_LUKS" => StorageNodeKind.CryptContainer,
            "LVM2_member" => StorageNodeKind.LvmPhysicalVolume,
            "linux_raid_member" => StorageNodeKind.RaidMember,
            _ => type switch
            {
                "disk" => StorageNodeKind.PhysicalDisk,
                "part" => StorageNodeKind.Partition,
                "crypt" => StorageNodeKind.CryptMapping,
                "lvm" => StorageNodeKind.LvmVolume,
                "md" or "raid" => StorageNodeKind.RaidArray,
                "mpath" or "dm" => StorageNodeKind.DeviceMapper,
                "loop" => StorageNodeKind.Loop,
                "zram" => StorageNodeKind.Zram,
                _ => StorageNodeKind.Unknown
            }
        };

    private static string Fingerprint(IEnumerable<StorageNode> nodes, IEnumerable<StorageMount> mounts, IEnumerable<SwapEntry> swaps)
    {
        var text = string.Join("\n", nodes.OrderBy(x => x.StableId).Select(x =>
            $"N|{x.StableId}|{x.DevicePath}|{x.Kind}|{string.Join(',', x.ParentIds.OrderBy(y => y))}|{string.Join(',', x.MountPoints.OrderBy(y => y))}")) +
            "\n" + string.Join("\n", mounts.OrderBy(x => x.StableDeviceId).ThenBy(x => x.Target).Select(x => $"M|{x.StableDeviceId}|{x.Target}|{x.FileSystemType}|{x.IsReadOnly}")) +
            "\n" + string.Join("\n", swaps.OrderBy(x => x.Path).Select(x => $"S|{x.Path}|{x.StableDeviceId}|{x.IsActive}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static async Task<string> RunAsync(string command, IReadOnlyList<string> arguments, CancellationToken token)
    {
        var executable = command == "lsblk" ? "/usr/bin/lsblk" : command == "findmnt" ? "/usr/bin/findmnt" : "/usr/bin/udisksctl";
        if (!File.Exists(executable)) throw new IOException($"{executable} is not available.");
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new IOException($"Could not start {command}.");
        using var registration = token.Register(() => { try { process.Kill(true); } catch { } });
        await process.WaitForExitAsync(token);
        var output = await process.StandardOutput.ReadToEndAsync(token);
        var error = await process.StandardError.ReadToEndAsync(token);
        if (process.ExitCode != 0) throw new IOException(string.IsNullOrWhiteSpace(error) ? $"{command} failed." : error.Trim());
        return output;
    }

    private static string String(JsonElement node, string key) => node.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static string? NullString(JsonElement node, string key) => node.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;
    private static long Long(JsonElement node, string key) => node.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var result) ? result : 0;
    private static bool Bool(JsonElement node, string key) => node.TryGetProperty(key, out var value) && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n != 0);
}

/// <summary>UDisks2 command-line client. udisksctl is a D-Bus client and delegates authorization to PolicyKit.</summary>
public sealed class UDisks2DiskOperationBackend : IDiskOperationBackend
{
    public async Task<DiskOperationBackendResult> ExecuteAsync(StorageNode node, DiskOperationRequest request, CancellationToken cancellationToken = default)
    {
        if (node.DevicePath is null) return new(false, Error: "The device path is unavailable.");
        if (request.Operation is DiskOperationKind.Eject && !node.CanEject)
            return new(false, Error: "UDisks2 does not report this device as ejectable.");
        if (request.Operation is DiskOperationKind.Eject && !node.CanPowerOff)
            return new(false, Error: "UDisks2 does not report safe removal capability for this device.");
        if (request.Operation is DiskOperationKind.PowerOff && !node.CanPowerOff)
            return new(false, Error: "UDisks2 does not allow powering off this device.");
        var args = request.Operation switch
        {
            DiskOperationKind.Mount => new List<string>(["mount", "--block-device", node.DevicePath]),
            DiskOperationKind.Unmount => new List<string>(["unmount", "--block-device", node.DevicePath]),
            DiskOperationKind.Eject or DiskOperationKind.PowerOff => new List<string>(["power-off", "--block-device", node.DevicePath]),
            _ => new List<string>()
        };
        if (request.Operation == DiskOperationKind.Mount && request.ReadOnly) { args.Add("--options"); args.Add("ro"); }
        try
        {
            var output = await RunAsync(args, cancellationToken);
            var match = Regex.Match(output, @"\bat\s+(?<path>/\S+)", RegexOptions.IgnoreCase);
            return new(true, match.Success ? match.Groups["path"].Value.TrimEnd('.') : null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var text = ex.Message;
            return new(false, Error: text, IsBusy: text.Contains("busy", StringComparison.OrdinalIgnoreCase) || text.Contains("in use", StringComparison.OrdinalIgnoreCase));
        }
    }

    private static async Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken token)
    {
        const string executable = "/usr/bin/udisksctl";
        if (!File.Exists(executable)) throw new IOException("udisksctl is not installed.");
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new IOException("Could not start udisksctl.");
        using var registration = token.Register(() => { try { process.Kill(true); } catch { } });
        await process.WaitForExitAsync(token);
        var output = await process.StandardOutput.ReadToEndAsync(token);
        var error = await process.StandardError.ReadToEndAsync(token);
        if (process.ExitCode != 0) throw new IOException(string.IsNullOrWhiteSpace(error) ? "UDisks2 operation failed." : error.Trim());
        return output;
    }
}
