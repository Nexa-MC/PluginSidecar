using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;

internal static class DefinitionTests
{
    internal static async Task Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "nexa-composed-" + Guid.NewGuid().ToString("N"));
        var schema = new SchemaRef(new("nexa:test"), 1, "fixture");
        var first = new Definition(new("owner:a"), schema.Id, schema, DataCodec.WriteString("one"), []);
        var second = first with { Id = new("owner:b"), References = [new(first.Id, true)] };
        try
        {
            await using var runtime = new PluginHostRuntime(root, d => d.Schema == schema && d.Body is AbiValue.Utf8String,
                (s, v) => s == schema && v is AbiValue.Utf8String);
            var entry = new Entry([first, second]);
            var report = await runtime.LoadAsync([entry.Source()]);
            Check(report.Single().State == LoadState.Active);
            using var api = entry.Context!.AsAbi100();
            var read = await api.Definitions.Get(new(first.Registry, first.Id), Options());
            Check(read.Success && AbiValueBinary.Encode(Definition.Write(read.Value.Value)).SequenceEqual(AbiValueBinary.Encode(Definition.Write(first))));
            var page = await api.Definitions.Query(new(first.Registry, [], new(null, 1)), Options());
            Check(page.Success && page.Value.Items.Length == 1 && page.Value.NextCursor is not null);
            var updated = await api.Definitions.UpdateOwned(new(first with { Body = DataCodec.WriteString("updated") }, read.Value.Revision), Options());
            Check(updated.Success);
            var duplicateRevision = await api.Definitions.UpdateOwned(new(first, read.Value.Revision), Options());
            Check(duplicateRevision.Error?.Code == AbiErrors.STALE_REVISION);
            var stalePage = await api.Definitions.Query(new(first.Registry, [], new(page.Value.NextCursor, 1)), Options());
            Check(stalePage.Error?.Code == AbiErrors.STALE_REVISION);
            var current = await api.Definitions.Get(new(first.Registry, first.Id), Options());
            var dependent = await api.Definitions.RemoveOwned(new(first.Registry, first.Id, current.Value.Revision), Options());
            Check(dependent.Error?.Code == AbiErrors.CONFLICT);
            var wrongSchema = await api.Definitions.UpdateOwned(new(first with { Body = new AbiValue.Null() }, current.Value.Revision), Options());
            Check(wrongSchema.Error?.Code == AbiErrors.SCHEMA_INVALID);
            var other = new HostInvocation(new("other", "1.0.0", Guid.NewGuid()), Guid.NewGuid(), "Active", Options());
            Reject(() => runtime.Definitions.Update(other, new(first, current.Value.Revision)), AbiErrors.HANDLE_OWNER_MISMATCH);
            await runtime.StopAsync("owner");
            Check(runtime.Definitions.Entries.IsEmpty);
            Check((await api.Definitions.Get(new(first.Registry, first.Id), Options())).Error?.Code == AbiErrors.PLUGIN_STOPPED);
            try { await runtime.Storage.Read(entry.Caller!, new(entry.Namespace!, "key")); throw new InvalidOperationException("Storage lease survived stop."); }
            catch (AbiHostException e) when (e.Code == AbiErrors.HANDLE_OWNER_MISMATCH) { }
            var malformed = new Entry([first with { Body = new AbiValue.Null() }]);
            var failed = await runtime.LoadAsync([malformed.Source()]);
            Check(failed.Single().State == LoadState.Failed && runtime.Definitions.Entries.IsEmpty);
            try { await runtime.Storage.Read(malformed.Caller!, new(malformed.Namespace!, "key")); throw new InvalidOperationException("Storage lease survived abort."); }
            catch (AbiHostException e) when (e.Code == AbiErrors.HANDLE_OWNER_MISMATCH) { }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine("PASS: composed runtime, typed definition routes, schema rejection, CAS, stale cursors, required references and storage lease retirement on stop/abort.");
    }
    private static InvocationOptions Options() => new(Guid.NewGuid(), TimeSpan.FromSeconds(5), default) { IdempotencyKey = Guid.NewGuid().ToString("N") };
    private static void Check(bool condition, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(condition))] string? expression = null)
    { if (!condition) throw new InvalidOperationException("Definition assertion failed: " + expression); }
    private static void Reject(Action action, string code)
    {
        try { action(); } catch (AbiHostException e) when (e.Code == code) { return; }
        throw new InvalidOperationException("Expected " + code);
    }
    private sealed class Entry(ImmutableArray<Definition> definitions) : IPluginBootstrapV1
    {
        internal ABIContext? Context;
        internal StorageNamespace? Namespace;
        internal HostInvocation? Caller;
        private static readonly ImmutableArray<string> Permissions = ["nexa.definitions.read", "nexa.definitions.write", "nexa.storage.read"];
        internal PluginLoadSource Source() => new(new(1, "owner", "1.0.0", 1, new("test", "test"), new([], [], "fixture"), [100], [], [], [], Permissions, []),
            Permissions, _ => ValueTask.FromResult<IPluginBootstrapV1>(this), () => ValueTask.CompletedTask);
        public async ValueTask<AbiReply<PreInitResult>> OnPreInitialize(PreInitContext context)
        {
            var draft = await context.Negotiator.GetABIContext(100, Options());
            var ready = await new Abi100Draft(draft.Value).Require("nexa.definitions").Require("nexa.storage").Make(context.Negotiator, Guid.NewGuid(), Permissions, default);
            Context = ready.Value;
            return AbiReply<PreInitResult>.Ok(new(Context, [], [], [], []));
        }
        public async ValueTask<AbiReply<Unit>> OnInitialize(ABIContext abi, StageContext stage)
        {
            Caller = new(stage.Identity, stage.LoadGroupId, "Active", Options());
            using var api = abi.ForStage100(stage);
            var storage = await api.Storage.OpenNamespace(new(new("owner:data"), 1), Options());
            Check(storage.Success); Namespace = storage.Value;
            var registered = await api.Definitions.RegisterBatch(new(definitions), Options());
            if (!registered.Success) return AbiReply<Unit>.Fail(registered.Error!);
            var staged = await api.Definitions.Get(new(definitions[0].Registry, definitions[0].Id), Options());
            Check(staged.Success);
            return AbiReply<Unit>.Ok(new());
        }
        public ValueTask<AbiReply<Unit>> OnPostInitialize(ABIContext abi, StageContext stage) => Ok();
        public ValueTask<AbiReply<CheckupResult>> OnCheckup(ABIContext abi, StageContext stage) => ValueTask.FromResult(AbiReply<CheckupResult>.Ok(new(CheckupStatus.Passed, [], [])));
        public ValueTask<AbiReply<Unit>> OnFinalize(ABIContext abi, StageContext stage) => Ok();
        public ValueTask<AbiReply<Unit>> OnActivated(ABIContext abi, InvocationContext invocation) => Ok();
        public ValueTask<AbiReply<Unit>> OnAbort(AbortContext context) => Ok();
        public ValueTask<AbiReply<Unit>> OnStop(StopContext context) => Ok();
        private static ValueTask<AbiReply<Unit>> Ok() => ValueTask.FromResult(AbiReply<Unit>.Ok(new()));
    }
}
