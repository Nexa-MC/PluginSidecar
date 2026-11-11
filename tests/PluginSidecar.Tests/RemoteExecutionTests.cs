using System.Diagnostics;
using System.IO.Compression;
using Nexa.Plugin.Bootstrap.V1;
using System.Security.Cryptography;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;
using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using PluginSidecar.Packages;
using PluginSidecar.WasmRuntime;

internal static class RemoteExecutionTests
{
    internal static async Task Run()
    {
        using(var endpoint=new ChunkedPluginEndpoint(new Echo()))
        {
            var exchange=new ChunkedPluginExchange(endpoint);var value=RandomNumberGenerator.GetBytes(200000);
            Check((await exchange.ExchangeAsync(value,default)).SequenceEqual(value));
        }
        await ChunkRejections();
        string? worker=Environment.GetEnvironmentVariable("NEXA_PLUGIN_WORKER");
        string? sample=Environment.GetEnvironmentVariable("NEXA_WASM_SAMPLE");
        if(string.IsNullOrEmpty(worker)||string.IsNullOrEmpty(sample))
        {Console.WriteLine("SKIP: process execution test (set NEXA_PLUGIN_WORKER and NEXA_WASM_SAMPLE).");return;}
        await FaultSealsGuest(sample);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(40));var token=timeout.Token;
        using var listener=SidecarIpcListener.Bind("nexa-plugin-test-"+Guid.NewGuid().ToString("N"));
        var start=new ProcessStartInfo{UseShellExecute=false,RedirectStandardInput=true,RedirectStandardError=true,RedirectStandardOutput=true};
        if(worker.EndsWith(".dll",StringComparison.OrdinalIgnoreCase))
        {start.FileName=Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")??"dotnet";start.ArgumentList.Add(Path.GetFullPath(worker));}
        else start.FileName=Path.GetFullPath(worker);
        start.ArgumentList.Add("--nexa-sidecar");start.ArgumentList.Add("--endpoint");start.ArgumentList.Add(listener.Endpoint);
        using var process=Process.Start(start)??throw new InvalidOperationException("Worker not started.");
        var errors=process.StandardError.ReadToEndAsync(token);var output=process.StandardOutput.ReadToEndAsync(token);
        string storage=Path.Combine(Path.GetTempPath(),"nexa-remote-"+Guid.NewGuid().ToString("N"));
        try
        {
            var secret=RandomNumberGenerator.GetBytes(SidecarBootstrap.ChallengeSize);
            await process.StandardInput.BaseStream.WriteAsync(secret,token);await process.StandardInput.BaseStream.FlushAsync(token);
            await using var stream=await listener.AcceptAsync(token);await SidecarBootstrap.AuthenticateAsync(stream,secret,token);CryptographicOperations.ZeroMemory(secret);
            using var connection=new SidecarConnection(stream);using var session=new SidecarHostSession(connection,RemoteWasmPluginMachine.PackageName);
            await session.HandshakeAsync(token);await session.AcceptRegistrationAsync(token);await session.AcceptStateSnapshotAsync(token);await session.ActivateAsync(token);
            Task receive=session.RunReceiveLoopAsync(token).AsTask();
            var exchange=new ChunkedPluginExchange(new Host(session));
            await using(var runtime=new PluginHostRuntime(storage,_=>true,Abi100Schemas.Validate,options:new(){AutomaticSchedules=false}))
            {
                using var package=VerifiedPluginPackage.Parse(await File.ReadAllBytesAsync(sample,token),"example.wasm","1.0.0");
                var report=(await runtime.LoadAsync([package.CreateLoadSource(["nexa.definitions.write"],(bytes,stop)=>RemoteWasmPluginMachine.CreateAsync(exchange,bytes,stop))],token)).Single();
                Check(report.State==Nexa.Plugin.Bootstrap.V1.LoadState.Active&&runtime.Definitions.Entries.Length==1);
                await runtime.StopAsync("example.wasm",token);Check(runtime.Active.IsEmpty);
                string workflow=Path.Combine(Path.GetDirectoryName(sample)!,"workflow.zip");
                using var flow=VerifiedPluginPackage.Parse(await File.ReadAllBytesAsync(workflow,token),"example.workflow","1.0.0");
                WasmWorkflowTests.Capture? capture=null;
                var source=flow.CreateLoadSource(flow.Manifest.RequestedPermissions,(bytes,stop)=>RemoteWasmPluginMachine.CreateAsync(exchange,bytes,stop));
                var originalFactory=source.CreateVerifiedEntry;
                source=source with {CreateVerifiedEntry=async stop=>capture=new(await originalFactory(stop))};
                Check((await runtime.LoadAsync([source],token)).Single().State==LoadState.Active);
                using var api=capture!.Context!.AsAbi100();
                var runs=await api.Workflows.ListRuns(new(null,32),new(Guid.NewGuid(),TimeSpan.FromSeconds(5),token));
                Check(runs.Success);await WasmWorkflowTests.Completed(api,runs.Value.Items.Single().Id);
                Check(capture.BusinessCallbacks==1);
                // A whole independent process executes code; the Host owns committed definitions.
                Check(process.Id!=Environment.ProcessId&&!process.HasExited);
                await runtime.StopAsync("example.workflow",token);Check(capture.CleanupCallbacks==1);
            }
            await session.ShutdownAsync(token);await receive.WaitAsync(token);await process.WaitForExitAsync(token);
            Check(process.ExitCode==0,await errors);Check((await output).Length==0);
        }
        finally
        {
            if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync();}
            if(Directory.Exists(storage))Directory.Delete(storage,true);
        }
        Console.WriteLine("PASS: authenticated independent PluginSidecar process, existing binary Host session, chunked payloads, remote Wasm registration/workflow callbacks and retirement.");
    }
    private static async Task FaultSealsGuest(string sample)
    {
        using var package=VerifiedPluginPackage.Parse(await File.ReadAllBytesAsync(sample),"example.wasm","1.0.0");
        byte[] bytes=package.CopyPayload(package.Manifest.WebAssemblyEntryPath!);
        await using var server=new GuestExecutionServer();var flaky=new Flaky(server);
        await using var machine=await RemoteWasmPluginMachine.CreateAsync(flaky,bytes);
        CryptographicOperations.ZeroMemory(bytes);flaky.Fail=true;
        try{await Run();throw new Exception("Expected disconnected exchange.");}catch(IOException){}
        int calls=flaky.Calls;
        try{await Run();throw new Exception("Expected sealed guest.");}catch(InvalidOperationException){}
        Check(flaky.Calls==calls);flaky.Fail=false;
        ValueTask<AbiValue> Run()=>machine.RunDetailedAsync(DataCodec.Map(("phase",new AbiValue.Utf8String("Initialize"))),
            (_,_)=>throw new Exception("No Host effects may run after transport loss."),TimeSpan.FromSeconds(5));
    }
    private sealed class Flaky(IPluginBinaryExchange inner):IPluginBinaryExchange
    {
        internal bool Fail;internal int Calls;
        public ValueTask<byte[]> ExchangeAsync(ReadOnlyMemory<byte> request,CancellationToken token)
        {Calls++;if(Fail)throw new IOException("Disconnected");return inner.ExchangeAsync(request,token);}
    }
    private static byte[] Packet(byte op,Guid id,Action<BinaryWriter>? body=null)
    {using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);writer.Write(op);writer.Write(id.ToByteArray());body?.Invoke(writer);return stream.ToArray();}
    private static async Task ChunkRejections()
    {
        using var endpoint=new ChunkedPluginEndpoint(new Echo());Guid id=Guid.NewGuid();byte[] bytes=[1,2,3];
        var begin=Packet(1,id,w=>{w.Write(3);w.Write(SHA256.HashData(bytes));});await endpoint.ExchangeAsync(begin,default);
        await Reject(begin);
        await Reject(Packet(2,id,w=>{w.Write(1);w.Write(1);w.Write((byte)1);}));
        await Reject(Packet(3,id));
        await endpoint.ExchangeAsync(Packet(2,id,w=>{w.Write(0);w.Write(3);w.Write(bytes);}),default);
        await endpoint.ExchangeAsync(Packet(3,id),default);
        await Reject(Packet(3,id));
        await endpoint.ExchangeAsync(Packet(5,id),default);await Reject(Packet(4,id,w=>{w.Write(0);w.Write(3);}));
        await Reject(Packet(1,Guid.NewGuid(),w=>{w.Write(ChunkedPluginExchange.MaximumBytes+1);w.Write(new byte[32]);}));
        Guid corrupt=Guid.NewGuid();await endpoint.ExchangeAsync(Packet(1,corrupt,w=>{w.Write(3);w.Write(new byte[32]);}),default);
        await endpoint.ExchangeAsync(Packet(2,corrupt,w=>{w.Write(0);w.Write(3);w.Write(bytes);}),default);await Reject(Packet(3,corrupt));
        async Task Reject(byte[] packet)
        {try{await endpoint.ExchangeAsync(packet,default);}catch(InvalidDataException){return;}throw new InvalidOperationException("Invalid transfer accepted.");}
    }
    private static void Check(bool value,string message="Remote execution assertion failed."){if(!value)throw new InvalidOperationException(message);}
    private sealed class Echo:IPluginBinaryExchange
    {public ValueTask<byte[]> ExchangeAsync(ReadOnlyMemory<byte> input,CancellationToken cancellation)=>ValueTask.FromResult(input.ToArray());}
    private sealed class Host(SidecarHostSession session):IPluginBinaryExchange
    {
        public async ValueTask<byte[]> ExchangeAsync(ReadOnlyMemory<byte> input,CancellationToken cancellation)
        {
            var result=await session.SendBinaryCommandAsync(XsrSemanticId.Parse(RemoteWasmPluginMachine.ExecutionContract),new(SidecarWireCodecs.GeneratedDto,input.Span),TimeSpan.FromSeconds(30),cancellation);
            if(!result.IsSuccess)throw new IOException(result.Error!.Message);
            return result.Value!.ToArray();
        }
    }
}
