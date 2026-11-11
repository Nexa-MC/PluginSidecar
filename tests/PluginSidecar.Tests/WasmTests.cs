using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100.Data;
using PluginSidecar.WasmRuntime;
using Wasmtime;

internal static class WasmTests
{
    internal static async Task Run()
    {
        var request = DataCodec.Map(("kind", DataCodec.WriteString("invoke")), ("contract", DataCodec.WriteString("nexa.tasks/100")),
            ("method", DataCodec.WriteUInt64(6)), ("arguments", EntityRequest.Write(new(new("operation")))));
        var completed = DataCodec.Map(("kind", DataCodec.WriteString("complete")), ("value", DataCodec.WriteString("done")));
        byte[] requestBytes = AbiValueBinary.Encode(request), completeBytes = AbiValueBinary.Encode(completed);
        string wat = $$"""
        (module
          (memory (export "memory") 1 1)
          (global $calls (mut i32) (i32.const 0))
          (data (i32.const 0) "{{Escaped(requestBytes)}}")
          (data (i32.const 2048) "{{Escaped(completeBytes)}}")
          (func (export "nexa_alloc") (param i32) (result i32) i32.const 4096)
          (func (export "nexa_resume") (param i32 i32) (result i64)
             global.get $calls i32.eqz
             if (result i64)
               i32.const 1 global.set $calls
               i64.const {{requestBytes.Length}}
             else
               i64.const {{(2048UL << 32) | (uint)completeBytes.Length}}
             end))
        """;
        await using (var machine = new WasmPluginMachine(Module.ConvertText(wat)))
        {
            int calls = 0;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<AbiReply<AbiValue>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = machine.RunAsync(new AbiValue.Null(), (contract, method, arguments, _) =>
            {
                Check(contract == "nexa.tasks/100" && method == 6 && EntityRequest.Read(arguments).Id.Value == "operation");
                calls++; entered.TrySetResult(); return new(release.Task);
            }, TimeSpan.FromSeconds(5)).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!pending.IsCompleted);
            release.SetResult(AbiReply<AbiValue>.Ok(new AbiValue.Null()));
            Check(DataCodec.ReadString(await pending) == "done" && calls == 1);
        }
        bool rejected = false;
        try { await using var invalid = new WasmPluginMachine(Module.ConvertText("(module (import \"wasi_snapshot_preview1\" \"proc_exit\" (func (param i32))))")); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected);
        foreach (var body in new[] { "(loop $again br $again) i64.const 0", "i64.const 9223372036854775807" })
        {
            string invalid = $$"""
            (module (memory (export "memory") 1 1)
             (func (export "nexa_alloc") (param i32) (result i32) i32.const 0)
             (func (export "nexa_resume") (param i32 i32) (result i64) {{body}}))
            """;
            await using var machine = new WasmPluginMachine(Module.ConvertText(invalid), new(FuelPerCallback: 1000));
            rejected = false;
            try { await machine.RunAsync(new AbiValue.Null(), (_, _, _, _) => throw new InvalidOperationException("Unexpected Host call."), TimeSpan.FromSeconds(1)); }
            catch (Exception e) when (e is WasmtimeException or InvalidDataException or ArgumentException) { rejected = true; }
            Check(rejected);
            try { await machine.RunAsync(new AbiValue.Null(), (_, _, _, _) => throw new InvalidOperationException(), TimeSpan.FromSeconds(1)); throw new Exception("Faulted machine reused."); }
            catch (InvalidOperationException) { }
        }
        var binary = AbiValueBinary.Encode(DataCodec.Map(("max", new AbiValue.UInt64(ulong.MaxValue)), ("bytes", new AbiValue.Bytes([0, 255]))));
        var roundtrip = DataCodec.ReadMap(AbiValueBinary.Decode(binary));
        Check(DataCodec.ReadUInt64(roundtrip["max"]) == ulong.MaxValue && ((AbiValue.Bytes)roundtrip["bytes"]).Value.SequenceEqual(new byte[] { 0, 255 }));
        for (int i = 0; i < binary.Length; i++)
        { rejected = false; try { AbiValueBinary.Decode(binary.AsSpan(0, i)); } catch (FormatException) { rejected = true; } Check(rejected); }
        try { AbiValueBinary.Decode([1, 0, 0, 0, 1, 2]); throw new Exception("Noncanonical boolean accepted."); } catch (FormatException) { }
        Console.WriteLine("PASS: real import-free Wasm execution, asynchronous Host continuation, fuel traps, bounded memory replies, fault retirement and strict binary decoding.");
    }
    private static string Escaped(byte[] bytes) => string.Concat(bytes.Select(b => "\\" + b.ToString("x2")));
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Wasm assertion failed."); }
}
