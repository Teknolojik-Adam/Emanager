namespace Emanager.App;

/// <summary>
/// Drag data is only a one-use lookup ticket. Never trust external MIME payloads
/// as filesystem paths, especially while the KIO administrator worker is enabled.
/// </summary>
internal static class InternalDragRegistry
{
    private static readonly object gate = new();
    private static readonly Dictionary<string, string[]> pending = new(StringComparer.Ordinal);

    internal static string Begin(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count is 0 or > 1000) throw new ArgumentException("Invalid drag selection.");
        var normalized = paths.Select(path =>
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                throw new ArgumentException("Only absolute drag sources are allowed.");
            return Path.GetFullPath(path);
        }).Distinct(StringComparer.Ordinal).ToArray();
        var ticket = Guid.NewGuid().ToString("N");
        lock (gate) pending.Add(ticket, normalized);
        return ticket;
    }

    internal static bool IsPending(object? value)
    {
        if (value is not string token) return false;
        lock (gate) return pending.ContainsKey(token);
    }

    internal static IReadOnlyList<string>? Consume(object? value)
    {
        if (value is not string token) return null;
        lock (gate)
        {
            if (!pending.Remove(token, out var paths)) return null;
            return Array.AsReadOnly(paths);
        }
    }

    internal static void Revoke(string token)
    {
        lock (gate) pending.Remove(token);
    }
}
