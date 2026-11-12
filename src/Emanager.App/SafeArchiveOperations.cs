using System.Diagnostics;

namespace Emanager.App;

/// <summary>Build an archive inside a private same-volume directory. Publish
/// only on complete success using a no-overwrite rename. Never delete another
/// process's chosen destination when the archiver fails.</summary>
public static class SafeArchiveOperations
{
    public static async Task<string> CreateAsync(IReadOnlyList<string> sources, string destinationDirectory,
        string format, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        if (format is not ("zip" or "7z" or "tar.gz"))
            throw new ArgumentException("Unsupported archive format.", nameof(format));
        if (sources.Count == 0) throw new ArgumentException("No archive sources.", nameof(sources));
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetFullPath(destinationDirectory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        var paths = sources.Select(path => Path.GetFullPath(path)).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var path in paths)
        {
            if (!string.Equals(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)), directory,
                StringComparison.Ordinal))
                throw new ArgumentException("Archive sources must be direct children of the destination directory.");
            try { _ = File.GetAttributes(path); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            { throw new FileNotFoundException("Archive input is missing.", path); }
        }

        var baseName = paths.Length == 1 ? Path.GetFileName(Path.TrimEndingDirectorySeparator(paths[0])) : "Arsiv";
        var extension = "." + format;
        var target = Path.Combine(directory, baseName + extension);
        int suffix = 2;
        while (File.Exists(target) || Directory.Exists(target))
            target = Path.Combine(directory, baseName + " (" + suffix++ + ")" + extension);

        var stagingDir = Path.Combine(directory, ".emanager-archive-" + Guid.NewGuid().ToString("N"));
        var stagingFile = Path.Combine(stagingDir, "payload" + extension);
        try
        {
            if (OperatingSystem.IsLinux())
                Directory.CreateDirectory(stagingDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            else Directory.CreateDirectory(stagingDir);
            var info = new ProcessStartInfo(TrustedTools.Require(format == "tar.gz" ? "tar" : "7z"))
            {
                UseShellExecute = false, WorkingDirectory = directory,
                RedirectStandardError = true, RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            info.Environment["PATH"] = "/usr/bin:/bin";
            if (format == "tar.gz")
            {
                info.ArgumentList.Add("-czf");
                info.ArgumentList.Add(stagingFile);
            }
            else
            {
                info.ArgumentList.Add("a");
                info.ArgumentList.Add("-t" + format);
                info.ArgumentList.Add(stagingFile);
            }
            foreach (var path in paths)
                info.ArgumentList.Add("./" + Path.GetFileName(Path.TrimEndingDirectorySeparator(path)));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            using var process = Process.Start(info) ?? throw new IOException("Archive tool could not start.");
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            var stdoutDrain = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                await Task.WhenAll(stderr, stdoutDrain);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                if (cancellationToken.IsCancellationRequested) throw;
                throw new TimeoutException("Archive timeout: incomplete archive was not published.");
            }
            if (process.ExitCode != 0)
                throw new IOException("Archive tool failed: " + stderr.Result[..Math.Min(350, stderr.Result.Length)]);
            if (!File.Exists(stagingFile)) throw new IOException("Archive output was not created.");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stagingFile, target, overwrite: false);
            return target;
        }
        finally
        {
            // The staging directory is private and has an unpredictable name.
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
