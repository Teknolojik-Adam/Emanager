using System.Diagnostics;
using Emanager.Core.Network;

namespace Emanager.Core.Storage;

public sealed class GvfsMtpProvider : IMtpProvider
{
    public bool IsAvailable => File.Exists("/usr/bin/gio") || File.Exists("/bin/gio");
    public async Task<IReadOnlyList<MtpDevice>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return Array.Empty<MtpDevice>();
        var output = await RunAsync("list -d", "mtp://", cancellationToken);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(path => new MtpDevice(path, Path.GetFileName(path), true)).ToArray();
    }
    public async IAsyncEnumerable<NetworkEntry> ListAsync(string deviceId, string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) throw new PlatformNotSupportedException("GVfs MTP backend is unavailable.");
        var output = await RunAsync("list -d", $"mtp://{deviceId}/{path.TrimStart('/')}", cancellationToken);
        foreach (var entry in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) yield return new(Path.GetFileName(entry), entry, false);
    }
    public Task CopyToDeviceAsync(string localPath, string deviceId, string remotePath, IProgress<long>? progress = null, CancellationToken cancellationToken = default) => RunAsync("copy", $"{localPath} mtp://{deviceId}/{remotePath.TrimStart('/')}", cancellationToken);
    private static async Task<string> RunAsync(string verb, string args, CancellationToken token) { var psi = new ProcessStartInfo("gio", $"{verb} {args}") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }; using var p = Process.Start(psi) ?? throw new IOException("Unable to start gio."); using var registration = token.Register(() => { try { p.Kill(true); } catch { } }); var output = await p.StandardOutput.ReadToEndAsync(token); var error = await p.StandardError.ReadToEndAsync(token); await p.WaitForExitAsync(token); if (p.ExitCode != 0) throw new IOException(string.IsNullOrWhiteSpace(error) ? "GVfs MTP operation failed." : error.Trim()); return output; }
}
