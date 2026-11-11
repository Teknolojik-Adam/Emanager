using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Emanager.App;

/// <summary>
/// Conservative regular-file copy. The destination is published only after a complete
/// copy, using a same-directory staging file and a no-overwrite rename.
/// Not hardened against hostile concurrent path replacement; UI integration is disabled.
/// </summary>
public static class SafeFileOperations
{
    public static async Task CopyFileAsync(string source, string destinationDirectory, CancellationToken ct = default, Action<long>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ct.ThrowIfCancellationRequested();
        var input = Path.GetFullPath(source);
        var directory = Path.GetFullPath(destinationDirectory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        // Do not silently traverse symlinked parent directories. This is a conservative
        // policy, not a defense against concurrent path swaps by another process.
        RejectLinkedAncestors(input);
        RejectLinkedAncestors(directory);
        var attributes = File.GetAttributes(input);
        if (attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new NotSupportedException("Only regular files are supported; links and directories are blocked.");
        var original = new FileInfo(input);
        var originalLength = original.Length;
        var originalWriteTime = original.LastWriteTimeUtc;
        var output = Path.Combine(directory, Path.GetFileName(input));
        if (File.Exists(output) || Directory.Exists(output) || IsLink(output))
            throw new IOException("Destination already exists.");
        if (string.Equals(input, output, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("Source and destination are identical.");
        // Avoid wasting time copying when the destination is already occupied.
        // Stage on the destination filesystem to prevent exposing partial output files.
        var staging = Path.Combine(directory, ".emanager-copy-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var read = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var write = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                // Reject an opened symlink if its path changed between the metadata check and open.
                if (File.GetAttributes(input).HasFlag(FileAttributes.ReparsePoint))
                    throw new NotSupportedException("Source became a symbolic link.");
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[128 * 1024];
                long copied = 0;
                while (true)
                {
                    var count = await read.ReadAsync(buffer, ct);
                    if (count == 0) break;
                    await write.WriteAsync(buffer.AsMemory(0, count), ct);
                    digest.AppendData(buffer, 0, count);
                    copied += count;
                    progress?.Invoke(copied);
                    ct.ThrowIfCancellationRequested();
                }
                await write.FlushAsync(ct);
                // Ensure staged data is flushed to the filesystem before publication.
                write.Flush(flushToDisk: true);
                // A source changed during copying must not be published as a complete file.
                // This is a best-effort consistency check, not an atomic snapshot.
                var current = new FileInfo(input);
                current.Refresh();
                if (copied != originalLength || current.Length != originalLength ||
                    current.LastWriteTimeUtc != originalWriteTime)
                    throw new IOException("Source changed during copy.");
                // Detect same-size, timestamp-preserving edits that metadata alone misses.
                // This is a best-effort second read, not an atomic filesystem snapshot.
                await using var verify = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var verifyDigest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long verified = 0;
                while (true)
                {
                    int n = await verify.ReadAsync(buffer, ct);
                    if (n == 0) break;
                    verified += n;
                    verifyDigest.AppendData(buffer, 0, n);
                }
                if (verified != copied || !CryptographicOperations.FixedTimeEquals(digest.GetHashAndReset(), verifyDigest.GetHashAndReset()))
                    throw new IOException("Source content changed during copy.");
            }
            ct.ThrowIfCancellationRequested();
            // Recheck immediately before publication. These checks are best effort;
            // an adversarial process may still swap paths between operations.
            RejectLinkedAncestors(input);
            RejectLinkedAncestors(directory);
            var beforePublish = new FileInfo(input);
            beforePublish.Refresh();
            if (beforePublish.Length != originalLength || beforePublish.LastWriteTimeUtc != originalWriteTime)
                throw new IOException("Source changed before publication.");
            // Preserve Linux file permissions before publishing the staged file.
            // A chmod failure must not publish a file with unexpected permissions.
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(staging, File.GetUnixFileMode(input));
            File.Move(staging, output, overwrite: false);
        }
        finally
        {
            // Only ever clean up our private staging name, never an existing destination.
            try { File.Delete(staging); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool IsLink(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void RejectLinkedAncestors(string path, bool includeLeaf = true)
    {
        // Check every existing path component. Move/rename callers pass
        // includeLeaf: false because a symlink may legitimately be the operand.
        string? cursor = includeLeaf ? path : Directory.GetParent(path)?.FullName;
        while (cursor is not null)
        {
            if (IsLink(cursor))
                throw new NotSupportedException("Symbolic-link path components are not supported.");
            cursor = Directory.GetParent(cursor)?.FullName;
        }
    }

    // ---- Directory and trash operations (v16) --------------------------------

    /// <summary>
    /// Recursively copies a file or directory tree into destinationDirectory.
    /// Directories are staged under a private name and published with a
    /// no-overwrite rename, mirroring CopyFileAsync's publish-on-success rule.
    /// Symbolic links inside a tree are recreated as links, never followed.
    /// </summary>
    public static async Task CopyAsync(string source, string destinationDirectory, CancellationToken ct = default, Action<long>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ct.ThrowIfCancellationRequested();
        var input = Path.GetFullPath(source);
        var directory = Path.GetFullPath(destinationDirectory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        RejectLinkedAncestors(input);
        RejectLinkedAncestors(directory);
        var attributes = File.GetAttributes(input);
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new NotSupportedException("Symbolic-link sources are not supported.");
        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            await CopyFileAsync(input, directory, ct, progress);
            return;
        }
        if (FileSystem.IsSubpath(input, directory))
            throw new IOException("Cannot copy a directory into itself.");
        var output = Path.Combine(directory, Path.GetFileName(input));
        if (File.Exists(output) || Directory.Exists(output) || IsLink(output))
            throw new IOException("Destination already exists.");
        var staging = Path.Combine(directory, ".emanager-copy-" + Guid.NewGuid().ToString("N"));
        try
        {
            await CopyDirectoryTreeAsync(input, staging, ct, progress);
            ct.ThrowIfCancellationRequested();
            // File.Move does not support directories; Directory.Move is atomic
            // within one filesystem and refuses an existing destination.
            Directory.Move(staging, output);
        }
        finally
        {
            TryCleanupStagingDirectory(staging);
        }
    }

    private static async Task CopyDirectoryTreeAsync(string source, string stagingRoot, CancellationToken ct, Action<long>? progress)
    {
        Directory.CreateDirectory(stagingRoot);
        if (OperatingSystem.IsLinux())
        {
            try { File.SetUnixFileMode(stagingRoot, File.GetUnixFileMode(source)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        var queue = new Queue<string>();
        queue.Enqueue(source);
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = queue.Dequeue();
            // The private staging root lives inside the destination directory
            // while publishing; it must never be enumerated as source content.
            if (string.Equals(dir, Path.TrimEndingDirectorySeparator(stagingRoot), StringComparison.Ordinal)) continue;
            var relative = Path.GetRelativePath(source, dir);
            var targetDir = relative == "." ? stagingRoot : Path.Combine(stagingRoot, relative);
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(entry);
                var target = Path.Combine(targetDir, name);
                var attrs = File.GetAttributes(entry);
                if (attrs.HasFlag(FileAttributes.ReparsePoint))
                {
                    var linkTarget = new FileInfo(entry).LinkTarget
                        ?? new DirectoryInfo(entry).LinkTarget
                        ?? throw new IOException($"Cannot read link target for {entry}");
                    File.CreateSymbolicLink(target, linkTarget);
                }
                else if (attrs.HasFlag(FileAttributes.Directory))
                {
                    Directory.CreateDirectory(target);
                    if (OperatingSystem.IsLinux())
                    {
                        try { File.SetUnixFileMode(target, File.GetUnixFileMode(entry)); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                    queue.Enqueue(entry);
                }
                else
                {
                    await CopyFileAsync(entry, targetDir, ct, progress);
                }
            }
        }
    }

    /// <summary>
    /// Moves a file or directory. Same-filesystem moves use a rename; a
    /// cross-filesystem fallback stages a complete copy in the destination
    /// before the source is deleted, so an interrupted move never loses data.
    /// </summary>
    public static async Task MoveAsync(string source, string destinationDirectory, CancellationToken ct = default, Action<long>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ct.ThrowIfCancellationRequested();
        var input = Path.GetFullPath(source);
        var directory = Path.GetFullPath(destinationDirectory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        RejectLinkedAncestors(input, includeLeaf: false);
        RejectLinkedAncestors(directory);
        if (FileSystem.IsSubpath(input, directory))
            throw new IOException("Cannot move a directory into itself.");
        var output = Path.Combine(directory, Path.GetFileName(input));
        if (File.Exists(output) || Directory.Exists(output) || IsLink(output))
            throw new IOException("Destination already exists.");
        if (string.Equals(input, output, StringComparison.Ordinal))
            throw new IOException("Source and destination are identical.");
        bool isLink = IsLink(input);
        if (!isLink && !Directory.Exists(input) && !File.Exists(input))
            throw new FileNotFoundException("Source not found", input);
        try
        {
            if (isLink)
            {
                if (OperatingSystem.IsLinux())
                {
                    if (RenameLibc(input, output) != 0)
                        throw new IOException($"rename failed (errno {Marshal.GetLastPInvokeError()})");
                }
                else
                {
                    var lt = new FileInfo(input).LinkTarget ?? new DirectoryInfo(input).LinkTarget
                        ?? throw new IOException("Cannot read symbolic-link target.");
                    File.CreateSymbolicLink(output, lt);
                    File.Delete(input);
                }
            }
            else if (Directory.Exists(input)) Directory.Move(input, output);
            else File.Move(input, output, overwrite: false);
            return;
        }
        catch (IOException)
        {
            // Likely EXDEV: fall back to copy-then-delete below. Re-throw if the
            // source vanished, since the fallback could not succeed either.
            if (!File.Exists(input) && !Directory.Exists(input) && !IsLink(input)) throw;
        }
        if (isLink)
        {
            var lt = new FileInfo(input).LinkTarget ?? new DirectoryInfo(input).LinkTarget
                ?? throw new IOException("Cannot read symbolic-link target.");
            File.CreateSymbolicLink(output, lt);
            ct.ThrowIfCancellationRequested();
            File.Delete(input);
            return;
        }
        await CopyAsync(input, directory, ct, progress);
        ct.ThrowIfCancellationRequested();
        DeleteRecursive(input);
    }

    [DllImport("libc", EntryPoint = "rename", SetLastError = true)]
    private static extern int RenameLibc(string oldPath, string newPath);

    /// <summary>Renames inside the containing directory; never overwrites.</summary>
    public static Task RenameAsync(string source, string newName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        ct.ThrowIfCancellationRequested();
        if (newName.Contains(Path.DirectorySeparatorChar) || newName.Contains('/') || newName is "." or "..")
            throw new ArgumentException("Name must not contain path separators.", nameof(newName));
        var input = Path.GetFullPath(source);
        var parent = Directory.GetParent(input)?.FullName
            ?? throw new IOException("Cannot resolve parent directory.");
        var target = Path.Combine(parent, newName);
        if (string.Equals(input, target, StringComparison.Ordinal)) return Task.CompletedTask;
        if (File.Exists(target) || Directory.Exists(target) || IsLink(target))
            throw new IOException("Destination already exists.");
        if (IsLink(input))
        {
            // File.Move follows symbolic links; rename(2) moves the link itself.
            if (OperatingSystem.IsLinux())
            {
                if (RenameLibc(input, target) != 0)
                    throw new IOException($"rename failed (errno {Marshal.GetLastPInvokeError()})");
            }
            else
            {
                var linkTarget = new FileInfo(input).LinkTarget
                    ?? new DirectoryInfo(input).LinkTarget
                    ?? throw new IOException("Cannot read symbolic-link target.");
                File.Delete(input);
                File.CreateSymbolicLink(target, linkTarget);
            }
        }
        else if (Directory.Exists(input)) Directory.Move(input, target);
        else File.Move(input, target, overwrite: false);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Moves an entry to the freedesktop.org Trash on its own volume
    /// (~/.local/share/Trash or volume-root .Trash-uid) with info metadata.
    /// Falls back to permanent deletion only when the trash directory cannot
    /// be created or the cross-volume rename fails.
    /// </summary>
    public static void Trash(string path, string? trashRootOverride = null)
    {
        var input = Path.GetFullPath(path);
        if (!File.Exists(input) && !Directory.Exists(input) && !IsLink(input))
            throw new FileNotFoundException("Nothing to trash", input);
        if (IsLink(input))
        {
            // A symlink is metadata-only; unlink directly (freedesktop spec).
            File.Delete(input);
            return;
        }
        var home = UserFolders.Home;
        var trashRoot = trashRootOverride ?? (SameDevice(input, home)
            ? Path.Combine(home, ".local", "share", "Trash")
            : FindStickyVolumeTrash(input));
        if (trashRoot is null)
        {
            // No usable sticky trash on the volume: permanent delete fallback.
            DeleteRecursive(input);
            return;
        }
        var filesDir = Path.Combine(trashRoot, "files");
        var infoDir = Path.Combine(trashRoot, "info");
        try
        {
            Directory.CreateDirectory(filesDir);
            Directory.CreateDirectory(infoDir);
        }
        catch (IOException) { DeleteRecursive(input); return; }
        catch (UnauthorizedAccessException) { DeleteRecursive(input); return; }
        var name = Path.GetFileName(input.TrimEnd(Path.DirectorySeparatorChar));
        if (name.Length == 0) throw new IOException("Cannot trash a filesystem root.");
        string trashedFile, infoPath;
        lock (trashNameGate)
        {
            int suffix = 1;
            trashedFile = Path.Combine(filesDir, name);
            infoPath = Path.Combine(infoDir, name + ".trashinfo");
            while (File.Exists(trashedFile) || Directory.Exists(trashedFile) || File.Exists(infoPath))
            {
                var stem = Path.GetFileNameWithoutExtension(name);
                var tail = Path.GetExtension(name);
                if (Directory.Exists(Path.Combine(filesDir, name)) && !Path.HasExtension(name))
                { stem = name; tail = ""; }
                trashedFile = Path.Combine(filesDir, $"{stem}{tail}-{suffix}");
                infoPath = Path.Combine(infoDir, $"{stem}{tail}-{suffix}.trashinfo");
                suffix++;
            }
            // Reserve the names so parallel callers cannot claim them meanwhile.
            File.WriteAllText(infoPath,
                $"[Trash Info]\nPath={Uri.EscapeDataString(input).Replace("%2F", "/", StringComparison.Ordinal)}\nDeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n");
        }
        try
        {
            if (Directory.Exists(input)) Directory.Move(input, trashedFile);
            else File.Move(input, trashedFile, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (trashNameGate)
            {
                try { File.Delete(infoPath); } catch (IOException) { }
            }
            throw;
        }
    }

    private static readonly object trashNameGate = new();

    [StructLayout(LayoutKind.Sequential)]
    private struct StatvfsLinux
    {
        public ulong f_bsize, f_frsize;
        public ulong f_blocks, f_bfree, f_bavail;
        public ulong f_files, f_ffree, f_favail;
        public ulong f_fsid;
        public ulong f_fsid2;
        public uint f_flag;
        public uint f_namemax;
    }

    [DllImport("libc", EntryPoint = "__statvfs", SetLastError = true)]
    private static extern int StatvfsLinuxNative(string path, out StatvfsLinux buf);

    /// <summary>Device identity for trash routing; falls back to a volume heuristic.</summary>
    private static bool SameDevice(string a, string b)
    {
        if (!OperatingSystem.IsLinux())
        {
            try
            {
                return string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { return false; }
        }
        return StatvfsDevice(Path.GetFullPath(a)) == StatvfsDevice(Path.GetFullPath(b))
            && StatvfsDevice(Path.GetFullPath(a)) != 0;
    }

    private static ulong StatvfsDevice(string path)
    {
        try
        {
            if (StatvfsLinuxNative(path, out var buf) == 0) return buf.f_fsid;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return 0;
    }

    private static string? FindStickyVolumeTrash(string path)
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            var root = Path.GetPathRoot(path);
            if (root is null) return null;
            var uid = Environment.UserName;
            var candidate = Path.Combine(root, $".Trash-{uid}");
            var attrs = File.GetAttributes(candidate);
            if (!attrs.HasFlag(FileAttributes.Directory)) return null;
            var mode = File.GetUnixFileMode(candidate);
            // Spec requires the sticky bit (0o1000) and user ownership.
            if (!mode.HasFlag(UnixFileMode.OtherWrite) || !mode.HasFlag((UnixFileMode)0x200)) return null;
            return candidate;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static void DeleteRecursive(string path)
    {
        if (File.Exists(path) || IsLink(path)) { File.Delete(path); return; }
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            if (IsLink(entry)) File.Delete(entry);
            else if (Directory.Exists(entry)) DeleteRecursive(entry);
            else File.Delete(entry);
        }
        Directory.Delete(path);
    }

    private static void TryCleanupStagingDirectory(string staging)
    {
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            else if (File.Exists(staging)) File.Delete(staging);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
