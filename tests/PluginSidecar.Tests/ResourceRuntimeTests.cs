using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;
using PluginSidecar.WasmRuntime;

internal static class ResourceRuntimeTests
{
    private static InvocationOptions Options()=>new(Guid.NewGuid(),TimeSpan.FromSeconds(5),default){IdempotencyKey=Guid.NewGuid().ToString("N")};
    private static T Ok<T>(AbiReply<T> reply)=>reply.Success?reply.Value:throw new InvalidOperationException(reply.Error!.Code+" "+reply.Error.Operation);
    private static void Check(bool value,[System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))]string? expression=null)
    {if(!value)throw new InvalidOperationException("Resource assertion: "+expression);}
    internal static async Task Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"nexa-resource-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            await using var host=new PluginHostRuntime(root,_=>true,Abi100Schemas.Validate,options:new(){AutomaticSchedules=false});
            var entry=new Entry();Check((await host.LoadAsync([entry.Source()])).Single().State==LoadState.Active);using var api=entry.Context!.AsAbi100();
            Check(entry.CleanupA!.Handle==entry.BootstrapRegistration!.Value.Value&&entry.CleanupA.Handle!=entry.Definition!.Handle&&entry.Definition.Handle!=entry.Action!.Handle);
            Check((await api.Resources.Release(new(entry.Action! with {DefinitionId=entry.Definition!.DefinitionId}),Options())).Error?.Code==AbiErrors.HANDLE_OWNER_MISMATCH);
            Ok(await api.Resources.Release(new(entry.Definition),Options()));Check(host.Definitions.Entries.IsEmpty);
            var run=Ok(await api.Actions.Invoke(new(new("owner:action"),new(new("nexa:global"),null,"test"),Empty.Write(new()),null,"work"),Options()));
            await entry.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check((await api.Resources.Release(new(entry.Action),Options())).Error?.Code==AbiErrors.CONFLICT);
            entry.Resume.TrySetResult();using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                while(Ok(await api.Tasks.Read(new(run.Id),Options())).State!=OperationState.Completed)await Task.Delay(5,timeout.Token);
            // Completion becomes visible just before the worker releases its registration lease.
            using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                while(true){var released=await api.Resources.Release(new(entry.Action),Options());if(released.Success)break;Check(released.Error?.Code==AbiErrors.CONFLICT);await Task.Delay(5,timeout.Token);}
            Check((await api.Actions.Describe(new(new("owner:action")),Options())).Error?.Code==AbiErrors.NOT_FOUND);
            int hostReleased=0;
            var caller=new HostInvocation(entry.Context!.Info.Identity,Guid.Empty,"Active",Options());
            var hostResource=host.Services.RegisterHostCleanup(caller,"nexa.resources.write",_=>{hostReleased++;return ValueTask.CompletedTask;});
            Check((await api.Resources.Release(new(hostResource with {DefinitionId=new("owner:forged")}),Options())).Error?.Code==AbiErrors.HANDLE_OWNER_MISMATCH);
            Ok(await api.Resources.Release(new(hostResource),Options()));Check(hostReleased==1);
            Check(!(await api.Resources.Release(new(hostResource),Options())).Success);
            host.Services.RegisterHostCleanup(caller,"nexa.resources.write",_=>{hostReleased++;return ValueTask.CompletedTask;});
            await host.StopAsync("owner");Check(hostReleased==2);Check(entry.Trace.SequenceEqual(["stop","b","a"]));
            var explicitEntry=new Entry(){ReleaseA=true};Check((await host.LoadAsync([explicitEntry.Source()])).Single().State==LoadState.Active);using var explicitApi=explicitEntry.Context!.AsAbi100();
            Ok(await explicitApi.Resources.Release(new(explicitEntry.CleanupA!),Options()));Check(explicitEntry.Trace.SequenceEqual(["a"]));
            await host.StopAsync("owner");Check(explicitEntry.Trace.SequenceEqual(["a","stop","b"]));
            var slow=new Entry(){ReleaseA=true,Slow=true};Check((await host.LoadAsync([slow.Source()])).Single().State==LoadState.Active);using var slowApi=slow.Context!.AsAbi100();
            using(var cancel=new CancellationTokenSource())
            {
                try
                {
                    var release=slowApi.Resources.Release(new(slow.CleanupA!),Options() with {Cancellation=cancel.Token}).AsTask();
                    await slow.SlowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));cancel.Cancel();
                    Check((await release.WaitAsync(TimeSpan.FromSeconds(2))).Error?.Code==AbiErrors.OUTCOME_UNKNOWN);
                }
                finally{slow.SlowRelease.Set();}
                await slow.SlowFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            await host.StopAsync("owner");Check(slow.Trace.SequenceEqual(["a","stop","b"]));slow.SlowRelease.Dispose();
            var failed=new Entry(){FailFinalize=true};Check((await host.LoadAsync([failed.Source()])).Single().State==LoadState.Failed);Check(failed.Trace.SequenceEqual(["abort","b","a"]));
        }
        finally{Directory.Delete(root,true);}
        await WasmCleanup();
        Console.WriteLine("PASS: Bootstrap/SDK shared cleanup, unique registration handles, owned release, live callback leases, reverse cleanup, release during cleanup and abort cleanup.");
    }
    private static async Task WasmCleanup()
    {
        string? path=Environment.GetEnvironmentVariable("NEXA_WASM_CALLBACK");if(string.IsNullOrEmpty(path))return;
        string root=Path.Combine(Path.GetTempPath(),"nexa-cleanup-wasm-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var manifest=new PluginManifest(1,"example.cleanup","1.0.0",1,new("", ""),new([],[],"nexa-wasm-1"),[100],[],[],[],["nexa.resources.write"],[]){RequiredCapabilities=[new("nexa.resources",1)]};
            await using var wasm=new WasmPluginEntry(manifest,new(await File.ReadAllBytesAsync(path)));
            var entry=new WasmCleanupEntry(wasm);await using var host=new PluginHostRuntime(root,_=>true,Abi100Schemas.Validate,options:new(){AutomaticSchedules=false});
            var source=new PluginLoadSource(manifest,manifest.RequestedPermissions,_=>ValueTask.FromResult<IPluginBootstrapV1>(entry),()=>ValueTask.CompletedTask);
            Check((await host.LoadAsync([source])).Single().State==LoadState.Active);
            await host.StopAsync(manifest.PluginId);Check(entry.Cleaned==1&&entry.Stopped);
        }
        finally{Directory.Delete(root,true);}
    }
    private sealed class WasmCleanupEntry(WasmPluginEntry wasm):IPluginBootstrapV1,IPluginCleanupInvocationV1
    {
        internal int Cleaned;internal bool Stopped;
        public ValueTask<AbiReply<PreInitResult>> OnPreInitialize(PreInitContext c)=>wasm.OnPreInitialize(c);
        public async ValueTask<AbiReply<Unit>> OnInitialize(ABIContext c,StageContext stage)
        {var result=await wasm.OnInitialize(c,stage);using var api=c.ForStage100(stage);Ok(await api.Resources.RegisterCleanup(new CleanupRequest(1),Options()));return result;}
        public ValueTask<AbiReply<Unit>> OnPostInitialize(ABIContext c,StageContext stage)=>wasm.OnPostInitialize(c,stage);
        public ValueTask<AbiReply<CheckupResult>> OnCheckup(ABIContext c,StageContext stage)=>wasm.OnCheckup(c,stage);
        public ValueTask<AbiReply<Unit>> OnFinalize(ABIContext c,StageContext stage)=>wasm.OnFinalize(c,stage);
        public ValueTask<AbiReply<Unit>> OnActivated(ABIContext c,InvocationContext invocation)=>wasm.OnActivated(c,invocation);
        public ValueTask<AbiReply<Unit>> OnAbort(AbortContext c)=>wasm.OnAbort(c);
        public ValueTask<AbiReply<Unit>> OnStop(StopContext c){Stopped=true;return wasm.OnStop(c);}
        public async ValueTask<AbiReply<AbiValue>> InvokeCleanup(ABIContext c,ulong handler,string phase,InvocationOptions options)
        {Check(Stopped&&phase=="Stop");var result=await wasm.InvokeCleanup(c,handler,phase,options);Check(result.Success&&result.Value is AbiValue.Null);Cleaned++;return result;}
    }
    private sealed class Entry:IPluginBootstrapV1
    {
        internal ABIContext? Context;internal RegistrationHandle? BootstrapRegistration;internal RegistrationRef? CleanupA,Definition,Action;
        internal bool ReleaseA,FailFinalize,Slow;internal List<string> Trace=[];
        internal TaskCompletionSource SlowStarted=new(TaskCreationOptions.RunContinuationsAsynchronously),SlowFinished=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim SlowRelease=new();internal TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously),Resume=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ulong _a,_b,_action;
        private static readonly ImmutableArray<string> Permissions=["nexa.resources.read","nexa.resources.write","nexa.definitions.read","nexa.definitions.write","nexa.actions.read","nexa.actions.write","nexa.tasks.read"];
        internal PluginLoadSource Source()=>new(new(1,"owner","1.0.0",1,new("test","test"),new([],[],"fixture"),[100],[],[],[],Permissions,[]),Permissions,_=>ValueTask.FromResult<IPluginBootstrapV1>(this),()=>ValueTask.CompletedTask);
        public async ValueTask<AbiReply<PreInitResult>> OnPreInitialize(PreInitContext c)
        {
            _a=c.Callbacks.Register("a",(_,_)=>{Trace.Add("a");if(Slow){SlowStarted.TrySetResult();SlowRelease.Wait();SlowFinished.TrySetResult();}return ValueTask.FromResult(AbiReply<AbiValue>.Ok(new AbiValue.Null()));}).Value;
            _b=c.Callbacks.Register("b",async(_,options)=>
            {
                Trace.Add("b");if(!ReleaseA){using var api=Context!.AsAbi100();Ok(await api.Resources.Release(new(CleanupA!),options with {IdempotencyKey="cleanup-a"}));}
                return AbiReply<AbiValue>.Ok(new AbiValue.Null());
            }).Value;
            _action=c.Callbacks.Register("action",async(_,options)=>{Entered.TrySetResult();await Resume.Task.WaitAsync(options.Cancellation);return AbiReply<AbiValue>.Ok(Empty.Write(new()));}).Value;
            BootstrapRegistration=Ok(await c.Resources.RegisterCleanup(new CallbackHandle(_a),Options()));
            var draft=new Abi100Draft(Ok(await c.Negotiator.GetABIContext(100,Options()))).Require("nexa.resources").Require("nexa.definitions").Require("nexa.actions").Require("nexa.tasks");
            Context=Ok(await draft.Make(c.Negotiator,Guid.NewGuid(),Permissions,default));
            using var pre=Context.AsAbi100();CleanupA=Ok(await pre.Resources.RegisterCleanup(new CleanupRequest(_a),Options()));
            return AbiReply<PreInitResult>.Ok(new(Context,[],[],[],[]));
        }
        public async ValueTask<AbiReply<Unit>> OnInitialize(ABIContext c,StageContext stage)
        {
            using var api=c.ForStage100(stage);Ok(await api.Resources.RegisterCleanup(new CleanupRequest(_b),Options()));
            Definition=Ok(await api.Definitions.Register(new(new("owner:definition"),new("nexa:test"),Abi100Schemas.Empty,Empty.Write(new()),[]),Options()));
            Action=Ok(await api.Actions.Register(new(new("owner:action"),Abi100Schemas.Empty,Abi100Schemas.Empty,[],[],IdempotencyMode.ReadOnly,true,false,ConfirmationRequirement.None,null,new("action",[]),_action),Options()));
            return AbiReply<Unit>.Ok(new());
        }
        public ValueTask<AbiReply<Unit>> OnPostInitialize(ABIContext c,StageContext stage)=>Done();
        public ValueTask<AbiReply<CheckupResult>> OnCheckup(ABIContext c,StageContext stage)=>ValueTask.FromResult(AbiReply<CheckupResult>.Ok(new(CheckupStatus.Passed,[],[])));
        public ValueTask<AbiReply<Unit>> OnFinalize(ABIContext c,StageContext stage)=>FailFinalize?ValueTask.FromResult(AbiReply<Unit>.Fail(new(AbiErrors.CONFLICT,"fail","Finalize","test","owner",stage.Identity.LoadInstanceId,Guid.Empty,false,ImmutableDictionary<string,string>.Empty))):Done();
        public ValueTask<AbiReply<Unit>> OnActivated(ABIContext c,InvocationContext invocation)=>Done();
        public ValueTask<AbiReply<Unit>> OnStop(StopContext c){Trace.Add("stop");return Done();}public ValueTask<AbiReply<Unit>> OnAbort(AbortContext c){Trace.Add("abort");return Done();}
        private static ValueTask<AbiReply<Unit>> Done()=>ValueTask.FromResult(AbiReply<Unit>.Ok(new()));
    }
}
