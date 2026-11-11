using System.Collections.Immutable;
using System.Text.Json;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;
using PluginSidecar.WasmRuntime;

internal static class GuestConformanceTests
{
    internal static async Task Run()
    {
        string? path = Environment.GetEnvironmentVariable("NEXA_WASM_CONFORMANCE");
        if (string.IsNullOrEmpty(path)) { Console.WriteLine("SKIP: complete C guest conformance (set NEXA_WASM_CONFORMANCE)."); return; }
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "abi100.json")));
        var spec = document.RootElement;
        using var legacyDocument = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "abi100-legacy.json")));
        var legacy = legacyDocument.RootElement;
        var operations = Abi100Catalog.Operations.Concat(legacy.GetProperty("groups").EnumerateArray().SelectMany(g => g.GetProperty("operations").EnumerateArray().Select(o =>
            new AbiOperationDescriptor(g.GetProperty("name").GetString()!, g.GetProperty("contractId").GetString()!, o.GetProperty("methodId").GetUInt32(), o.GetProperty("name").GetString()!,
                o.GetProperty("input").GetString()!, o.GetProperty("output").GetString()!, "Legacy", [], "", "", "HostMappingPending")))).ToArray();
        await using var machine = new WasmPluginMachine(await File.ReadAllBytesAsync(path), new(FuelPerCallback: 100_000_000));
        var expectedError = new AbiError(AbiErrors.CAPABILITY_UNAVAILABLE, "missing", "Active", "fixture", "test", Guid.NewGuid(), Guid.NewGuid(), false,
            ImmutableDictionary<string, string>.Empty.Add("reason", "not installed"));
        int index = 0;
        foreach (var operation in operations)
        {
            var arguments = Fixture(operation.Input); var response = Fixture(operation.Output);
            foreach (bool failure in new[] { false, true })
            {
                int calls = 0;
                var actual = await machine.RunDetailedAsync(Input(index, arguments), (call, token) =>
                {
                    if (call.Contract != operation.ContractId || call.Method != operation.MethodId || !Equal(call.Arguments, arguments) ||
                        call.OperationId != Guid.Parse("12345678-1234-1234-1234-123456789abc") || call.IdempotencyKey != "conformance" || call.ExpectedRevision != "revision" || call.Budget != TimeSpan.FromSeconds(1))
                        throw new InvalidOperationException("Guest dispatch mismatch: " + operation.Group + "." + operation.Name);
                    calls++;
                    return ValueTask.FromResult(failure ? AbiReply<AbiValue>.Fail(expectedError) : AbiReply<AbiValue>.Ok(response));
                }, TimeSpan.FromSeconds(10));
                if (calls != 1 || (!failure && !Equal(actual, response)) || failure && DataCodec.ReadString(DataCodec.Field(DataCodec.ReadMap(actual), "code")) != expectedError.Code)
                    throw new InvalidOperationException("Guest result mismatch: " + operation.Name);
            }
            index++;
        }
        var models = spec.GetProperty("models").EnumerateObject().Concat(legacy.GetProperty("models").EnumerateObject()).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var operation in operations)
        { if (operation.Input.Contains('<')) models.Add(operation.Input); if (operation.Output.Contains('<')) models.Add(operation.Output); }
        foreach (var type in models.Order(StringComparer.Ordinal))
        {
            var value = Fixture(type);
            var roundtrip = await machine.RunAsync(Input(index++, value), (_, _, _, _) => throw new InvalidOperationException("A model roundtrip called Host."), TimeSpan.FromSeconds(10));
            if (!Equal(value, roundtrip)) throw new InvalidOperationException("Guest model roundtrip mismatch: " + type);
        }
        Console.WriteLine($"PASS: all {operations.Length} compiled C operations, success/error propagation and {models.Count} typed model/collection roundtrips against C# wire data.");

        AbiValue Fixture(string type, int depth = 0)
        {
            if (type.EndsWith('?')) return depth % 2 == 0 ? new AbiValue.Null() : Fixture(type[..^1], depth + 1);
            if (type.EndsWith("[]")) return new AbiValue.Array(depth < 4 ? [Fixture(type[..^2], depth + 1)] : []);
            if (type.StartsWith("Page<", StringComparison.Ordinal)) return DataCodec.Map(("items", new AbiValue.Array([Fixture(type[5..^1], depth+1)])), ("nextCursor", new AbiValue.Null()), ("revision", DataCodec.WriteString("revision")));
            if (type.StartsWith("Snapshot<", StringComparison.Ordinal)) return DataCodec.Map(("value", Fixture(type[9..^1], depth+1)), ("revision", DataCodec.WriteString("revision")), ("schemaVersion", new AbiValue.UInt64(1)), ("observedAt", DataCodec.WriteTimestamp(DateTimeOffset.UnixEpoch)));
            if (spec.GetProperty("enums").TryGetProperty(type, out var values)) return DataCodec.WriteString(values[0].GetString()!);
            if (spec.GetProperty("models").TryGetProperty(type, out var model) || legacy.GetProperty("models").TryGetProperty(type, out model))
                return new AbiValue.Map(model.EnumerateArray().ToImmutableDictionary(f => { string n=f.GetProperty("name").GetString()!; return char.ToLowerInvariant(n[0])+n[1..]; }, f=>f.TryGetProperty("constant", out var constant) ? DataCodec.WriteString(constant.GetString()!) : Fixture(f.GetProperty("type").GetString()!,depth+1)));
            return type switch
            {
                "string" => DataCodec.WriteString("测试\0🌟"), "QualifiedId" => DataCodec.WriteString("example:fixture"),
                "EntityId" or "Revision" => DataCodec.WriteString("opaque"), "UriRef" => DataCodec.WriteString("https://example.invalid/"),
                "Guid" => DataCodec.WriteGuid(Guid.Empty), "DateTimeOffset" => DataCodec.WriteTimestamp(DateTimeOffset.UnixEpoch),
                "int" => new AbiValue.Int64(int.MinValue), "uint" => new AbiValue.UInt64(uint.MaxValue),
                "bool" => new AbiValue.Bool(true), "long" => new AbiValue.Int64(long.MinValue), "ulong" => new AbiValue.UInt64(ulong.MaxValue),
                "double" => new AbiValue.Float64(-0.125), "AbiValue" => DataCodec.Map(("bytes", new AbiValue.Bytes([0,255,1])), ("null", new AbiValue.Null())),
                _ => throw new InvalidOperationException(type)
            };
        }
    }
    private static AbiValue Input(int index, AbiValue arguments) => DataCodec.Map(("index", new AbiValue.UInt64((ulong)index)), ("arguments", arguments));
    private static bool Equal(AbiValue a, AbiValue b) => AbiValueBinary.Encode(a).SequenceEqual(AbiValueBinary.Encode(b));
}
