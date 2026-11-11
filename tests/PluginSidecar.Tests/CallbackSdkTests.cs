using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;
using PluginSidecar.WasmRuntime;

internal static class CallbackSdkTests
{
    internal static async Task Run()
    {
        var callbacks = new CallbackPort(); int called = 0;
        using (var lease = callbacks.Register<Empty, ArchitectureInfo>("example:architecture", (_, options) =>
        { called++; return ValueTask.FromResult(AbiReply<ArchitectureInfo>.Ok(new("fixture", "wasm32"))); }))
        {
            var options = new InvocationOptions(Guid.NewGuid(), TimeSpan.FromSeconds(2), default);
            var good = await callbacks.Handler!(Empty.Write(new()), options);
            Check(good.Success && ArchitectureInfo.Read(good.Value).Architecture == "wasm32" && called == 1);
            var bad = await callbacks.Handler!(new AbiValue.Int64(7), options);
            Check(bad.Error?.Code == AbiErrors.SCHEMA_INVALID && called == 1);
            lease.Dispose(); lease.Dispose();
        }
        Check(callbacks.Released == 1);
        string? path = Environment.GetEnvironmentVariable("NEXA_WASM_CALLBACK");
        if (string.IsNullOrEmpty(path)) { Console.WriteLine("SKIP: compiled callback adapter (set NEXA_WASM_CALLBACK)."); return; }
        var descriptor = Abi100Catalog.Operations.Single(d => d.Group == "Platform" && d.Name == "Describe");
        var correlation = Guid.NewGuid(); var cause = Guid.NewGuid(); int hostCalls = 0;
        var authority = new CapabilityAuthority([HostRoutes.Typed<Empty, PlatformDescription>("Platform", "Describe", (caller, _) =>
        { Check(caller.Identity.PluginId == "example.callback" && caller.Options.CorrelationId == correlation && caller.Options.CausationId == cause); hostCalls++; return ValueTask.FromResult(new PlatformDescription([])); })]);
        var identity = new PluginIdentity("example.callback", "1.0.0", Guid.NewGuid()); var group = Guid.NewGuid();
        var admission = authority.Admit(identity, group, [descriptor.Permission]);
        var manifest = new PluginManifest(1, identity.PluginId, identity.PluginVersion, 1, new("", ""), new([], [], "nexa-wasm-1"), [100], [], [], [], [descriptor.Permission], [])
        { RequiredCapabilities = [new("nexa.platform", 1)] };
        await using var entry = new WasmPluginEntry(manifest, new(await File.ReadAllBytesAsync(path)));
        var pre = await entry.OnPreInitialize(new(identity, 1, admission.Negotiator, new BootstrapPort(), new BootstrapPort(), callbacks, default, TimeSpan.FromSeconds(5)));
        Check(pre.Success); var abi = pre.Value.FinalContext;
        var invocation = new InvocationContext(identity, Guid.NewGuid(), correlation, cause, default, TimeSpan.FromSeconds(5));
        var input = DataCodec.Map(("text", DataCodec.WriteString("回调\0🌟")), ("number", DataCodec.WriteUInt64(ulong.MaxValue)));
        Check((await entry.InvokeHandler(abi, 1, input, invocation)).Error?.Code == AbiErrors.PLUGIN_STOPPED);
        var probeRequest=new PredicateRequest(new("example.callback:probe"),Empty.Write(new()),new("probe-revision"));
        var probeResult=await entry.InvokeProbe(abi,5,PredicateRequest.Write(probeRequest),invocation);
        Check(probeResult.Success&&PredicateResult.Read(probeResult.Value).Outcome==PredicateOutcome.True&&PredicateResult.Read(probeResult.Value).InputRevision==probeRequest.Consistency);
        foreach (var phase in new[] { LoadStage.Initialize, LoadStage.PostInitialize, LoadStage.Checkup, LoadStage.Finalize })
        {
            var stage = new StageContext(identity, phase, Guid.NewGuid(), group, default, TimeSpan.FromSeconds(5)); admission.Enter(stage);
            bool success = phase switch
            { LoadStage.Initialize => (await entry.OnInitialize(abi, stage)).Success, LoadStage.PostInitialize => (await entry.OnPostInitialize(abi, stage)).Success,
              LoadStage.Checkup => (await entry.OnCheckup(abi, stage)).Success, _ => (await entry.OnFinalize(abi, stage)).Success };
            admission.End(stage); Check(success);
        }
        admission.Activate(); Check((await entry.OnActivated(abi, invocation)).Success);
        var echoed = await entry.InvokeHandler(abi, 1, input, invocation);
        Check(echoed.Success && AbiValueBinary.Encode(echoed.Value).SequenceEqual(AbiValueBinary.Encode(input)));
        var unavailable = await entry.InvokeHandler(abi, 4, Empty.Write(new()), invocation);
        Check(unavailable.Error?.Code == AbiErrors.EXPERIMENT_UNAVAILABLE && hostCalls == 0);
        var queried = await entry.InvokeHandler(abi, 2, Empty.Write(new()), invocation);
        Check(queried.Success && PlatformDescription.Read(queried.Value).Capabilities.IsEmpty && hostCalls == 1);
        Check((await entry.InvokeHandler(abi, 1, input, invocation with { Identity = identity with { LoadInstanceId = Guid.NewGuid() } })).Error?.Code == AbiErrors.HANDLE_OWNER_MISMATCH);
        var errorInput = DataCodec.Map(("code", DataCodec.WriteString("CUSTOM")), ("pluginId", DataCodec.WriteString("forged")), ("messageKey", DataCodec.WriteString("custom.message")));
        var failed = await entry.InvokeHandler(abi, 3, errorInput, invocation);
        Check(failed.Error is { Code: "CUSTOM", MessageKey: "custom.message" } && failed.Error.PluginId == identity.PluginId && failed.Error.CorrelationId == correlation);
        admission.Retire(false); Check((await entry.OnStop(new(identity, abi, "test", default, TimeSpan.FromSeconds(5)))).Success);
        Check((await entry.InvokeCleanup(abi,1,"Stop",new(Guid.NewGuid(),TimeSpan.FromSeconds(5),default))).Success);
        admission.CompleteStop();
        Check((await entry.InvokeHandler(abi, 1, input, invocation)).Error?.Code == AbiErrors.PLUGIN_STOPPED);
        Check((await entry.InvokeProbe(abi,5,PredicateRequest.Write(probeRequest),invocation)).Error?.Code==AbiErrors.HANDLE_OWNER_MISMATCH);
        Console.WriteLine("PASS: typed managed callbacks, compiled Wasm handlers, context metadata, Host continuations, causation, owner checks and activation/stop fencing.");
    }
    private static void Check(bool ok, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(ok))] string? expression = null) { if (!ok) throw new InvalidOperationException(expression); }
    private sealed class CallbackPort : ICallbacksV1
    {
        public Func<AbiValue, InvocationOptions, ValueTask<AbiReply<AbiValue>>>? Handler;
        public int Released;
        public CallbackHandle Register(string contractId, Func<AbiValue, InvocationOptions, ValueTask<AbiReply<AbiValue>>> callback) { Handler=callback; return new(1); }
        public void Release(CallbackHandle handle) { Released++; Handler=null; }
    }
    private sealed class BootstrapPort : IDiagnosticsV1, IResourcesV1
    {
        public ValueTask<AbiReply<Unit>> ReportDiagnostic(Finding finding, InvocationOptions options) => ValueTask.FromResult(AbiReply<Unit>.Ok(new()));
        public ValueTask<AbiReply<RegistrationHandle>> RegisterCleanup(CallbackHandle callback, InvocationOptions options) => ValueTask.FromResult(AbiReply<RegistrationHandle>.Ok(new(callback.Value)));
    }
}
