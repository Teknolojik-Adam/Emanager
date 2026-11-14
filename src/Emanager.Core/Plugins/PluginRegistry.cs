namespace Emanager.Core.Plugins;

public interface IPluginHost { void Log(string message, Exception? error = null); }
public interface IFileManagerPlugin { string Id { get; } Version ApiVersion { get; } bool IsTrustedBuiltIn { get; } void Initialize(IPluginHost host); }
public interface IFileActionProvider { IReadOnlyList<string> Actions { get; } }
public interface IPreviewProvider { bool CanPreview(string path, string? mimeType); Task<object?> PreviewAsync(string path, CancellationToken cancellationToken = default); }
public interface INetworkProvider { string Scheme { get; } bool IsAvailable { get; } }
public interface IMetadataProvider { string Name { get; } }
public sealed record PluginRegistrationResult(bool Accepted, string? Error = null);

public sealed class PluginRegistry
{
    private readonly Version _apiVersion;
    private readonly List<IFileManagerPlugin> _plugins = new();
    public PluginRegistry(Version apiVersion) => _apiVersion = apiVersion;
    public IReadOnlyList<IFileManagerPlugin> Plugins => _plugins.AsReadOnly();
    public PluginRegistrationResult Register(IFileManagerPlugin plugin)
    {
        if (!plugin.IsTrustedBuiltIn) return new(false, "Third-party plugins are disabled by default.");
        if (plugin.ApiVersion.Major != _apiVersion.Major) return new(false, "Plugin API version is incompatible.");
        if (_plugins.Any(x => x.Id.Equals(plugin.Id, StringComparison.OrdinalIgnoreCase))) return new(false, "Plugin id already registered.");
        try { plugin.Initialize(new Host()); _plugins.Add(plugin); return new(true); }
        catch (Exception ex) { return new(false, $"Plugin initialization failed: {ex.Message}"); }
    }
    private sealed class Host : IPluginHost { public void Log(string message, Exception? error = null) { } }
}
