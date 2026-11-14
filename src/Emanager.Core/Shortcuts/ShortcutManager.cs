using System.Text.Json;

namespace Emanager.Core.Shortcuts;

public sealed record ShortcutBinding(string ActionId, string Gesture);
public sealed record ShortcutRegistrationResult(bool Accepted, string? Error = null);
public interface IShortcutManager
{
    ShortcutRegistrationResult Register(string actionId, string gesture);
    ShortcutBinding? Get(string actionId);
    void ResetDefaults();
    string ToJson();
}

public sealed class ShortcutManager : IShortcutManager
{
    private readonly Dictionary<string, ShortcutBinding> _bindings = new(StringComparer.OrdinalIgnoreCase);
    public ShortcutRegistrationResult Register(string actionId, string gesture)
    {
        if (string.IsNullOrWhiteSpace(actionId) || string.IsNullOrWhiteSpace(gesture)) return new(false, "Action and gesture are required.");
        if (_bindings.Values.Any(x => x.Gesture.Equals(gesture, StringComparison.OrdinalIgnoreCase) && !x.ActionId.Equals(actionId, StringComparison.OrdinalIgnoreCase))) return new(false, "Shortcut is already assigned.");
        _bindings[actionId] = new(actionId, gesture); return new(true);
    }
    public ShortcutBinding? Get(string actionId) => _bindings.GetValueOrDefault(actionId);
    public IReadOnlyList<ShortcutBinding> Bindings => _bindings.Values.OrderBy(x => x.ActionId, StringComparer.OrdinalIgnoreCase).ToArray();
    public void ResetDefaults() { _bindings.Clear(); Register("copy", "Ctrl+C"); Register("paste", "Ctrl+V"); Register("rename", "F2"); }
    public string ToJson() => JsonSerializer.Serialize(_bindings.Values);
    public static ShortcutManager FromJson(string json)
    {
        var result = new ShortcutManager();
        foreach (var binding in JsonSerializer.Deserialize<ShortcutBinding[]>(json) ?? Array.Empty<ShortcutBinding>()) result.Register(binding.ActionId, binding.Gesture);
        return result;
    }
}
