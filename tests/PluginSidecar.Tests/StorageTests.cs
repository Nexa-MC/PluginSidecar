using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100.Data;
namespace PluginSidecar.Tests;

internal static class StorageTests
{
    internal static async Task Run()
    {
        await StorageMigrationTests.Run();
        string root = Path.Combine(Path.GetTempPath(), "nexa-storage-" + Guid.NewGuid().ToString("N"));
        var identity = new PluginIdentity("example.owner", "1.0.0", Guid.NewGuid());
        var caller = new HostInvocation(identity, Guid.NewGuid(), "Active", new(Guid.NewGuid(), TimeSpan.FromSeconds(5), default));
        var schema = new SchemaRef(new("nexa:string"), 1, "string-schema");
        bool Validate(SchemaRef shape, AbiValue value) => shape == schema && value is AbiValue.Utf8String;
        try
        {
            StorageNamespace old;
            await using (var storage = new DurablePluginStorage(root, Validate))
            {
                old = await storage.Open(caller, new(new("example.owner:data"), 1));
                var first = await storage.Write(caller, new(old, "a", DataCodec.WriteString("first"), schema, null));
                await Reject(() => storage.Write(caller, new(old, "a", DataCodec.WriteString("blind-overwrite"), schema, null)).AsTask(), AbiErrors.STALE_REVISION);
                Check(DataCodec.ReadString((await storage.Read(caller, new(old, "a"))).Value) == "first");
                var transaction = await storage.Begin(caller, old);
                await Reject(() => storage.Commit(caller, new(transaction, [new(old, "b", DataCodec.WriteString("b"), schema, null), new(old, "a", DataCodec.WriteString("bad"), schema, new("stale"))])).AsTask(), AbiErrors.STALE_REVISION);
                await Reject(() => storage.Read(caller, new(old, "b")).AsTask(), AbiErrors.NOT_FOUND);
                await storage.Commit(caller, new(transaction, [new(old, "b", DataCodec.WriteString("b"), schema, null), new(old, "a", DataCodec.WriteString("second"), schema, first.Revision)]));
                var page = await storage.Query(caller, new(old, "", new(null, 1)));
                Check(page.Items.Single().Key == "a" && page.NextCursor is not null);
                Check((await storage.Query(caller, new(old, "", new(page.NextCursor, 1)))).Items.Single().Key == "b");
                await storage.Write(caller, new(old, "c", DataCodec.WriteString("c"), schema, null));
                await Reject(() => storage.Query(caller, new(old, "", new(page.NextCursor, 1))).AsTask(), AbiErrors.STALE_REVISION);
                var other = caller with { Identity = identity with { PluginId = "example.other", LoadInstanceId = Guid.NewGuid() } };
                await Reject(() => storage.Read(other, new(old, "a")).AsTask(), AbiErrors.HANDLE_OWNER_MISMATCH);
                await Reject(() => storage.Open(other, new(new("example.owner:data"), 1)).AsTask(), AbiErrors.CAPABILITY_DENIED);
                await storage.RetireAsync(identity.LoadInstanceId);
                await Reject(() => storage.Read(caller, new(old, "a")).AsTask(), AbiErrors.HANDLE_OWNER_MISMATCH);
            }
            await using (var restarted = new DurablePluginStorage(root, Validate))
            {
                caller = caller with { Identity = identity with { LoadInstanceId = Guid.NewGuid() } };
                var reopened = await restarted.Open(caller, new(new("example.owner:data"), 1));
                Check(DataCodec.ReadString((await restarted.Read(caller, new(reopened, "a"))).Value) == "second");
                Check(DataCodec.ReadString((await restarted.Read(caller, new(reopened, "b"))).Value) == "b");
                await Reject(() => restarted.Read(caller, new(old, "a")).AsTask(), AbiErrors.HANDLE_OWNER_MISMATCH);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine("PASS: durable private namespaces, revision CAS, all-or-nothing storage transactions, cursor invalidation, owner isolation, retirement and restart persistence.");
    }
    private static async Task Reject(Func<Task> action, string code)
    { try { await action(); } catch (AbiHostException error) when (error.Code == code) { return; } throw new InvalidOperationException("Expected " + code); }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Storage assertion failed."); }
}
