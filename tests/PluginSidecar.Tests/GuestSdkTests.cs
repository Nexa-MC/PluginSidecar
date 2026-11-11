using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100.Data;
using PluginSidecar.Packages;

internal static class GuestSdkTests
{
    internal static async Task Run()
    {
        string? packagePath = Environment.GetEnvironmentVariable("NEXA_WASM_SAMPLE");
        if (string.IsNullOrEmpty(packagePath))
        {
            Console.WriteLine("SKIP: compiled C guest (set NEXA_WASM_SAMPLE to the ZIP built by eng/build_wasm_sample.py).");
            return;
        }
        var root = Path.Combine(Path.GetTempPath(), "nexa-guest-sdk-" + Guid.NewGuid().ToString("N"));
        try
        {
            // This is explicitly trusted development input, not an unsigned production install path.
            using var package = VerifiedPluginPackage.Parse(await File.ReadAllBytesAsync(packagePath), "example.wasm", "1.0.0");
            await using var runtime = new PluginHostRuntime(root,
                d => d.Registry.Value == "example.wasm:messages" && d.Schema == new SchemaRef(new("example.wasm:message"), 1, "example-string-v1") && d.Body is AbiValue.Utf8String,
                (_, _) => false);
            var reports = await runtime.LoadAsync([package.CreateLoadSource(["nexa.definitions.write"])]);
            if (reports.Single().State != LoadState.Active) throw new InvalidOperationException("Compiled C guest failed: " + reports.Single().Error?.Code);
            var definition = runtime.Definitions.Entries.Single().Definition;
            if (definition.Id.Value != "example.wasm:hello" || DataCodec.ReadString(definition.Body) != "Hello from the C WebAssembly SDK") throw new InvalidOperationException("Guest registration was not consumed.");
            await runtime.StopAsync("example.wasm");
            if (!runtime.Definitions.Entries.IsEmpty) throw new InvalidOperationException("Guest registration survived stop.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine("PASS: compiled freestanding C guest -> verified developer package -> real continuation SDK call -> atomic registry publication -> stop.");
    }
}
