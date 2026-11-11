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
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, FilePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
