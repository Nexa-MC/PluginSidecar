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
    public PluginRuntimeServices Services { get; }
    private readonly PluginLoadCoordinator _loads;

    public PluginHostRuntime(string storageDirectory, Func<Definition,bool> validateDefinition,
        Func<SchemaRef,AbiValue,bool> validateStorage, IEnumerable<HostCapabilityRoute>? businessRoutes)
        : this(storageDirectory,validateDefinition,validateStorage,businessRoutes,null) { }

    public PluginHostRuntime(string storageDirectory, Func<Definition, bool> validateDefinition,
        Func<SchemaRef, AbiValue, bool> validateStorage, IEnumerable<HostCapabilityRoute>? businessRoutes = null, PluginRuntimeOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        ArgumentNullException.ThrowIfNull(validateDefinition); ArgumentNullException.ThrowIfNull(validateStorage);
        options ??= new();
        ArgumentNullException.ThrowIfNull(options.Clock);
        Storage = new(Path.GetFullPath(storageDirectory), validateStorage);
        AtomicDefinitionRegistry? definitions = null;
        PluginRuntimeServices? services = null;
        PluginLoadCoordinator? coordinator = null;
        Authority = new(owner =>
        {
            definitions = new(owner, validateDefinition);
            services = new(owner.PublicationGate, validateStorage, (identity, handle, input, options) => coordinator!.InvokeHandler(identity, handle, input, options), owner.HasCapabilities, Path.Combine(Path.GetFullPath(storageDirectory), "workflow-runs"), options, owner.InvokeAdmitted, owner.IsAuthorized);
            return definitions.Routes.Concat(Storage.Routes).Concat(services.Routes).Concat(businessRoutes ?? []);
        });
        Definitions = definitions!; Services = services!;
        _loads = coordinator = new(Authority, Definitions, identity => { Services.Retire(identity.LoadInstanceId); return Storage.RetireAsync(identity.LoadInstanceId); }, Services);
        Services.StartScheduler();
    }

    public ImmutableArray<PluginIdentity> Active => _loads.Active;
    /// <summary>Sources must come from verified acquisition; grants must come from trusted Host policy.</summary>
    public async ValueTask<ImmutableArray<PluginLoadReport>> LoadAsync(IReadOnlyList<PluginLoadSource> sources, CancellationToken cancellation = default)
    {
        var reports=await _loads.LoadAsync(sources,cancellation).ConfigureAwait(false);
        await Services.PollSchedulesAsync(cancellation).ConfigureAwait(false);
        return reports;
    }
    public ValueTask StopAsync(string pluginId, CancellationToken cancellation = default) => _loads.StopAsync(pluginId, cancellation);
    public async ValueTask DisposeAsync()
    {
        try { await Services.StopScheduler().ConfigureAwait(false); await _loads.DisposeAsync().ConfigureAwait(false); }
        finally { await Storage.DisposeAsync().ConfigureAwait(false); }
    }
}
