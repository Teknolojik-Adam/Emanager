using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Emanager.App;

public static class BulkRename
{
    private sealed record Change(string Source, string Target, bool Directory);

    public static async Task<bool> ShowAsync(Window owner, IReadOnlyList<string> paths)
    {
        if (paths.Count < 2) return false;
        var dialog = new Window { Title = "Rename files", Width = 620, Height = 490,
            MinWidth = 560, MinHeight = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = MainWindow.SidebarBrush };
        var root = new DockPanel { Margin = new Thickness(20), LastChildFill = true };
        var fields = new StackPanel { Spacing = 9 };
        DockPanel.SetDock(fields, Dock.Top);
        root.Children.Add(fields);
        fields.Children.Add(new TextBlock { Text = $"{paths.Count} files/folders selected", FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = MainWindow.TextPrimary });
        fields.Children.Add(new TextBlock { Text = "Enter a name; files are numbered automatically.", Foreground = MainWindow.TextMuted });
        var simpleName = new TextBox { Watermark = "New name (e.g. Holiday)", Text = "File" };
        fields.Children.Add(simpleName);
        var advanced = new CheckBox { Content = "Show advanced options" };
        fields.Children.Add(advanced);
        var advancedFields = new StackPanel { Spacing = 8, IsVisible = false };
        fields.Children.Add(advancedFields);
        var find = new TextBox { Watermark = "Find (optional)" };
        var replace = new TextBox { Watermark = "Replace with" };
        var prefix = new TextBox { Watermark = "Prefix (optional)" };
        var suffix = new TextBox { Watermark = "Suffix (before extension)" };
        var number = new CheckBox { Content = "Add sequence numbers (001, 002, …)" };
        var preserve = new CheckBox { Content = "Keep file extensions", IsChecked = true };
        advancedFields.Children.Add(new TextBlock { Text = "Optional customization", Foreground = MainWindow.TextMuted });
        advancedFields.Children.Add(find); advancedFields.Children.Add(replace);
        advancedFields.Children.Add(prefix); advancedFields.Children.Add(suffix);
        advancedFields.Children.Add(number); advancedFields.Children.Add(preserve);
        var status = new TextBlock { Foreground = MainWindow.TextMuted, TextWrapping = TextWrapping.Wrap };
        fields.Children.Add(status);
        var preview = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("monospace"), FontSize = 12 };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 8) };
        var apply = new Button { Content = "Rename", Padding = new Thickness(16, 8) };
        footer.Children.Add(cancel); footer.Children.Add(apply);
        root.Children.Add(preview);
        dialog.Content = root;
        List<Change> changes = [];
        bool valid = false;
        void Update()
        {
            changes = [];
            var used = new HashSet<string>(StringComparer.Ordinal);
            var sourceSet = paths.ToHashSet(StringComparer.Ordinal);
            var problems = new List<string>();
            int index = 0;
            foreach (var path in paths)
            {
                index++;
                var oldName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
                var directory = Directory.Exists(path);
                var ext = !directory && (advanced.IsChecked != true || preserve.IsChecked == true) ? Path.GetExtension(oldName) : "";
                var stem = ext.Length > 0 ? oldName[..^ext.Length] : oldName;
                string next;
                if (advanced.IsChecked != true)
                    next = (simpleName.Text ?? "").Trim() + "_" + index.ToString("D3") + ext;
                else
                {
                    if (!string.IsNullOrEmpty(find.Text)) stem = stem.Replace(find.Text, replace.Text ?? "", StringComparison.Ordinal);
                    next = (prefix.Text ?? "") + stem + (suffix.Text ?? "") +
                        (number.IsChecked == true ? "_" + index.ToString("D3") : "") + ext;
                }
                var target = Path.Combine(Path.GetDirectoryName(path)!, next);
                if (string.IsNullOrWhiteSpace(next) || next is "." or ".." || next.Contains('/') || next.Contains((char)92) || next.Contains((char)0))
                    problems.Add("Invalid name: " + next);
                if (!used.Add(target)) problems.Add("Duplicate target name: " + next);
                if (target != path && !sourceSet.Contains(target) && (File.Exists(target) || Directory.Exists(target)))
                    problems.Add("Target already exists: " + next);
                if (target != path) changes.Add(new Change(path, target, directory));
            }
            preview.Text = string.Join("\n", paths.Select(path =>
            {
                var match = changes.FirstOrDefault(c => c.Source == path);
                return Path.GetFileName(path) + "  →  " + (match is null ? "(unchanged)" : Path.GetFileName(match.Target));
            }));
            var moving = changes.Select(c => c.Source).ToHashSet(StringComparer.Ordinal);
            foreach (var change in changes)
                if (sourceSet.Contains(change.Target) && !moving.Contains(change.Target))
                    problems.Add("Name belongs to an unchanged file: " + Path.GetFileName(change.Target));
            valid = problems.Count == 0 && changes.Count > 0;
            apply.IsEnabled = valid;
            status.Text = problems.Count > 0 ? string.Join(" | ", problems.Take(3)) : $"{changes.Count} names will change.";
        }
        advanced.IsCheckedChanged += (_, _) => { advancedFields.IsVisible = advanced.IsChecked == true; Update(); };
        simpleName.TextChanged += (_, _) => Update();
        find.TextChanged += (_, _) => Update(); replace.TextChanged += (_, _) => Update();
        prefix.TextChanged += (_, _) => Update(); suffix.TextChanged += (_, _) => Update();
        number.IsCheckedChanged += (_, _) => Update(); preserve.IsCheckedChanged += (_, _) => Update();
        cancel.Click += (_, _) => dialog.Close(false);
        apply.Click += async (_, _) =>
        {
            Update();
            if (!valid) return;
            apply.IsEnabled = false;
            try { Execute(changes); dialog.Close(true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                status.Text = "Operation failed: " + ex.Message;
                if (ex.Message.Contains("Kurtarma raporu:", StringComparison.Ordinal))
                {
                    // Make any hidden .emanager-rename-* leftovers visible.
                    if (owner is MainWindow main)
                    {
                        main.Prefs.ShowHidden = true;
                        main.ApplyViewPrefsToAllPanes();
                    }
                    apply.IsEnabled = false;
                    await MessageDialog.ShowAsync(dialog, "Toplu adlandırma: kurtarma gerekli", ex.Message);
                }
                else apply.IsEnabled = true;
            }
        };
        Update();
        return await dialog.ShowDialog<bool>(owner);
    }

    private static void Move(string from, string to, bool directory)
    {
        if (directory) Directory.Move(from, to);
        else File.Move(from, to);
    }

    private static void Execute(List<Change> changes)
    {
        var staged = new List<(Change Change, string Temporary)>();
        var completed = new List<(Change Change, string Temporary)>();
        try
        {
            foreach (var change in changes)
            {
                var temp = Path.Combine(Path.GetDirectoryName(change.Source)!, ".emanager-rename-" + Guid.NewGuid().ToString("N"));
                Move(change.Source, temp, change.Directory);
                staged.Add((change, temp));
            }
            foreach (var item in staged)
            {
                Move(item.Temporary, item.Change.Target, item.Change.Directory);
                completed.Add(item);
            }
        }
        catch (Exception original)
        {
            var rollbackErrors = new List<string>();
            // Reverse the published targets before restoring original names.
            foreach (var item in completed.AsEnumerable().Reverse())
            {
                try { Move(item.Change.Target, item.Temporary, item.Change.Directory); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { rollbackErrors.Add($"{item.Change.Target} → {item.Temporary}: {ex.Message}"); }
            }
            foreach (var item in staged.AsEnumerable().Reverse())
            {
                try { Move(item.Temporary, item.Change.Source, item.Change.Directory); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { rollbackErrors.Add($"{item.Temporary} → {item.Change.Source}: {ex.Message}"); }
            }
            if (rollbackErrors.Count == 0) throw;
            var recoveryReport = SaveRecoveryReport(staged, rollbackErrors);
            throw new IOException("Toplu adlandırma geri alınamadı! Dosyalar gizli geçici adlarda kalmış olabilir. " +
                "Kurtarma raporu: " + recoveryReport + "\n" +
                string.Join("\n", rollbackErrors.Take(8)), original);
        }
    }

    private static string SaveRecoveryReport(
        List<(Change Change, string Temporary)> staged, IReadOnlyList<string> failures)
    {
        var report = new System.Text.StringBuilder("Emanager toplu yeniden adlandırma kurtarma raporu\n");
        report.AppendLine($"Zaman: {DateTime.UtcNow:O}");
        foreach (var (change, temp) in staged)
        {
            report.AppendLine("Orijinal: " + change.Source);
            report.AppendLine("Geçici: " + temp);
            report.AppendLine("Hedef: " + change.Target);
            report.AppendLine();
        }
        report.AppendLine("Geri alma hataları:");
        foreach (var item in failures) report.AppendLine(item);
        try
        {
            var folder = Path.Combine(UserFolders.ConfigDirectory, "rename-recovery");
            if (OperatingSystem.IsLinux())
                Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            else Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "rename-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") +
                "-" + Guid.NewGuid().ToString("N") + ".txt");
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(file);
            writer.Write(report.ToString());
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A failed manifest must not hide the remaining temporary filenames.
            return "RAPOR YAZILAMADI: " + ex.Message + "\n" + report;
        }
    }
}
