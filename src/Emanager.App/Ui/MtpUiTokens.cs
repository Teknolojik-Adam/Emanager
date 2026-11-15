using Avalonia.Media;

namespace Emanager.App.Ui;

internal static class MtpUiTokens
{
    internal const double Space4 = 4;
    internal const double Space8 = 8;
    internal const double Space12 = 12;
    internal const double Space16 = 16;
    internal const double Space24 = 24;
    internal const double MinimumHeight = 44;
    internal const double CardRadius = 10;
    internal const double ButtonRadius = 8;
    internal const double RowRadius = 6;

    internal static readonly IBrush Canvas = Make("#0B1220");
    internal static readonly IBrush Surface = Make("#121C2E");
    internal static readonly IBrush Stroke = Make("#24334D");
    internal static readonly IBrush Text = Make("#E8EEF9");
    internal static readonly IBrush Muted = Make("#8FA3C4");
    internal static readonly IBrush Accent = Make("#38BDF8");
    internal static readonly IBrush Flow = Make("#A78BFA");
    internal static readonly IBrush Connected = Make("#34D399");
    internal static readonly IBrush Warning = Make("#FBBF24");
    internal static readonly IBrush Error = Make("#F87171");

    internal const string ConnectedLabel = "● connected";
    internal const string OfflineLabel = "○ offline";
    internal const string PairingLabel = "◌ pairing";
    internal const string WarningLabel = "⚠ warning";
    internal const string DoneLabel = "✓ done";
    internal const string ErrorLabel = "× error";

    private static SolidColorBrush Make(string hex) => new(Color.Parse(hex));
}
