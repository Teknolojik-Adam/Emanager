using System.Diagnostics;

namespace Emanager.Core.Network;

/// <summary>Uses the desktop GVfs/GIO command surface and never embeds credentials in URIs.</summary>
public sealed class GvfsNetworkProvider(string scheme) : INetworkProvider
{
    private static readonly string[] Supported = ["smb", "sftp", "ftp", "ftps", "dav", "davs"];
    public string Scheme { get; } = scheme.ToLowerInvariant();
    public bool IsAvailable => Supported.Contains(Scheme, StringComparer.OrdinalIgnoreCase) && (File.Exists("/usr/bin/gio") || File.Exists("/bin/gio"));
    public Task ConnectAsync(NetworkConnection connection, CancellationToken cancellationToken = default) => RunAsync(cancellationToken, "mount", connection.Uri);
    public Task DisconnectAsync(NetworkConnection connection, CancellationToken cancellationToken = default) => RunAsync(cancellationToken, "mount", "-u", connection.Uri);
    public async IAsyncEnumerable<NetworkEntry> ListAsync(string uri, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) throw new PlatformNotSupportedException($"GVfs provider '{Scheme}' is unavailable.");
        var output = await RunOutputAsync(cancellationToken, "list", "-d", uri);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) yield return new(Path.GetFileName(line), line, false);
    }
    public Task CopyToAsync(string sourceUri, string destinationUri, IProgress<long>? progress = null, CancellationToken cancellationToken = default) => RunAsync(cancellationToken, "copy", sourceUri, destinationUri);
    private static Task RunAsync(CancellationToken token, params string[] args) => RunOutputAsync(token, args);
    private static async Task<string> RunOutputAsync(CancellationToken token, params string[] args)
    {
        var psi = new ProcessStartInfo("gio") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi) ?? throw new IOException("Unable to start gio."); using var registration = token.Register(() => { try { p.Kill(true); } catch { } }); var output = await p.StandardOutput.ReadToEndAsync(token); var error = await p.StandardError.ReadToEndAsync(token); await p.WaitForExitAsync(token); if (p.ExitCode != 0) throw new IOException(string.IsNullOrWhiteSpace(error) ? "GIO operation failed." : error.Trim()); return output;
    }
}
