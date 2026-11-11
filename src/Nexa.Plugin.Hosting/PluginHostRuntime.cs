using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100.Data;
namespace Nexa.Plugin.Hosting;

/// <summary>Trusted composition root. Schema policies, grants and routes are provided by the embedding Host.</summary>
public sealed class PluginHostRuntime : IAsyncDisposable
{
    public CapabilityAuthority Authority { get; }
    public AtomicDefinitionRegistry Definitions { get; }
    public DurablePluginStorage Storage { get; }
    private readonly PluginLoadCoordinator _loads;

    public PluginHostRuntime(string storageDirectory, Func<Definition, bool> validateDefinition,
        Func<SchemaRef, AbiValue, bool> validateStorage, IEnumerable<HostCapabilityRoute>? businessRoutes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        ArgumentNullException.ThrowIfNull(validateDefinition); ArgumentNullException.ThrowIfNull(validateStorage);
        Storage = new(Path.GetFullPath(storageDirectory), validateStorage);
        AtomicDefinitionRegistry? definitions = null;
        Authority = new(owner =>
        {
            definitions = new(owner, validateDefinition);
            return definitions.Routes.Concat(Storage.Routes).Concat(businessRoutes ?? []);
        });
        Definitions = definitions!;
        _loads = new(Authority, Definitions, identity => Storage.RetireAsync(identity.LoadInstanceId));
    }

    public ImmutableArray<PluginIdentity> Active => _loads.Active;
    /// <summary>Sources must come from verified acquisition; grants must come from trusted Host policy.</summary>
    public ValueTask<ImmutableArray<PluginLoadReport>> LoadAsync(IReadOnlyList<PluginLoadSource> sources, CancellationToken cancellation = default)
        => _loads.LoadAsync(sources, cancellation);
    public ValueTask StopAsync(string pluginId, CancellationToken cancellation = default) => _loads.StopAsync(pluginId, cancellation);
    public async ValueTask DisposeAsync()
    {
        try { await _loads.DisposeAsync().ConfigureAwait(false); }
        finally { await Storage.DisposeAsync().ConfigureAwait(false); }
    }
}
