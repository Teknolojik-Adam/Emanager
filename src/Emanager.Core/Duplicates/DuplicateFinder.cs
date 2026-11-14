using System.Security.Cryptography;

namespace Emanager.Core.Duplicates;

public sealed record DuplicateFile(string Path, long Size, string Hash, string? FileSystemId);
public sealed record DuplicateGroup(IReadOnlyList<DuplicateFile> Files);
public interface IDuplicateFinder { Task<IReadOnlyList<DuplicateGroup>> FindAsync(string root, bool includeHardlinks = false, CancellationToken cancellationToken = default); }

public sealed class DuplicateFinder : IDuplicateFinder
{
    public async Task<IReadOnlyList<DuplicateGroup>> FindAsync(string root, bool includeHardlinks = false, CancellationToken cancellationToken = default)
    {
        var bySize = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(path => new FileInfo(path)).Where(x => x.Exists && !x.Attributes.HasFlag(FileAttributes.ReparsePoint)).GroupBy(x => x.Length).Where(x => x.Count() > 1);
        var groups = new List<DuplicateGroup>();
        foreach (var candidates in bySize)
        {
            var byHash = new Dictionary<string, List<DuplicateFile>>(StringComparer.Ordinal);
            var seenFileIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileId = await FileIdAsync(file.FullName, cancellationToken);
                if (!includeHardlinks && fileId is not null && !seenFileIds.Add(fileId)) continue;
                var hash = await HashAsync(file.FullName, cancellationToken); var item = new DuplicateFile(file.FullName, file.Length, hash, fileId); byHash.TryAdd(hash, new()); byHash[hash].Add(item);
            }
            groups.AddRange(byHash.Values.Where(x => x.Count > 1).Select(x => new DuplicateGroup(x)));
        }
        return groups;
    }
    private static async Task<string> HashAsync(string path, CancellationToken token) { using var sha = SHA256.Create(); await using var stream = File.OpenRead(path); return Convert.ToHexString(await sha.ComputeHashAsync(stream, token)); }
    private static async Task<string?> FileIdAsync(string path, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) return null;
        var executable = new[] { "/usr/bin/stat", "/bin/stat" }.FirstOrDefault(File.Exists); if (executable is null) return null;
        var start = new System.Diagnostics.ProcessStartInfo(executable, $"-c %d:%i {path}") { RedirectStandardOutput = true, UseShellExecute = false };
        using var process = System.Diagnostics.Process.Start(start); if (process is null) return null; var value = await process.StandardOutput.ReadToEndAsync(token); await process.WaitForExitAsync(token); return process.ExitCode == 0 ? value.Trim() : null;
    }
}
