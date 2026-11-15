namespace PluginSidecar.Presentation;

/// <summary>One raster in an owned document; all names and callback arguments are already scoped.</summary>
public sealed record PluginRasterBinding(int Document, string Slot, double Width, double Height, PluginRasterImage Image, string InputToken);

/// <summary>Replaces the complete raster set at an acknowledged presentation revision.</summary>
public interface IPluginRasterHost
{
    ValueTask ReplaceAsync(long revision, IReadOnlyList<PluginRasterBinding> bindings, CancellationToken token);
}
