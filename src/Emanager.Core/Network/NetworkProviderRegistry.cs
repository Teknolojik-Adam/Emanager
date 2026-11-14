namespace Emanager.Core.Network;

public sealed class NetworkProviderRegistry
{
    private readonly Dictionary<string, INetworkProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    public void Register(INetworkProvider provider) => _providers[provider.Scheme] = provider;
    public INetworkProvider? Find(Uri uri) => _providers.GetValueOrDefault(uri.Scheme);
    public IReadOnlyList<INetworkProvider> Providers => _providers.Values.ToArray();
}
