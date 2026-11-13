namespace Emanager.App;

internal static class TrashDiagnostics
{
    private static readonly object Gate = new();

    public static void Event(string name, string details = "", Exception? exception = null)
    {
        var line = $"[{DateTime.UtcNow:O}] {name}" +
                   (string.IsNullOrWhiteSpace(details) ? "" : $" {Clean(details)}") +
                   (exception is null ? "" : $" exception={exception.GetType().FullName} frames={Frames(exception)}") +
                   Environment.NewLine;
        try
        {
            Directory.CreateDirectory(UserFolders.ConfigDirectory);
            var path = Path.Combine(UserFolders.ConfigDirectory, "trash.log");
            lock (Gate)
            {
                File.AppendAllText(path, line);
                var info = new FileInfo(path);
                if (info.Length > 1024 * 1024)
                {
                    var text = File.ReadAllText(path);
                    File.WriteAllText(path, text[^Math.Min(text.Length, 512 * 1024)..]);
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static string Frames(Exception exception)
    {
        var frames = (exception.StackTrace ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Select(x => x.Contains(" in ", StringComparison.Ordinal) ? x[..x.IndexOf(" in ", StringComparison.Ordinal)] : x)
            .Take(12);
        return Clean(string.Join(";", frames));
    }

    private static string Clean(string value) => value.Replace('\r', ' ').Replace('\n', ' ');
}
