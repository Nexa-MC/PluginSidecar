using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100.Data;
using PluginSidecar.WasmRuntime;
using Wasmtime;
internal static class GuestCodecTests
{
    internal static async Task Run()
    {
        string? path=Environment.GetEnvironmentVariable("NEXA_WASM_CODEC");
        if (string.IsNullOrEmpty(path)) { Console.WriteLine("SKIP: compiled C codec rejection tests (set NEXA_WASM_CODEC)."); return; }
        byte[] module=await File.ReadAllBytesAsync(path);
        byte[][] invalid = [
            [1,0,0,0,1,2], // noncanonical bool
            [1,0,0,0,5,2,0,0,0,0xc0,0xaf], // overlong UTF-8
            [1,0,0,0,5,3,0,0,0,0xed,0xa0,0x80], // encoded surrogate
            [1,0,0,0,4,0,0,0,0,0,0,0xf0,0x7f], // infinity
            [1,0,0,0,0,0], // trailing bytes
            [1,0,0,0,7,255,255,255,127], // oversized count
            [1,0,0,0,8,2,0,0,0,1,0,0,0,97,0,1,0,0,0,97,0], // duplicate key
            [1,0,0,0,6,255,255,255,255] // negative byte length
        ];
        int fixture = 0;
        foreach (var bytes in invalid)
        {
            Console.WriteLine($"Codec rejection fixture {++fixture}/{invalid.Length}");
            try { AbiValueBinary.Decode(bytes); throw new InvalidOperationException("Invalid test fixture."); } catch (FormatException) { }
            await using var machine=new WasmPluginMachine(module);
            try { await machine.RunAsync(new AbiValue.Bytes(bytes.ToImmutableArray()), (_,_,_,_)=>throw new InvalidOperationException(),TimeSpan.FromSeconds(5)); throw new InvalidOperationException("Malformed C codec value accepted."); }
            catch (TrapException) { }
        }
        var valid=DataCodec.Map(("unicode",DataCodec.WriteString("\0汉字🌟")),("numbers",new AbiValue.Array([new AbiValue.Int64(long.MinValue),new AbiValue.UInt64(ulong.MaxValue),new AbiValue.Float64(-0.0)])));
        await using (var machine=new WasmPluginMachine(module))
        {
            var output=await machine.RunAsync(new AbiValue.Bytes(AbiValueBinary.Encode(valid).ToImmutableArray()),(_,_,_,_)=>throw new InvalidOperationException(),TimeSpan.FromSeconds(5));
            if(!AbiValueBinary.Encode(output).SequenceEqual(AbiValueBinary.Encode(valid))) throw new InvalidOperationException("C codec changed a valid value.");
        }
        Console.WriteLine("PASS: C/C# codec rejection parity, exact integer limits, embedded NUL, Unicode and signed zero.");
    }
}
