using System.Reflection;
using System.Runtime.Loader;
using Nexa.Plugin.Bootstrap.V1;
namespace PluginSidecar.RuntimeAdapter;

/// <summary>CoreCLR-only adapter. The caller must authenticate, authorize and hash all payloads first.</summary>
public sealed class MemoryPlugin : IDisposable
{
    private Context? _context;
    private IPluginBootstrapV1? _entry;
    public IPluginBootstrapV1 Entry => _entry ?? throw new ObjectDisposedException(nameof(MemoryPlugin));
    private MemoryPlugin(Context context, IPluginBootstrapV1 entry) { _context = context; _entry = entry; }
    public static MemoryPlugin LoadVerifiedPayloads(string assemblyName, string typeName, IReadOnlyDictionary<string, byte[]> payloads)
    {
        var context = new Context(payloads);
        try
        {
            var assembly = context.LoadFromAssemblyName(new AssemblyName(assemblyName));
            var type = assembly.GetType(typeName, throwOnError: true)!;
            if (!typeof(IPluginBootstrapV1).IsAssignableFrom(type) || type.IsAbstract) throw new InvalidDataException(AbiErrors.ENTRY_INVALID);
            return new(context, (IPluginBootstrapV1)Activator.CreateInstance(type)!);
        }
        catch { context.Unload(); throw; }
    }
    // Unload is cooperative: plugin-created threads/references may keep the ALC alive.
    public void Dispose() { _entry = null; Interlocked.Exchange(ref _context, null)?.Unload(); }
    private sealed class Context : AssemblyLoadContext
    {
        private readonly Dictionary<string, byte[]> _payloads;
        private static readonly Assembly Bootstrap = typeof(IPluginBootstrapV1).Assembly;
        private static readonly HashSet<string> Framework = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator).Where(p => Path.GetDirectoryName(p) == Path.GetDirectoryName(typeof(object).Assembly.Location))
            .Select(p => Path.GetFileNameWithoutExtension(p)!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        internal Context(IReadOnlyDictionary<string, byte[]> payloads) : base(isCollectible: true)
        { _payloads = payloads.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.OrdinalIgnoreCase); }
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == Bootstrap.GetName().Name)
            {
                if (name.Version != Bootstrap.GetName().Version) throw new FileLoadException(AbiErrors.BOOTSTRAP_UNSUPPORTED);
                return Bootstrap;
            }
            if (name.Name is not null && Framework.Contains(name.Name)) return Default.LoadFromAssemblyName(name);
            if (name.Name is null || !_payloads.TryGetValue(name.Name, out var bytes)) throw new FileNotFoundException("Verified dependency missing: " + name.Name);
            using var stream = new MemoryStream(bytes, writable: false);
            var loaded = LoadFromStream(stream);
            if (!AssemblyName.ReferenceMatchesDefinition(name, loaded.GetName()) || (name.Version is not null && name.Version != loaded.GetName().Version))
                throw new FileLoadException("Verified assembly identity mismatch.");
            return loaded;
        }
        protected override nint LoadUnmanagedDll(string name) => throw new NotSupportedException(AbiErrors.PAYLOAD_KIND_UNSUPPORTED);
    }
}
