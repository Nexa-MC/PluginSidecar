using Nexa.Xsr.Renderer.Nxml;

namespace PluginSidecar.Presentation;

/// <summary>Uses the exact pinned renderer assets embedded with the Worker, independent of Host files.</summary>
internal static class PluginNxmlAssets
{
    private const string Prefix = "PluginSidecar.Ui.";
    private static readonly Lazy<NxmlComponentLibrary> Components = new(() => new(Sources()));
    private static readonly Lazy<NssStyleSheet> Styles = new(() => NssStyleSheet.Parse(Read(Prefix + "Renderer.nss")));

    internal static NxmlExtensionProgram Create(string entryPoint, IReadOnlyDictionary<string, string> files)
        => new(entryPoint, files, Components.Value, Styles.Value);

    private static Dictionary<string, string> Sources()
    {
        var assembly = typeof(PluginNxmlAssets).Assembly;
        var names = assembly.GetManifestResourceNames().Where(name => name.StartsWith(Prefix, StringComparison.Ordinal)
            && name.EndsWith(".c.nxml", StringComparison.Ordinal)).ToArray();
        if (names.Length == 0) throw new InvalidDataException("worker.ui.assets.missing");
        return names.ToDictionary(name => name[Prefix.Length..^7], Read, StringComparer.Ordinal);
    }

    private static string Read(string name)
    {
        using var stream = typeof(PluginNxmlAssets).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidDataException("worker.ui.assets.missing");
        using var reader = new StreamReader(stream, new System.Text.UTF8Encoding(false, true));
        return reader.ReadToEnd();
    }
}
