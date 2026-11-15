namespace PluginSidecar.Presentation;

public sealed record PluginShortcutBinding(int Document, string Gesture, string Argument);

/// <summary>A complete native shortcut set for the publisher's acknowledged document revision.</summary>
public interface IPluginShortcutHost
{
    ValueTask ReplaceAsync(long revision, IReadOnlyList<PluginShortcutBinding> bindings, CancellationToken token);
}
