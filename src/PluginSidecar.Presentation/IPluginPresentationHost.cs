namespace PluginSidecar.Presentation;

/// <summary>Local presentation port. Implementations serialize only general documents and models to the Host.</summary>
public interface IPluginPresentationHost
{
    ValueTask<long> ReadRevisionAsync(CancellationToken token);
    ValueTask ReplaceAsync(long revision, IReadOnlyList<PluginPreparedView> views, CancellationToken token);
    ValueTask UpdateModelsAsync(long revision, IReadOnlyList<PluginPreparedView> views, CancellationToken token);
    ValueTask SetOpenAsync(long revision, int document, bool open, CancellationToken token);
    ValueTask<IReadOnlyList<int>> ReadVisibleAsync(long revision, CancellationToken token);
}
