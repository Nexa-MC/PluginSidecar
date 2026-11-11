using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;
internal static class RuntimeServicesTests
{
    private static readonly ScopedTarget Scope = new(new("nexa:global"),null,"test");
    private static readonly PublicContract Contract = new(new("provider:echo"),1,Abi100Schemas.TextResult,
        [new(1,"echo",Abi100Schemas.TextResult,Abi100Schemas.TextResult,true,IdempotencyMode.ReadOnly,false)]);
    private static WorkflowNode Node(string id,WorkflowNodeKind kind)=>new(id,kind,null,new AbiValue.Null(),[],1,1,0,ParallelCompletion.All,[],Abi100Schemas.TextResult,Abi100Schemas.TextResult);
    private static InvocationOptions Options()=>new(Guid.NewGuid(),TimeSpan.FromSeconds(5),default){IdempotencyKey=Guid.NewGuid().ToString("N"),CorrelationId=Guid.NewGuid()};
    private static void Check(bool value,[System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? text=null)
    {if(!value)throw new InvalidOperationException("Runtime services assertion: "+text);}
    private static T Success<T>(AbiReply<T> reply)=>reply.Success?reply.Value:throw new InvalidOperationException(reply.Error!.Code+" "+reply.Error.Operation);
    internal static async Task Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"nexa-runtime-"+Guid.NewGuid().ToString("N"));
        try
        {
            await using var runtime=new PluginHostRuntime(root,_=>true,Abi100Schemas.Validate);
            var received=new TaskCompletionSource<EventEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
            var provider=new Entry("provider",true,received);var consumer=new Entry("consumer",false,received);
            var reports=await runtime.LoadAsync([provider.Source(),consumer.Source()]);
            Check(reports.All(r=>r.State==LoadState.Active));
            using var p=provider.Context!.AsAbi100();using var c=consumer.Context!.AsAbi100();
            var hostCaller = new HostInvocation(provider.Identity!, Guid.NewGuid(), "Active", Options());
            var hostWatch = runtime.Services.CreateHostSubscription(hostCaller, "nexa.events/100", "nexa.events.read", TextResult.Write(new("initial")), 1);
            Check(runtime.Services.PublishHostSubscription(provider.Identity!, hostWatch, TextResult.Write(new("overflow"))));
            var hostBatch = runtime.Services.ReadSubscription(provider.Identity!, hostWatch);
            Check(hostBatch.ResyncRequired && TextResult.Read(hostBatch.Items.Single()).Text == "initial");
            try { runtime.Services.PublishHostSubscription(consumer.Identity!, hostWatch, Empty.Write(new())); throw new InvalidOperationException("Foreign Host subscription published."); }
            catch (AbiHostException e) when (e.Code == AbiErrors.HANDLE_OWNER_MISMATCH) { }
            runtime.Services.ReleaseSubscription(provider.Identity!, hostWatch);
            Check(!runtime.Services.PublishHostSubscription(provider.Identity!, hostWatch, Empty.Write(new())));
            var hostOperation = runtime.Services.StartHostOperation(hostCaller, "nexa.events.write", Abi100Schemas.TextResult,
                _ => ValueTask.FromResult(AbiReply<AbiValue>.Ok(TextResult.Write(new("host-complete")))));
            Check(TextResult.Read((await Completed(p, hostOperation.Id)).Result).Text == "host-complete");

            var availability=Success(await c.Plugins.WatchAvailability(new("provider",new("provider:service"),1,1,false),Options()));
            var contractWatch=Success(await c.Contracts.WatchAvailability(new(new("provider:echo")),Options()));
            foreach(var subscription in new[]{availability,contractWatch})
                Check(CapabilityDescription.Read(Success(await c.Resources.ReadSubscription(new(subscription,256),Options())).Items.Single()).Availability==CapabilityAvailability.Supported);
            var absent=Success(await c.Contracts.WatchAvailability(new(new("absent:contract")),Options()));
            Check(CapabilityDescription.Read(Success(await c.Resources.ReadSubscription(new(absent,256),Options())).Items.Single()).Availability==CapabilityAvailability.Unavailable);
            var initial=Success(await p.State.Read(new(new("provider:value"),Scope),Options()));
            Check(initial.Revision.Value=="uninitialized");
            var watch=Success(await c.State.Watch(new(new("provider:value"),Scope),Options()));
            var changed=Success(await p.State.UpdateOwned(new(new("provider:value"),Scope,TextResult.Write(new("one")),initial.Revision),Options()));
            Check((await p.State.CompareExchangeOwned(new(new("provider:value"),Scope,TextResult.Write(new("two")),initial.Revision),Options())).Error?.Code==AbiErrors.STALE_REVISION);
            Check((await c.State.UpdateOwned(new(new("provider:value"),Scope,TextResult.Write(new("other")),changed.Revision),Options())).Error?.Code==AbiErrors.HANDLE_OWNER_MISMATCH);
            var notifications=Success(await c.Resources.ReadSubscription(new(watch,256),Options()));
            Check(notifications.Items.Length==1&&StateSnapshot.Read(notifications.Items[0]).Revision==changed.Revision);
            try {runtime.Services.ReadSubscription(provider.Identity!,watch);throw new InvalidOperationException("Leaked watch.");}catch(AbiHostException e)when(e.Code==AbiErrors.HANDLE_OWNER_MISMATCH){}
            var echo=Success(await c.Plugins.CallService(new(consumer.Binding!,1,TextResult.Write(new("hello")),[],null),Options()));
            var result=await Completed(c,echo.Id);Check(TextResult.Read(result.Result).Text=="hello");
            Check((await p.Tasks.Read(new(echo.Id),Options())).Error?.Code==AbiErrors.HANDLE_OWNER_MISMATCH);
            Check((await p.Plugins.CallService(new(consumer.Binding!,1,TextResult.Write(new("stolen")),[],null),Options())).Error?.Code==AbiErrors.HANDLE_OWNER_MISMATCH);
            var flow=Success(await c.Workflows.Start(new(new("provider:flow"),TextResult.Write(new("workflow")),"v1"),Options()));
            var flowResult=await Completed(c,flow.Id);Check(TextResult.Read(flowResult.Result).Text=="workflow");
            var trace=Success(await c.Workflows.ReadRunTrace(new(flow.Id),Options()));Check(trace.Steps.All(step=>step.State==OperationState.Completed)&&trace.Steps.All(step=>step.Id!="unreachable"));
            Check((await c.Workflows.Start(new(new("provider:flow"),TextResult.Write(new("stale")),"v0"),Options())).Error?.Code==AbiErrors.PLAN_STALE);
            var delay=Success(await c.Workflows.Start(new(new("provider:delay"),TextResult.Write(new("wait")),"v1"),Options()));
            Success(await c.Workflows.Cancel(new(delay.Id),Options()));
            using(var cancelled=new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                while(Success(await c.Workflows.Read(new(delay.Id),Options())).State!=OperationState.Cancelled)await Task.Delay(5,cancelled.Token);
            int before=provider.ActionCalls;
            var once=new ActionRequest(new("provider:once"),Scope,TextResult.Write(new("once")),null,"same-effect");
            var once1=Success(await c.Actions.Invoke(once,Options()));await Completed(c,once1.Id);
            var once2=Success(await c.Actions.Invoke(once,Options()));await Completed(c,once2.Id);
            Check(provider.ActionCalls==before+1);
            var changedKey=Success(await c.Actions.Invoke(once with {Inputs=TextResult.Write(new("different"))},Options()));
            using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {OperationRecord invalid;do{invalid=Success(await c.Tasks.Read(new(changedKey.Id),Options()));if(invalid.Failure is not null)break;await Task.Delay(5,timeout.Token);}while(true);Check(invalid.Failure.Code==AbiErrors.REQUEST_ID_REUSED);}
            var ev=new EventEnvelope(Guid.NewGuid(),new("provider:changed"),Abi100Schemas.TextResult,null,changed.Revision,DateTimeOffset.UtcNow,Guid.NewGuid(),null,"spoofed",Scope,TextResult.Write(new("event")));
            Success(await p.Events.Publish(ev,Options()));
            var actual=await received.Task.WaitAsync(TimeSpan.FromSeconds(5));Check(actual.Origin=="provider"&&actual.EventId==ev.EventId);
            var task=Success(await c.Tasks.CreateOwned(new(new("consumer:task"),new("task",[]),new("parent"),true),Options()));
            var record=Success(await c.Tasks.Read(new(task.Id),Options()));
            Success(await c.Tasks.Complete(new(task.Id,TextResult.Write(new("done")),Abi100Schemas.TextResult,record.Revision),Options()));
            Check((await c.Tasks.Report(new(task.Id,"late",0.5,record.Revision),Options())).Error?.Code==AbiErrors.STALE_REVISION);
            await runtime.StopAsync("provider");
            foreach(var subscription in new[]{availability,contractWatch})
                Check(CapabilityDescription.Read(Success(await c.Resources.ReadSubscription(new(subscription,256),Options())).Items.Single()).Availability==CapabilityAvailability.Unavailable);
            Check((await c.Plugins.CallService(new(consumer.Binding!,1,TextResult.Write(new("gone")),[],null),Options())).Error?.Code==AbiErrors.PROVIDER_GONE);
            Check((await c.State.Read(new(new("provider:value"),Scope),Options())).Error?.Code==AbiErrors.NOT_FOUND);
            await runtime.StopAsync("consumer");
            try{runtime.Services.ReadSubscription(consumer.Identity!,watch);throw new InvalidOperationException("Watch survived stop.");}catch(AbiHostException e)when(e.Code==AbiErrors.HANDLE_EXPIRED){}
            var bad=new Entry("provider",true,received){FailFinalize=true};
            Check((await runtime.LoadAsync([bad.Source()])).Single().State==LoadState.Failed);
            var replacement=new Entry("provider",true,received);
            Check((await runtime.LoadAsync([replacement.Source()])).Single().State==LoadState.Active);
            var requiredConsumer=new Entry("consumer",false,received){RequiredService=true};
            Check((await runtime.LoadAsync([requiredConsumer.Source()])).Single().State==LoadState.Active);
            await runtime.StopAsync("provider");Check(runtime.Active.IsEmpty);
        }
        finally {if(Directory.Exists(root))Directory.Delete(root,true);}
        await Recovery();
        Console.WriteLine("PASS: real service callback execution, typed results, state CAS/watches, event delivery, operation ownership, atomic rollback, required-consumer retirement, actions and workflow execution.");
    }
    private static async Task Recovery()
    {
        string root=Path.Combine(Path.GetTempPath(),"nexa-workflow-recovery-"+Guid.NewGuid().ToString("N"));
        var events=new TaskCompletionSource<EventEnvelope>();EntityId runId;EntityId uncertain;
        try
        {
            await using(var first=new PluginHostRuntime(root,_=>true,Abi100Schemas.Validate))
            {
                var entry=new Entry("provider",true,events);Check((await first.LoadAsync([entry.Source()])).Single().State==LoadState.Active);
                using var api=entry.Context!.AsAbi100();runId=Success(await api.Workflows.Start(new(new("provider:persist"),TextResult.Write(new("restart")),"v1"),Options())).Id;
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while(!Success(await api.Workflows.ReadRunTrace(new(runId),Options())).Steps.Any(s=>s.Id=="checkpoint"&&s.State==OperationState.Completed))await Task.Delay(5,timeout.Token);
            }
            await using(var second=new PluginHostRuntime(root,_=>true,Abi100Schemas.Validate))
            {
                var entry=new Entry("provider",true,events){ActionEntered=new(TaskCreationOptions.RunContinuationsAsynchronously)};
                Check((await second.LoadAsync([entry.Source()])).Single().State==LoadState.Active);using var api=entry.Context!.AsAbi100();
                var interrupted=Success(await api.Workflows.Read(new(runId),Options()));Check(interrupted.State==OperationState.Failed);
                Success(await api.Workflows.Resume(new(runId,interrupted.Revision,"v1"),Options()));var done=await Completed(api,runId);
                Check(TextResult.Read(done.Result).Text=="restart"&&entry.ActionCalls==1);
                uncertain=Success(await api.Workflows.Start(new(new("provider:persist"),TextResult.Write(new("block")),"v1"),Options())).Id;
                await entry.ActionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            await using(var third=new PluginHostRuntime(root,_=>true,Abi100Schemas.Validate))
            {
                var entry=new Entry("provider",true,events);Check((await third.LoadAsync([entry.Source()])).Single().State==LoadState.Active);using var api=entry.Context!.AsAbi100();
                Check(Success(await api.Workflows.Read(new(runId),Options())).State==OperationState.Completed);
                var unknown=Success(await api.Workflows.Read(new(uncertain),Options()));Check(unknown.State==OperationState.OutcomeUnknown);
                Check((await api.Workflows.Resume(new(uncertain,unknown.Revision,"v1"),Options())).Error?.Code==AbiErrors.OUTCOME_UNKNOWN);
                Check(entry.ActionCalls==0);
            }
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
        Console.WriteLine("PASS: atomic durable workflow checkpoints, explicit restart recovery and refusal to replay an uncertain effect.");
    }
    private static async Task<OperationRecord> Completed(Abi100Context api,EntityId id)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while(true){var record=Success(await api.Tasks.Read(new(id),Options()));if(record.State==OperationState.Completed)return record;if(record.Failure is not null)throw new InvalidOperationException(record.Failure.Code);await Task.Delay(5,timeout.Token);}
    }
    private sealed class Entry(string id,bool provider,TaskCompletionSource<EventEnvelope> received):IPluginBootstrapV1
    {
        internal ABIContext? Context;internal PluginIdentity? Identity;internal ServiceBinding? Binding;internal bool FailFinalize,RequiredService;private ulong _handler,_action;internal int ActionCalls;internal TaskCompletionSource? ActionEntered;
        private static readonly string[] Groups=["Contracts","State","Events","Plugins","Tasks","Diagnostics","Resources","Actions","Predicates","Workflows"];
        private static readonly ImmutableArray<string> Permissions=Abi100Catalog.Operations.Where(o=>Groups.Contains(o.Group)).Select(o=>o.Permission).Where(p=>!string.IsNullOrEmpty(p)).Distinct().ToImmutableArray();
        internal PluginLoadSource Source()=>new(new(1,id,"1.0.0",1,new("test","test"),new([],[],"fixture"),[100],[],[],[],Permissions,[]),Permissions,_=>ValueTask.FromResult<IPluginBootstrapV1>(this),()=>ValueTask.CompletedTask);
        public async ValueTask<AbiReply<PreInitResult>> OnPreInitialize(PreInitContext context)
        {
            Identity=context.Identity;_action=context.Callbacks.Register("action",async (value,options)=>{Interlocked.Increment(ref ActionCalls);var input=ActionRequest.Read(value).Inputs;if(TextResult.Read(input).Text=="block"){ActionEntered?.TrySetResult();await Task.Delay(Timeout.Infinite,options.Cancellation);}return AbiReply<AbiValue>.Ok(input);}).Value;_handler=context.Callbacks.Register("test",(value,options)=>
            {if(provider){var call=ServiceCall.Read(value);return ValueTask.FromResult(AbiReply<AbiValue>.Ok(call.Input));}received.TrySetResult(EventEnvelope.Read(value));return ValueTask.FromResult(AbiReply<AbiValue>.Ok(Empty.Write(new())));}).Value;
            var draft=new Abi100Draft(Success(await context.Negotiator.GetABIContext(100,Options())));
            foreach(var contract in Abi100Catalog.Operations.Where(o=>Groups.Contains(o.Group)).Select(o=>o.ContractId).Distinct())draft=draft.Require(contract);
            Context=Success(await draft.Make(context.Negotiator,Guid.NewGuid(),Permissions,default));return AbiReply<PreInitResult>.Ok(new(Context,[],[],[],[]));
        }
        public async ValueTask<AbiReply<Unit>> OnInitialize(ABIContext context,StageContext stage)
        {
            using var api=context.ForStage100(stage);
            if(provider)
            {
                Success(await api.Contracts.DefineService(Contract,Options()));
                Success(await api.Actions.Register(new(new("provider:action"),Abi100Schemas.TextResult,Abi100Schemas.TextResult,[],[],IdempotencyMode.ReadOnly,true,false,ConfirmationRequirement.None,null,new("echo",[]),_action),Options()));
                Success(await api.Predicates.Register(new(new("provider:yes"),Abi100Schemas.TextResult,null,new AbiValue.Bool(true)),Options()));
                Success(await api.Actions.Register(new(new("provider:once"),Abi100Schemas.TextResult,Abi100Schemas.TextResult,[],[],IdempotencyMode.RequiredKey,true,false,ConfirmationRequirement.None,null,new("once",[]),_action),Options()));
                Success(await api.Workflows.Register(new(new("provider:persist"),Abi100Schemas.TextResult,Abi100Schemas.TextResult,[Node("checkpoint",WorkflowNodeKind.Checkpoint),Node("pause",WorkflowNodeKind.Delay) with {BackoffMilliseconds=2000},Node("effect",WorkflowNodeKind.Action) with {Target=new("provider:once")}],"v1",true,ConcurrencyPolicy.Parallel,16,128),Options()));
                var action=Node("action",WorkflowNodeKind.Action) with {Target=new("provider:action")};
                var condition=Node("if",WorkflowNodeKind.If) with {Target=new("provider:yes"),Children=[action]};
                Success(await api.Workflows.Register(new(new("provider:flow"),Abi100Schemas.TextResult,Abi100Schemas.TextResult,[condition,Node("return",WorkflowNodeKind.Return),Node("unreachable",WorkflowNodeKind.Fail)],"v1",false,ConcurrencyPolicy.Parallel,16,128),Options()));
                Success(await api.Workflows.Register(new(new("provider:delay"),Abi100Schemas.TextResult,Abi100Schemas.TextResult,[Node("delay",WorkflowNodeKind.Delay) with {BackoffMilliseconds=300000}],"v1",false,ConcurrencyPolicy.Parallel,16,128),Options()));
                Success(await api.Plugins.ExportService(new(new("provider:service"),Contract,_handler,null),Options()));
                Success(await api.State.DefineOwned(new(new("provider:value"),Abi100Schemas.TextResult,Scope),Options()));
                Success(await api.Events.Define(new(new("provider:changed"),Abi100Schemas.TextResult,"entity",DeliveryGuarantee.BestEffortAtMostOnce,false,16),Options()));
            }
            else Success(await api.Events.Subscribe(new EventSubscribeRequest(new("provider:changed"),Abi100Schemas.TextResult,null,_handler,false,true,Scope,SubscriptionLifetime.Transient,ReentryPolicy.Queue,16),Options()));
            return AbiReply<Unit>.Ok(new());
        }
        public async ValueTask<AbiReply<Unit>> OnPostInitialize(ABIContext context,StageContext stage)
        {if(!provider){using var api=context.ForStage100(stage);Binding=Success(await api.Plugins.BindService(new("provider",new("provider:service"),1,1,RequiredService),Options()));}return AbiReply<Unit>.Ok(new());}
        public ValueTask<AbiReply<CheckupResult>> OnCheckup(ABIContext context,StageContext stage)=>ValueTask.FromResult(AbiReply<CheckupResult>.Ok(new(CheckupStatus.Passed,[],[])));
        public ValueTask<AbiReply<Unit>> OnFinalize(ABIContext context,StageContext stage)=>FailFinalize?ValueTask.FromResult(AbiReply<Unit>.Fail(new(AbiErrors.CHECKUP_FAILED,"failed","Finalize","test",id,Identity!.LoadInstanceId,Guid.NewGuid(),false,ImmutableDictionary<string,string>.Empty))):Ok();
        public ValueTask<AbiReply<Unit>> OnActivated(ABIContext context,InvocationContext invocation)=>Ok();
        public ValueTask<AbiReply<Unit>> OnStop(StopContext context)=>Ok();public ValueTask<AbiReply<Unit>> OnAbort(AbortContext context)=>Ok();
        private static ValueTask<AbiReply<Unit>> Ok()=>ValueTask.FromResult(AbiReply<Unit>.Ok(new()));
    }
}
