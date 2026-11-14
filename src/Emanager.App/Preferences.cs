using System.Text.Json;

namespace Emanager.App;

public sealed class Preferences
{
    public string LastDirectory { get; set; } = UserFolders.Home;
    public bool DarkTheme { get; set; } = true;
    public string Language { get; set; } = "en";
    public bool ShowHidden { get; set; }
    public bool ShowPreview { get; set; }
    public bool FoldersFirst { get; set; } = true;
    public string SortBy { get; set; } = "Ad";
    public bool Descending { get; set; }
    public string ViewMode { get; set; } = "Detaylar";
    public List<string> Pins { get; set; } = [];
    public List<string> NetworkBookmarks { get; set; } = [];
    public Dictionary<string, string> Shortcuts { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["copy"] = "Ctrl+C", ["paste"] = "Ctrl+V", ["rename"] = "F2", ["new-tab"] = "Ctrl+T", ["close-tab"] = "Ctrl+W"
    };
    private static string FilePath => Path.Combine(UserFolders.ConfigDirectory, "settings.json");
    public static Preferences Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new();
    }
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(UserFolders.ConfigDirectory);
            var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None
                };
                if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var stream = new FileStream(temporary, options))
                using (var writer = new StreamWriter(stream))
                    writer.Write(JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, FilePath, true);
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            finally
            {
                try { File.Delete(temporary); } catch (IOException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
