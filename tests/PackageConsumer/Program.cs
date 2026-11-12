using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;
using PluginSidecar.Packages;

// This file is compiled outside the repository, with PackageReferences only.
string root=Path.Combine(Path.GetTempPath(),"nexa-sdk-consumer-"+Guid.NewGuid().ToString("N"));
try
{
    await using var runtime=new PluginHostRuntime(root,_=>true,Abi100Schemas.Validate,options:new()
        {AutomaticSchedules=false,RegistryConsumers=[new Consumer()],ActionConfirmation=new Confirmation()});
    int routes=runtime.Services.Routes.Count()+runtime.Definitions.Routes.Count()+runtime.Storage.Routes.Count();
    if(routes!=98)throw new InvalidOperationException("Missing packaged runtime routes: "+routes);
    IPluginCleanupInvocationV1 cleanup=new Adapter();IPluginProbeInvocationV1 probe=new Adapter();
    GC.KeepAlive(cleanup);GC.KeepAlive(probe);
    using var package=VerifiedPluginPackage.Parse(await File.ReadAllBytesAsync(args.Single()),"example.wasm","1.0.0");
    var result=(await runtime.LoadAsync([package.CreateLoadSource(["nexa.definitions.write"])])).Single();
    if(result.State!=LoadState.Active||runtime.Definitions.Entries.Length!=1)throw new InvalidOperationException(result.Error?.Code??"Missing content");
    await runtime.StopAsync("example.wasm");
    if(!runtime.Active.IsEmpty)throw new InvalidOperationException("Package remained active");
    Console.WriteLine("PASS: independent NuGet-only consumer, 98 routes, cleanup/probe/confirmation ports, real compiled Wasm activation and retirement.");
}
finally{if(Directory.Exists(root))Directory.Delete(root,true);}
sealed class Consumer:IRegistryConsumer
{
    public QualifiedId Id=>new("nexa:test");
    public bool Validate(RegistryDescriptor registry,DefinitionBatch definitions)=>true;
    public void Publish(RegistryDescriptor registry,Snapshot<DefinitionBatch> snapshot){}
}
sealed class Confirmation:IActionConfirmation
{
    public ValueTask<bool> ConfirmAsync(ActionConfirmationRequest request,CancellationToken cancellation)=>ValueTask.FromResult(false);
}
sealed class Adapter:IPluginCleanupInvocationV1,IPluginProbeInvocationV1
{
    public ValueTask<AbiReply<AbiValue>> InvokeCleanup(ABIContext abi,ulong handler,string phase,InvocationOptions options)=>ValueTask.FromResult(AbiReply<AbiValue>.Ok(new AbiValue.Null()));
    public ValueTask<AbiReply<AbiValue>> InvokeProbe(ABIContext abi,ulong handler,AbiValue input,InvocationContext invocation)=>ValueTask.FromResult(AbiReply<AbiValue>.Ok(input));
}
