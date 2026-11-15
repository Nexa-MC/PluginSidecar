using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SkiaSharp;
using Wasmtime;

namespace PluginSidecar;

/// <summary>Keeps a signed Worker image independent of the launcher's native libraries.</summary>
internal sealed class NativeWorkerLibraries : IDisposable
{
    private const string Prefix = "NexaCL.PluginSidecar.native.", ResourcePrefix = "PluginSidecar.Native.", LeaseName = "native.bundle.v1";
    private static readonly byte[] Marker = "Nexa.Plugin.Native/1"u8.ToArray();
    private readonly DirectoryInfo _directory;
    private readonly FileStream _lease;
    private readonly List<FileStream> _files = [];
    private readonly Dictionary<string, string> _paths = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<nint>> _handles = new(StringComparer.Ordinal);
    private int _disposed;
    private NativeWorkerLibraries(DirectoryInfo directory, FileStream lease) { _directory = directory; _lease = lease; }

    internal static NativeWorkerLibraries? Activate()
    {
        var assembly = typeof(NativeWorkerLibraries).Assembly;
        string[] resources = assembly.GetManifestResourceNames().Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)).ToArray();
        if (resources.Length == 0 && RuntimeFeature.IsDynamicCodeSupported) return null;
        string[] files = FileNames();
        if (resources.Length != files.Length || !files.All(file => resources.Contains(ResourcePrefix + file, StringComparer.Ordinal)))
            throw new InvalidDataException("native.bundle.missing");
        foreach (string path in Directory.EnumerateDirectories(Path.GetTempPath(), Prefix + "*", SearchOption.TopDirectoryOnly).Take(64))
            TryDelete(path);
        var directory = Directory.CreateTempSubdirectory(Prefix);
        FileStream? lease = null; NativeWorkerLibraries? bundle = null;
        try
        {
            lease = new(Path.Combine(directory.FullName, LeaseName), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            lease.Write(Marker); lease.Flush(true);
            bundle = new(directory, lease);
            for (int index = 0; index < files.Length; index++)
            {
                string name = files[index], path = Path.Combine(directory.FullName, name);
                using var source = assembly.GetManifestResourceStream(ResourcePrefix + name) ?? throw new InvalidDataException("native.bundle.missing");
                if (source.Length is <= 0 or > 128 * 1024 * 1024) throw new InvalidDataException("native.bundle.size");
                byte[] expected = SHA256.HashData(source); source.Position = 0;
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { source.CopyTo(output); output.Flush(true); }
                var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                bundle._files.Add(held);
                if (!CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(held))) throw new InvalidDataException("native.bundle.integrity");
                bundle._paths.Add(index == 0 ? "wasmtime" : "libSkiaSharp", path);
            }
            NativeLibrary.SetDllImportResolver(typeof(Engine).Assembly, bundle.Resolve);
            NativeLibrary.SetDllImportResolver(typeof(SKBitmap).Assembly, bundle.Resolve);
            return bundle;
        }
        catch
        {
            if (bundle is not null) bundle.Dispose();
            else { lease?.Dispose(); TryDelete(directory.FullName); }
            throw;
        }
    }

    private nint Resolve(string name, Assembly _, DllImportSearchPath? __)
        => _paths.TryGetValue(name, out var path)
            ? _handles.GetOrAdd(name, _ => new(() => NativeLibrary.Load(path), LazyThreadSafetyMode.ExecutionAndPublication)).Value : 0;

    internal static void Probe()
    {
        using var engine = new Engine();
        using var module = Wasmtime.Module.FromText(engine, "native-probe", "(module)");
        using var bitmap = new SKBitmap(2, 2); bitmap.Erase(SKColors.Red);
        if (bitmap.GetPixel(0, 0) != SKColors.Red) throw new InvalidOperationException("native.bundle.raster");
    }

    private static string[] FileNames() => OperatingSystem.IsWindows() ? ["wasmtime.dll", "libSkiaSharp.dll"]
        : OperatingSystem.IsMacOS() ? ["libwasmtime.dylib", "libSkiaSharp.dylib"] : ["libwasmtime.so", "libSkiaSharp.so"];

    private static void TryDelete(string path)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
            string[] allowed = [.. FileNames(), LeaseName];
            var entries = directory.EnumerateFileSystemInfos().Take(allowed.Length + 1).ToArray();
            if (entries.Length > allowed.Length || entries.Any(entry => !allowed.Contains(entry.Name, StringComparer.Ordinal)
                || (entry.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)) return;
            string leasePath = Path.Combine(path, LeaseName);
            using (var lease = new FileStream(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                if (lease.Length != Marker.Length) return;
                byte[] marker = new byte[Marker.Length]; lease.ReadExactly(marker);
                if (!marker.AsSpan().SequenceEqual(Marker)) return;
                foreach (var entry in entries) if (entry.Name != LeaseName) File.Delete(entry.FullName);
            }
            File.Delete(leasePath); directory.Delete(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { /* Active or inaccessible extractions are left for their owner or a later launch. */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var file in _files) file.Dispose();
        _lease.Dispose(); TryDelete(_directory.FullName);
        // P/Invoke stubs may be used by shutdown finalizers. Keep native handles until process exit.
    }
}
