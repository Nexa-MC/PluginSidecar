using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;
using PluginSidecar.Packages;

internal static class WasmWorkflowTests
{
    private static InvocationOptions Options()=>new(Guid.NewGuid(),TimeSpan.FromSeconds(5),default){IdempotencyKey=Guid.NewGuid().ToString("N")};
    private static T Ok<T>(AbiReply<T> reply)=>reply.Success?reply.Value:throw new InvalidOperationException(reply.Error!.Code+" "+reply.Error.Operation);
    private static void Check(bool value){if(!value)throw new InvalidOperationException("Compiled workflow example assertion failed.");}
    internal static async Task Run()
    {
        string? sample=Environment.GetEnvironmentVariable("NEXA_WASM_SAMPLE");if(string.IsNullOrEmpty(sample))return;
        string path=Path.Combine(Path.GetDirectoryName(sample)!,"workflow.zip");
        string root=Path.Combine(Path.GetTempPath(),"nexa-wasm-workflow-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            using var package=VerifiedPluginPackage.Parse(await File.ReadAllBytesAsync(path),"example.workflow","1.0.0");
            var original=package.CreateLoadSource(["nexa.actions.write","nexa.workflows.write","nexa.workflows.read","nexa.schedules.write","nexa.schedules.read","nexa.resources.write"]);
            Capture? capture=null;
            var source=original with {CreateVerifiedEntry=async token=>capture=new(await original.CreateVerifiedEntry(token))};
            await using var host=new PluginHostRuntime(root,_=>true,Abi100Schemas.Validate,options:new(){AutomaticSchedules=false});
            var report=(await host.LoadAsync([source])).Single();if(report.State!=LoadState.Active)throw new InvalidOperationException(report.Error?.Code+" "+report.Error?.Stage);
            using var api=capture!.Context!.AsAbi100();
            var first=Ok(await api.Workflows.ListRuns(new(null,32),Options())).Items.Single();await Completed(api,first.Id);
            var schedule=Ok(await api.Schedules.List(new(null,32),Options())).Items.Single();Check(schedule.Id.Value=="example.workflow:timer"&&schedule.Enabled);
            var manual=Ok(await api.Schedules.TriggerNow(new(schedule.Id),Options()));await Completed(api,manual.Id);
            Check(capture.BusinessCallbacks==2);
            await host.StopAsync("example.workflow");Check(capture.CleanupCallbacks==1&&host.Active.IsEmpty);
        }
        finally{Directory.Delete(root,true);}
        Console.WriteLine("PASS: compiled C workflow package, sequential registration continuations, activation-triggered queued workflow, typed action callback, schedule trigger and post-stop cleanup.");
    }
    internal static async Task Completed(Abi100Context api,EntityId id)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while(true)
        {
            var operation=Ok(await api.Workflows.Read(new(id),Options()));
            if(operation.State==OperationState.Completed){Check(TextResult.Read(operation.Result).Text=="C callback completed");return;}
            if(operation.Failure is not null)throw new InvalidOperationException(operation.Failure.Code);
            await Task.Delay(5,timeout.Token);
        }
    }
    internal sealed class Capture(IPluginBootstrapV1 entry):IPluginBootstrapV1,IPluginInvocationV1,IPluginCleanupInvocationV1
    {
        internal ABIContext? Context;internal int BusinessCallbacks,CleanupCallbacks;
        public async ValueTask<AbiReply<PreInitResult>> OnPreInitialize(PreInitContext c)
        {var result=await entry.OnPreInitialize(c);if(result.Success)Context=result.Value.FinalContext;return result;}
        public ValueTask<AbiReply<Unit>> OnInitialize(ABIContext c,StageContext s)=>entry.OnInitialize(c,s);
        public ValueTask<AbiReply<Unit>> OnPostInitialize(ABIContext c,StageContext s)=>entry.OnPostInitialize(c,s);
        public ValueTask<AbiReply<CheckupResult>> OnCheckup(ABIContext c,StageContext s)=>entry.OnCheckup(c,s);
        public ValueTask<AbiReply<Unit>> OnFinalize(ABIContext c,StageContext s)=>entry.OnFinalize(c,s);
        public ValueTask<AbiReply<Unit>> OnActivated(ABIContext c,InvocationContext invocation)=>entry.OnActivated(c,invocation);
        public ValueTask<AbiReply<Unit>> OnStop(StopContext c)=>entry.OnStop(c);
        public ValueTask<AbiReply<Unit>> OnAbort(AbortContext c)=>entry.OnAbort(c);
        public async ValueTask<AbiReply<AbiValue>> InvokeHandler(ABIContext c,ulong handler,AbiValue input,InvocationContext invocation)
        {var result=await ((IPluginInvocationV1)entry).InvokeHandler(c,handler,input,invocation);if(result.Success)Interlocked.Increment(ref BusinessCallbacks);return result;}
        public async ValueTask<AbiReply<AbiValue>> InvokeCleanup(ABIContext c,ulong handler,string phase,InvocationOptions options)
        {Check(handler==2&&phase=="Stop");var result=await ((IPluginCleanupInvocationV1)entry).InvokeCleanup(c,handler,phase,options);if(result.Success)Interlocked.Increment(ref CleanupCallbacks);return result;}
    }
}
