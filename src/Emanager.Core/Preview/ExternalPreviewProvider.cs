using System.Diagnostics;

namespace Emanager.Core.Preview;

/// <summary>Runs only allow-listed, non-shell metadata/text tools with a timeout.</summary>
public sealed class ExternalPreviewProvider : IPreviewProvider
{
    private readonly TimeSpan _timeout;
    public ExternalPreviewProvider(TimeSpan? timeout = null) => _timeout = timeout ?? TimeSpan.FromSeconds(8);
    public bool CanHandle(string path, string? mimeType) => File.Exists(path) &&
        (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase) ||
         new[] { ".mp3", ".wav", ".flac", ".ogg", ".mp4", ".mkv", ".webm", ".avi" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));

    public async Task<PreviewResult?> PreviewAsync(string path, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) && FindExecutable("pdftotext") is { } pdftotext)
            return new(path, "pdf", await RunAsync(pdftotext, ["-f", "1", "-l", "3", "-layout", path, "-"], cancellationToken));
        if (FindExecutable("ffprobe") is { } ffprobe)
            return new(path, "media", await RunAsync(ffprobe, ["-v", "quiet", "-show_entries", "format=duration,size,format_name", "-of", "default=noprint_wrappers=1", path], cancellationToken));
        return new(path, extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? "pdf-metadata" : "media-metadata", new FileInfo(path).Length);
    }

    private async Task<string> RunAsync(string executable, string[] arguments, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(_timeout);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Preview helper could not be started.");
        using var registration = timeout.Token.Register(() => { try { process.Kill(true); } catch { } });
        var output = await process.StandardOutput.ReadToEndAsync(timeout.Token); await process.WaitForExitAsync(timeout.Token);
        return process.ExitCode == 0 ? output : throw new IOException("Preview helper failed.");
    }

    private static string? FindExecutable(string name) => Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator).Select(x => Path.Combine(x, name)).FirstOrDefault(File.Exists);
}
