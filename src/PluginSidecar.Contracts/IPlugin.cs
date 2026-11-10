namespace PluginSidecar.Contracts;

/// <summary>Experimental lifecycle for statically registered plugins; not a wire contract.</summary>
public interface IPlugin
{
    string Id { get; }
    ValueTask StartAsync(CancellationToken cancellationToken);
    ValueTask StopAsync(CancellationToken cancellationToken);
}
