using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;

internal static class LoadingTests
{
    internal static async Task Run()
    {
        AtomicDefinitionRegistry? registry = null;
        var descriptor = Abi100Catalog.Operations.Single(o => o.Group == "Definitions" && o.Name == "RegisterBatch");
        var authority = new CapabilityAuthority([new(descriptor, (invocation, input) =>
        {
            var registrations = registry!.Stage(invocation, DefinitionBatch.Read(input).Definitions);
            return ValueTask.FromResult(AbiReply<AbiValue>.Ok(Page<RegistrationRef>.Write(new(registrations, null, new("staged")))));
        })]);
        registry = new(authority, d => d.Registry.Value == "nexa:test" && d.Schema.Digest == "hash"); await using var coordinator = new PluginLoadCoordinator(authority, registry);
        var trace = new List<string>();
        PluginLoadSource Source(string id, string[]? required = null, string[]? optional = null, string[]? conflicts = null, LoadStage? fail = null)
        {
            var range = new VersionRange(null, true, null, true);
            var manifest = new PluginManifest(1, id, "1.0.0", 1, new("fixture", "fixture"), new([], [], "fixture"), [100],
                (required ?? []).Select(x => new PluginDependency(x, range)).ToImmutableArray(),
                (optional ?? []).Select(x => new PluginDependency(x, range)).ToImmutableArray(),
                (conflicts ?? []).Select(x => new PluginConflict(x, range, "test")).ToImmutableArray(), [descriptor.Permission], []);
            return new(manifest, [descriptor.Permission], _ => ValueTask.FromResult<IPluginBootstrapV1>(new Entry(manifest, registry, trace, fail)), () => { trace.Add(id + ":release"); return ValueTask.CompletedTask; });
        }
        var initial = await coordinator.LoadAsync([Source("a"), Source("b", ["a"]), Source("c", ["b"]), Source("d", optional: ["a"])]);
        Check(initial.All(r => r.State == LoadState.Active) && registry.Entries.Length == 4);
        Check(trace.IndexOf("c:finalize") < trace.IndexOf("a:activated"));
        var early = await coordinator.LoadAsync([Source("early", conflicts: ["a"], fail: LoadStage.Initialize)]);
        Check(early.Single().State == LoadState.Failed && coordinator.Active.Length == 4);
        var hot = await coordinator.LoadAsync([Source("x", conflicts: ["a"])]);
        Check(hot.Single().Error?.Code == AbiErrors.PLUGIN_CONFLICT && coordinator.Active.Select(i => i.PluginId).SequenceEqual(["d"]));
        Check(registry.Entries.Single().Owner.PluginId == "d");
        Check(trace.IndexOf("x:checkup") < trace.IndexOf("c:stop") && trace.IndexOf("c:stop") < trace.IndexOf("b:stop") && trace.IndexOf("b:stop") < trace.IndexOf("a:stop"));
        Check(!trace.Contains("x:finalize") && trace.Contains("x:abort"));
        var batch = await coordinator.LoadAsync([Source("bad", fail: LoadStage.Finalize), Source("dependent", ["bad"]), Source("independent")]);
        Check(batch.Single(r => r.Identity.PluginId == "bad").State == LoadState.Failed && batch.Single(r => r.Identity.PluginId == "dependent").State == LoadState.Failed);
        Check(batch.Single(r => r.Identity.PluginId == "independent").State == LoadState.Active);
        Check(!registry.Entries.Any(e => e.Owner.PluginId is "bad" or "dependent"));
        var missing = await coordinator.LoadAsync([Source("missing", ["absent"])]);
        Check(missing.Single().Error?.Code == AbiErrors.DEPENDENCY_MISSING);
        await coordinator.StopAsync("d");
        Check(trace.Contains("d:cleanup") && trace.IndexOf("d:stop") < trace.IndexOf("d:cleanup"));
        var normalConflicts = await coordinator.LoadAsync([Source("left", conflicts: ["right"]), Source("right")]);
        Check(normalConflicts.All(r => r.Error?.Code == AbiErrors.PLUGIN_CONFLICT));
        Check(!trace.Contains("left:activated") && !trace.Contains("right:activated"));
        var corrupt = Source("corrupt", conflicts: ["safe"]) with
        { CreateVerifiedEntry = _ => throw new System.Security.Cryptography.CryptographicException("invalid package") };
        var verifiedOnly = await coordinator.LoadAsync([Source("safe"), corrupt]);
        Check(verifiedOnly.Single(r => r.Identity.PluginId == "safe").State == LoadState.Active);
        Check(!trace.Contains("corrupt:abort"));
        var invalid = Source("malformed") with { Manifest = Source("malformed").Manifest with { PluginVersion = "invalid" } };
        var validation = await coordinator.LoadAsync([invalid, Source("valid")]);
        Check(validation.Single(r => r.Identity.PluginId == "malformed").State == LoadState.Failed && validation.Single(r => r.Identity.PluginId == "valid").State == LoadState.Active);
        Console.WriteLine("PASS: real atomic definition publication, dependency-group rollback, independent continuation, fail-before-stop conflict closure and consumer-first cleanup.");
    }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Loading assertion failed."); }
    private sealed class Entry(PluginManifest manifest, AtomicDefinitionRegistry registry, List<string> trace, LoadStage? fail) : IPluginBootstrapV1
    {
        public async ValueTask<AbiReply<PreInitResult>> OnPreInitialize(PreInitContext context)
        {
            var callback = context.Callbacks.Register("cleanup", (_, _) => { trace.Add(manifest.PluginId + ":cleanup"); return ValueTask.FromResult(AbiReply<AbiValue>.Ok(new AbiValue.Null())); });
            await context.Resources.RegisterCleanup(callback, new(Guid.NewGuid(), context.Budget, context.Cancellation));
            var draft = await context.Negotiator.GetABIContext(100, new(Guid.NewGuid(), context.Budget, context.Cancellation));
            var ready = await new Abi100Draft(draft.Value).Require("nexa.definitions").Make(context.Negotiator, Guid.NewGuid(), manifest.RequestedPermissions, context.Cancellation);
            return AbiReply<PreInitResult>.Ok(new(ready.Value, manifest.RequiredDependencies, manifest.OptionalDependencies, manifest.Conflicts, []));
        }
        public async ValueTask<AbiReply<Unit>> OnInitialize(ABIContext abi, StageContext stage)
        {
            trace.Add(manifest.PluginId + ":initialize");
            if (fail == LoadStage.Initialize) return Failure();
            using var api = abi.ForStage100(stage);
            var definition = new Definition(new(manifest.PluginId + ":entry"), new("nexa:test"), new(new("nexa:test"), 1, "hash"), new AbiValue.Null(), []);
            var result = await api.Definitions.RegisterBatch(new([definition]), new(Guid.NewGuid(), stage.Budget, stage.Cancellation) { IdempotencyKey = "register" });
            Check(result.Success);
            Check(!registry.Entries.Any(e => e.Owner.LoadInstanceId == stage.Identity.LoadInstanceId));
            return Ok();
        }
        public ValueTask<AbiReply<Unit>> OnPostInitialize(ABIContext abi, StageContext stage) => ValueTask.FromResult(Ok());
        public ValueTask<AbiReply<CheckupResult>> OnCheckup(ABIContext abi, StageContext stage)
        { trace.Add(manifest.PluginId + ":checkup"); return ValueTask.FromResult(AbiReply<CheckupResult>.Ok(new(CheckupStatus.Passed, [], []))); }
        public ValueTask<AbiReply<Unit>> OnFinalize(ABIContext abi, StageContext stage)
        { trace.Add(manifest.PluginId + ":finalize"); return ValueTask.FromResult(fail == LoadStage.Finalize ? Failure() : Ok()); }
        public ValueTask<AbiReply<Unit>> OnActivated(ABIContext abi, InvocationContext invocation)
        { trace.Add(manifest.PluginId + ":activated"); Check(registry.Entries.Any(e => e.Owner.LoadInstanceId == invocation.Identity.LoadInstanceId)); return ValueTask.FromResult(Ok()); }
        public ValueTask<AbiReply<Unit>> OnStop(StopContext context) { trace.Add(manifest.PluginId + ":stop"); return ValueTask.FromResult(Ok()); }
        public ValueTask<AbiReply<Unit>> OnAbort(AbortContext context) { trace.Add(manifest.PluginId + ":abort"); return ValueTask.FromResult(Ok()); }
        private static AbiReply<Unit> Ok() => AbiReply<Unit>.Ok(new());
        private AbiReply<Unit> Failure() => AbiReply<Unit>.Fail(new(AbiErrors.PLUGIN_EXCEPTION, "fixture", "Test", "Test", manifest.PluginId, Guid.Empty, Guid.Empty, false, ImmutableDictionary<string, string>.Empty));
    }
}
