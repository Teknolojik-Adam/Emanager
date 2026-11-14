using System.Diagnostics;

namespace Emanager.Core.Network;

/// <summary>Secret Service integration through secret-tool; failures are explicit and secrets never enter logs.</summary>
public sealed class SecretServiceCredentialStore : ICredentialStore
{
    public bool IsAvailable => File.Exists("/usr/bin/secret-tool") || File.Exists("/bin/secret-tool");
    public async Task SaveAsync(string key, string user, string secret, CancellationToken cancellationToken = default) { Ensure(); await RunAsync(new[] { "store", "--label=Emanager", "service", "Emanager", "key", key }, secret, cancellationToken); await RunAsync(new[] { "store", "--label=Emanager-user", "service", "Emanager-user", "key", key }, user, cancellationToken); }
    public async Task<(string User, string Secret)?> GetAsync(string key, CancellationToken cancellationToken = default) { Ensure(); var secret = await RunAsync(new[] { "lookup", "service", "Emanager", "key", key }, null, cancellationToken); var user = await RunAsync(new[] { "lookup", "service", "Emanager-user", "key", key }, null, cancellationToken); return string.IsNullOrEmpty(secret) ? null : (user, secret); }
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default) { Ensure(); await RunAsync(new[] { "clear", "service", "Emanager", "key", key }, null, cancellationToken); await RunAsync(new[] { "clear", "service", "Emanager-user", "key", key }, null, cancellationToken); }
    private void Ensure() { if (!IsAvailable) throw new PlatformNotSupportedException("Linux Secret Service is unavailable."); }
    private static async Task<string> RunAsync(string[] arguments, string? stdin, CancellationToken token) { var psi = new ProcessStartInfo("secret-tool") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }; foreach (var argument in arguments) psi.ArgumentList.Add(argument); using var p = Process.Start(psi) ?? throw new IOException("Unable to start secret-tool."); if (stdin is not null) await p.StandardInput.WriteAsync(stdin); p.StandardInput.Close(); using var registration = token.Register(() => { try { p.Kill(true); } catch { } }); var output = await p.StandardOutput.ReadToEndAsync(token); await p.WaitForExitAsync(token); if (p.ExitCode != 0) throw new IOException("Secret Service operation failed."); return output.TrimEnd(); }
}
