namespace Emanager.App;

/// <summary>
/// Filesystem size measurement shared by the file pane, the disk-usage view and
/// the smoke tests. Never follows symbolic links and gives up on unbounded trees
/// so a scan can never hang the UI thread.
/// </summary>
public static class FolderMeasure
{
    // Same ceiling the details list has always used: stop counting rather than
    // stall on pathological trees. Unknown results are reported as null/-1.
    public const int MaxVisitedEntries = 100_000;

    public static long? Measure(string root, CancellationToken token)
    {
        long total = 0;
        int visited = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        try
        {
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                foreach (var path in Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    token.ThrowIfCancellationRequested();
                    if (++visited > MaxVisitedEntries) return null;
                    try
                    {
                        var attr = File.GetAttributes(path);
                        if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                        if ((attr & FileAttributes.Directory) != 0) pending.Push(path);
                        else total = checked(total + new FileInfo(path).Length);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            return total;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (OverflowException) { return null; }
        catch (OperationCanceledException) { return null; }
    }

    public sealed record BreakdownItem(string Path, string Name, long Bytes, bool IsDirectory, bool IsLink)
    {
        public bool IsUnknown => Bytes < 0;
    }

    public sealed record Progress(int DirectoriesScanned, long BytesSoFar);

    public sealed record Breakdown(
        IReadOnlyList<BreakdownItem> Items,
        long TotalBytes,
        int DirectoryCount,
        int FileCount,
        int LinkCount,
        int UnknownCount)
    {
        public long Largest => Items.Count == 0 ? 0 : Items.Max(x => Math.Max(x.Bytes, 0));
    }

    /// <summary>
    /// Measures every top-level entry of <paramref name="root"/>. Symbolic links
    /// are listed with zero size and never followed; unreadable trees are
    /// reported with Bytes = -1 instead of failing the whole scan.
    /// </summary>
    public static Breakdown TopLevelBreakdown(string root, CancellationToken token, IProgress<Progress>? progress = null)
    {
        var items = new List<BreakdownItem>();
        long total = 0;
        int dirs = 0, files = 0, links = 0, unknown = 0;
        long running = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            token.ThrowIfCancellationRequested();
            FileAttributes attr;
            try { attr = File.GetAttributes(entry); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            var name = Path.GetFileName(entry);
            if (name.Length == 0) continue;
            if ((attr & FileAttributes.ReparsePoint) != 0)
            {
                links++;
                items.Add(new BreakdownItem(entry, name, 0, false, true));
                continue;
            }
            if ((attr & FileAttributes.Directory) != 0)
            {
                dirs++;
                var size = Measure(entry, token);
                if (size is null) { unknown++; items.Add(new BreakdownItem(entry, name, -1, true, false)); }
                else
                {
                    total = checked(total + size.Value);
                    running = total;
                    items.Add(new BreakdownItem(entry, name, size.Value, true, false));
                }
            }
            else
            {
                files++;
                long length = 0;
                try { length = new FileInfo(entry).Length; }
                catch (IOException) { unknown++; items.Add(new BreakdownItem(entry, name, -1, false, false)); continue; }
                total = checked(total + length);
                running = total;
                items.Add(new BreakdownItem(entry, name, length, false, false));
            }
            progress?.Report(new Progress(dirs, running));
        }
        var sorted = items.OrderByDescending(x => x.Bytes).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        return new Breakdown(sorted, total, dirs, files, links, unknown);
    }
}

public sealed record PermissionState(
    bool SetUid, bool SetGid, bool Sticky,
    bool UserRead, bool UserWrite, bool UserExec,
    bool GroupRead, bool GroupWrite, bool GroupExec,
    bool OtherRead, bool OtherWrite, bool OtherExec);

/// <summary>
/// Pure Unix permission model used by the chmod dialog; unit-testable without
/// Avalonia. EffectiveFor() encodes the conservative recursive-chmod rules:
/// directories that gain read always gain execute, files never gain execute.
/// </summary>
public static class UnixPermissions
{
    // .NET 8's UnixFileMode only exposes the nine permission bits; the special
    // bits are the raw S_ISUID/S_ISGID/S_ISVTX values passed straight through
    // by chmod(2) via File.SetUnixFileMode.
    public const UnixFileMode SetUidBit = (UnixFileMode)0x800;
    public const UnixFileMode SetGidBit = (UnixFileMode)0x400;
    public const UnixFileMode VStickyBit = (UnixFileMode)0x200;

    public static PermissionState FromMode(UnixFileMode m) => new(
        m.HasFlag(SetUidBit), m.HasFlag(SetGidBit), m.HasFlag(VStickyBit),
        m.HasFlag(UnixFileMode.UserRead), m.HasFlag(UnixFileMode.UserWrite), m.HasFlag(UnixFileMode.UserExecute),
        m.HasFlag(UnixFileMode.GroupRead), m.HasFlag(UnixFileMode.GroupWrite), m.HasFlag(UnixFileMode.GroupExecute),
        m.HasFlag(UnixFileMode.OtherRead), m.HasFlag(UnixFileMode.OtherWrite), m.HasFlag(UnixFileMode.OtherExecute));

    public static UnixFileMode ToMode(PermissionState s)
    {
        UnixFileMode m = 0;
        if (s.SetUid) m |= SetUidBit;
        if (s.SetGid) m |= SetGidBit;
        if (s.Sticky) m |= VStickyBit;
        if (s.UserRead) m |= UnixFileMode.UserRead;
        if (s.UserWrite) m |= UnixFileMode.UserWrite;
        if (s.UserExec) m |= UnixFileMode.UserExecute;
        if (s.GroupRead) m |= UnixFileMode.GroupRead;
        if (s.GroupWrite) m |= UnixFileMode.GroupWrite;
        if (s.GroupExec) m |= UnixFileMode.GroupExecute;
        if (s.OtherRead) m |= UnixFileMode.OtherRead;
        if (s.OtherWrite) m |= UnixFileMode.OtherWrite;
        if (s.OtherExec) m |= UnixFileMode.OtherExecute;
        return m;
    }

    public static string FormatSymbolic(PermissionState s)
    {
        // Build explicitly to keep the three classes readable.
        var span = new char[9];
        span[0] = s.UserRead ? 'r' : '-'; span[1] = s.UserWrite ? 'w' : '-';
        span[2] = s.UserExec ? (s.SetUid ? 's' : 'x') : (s.SetUid ? 'S' : '-');
        span[3] = s.GroupRead ? 'r' : '-'; span[4] = s.GroupWrite ? 'w' : '-';
        span[5] = s.GroupExec ? (s.SetGid ? 's' : 'x') : (s.SetGid ? 'S' : '-');
        span[6] = s.OtherRead ? 'r' : '-'; span[7] = s.OtherWrite ? 'w' : '-';
        span[8] = s.OtherExec ? (s.Sticky ? 't' : 'x') : (s.Sticky ? 'T' : '-');
        return new string(span);
    }

    public static string FormatOctal(PermissionState s)
    {
        int special = (s.SetUid ? 4 : 0) | (s.SetGid ? 2 : 0) | (s.Sticky ? 1 : 0);
        int bits(bool r, bool w, bool x) => (r ? 4 : 0) | (w ? 2 : 0) | (x ? 1 : 0);
        return $"{special}{bits(s.UserRead, s.UserWrite, s.UserExec)}{bits(s.GroupRead, s.GroupWrite, s.GroupExec)}{bits(s.OtherRead, s.OtherWrite, s.OtherExec)}";
    }

    /// <summary>Accepts "755", "0755" or "4755"; rejects anything else.</summary>
    public static bool TryParseOctal(string? text, out PermissionState state)
    {
        state = new PermissionState(false, false, false, false, false, false, false, false, false, false, false, false);
        if (text is null) return false;
        var t = text.Trim();
        if (t.Length is not (3 or 4)) return false;
        foreach (var c in t) if (c < '0' || c > '7') return false;
        int special = t.Length == 4 ? t[0] - '0' : 0;
        if (t.Length == 4 && special > 7) return false;
        int Digit(char c) => c - '0';
        var u = Digit(t[^3]); var g = Digit(t[^2]); var o = Digit(t[^1]);
        state = new PermissionState(
            (special & 4) != 0, (special & 2) != 0, (special & 1) != 0,
            (u & 4) != 0, (u & 2) != 0, (u & 1) != 0,
            (g & 4) != 0, (g & 2) != 0, (g & 1) != 0,
            (o & 4) != 0, (o & 2) != 0, (o & 1) != 0);
        return true;
    }

    public static bool HasAnyExecute(PermissionState s) => s.UserExec || s.GroupExec || s.OtherExec;

    public sealed record ApplyResult(int Applied, int SkippedLinks, IReadOnlyList<string> Errors);

    /// <summary>
    /// The full chmod write path used by the dialog: per-entry failures are
    /// collected rather than thrown, links are never followed, and recursive
    /// apply keeps directories traversable and files non-executable.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public static ApplyResult ApplyToPaths(IReadOnlyList<string> paths, PermissionState state, bool recursive)
    {
        int applied = 0, skippedLinks = 0;
        var errors = new List<string>();
        var targets = new List<(string Path, bool IsDirectory, bool HadExecute)>();
        foreach (var p in paths)
        {
            try
            {
                if (File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint)) { skippedLinks++; continue; }
                bool isDir = Directory.Exists(p);
                var had = FromMode(File.GetUnixFileMode(p));
                targets.Add((p, isDir, HasAnyExecute(had)));
                if (recursive && isDir)
                {
                    var stack = new Stack<string>();
                    stack.Push(p);
                    while (stack.Count > 0)
                    {
                        foreach (var child in Directory.EnumerateFileSystemEntries(stack.Pop()))
                        {
                            FileAttributes attr;
                            try { attr = File.GetAttributes(child); }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                            if (attr.HasFlag(FileAttributes.ReparsePoint)) { skippedLinks++; continue; }
                            bool childIsDir = attr.HasFlag(FileAttributes.Directory);
                            if (childIsDir) stack.Push(child);
                            UnixFileMode childHad;
                            try { childHad = File.GetUnixFileMode(child); }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                            try
                            {
                                File.SetUnixFileMode(child, EffectiveFor(childIsDir,
                                    HasAnyExecute(FromMode(childHad)), state));
                                applied++;
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            { errors.Add(Path.GetFileName(child) + ": " + ex.Message); }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { errors.Add(Path.GetFileName(p) + ": " + ex.Message); }
        }
        foreach (var (p, isDir, had) in targets)
        {
            try { File.SetUnixFileMode(p, EffectiveFor(isDir, had, state)); applied++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { errors.Add(Path.GetFileName(p) + ": " + ex.Message); }
        }
        return new ApplyResult(applied, skippedLinks, errors);
    }

    /// <summary>
    /// The mode actually written for one entry during (possibly recursive) apply.
    /// Directories that gained read always keep execute so they stay traversable;
    /// plain files never gain execute unless they already were executable.
    /// </summary>
    public static UnixFileMode EffectiveFor(bool isDirectory, bool hadAnyExecute, PermissionState requested)
    {
        var m = ToMode(requested);
        if (isDirectory)
        {
            if (m.HasFlag(UnixFileMode.UserRead)) m |= UnixFileMode.UserExecute;
            if (m.HasFlag(UnixFileMode.GroupRead)) m |= UnixFileMode.GroupExecute;
            if (m.HasFlag(UnixFileMode.OtherRead)) m |= UnixFileMode.OtherExecute;
            return m;
        }
        if (!hadAnyExecute)
            m &= ~(UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        return m;
    }
}
