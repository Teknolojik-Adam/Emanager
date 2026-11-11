namespace Emanager.App;

/// <summary>
/// Tracks only successfully opened directories. Failed or cancelled navigation
/// does not alter history or its current position.
/// </summary>
public sealed class NavigationHistory
{
    private readonly List<string> entries = new();
    private int index = -1;
    public IReadOnlyList<string> Entries => entries.AsReadOnly();
    public int Position => index;
    public string? Current => index >= 0 ? entries[index] : null;
    public string? Peek(int delta)
    {
        var target = (long)index + delta;
        return target >= 0 && target < entries.Count ? entries[(int)target] : null;
    }
    public void RecordSuccess(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (string.Equals(Current, path, StringComparison.Ordinal)) return;
        if (index + 1 < entries.Count) entries.RemoveRange(index + 1, entries.Count - index - 1);
        entries.Add(path);
        index = entries.Count - 1;
    }
    public bool CommitTravel(int delta, string expectedPath)
    {
        var target = (long)index + delta;
        if (target < 0 || target >= entries.Count ||
            !string.Equals(entries[(int)target], expectedPath, StringComparison.Ordinal)) return false;
        index = (int)target;
        return true;
    }
}
