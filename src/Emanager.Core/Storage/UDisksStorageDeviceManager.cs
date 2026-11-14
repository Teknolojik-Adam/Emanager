using System.Diagnostics;
using System.Text.Json;

namespace Emanager.Core.Storage;

public sealed class UDisksStorageDeviceManager : IStorageDeviceManager
{
    public async Task<IReadOnlyList<StorageDevice>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists("/usr/bin/lsblk") && !File.Exists("/bin/lsblk")) return Array.Empty<StorageDevice>();
        var json = await RunAsync("lsblk", "-J -b -o PATH,FSTYPE,MOUNTPOINT,SIZE,RM,RO,TYPE", cancellationToken);
        using var doc = JsonDocument.Parse(json); var result = new List<StorageDevice>();
        foreach (var item in doc.RootElement.GetProperty("blockdevices").EnumerateArray()) Collect(item, result);
        return result;
    }
    // The legacy contract has no topology snapshot, stable identity, or
    // fail-closed policy context. Keep discovery compatibility, but reject
    // mutations so callers cannot bypass PolicyEnforcedDiskMountService.
    public Task MountAsync(StorageDevice device, bool readOnly, CancellationToken cancellationToken = default) =>
        Task.FromException(new InvalidOperationException("Use PolicyEnforcedDiskMountService for policy-checked disk operations."));
    public Task UnmountAsync(StorageDevice device, CancellationToken cancellationToken = default) =>
        Task.FromException(new InvalidOperationException("Use PolicyEnforcedDiskMountService for policy-checked disk operations."));
    public Task EjectAsync(StorageDevice device, CancellationToken cancellationToken = default) =>
        Task.FromException(new InvalidOperationException("Use PolicyEnforcedDiskMountService for policy-checked disk operations."));
    private static void Collect(JsonElement item, List<StorageDevice> result)
    {
        var type = item.GetProperty("type").GetString(); if (type is "part" or "disk") { var path = item.GetProperty("path").GetString(); if (path is not null && type == "part") result.Add(new(path, path, item.TryGetProperty("fstype", out var fs) ? fs.GetString() : null, item.TryGetProperty("mountpoint", out var mp) ? mp.GetString() : null, item.TryGetProperty("size", out var size) ? size.GetInt64() : 0, item.TryGetProperty("rm", out var rm) && rm.GetBoolean(), item.TryGetProperty("ro", out var ro) && ro.GetBoolean())); }
        if (item.TryGetProperty("children", out var children)) foreach (var child in children.EnumerateArray()) Collect(child, result);
    }
    private static async Task<string> RunAsync(string command, string args, CancellationToken token)
    {
        var psi = new ProcessStartInfo(command, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var process = Process.Start(psi) ?? throw new IOException($"Unable to start {command}."); using var registration = token.Register(() => { try { process.Kill(true); } catch { } }); await process.WaitForExitAsync(token); var output = await process.StandardOutput.ReadToEndAsync(token); var error = await process.StandardError.ReadToEndAsync(token); if (process.ExitCode != 0) throw new IOException(string.IsNullOrWhiteSpace(error) ? $"{command} failed." : error.Trim()); return output;
    }
}
