using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Emanager.App;

/// <summary>
/// Chmod editor for one or more selected entries. Reads the current mode, lets
/// the user set the nine permission bits plus setuid/setgid/sticky, optionally
/// as a recursive apply. Symbolic links themselves are never chmod'ed; they are
/// reported as skipped because their targets live outside the selection.
/// </summary>
public static class PermissionsDialog
{
    public static async Task<bool> ShowAsync(Window owner, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return false;
        if (!OperatingSystem.IsLinux())
        {
            await MessageDialog.ShowAsync(owner, UiText.T("İzinler"), UiText.T("İzin düzenleme yalnızca Linux'ta desteklenir."));
            return false;
        }

        // Read current state. Links are separated out: chmod on a link follows
        // it on Linux, which would silently change a target the user did not pick.
        var direct = new List<string>();
        var links = new List<string>();
        UnixFileMode? common = null;
        bool mixed = false;
        foreach (var p in paths)
        {
            try
            {
                if (File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint)) { links.Add(p); continue; }
                var mode = File.GetUnixFileMode(p);
                if (common is null) common = mode;
                else if (common != mode) mixed = true;
                direct.Add(p);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Entry vanished or unreadable between selection and dialog open.
            }
        }
        if (direct.Count == 0)
        {
            await MessageDialog.ShowAsync(owner, UiText.T("İzinler"),
                links.Count > 0
                    ? UiText.T("Seçimdeki tüm öğeler sembolik bağlantı; bağlantı hedefleri değiştirilmez.")
                    : UiText.T("İzinleri okunacak öğe bulunamadı."));
            return false;
        }
        bool singleDirectory = direct.Count == 1 && Directory.Exists(direct[0]);
        var initial = UnixPermissions.FromMode(common ?? 0);

        var dialog = new Window
        {
            Title = UiText.T("İzinler (chmod)"),
            Width = 560, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = MainWindow.SidebarBrush, CanResize = false
        };

        var root = new StackPanel { Margin = new Thickness(24, 18, 24, 16), Spacing = 10 };
        var headline = direct.Count == 1
            ? Path.GetFileName(direct[0])
            : UiText.F("{0} öğe seçili", direct.Count);
        root.Children.Add(new TextBlock
        {
            Text = UiText.T("İzinler (chmod)"), FontSize = 17, FontWeight = FontWeight.SemiBold,
            Foreground = MainWindow.TextPrimary
        });
        root.Children.Add(new TextBlock
        {
            Text = headline, Foreground = MainWindow.TextMuted, FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (links.Count > 0)
            root.Children.Add(new TextBlock
            {
                Text = UiText.F("{0} sembolik bağlantı atlandı (hedefleri değişmez).", links.Count),
                Foreground = Brushes.Orange, FontSize = 11, TextWrapping = TextWrapping.Wrap
            });

        // ---- bit matrix --------------------------------------------------
        var matrix = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("120,70,70,70"),
            RowDefinitions = new RowDefinitions("26,Auto,Auto,Auto"),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        void Header(string text, int col, int row)
        {
            var t = new TextBlock
            {
                Text = UiText.T(text), FontSize = 11, FontWeight = FontWeight.SemiBold,
                Foreground = MainWindow.TextMuted, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(t, col); Grid.SetRow(t, row);
            matrix.Children.Add(t);
        }
        Header("İzin", 1, 0); Header("Yazma", 2, 0); Header("Çalıştırma", 3, 0);
        CheckBox[,] boxes = new CheckBox[3, 3];
        string[] classNames = ["Sahip", "Grup", "Diğer"];
        for (int r0 = 0; r0 < 3; r0++)
        {
            Header(classNames[r0], 0, r0 + 1);
            for (int c0 = 0; c0 < 3; c0++)
            {
                var cb = new CheckBox
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = MainWindow.TextPrimary,
                    IsChecked = r0 switch
                    {
                        0 => new[] { initial.UserRead, initial.UserWrite, initial.UserExec }[c0],
                        1 => new[] { initial.GroupRead, initial.GroupWrite, initial.GroupExec }[c0],
                        _ => new[] { initial.OtherRead, initial.OtherWrite, initial.OtherExec }[c0],
                    }
                };
                Grid.SetColumn(cb, c0 + 1); Grid.SetRow(cb, r0 + 1);
                matrix.Children.Add(cb);
                boxes[r0, c0] = cb;
            }
        }
        root.Children.Add(matrix);

        // ---- special bits -------------------------------------------------
        var specialRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        var setUid = new CheckBox { Content = "setuid", Foreground = MainWindow.TextPrimary, IsChecked = initial.SetUid };
        var setGid = new CheckBox { Content = "setgid", Foreground = MainWindow.TextPrimary, IsChecked = initial.SetGid };
        var sticky = new CheckBox { Content = "sticky", Foreground = MainWindow.TextPrimary, IsChecked = initial.Sticky };
        ToolTip.SetTip(setUid, UiText.T("Yürütürken dosya sahibinin yetkisini kullanır. Dikkatli kullanın."));
        ToolTip.SetTip(setGid, UiText.T("Yeni dosyalar klasörün grubunu miras alır."));
        ToolTip.SetTip(sticky, UiText.T("Klasörde herkes yalnızca kendi dosyasını silebilir."));
        specialRow.Children.Add(setUid); specialRow.Children.Add(setGid); specialRow.Children.Add(sticky);
        root.Children.Add(specialRow);

        // ---- octal + symbolic preview -------------------------------------
        var octal = new TextBox
        {
            Width = 90, FontFamily = new FontFamily("monospace"), HorizontalAlignment = HorizontalAlignment.Left,
            Background = MainWindow.SurfaceBrush, Foreground = MainWindow.TextPrimary
        };
        octal.Text = mixed ? "" : UnixPermissions.FormatOctal(initial);
        octal.Watermark = UiText.T("örn. 755");
        var preview = new TextBlock
        {
            FontFamily = new FontFamily("monospace"), Foreground = MainWindow.AccentBrush,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0)
        };
        var octalRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        octalRow.Children.Add(new TextBlock
        {
            Text = UiText.T("Sekizlik"),
            Foreground = MainWindow.TextMuted, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        });
        octalRow.Children.Add(octal);
        octalRow.Children.Add(preview);
        root.Children.Add(octalRow);

        var recursive = new CheckBox
        {
            Content = UiText.T("Alt öğelere de uygula (özyinelemeli)"),
            Foreground = MainWindow.TextPrimary,
            IsVisible = direct.Any(Directory.Exists),
            IsChecked = false
        };
        root.Children.Add(recursive);

        var statusText = new TextBlock
        {
            Foreground = MainWindow.TextMuted, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            IsVisible = false
        };
        root.Children.Add(statusText);

        PermissionState Current() => new(
            setUid.IsChecked == true, setGid.IsChecked == true, sticky.IsChecked == true,
            boxes[0, 0].IsChecked == true, boxes[0, 1].IsChecked == true, boxes[0, 2].IsChecked == true,
            boxes[1, 0].IsChecked == true, boxes[1, 1].IsChecked == true, boxes[1, 2].IsChecked == true,
            boxes[2, 0].IsChecked == true, boxes[2, 1].IsChecked == true, boxes[2, 2].IsChecked == true);

        bool syncing = false;
        void RefreshPreview()
        {
            if (syncing) return;
            preview.Text = UnixPermissions.FormatSymbolic(Current()) + "   " +
                           "· " + UnixPermissions.FormatOctal(Current());
        }
        octal.GetObservable(Avalonia.Controls.TextBox.TextProperty).Subscribe(new TextObserver(() =>
        {
            if (syncing) return;
            if (UnixPermissions.TryParseOctal(octal.Text, out var parsed))
            {
                syncing = true;
                setUid.IsChecked = parsed.SetUid; setGid.IsChecked = parsed.SetGid; sticky.IsChecked = parsed.Sticky;
                boxes[0, 0].IsChecked = parsed.UserRead; boxes[0, 1].IsChecked = parsed.UserWrite; boxes[0, 2].IsChecked = parsed.UserExec;
                boxes[1, 0].IsChecked = parsed.GroupRead; boxes[1, 1].IsChecked = parsed.GroupWrite; boxes[1, 2].IsChecked = parsed.GroupExec;
                boxes[2, 0].IsChecked = parsed.OtherRead; boxes[2, 1].IsChecked = parsed.OtherWrite; boxes[2, 2].IsChecked = parsed.OtherExec;
                syncing = false;
            }
            preview.Text = UnixPermissions.FormatSymbolic(Current());
        }));
        foreach (var cb in new[] { setUid, setGid, sticky }.Concat(boxes.Cast<CheckBox>()))
            cb.Checked += (_, _) => RefreshPreview();
        foreach (var cb in new[] { setUid, setGid, sticky }.Concat(boxes.Cast<CheckBox>()))
            cb.Unchecked += (_, _) => RefreshPreview();
        RefreshPreview();

        // ---- buttons --------------------------------------------------------
        var apply = new Button
        {
            Content = UiText.T("İzinleri uygula"), Padding = new Thickness(18, 8),
            Background = MainWindow.AccentBrush, Foreground = MainWindow.CanvasBrush
        };
        var cancel = new Button
        {
            Content = UiText.T("Vazgeç"), Padding = new Thickness(18, 8),
            Background = MainWindow.SurfaceBrush, Foreground = MainWindow.TextPrimary,
            BorderBrush = MainWindow.StrokeBrush, BorderThickness = new Thickness(1), IsDefault = true
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(cancel); buttons.Children.Add(apply);
        root.Children.Add(buttons);
        dialog.Content = root;

        bool changed = false;
        apply.Click += (_, _) =>
        {
            // Flow guard for the analyzer; ShowAsync already rejects non-Linux hosts.
            if (!OperatingSystem.IsLinux()) return;
            apply.IsEnabled = false;
            try
            {
                var report = UnixPermissions.ApplyToPaths(direct, Current(), recursive.IsChecked == true);
                changed = report.Applied > 0;
                statusText.IsVisible = true;
                statusText.Foreground = report.Errors.Count > 0 ? Brushes.OrangeRed : MainWindow.TextMuted;
                statusText.Text = UiText.F("{0} öğede izin güncellendi.", report.Applied) +
                    (report.SkippedLinks > 0 ? " " + UiText.F("{0} sembolik bağlantı atlandı.", report.SkippedLinks) : "") +
                    (report.Errors.Count > 0 ? " " + UiText.F("{0} öğede hata: {1}", report.Errors.Count,
                        string.Join(" · ", report.Errors.Take(3))) : "");
                if (report.Errors.Count == 0) dialog.Close();
            }
            finally { apply.IsEnabled = true; }
        };
        cancel.Click += (_, _) => dialog.Close();
        // Deliberate-click contract for permission changes: Enter cancels, never applies.
        dialog.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape) { dialog.Close(); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        dialog.Opened += (_, _) => cancel.Focus();
        await dialog.ShowDialog(owner);
        owner.Focus();
        return changed;
    }
}
