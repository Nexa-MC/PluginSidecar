namespace PluginSidecar.Presentation;

/// <summary>The complete generic payload sent to the Host, independent of guest ownership.</summary>
public interface IPluginHostView
{
    Nexa.Sidecar.Protocol.SidecarNxmlDocument Document { get; }
    IReadOnlyDictionary<string, string> Model { get; }
}

/// <summary>Local presentation port. Implementations serialize only general documents and models to the Host.</summary>
public interface IPluginPresentationHost
{
    ValueTask<long> ReadRevisionAsync(CancellationToken token);
    ValueTask ReplaceAsync(long revision, IReadOnlyList<IPluginHostView> views, CancellationToken token);
    ValueTask UpdateModelsAsync(long revision, IReadOnlyList<IPluginHostView> views, CancellationToken token);
    ValueTask SetOpenAsync(long revision, int document, bool open, CancellationToken token);
    ValueTask<IReadOnlyList<int>> ReadVisibleAsync(long revision, CancellationToken token);
}

/// <summary>Opt-in source reconciliation; each index retains one previous source, or -1 adds a new source.</summary>
public interface IPluginRetainedPresentationHost : IPluginPresentationHost
{
    ValueTask ReconcileAsync(long revision, IReadOnlyList<IPluginHostView> views, IReadOnlyList<int> previousDocuments, CancellationToken token);
}
