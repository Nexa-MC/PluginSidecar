using System.Collections.Concurrent;
using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;

internal static class ScheduleWorkflowTests
{
    private static readonly DateTimeOffset Anchor=new(2026,11,11,0,0,0,TimeSpan.Zero);
    private static readonly ScopedTarget Scope=new(new("nexa:global"),null,"test");
    private static InvocationOptions Options()=>new(Guid.NewGuid(),TimeSpan.FromSeconds(10),default){IdempotencyKey=Guid.NewGuid().ToString("N"),CorrelationId=Guid.NewGuid()};
    private static T Ok<T>(AbiReply<T> reply)=>reply.Success?reply.Value:throw new InvalidOperationException(reply.Error!.Code+" "+reply.Error.Operation);
    private static void Check(bool value,[System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))]string? expression=null)
    {if(!value)throw new InvalidOperationException("Schedule/workflow assertion: "+expression);}
    private static WorkflowNode Node(string id,WorkflowNodeKind kind,SchemaRef schema)=>new(id,kind,null,new AbiValue.Null(),[],1,1,0,ParallelCompletion.All,[],schema,schema);
    private static WorkflowDefinition Flow(string name,SchemaRef input,SchemaRef output,ImmutableArray<WorkflowNode> nodes,ConcurrencyPolicy concurrency=ConcurrencyPolicy.Parallel,bool persistent=false)
        =>new(new("clock:"+name),input,output,nodes,"v1",persistent,concurrency,16,128);
    internal static async Task Run()
    {
        await Concurrency();await Schedules();await Waits();await Automatic();await DelayRecovery();await MissingInteraction();await DurableWaitRecovery();
        Console.WriteLine("PASS: queued/replacement/reject concurrency, cancelled middle queue fences, timed/event schedules, missed policies, permissions, debounce, persisted state, waits and trusted user interaction.");
    }
    private static async Task Concurrency()
    {
        string root=Temp();
        try
        {
            await using var host=new PluginHostRuntime(root,_=>true,Abi100Schemas.Validate,options:new(){AutomaticSchedules=false});
            var entry=new Entry();OkLoad(await host.LoadAsync([entry.Source()]));using var api=entry.Context!.AsAbi100();
            foreach(var mode in new[]{ConcurrencyPolicy.Queue,ConcurrencyPolicy.ReplacePending,ConcurrencyPolicy.RejectDuplicate})
            {
                var first=Ok(await api.Workflows.Start(new(new("clock:"+mode.ToString().ToLowerInvariant()),TextResult.Write(new("first")),"v1"),Options()));
                var pending=await entry.Blocked.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                var second=await api.Workflows.Start(new(new("clock:"+mode.ToString().ToLowerInvariant()),TextResult.Write(new("second")),"v1"),Options());
                if(mode==ConcurrencyPolicy.RejectDuplicate)
                {Check(second.Error?.Code==AbiErrors.CONFLICT);pending.TrySetResult();await Terminal(api,first.Id,OperationState.Completed);continue;}
                var third=Ok(await api.Workflows.Start(new(new("clock:"+mode.ToString().ToLowerInvariant()),TextResult.Write(new("third")),"v1"),Options()));
                if(mode==ConcurrencyPolicy.Queue)Ok(await api.Workflows.Cancel(new(Ok(second).Id),Options()));
                await Terminal(api,Ok(second).Id,OperationState.Cancelled);
                Check(Ok(await api.Workflows.Read(new(third.Id),Options())).State==OperationState.Pending);
                Check(!entry.Blocked.Reader.TryRead(out _));
                pending.TrySetResult();await Terminal(api,first.Id,OperationState.Completed);
                var last=await entry.Blocked.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));last.TrySetResult();
                var done=await Terminal(api,third.Id,OperationState.Completed);Check(TextResult.Read(done.Result).Text=="third");
            }
        }
        finally{Directory.Delete(root,true);}
    }
    private static async Task Schedules()
    {
        string root=Temp();var clock=new Clock();
        try
        {
            await using(var host=new PluginHostRuntime(root,_=>true,Abi100Schemas.Validate,options:new(){Clock=clock,AutomaticSchedules=false}))
            {
                var provider=new Entry();var scheduler=new Entry("scheduler",false,true);
                OkLoad(await host.LoadAsync([provider.Source(),scheduler.Source()]));using var api=scheduler.Context!.AsAbi100();
                var states=Ok(await api.Schedules.List(new(null,256),Options()));Check(states.Items.Length==5);
                foreach(var state in states.Items)Ok(await api.Schedules.Disable(new(state.Id),Options()));
                foreach(var (name,expected) in new[]{("skip",0),("coalesce",1),("replay",3)})
                {
                    Ok(await api.Schedules.Enable(new(new("scheduler:"+name)),Options()));clock.Advance(TimeSpan.FromMilliseconds(6500));
                    int before=provider.ScheduledCalls;await host.Services.PollSchedulesAsync();
                    await Until(()=>provider.ScheduledCalls==before+expected);Check(provider.ScheduledCalls==before+expected);
                    Check(Ok(await api.Schedules.ReadNextRun(new(new("scheduler:"+name)),Options())).NextRun>clock.GetUtcNow());
                    Ok(await api.Schedules.Disable(new(new("scheduler:"+name)),Options()));
                }
                using var p=provider.Context!.AsAbi100();
                Ok(await api.Schedules.Enable(new(new("scheduler:event")),Options()));
                int eventBefore=provider.EventCalls;
                Ok(await p.Events.Publish(Event("one"),Options()));clock.Advance(TimeSpan.FromMilliseconds(100));
                Ok(await p.Events.Publish(Event("two"),Options()));clock.Advance(TimeSpan.FromMilliseconds(150));await host.Services.PollSchedulesAsync();Check(provider.EventCalls==eventBefore);
                clock.Advance(TimeSpan.FromMilliseconds(100));await host.Services.PollSchedulesAsync();await Until(()=>provider.EventCalls==eventBefore+1);Check(provider.LastEvent=="two");
                Ok(await api.Schedules.Disable(new(new("scheduler:event")),Options()));
                Ok(await api.Schedules.Enable(new(new("scheduler:at")),Options()));clock.Set(Anchor.AddMinutes(1));
                int beforeAt=provider.ScheduledCalls;await host.Services.PollSchedulesAsync();await Until(()=>provider.ScheduledCalls==beforeAt+1);
                await host.Services.PollSchedulesAsync();Check(provider.ScheduledCalls==beforeAt+1);
                Ok(await api.Schedules.Disable(new(new("scheduler:at")),Options()));
                Check((await api.Schedules.TriggerNow(new(new("scheduler:at")),Options())).Error?.Code==AbiErrors.CAPABILITY_DENIED);
                Ok(await api.Schedules.Enable(new(new("scheduler:coalesce")),Options()));
                int manual=provider.ScheduledCalls;Ok(await api.Schedules.TriggerNow(new(new("scheduler:coalesce")),Options()));await Until(()=>provider.ScheduledCalls==manual+1);
                var denied=new Entry("denied",false,true){AllowWorkflowExecution=false};OkLoad(await host.LoadAsync([denied.Source()]));using var d=denied.Context!.AsAbi100();
                Check((await d.Schedules.TriggerNow(new(new("denied:coalesce")),Options())).Error?.Code==AbiErrors.CAPABILITY_DENIED);
                await host.StopAsync("denied");
            }
            clock.Advance(TimeSpan.FromSeconds(10));
            await using(var host=new PluginHostRuntime(root,_=>true,Abi100Schemas.Validate,options:new(){Clock=clock,AutomaticSchedules=false}))
            {
                var provider=new Entry();var scheduler=new Entry("scheduler",false,true);
                OkLoad(await host.LoadAsync([provider.Source(),scheduler.Source()]));await Until(()=>provider.ScheduledCalls==1);
                using var api=scheduler.Context!.AsAbi100();Check(!Ok(await api.Schedules.ReadNextRun(new(new("scheduler:skip")),Options())).Enabled);
                await host.StopAsync("scheduler");int count=provider.ScheduledCalls;clock.Advance(TimeSpan.FromSeconds(10));await host.Services.PollSchedulesAsync();Check(provider.ScheduledCalls==count);
            }
        }
        finally{Directory.Delete(root,true);}
    }
    private static async Task Waits()
    {
        string root=Temp();var interaction=new Interaction();
        try
        {
            await using var host=new PluginHostRuntime(root,_=>true,Abi100Schemas.Validate,options:new(){AutomaticSchedules=false,Interaction=interaction});
            var entry=new Entry();OkLoad(await host.LoadAsync([entry.Source()]));using var api=entry.Context!.AsAbi100();
            var waiting=Ok(await api.Workflows.Start(new(new("clock:wait"),Empty.Write(new()),"v1"),Options()));
            await Stage(api,waiting.Id,"waiting:event");
            Ok(await api.Events.Publish(Event("wrong") with {Scope=Scope with {Scope="other"}},Options()));
            Check(Ok(await api.Workflows.Read(new(waiting.Id),Options())).State==OperationState.Running);
            Ok(await api.Events.Publish(Event("answer"),Options()));Check(TextResult.Read((await Terminal(api,waiting.Id,OperationState.Completed)).Result).Text=="answer");
            var cancelled=Ok(await api.Workflows.Start(new(new("clock:wait"),Empty.Write(new()),"v1"),Options()));await Stage(api,cancelled.Id,"waiting:event");Ok(await api.Workflows.Cancel(new(cancelled.Id),Options()));await Terminal(api,cancelled.Id,OperationState.Cancelled);
            var question=Ok(await api.Workflows.Start(new(new("clock:ask"),Empty.Write(new()),"v1"),Options()));
            var request=await interaction.Requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));Check(request.Owner==entry.Identity&&request.Run==question.Id);
            interaction.Answer.TrySetResult(AbiReply<AbiValue>.Ok(TextResult.Write(new("chosen"))));Check(TextResult.Read((await Terminal(api,question.Id,OperationState.Completed)).Result).Text=="chosen");
        }
        finally{Directory.Delete(root,true);}
    }
    private static async Task Automatic()
    {
        string root=Temp();
        try
        {
            await using var host=new PluginHostRuntime(root,_=>true,Abi100Schemas.Validate);
            var provider=new Entry();var scheduler=new Entry("scheduler",false,true);
            OkLoad(await host.LoadAsync([provider.Source(),scheduler.Source()]));
            using var api=scheduler.Context!.AsAbi100();
            foreach(var name in new[]{"skip","replay","at","event"})Ok(await api.Schedules.Disable(new(new("scheduler:"+name)),Options()));
            int before=provider.ScheduledCalls;await Until(()=>provider.ScheduledCalls>before);
            await host.StopAsync("scheduler");
            // All callbacks already admitted may finish; no later timer can admit more.
            await Task.Delay(100);int stopped=provider.ScheduledCalls;
            await Task.Delay(1100);Check(provider.ScheduledCalls==stopped);
        }
        finally{Directory.Delete(root,true);}
    }
    private static async Task DelayRecovery()
    {
        string root=Temp();var clock=new Clock();EntityId id;
        try
        {
            await using(var host=new PluginHostRuntime(root,_=>true,Abi100Schemas.Validate,options:new(){Clock=clock,AutomaticSchedules=false}))
            {
                var entry=new Entry();OkLoad(await host.LoadAsync([entry.Source()]));using var api=entry.Context!.AsAbi100();
                id=Ok(await api.Workflows.Start(new(new("clock:delay"),Empty.Write(new()),"v1"),Options())).Id;
                await Stage(api,id,"waiting:delay");Ok(await api.Workflows.Cancel(new(id),Options()));await Terminal(api,id,OperationState.Cancelled);
            }
            clock.Advance(TimeSpan.FromMinutes(6));
            await using(var host=new PluginHostRuntime(root,_=>true,Abi100Schemas.Validate,options:new(){Clock=clock,AutomaticSchedules=false}))
            {
                var entry=new Entry();OkLoad(await host.LoadAsync([entry.Source()]));using var api=entry.Context!.AsAbi100();
                var saved=Ok(await api.Workflows.Read(new(id),Options()));
                Ok(await api.Workflows.Resume(new(id,saved.Revision,"v1"),Options()));await Terminal(api,id,OperationState.Completed);
            }
        }
        finally{Directory.Delete(root,true);}
    }
    private static async Task MissingInteraction()
    {
        string root=Temp();
        try
        {
            await using var host=new PluginHostRuntime(root,_=>true,Abi100Schemas.Validate,options:new(){AutomaticSchedules=false});
            var entry=new Entry();OkLoad(await host.LoadAsync([entry.Source()]));using var api=entry.Context!.AsAbi100();
            var op=Ok(await api.Workflows.Start(new(new("clock:ask"),Empty.Write(new()),"v1"),Options()));
            Check((await Terminal(api,op.Id,OperationState.Failed)).Failure?.Code==AbiErrors.CAPABILITY_UNAVAILABLE);
        }
        finally{Directory.Delete(root,true);}
    }
    private static async Task DurableWaitRecovery()
    {
        string root=Temp();EntityId id;
        try
        {
            await using(var host=new PluginHostRuntime(root,_=>true,Abi100Schemas.Validate,options:new(){AutomaticSchedules=false}))
            {
                var entry=new Entry(){DurableEvents=true};OkLoad(await host.LoadAsync([entry.Source()]));using var api=entry.Context!.AsAbi100();
                id=Ok(await api.Workflows.Start(new(new("clock:wait"),Empty.Write(new()),"v1"),Options())).Id;await Stage(api,id,"waiting:event");
                Ok(await api.Workflows.Cancel(new(id),Options()));await Terminal(api,id,OperationState.Cancelled);
                Ok(await api.Events.Publish(Event("after-interruption"),Options()));
            }
            await using(var host=new PluginHostRuntime(root,_=>true,Abi100Schemas.Validate,options:new(){AutomaticSchedules=false}))
            {
                var entry=new Entry(){DurableEvents=true};OkLoad(await host.LoadAsync([entry.Source()]));using var api=entry.Context!.AsAbi100();
                var saved=Ok(await api.Workflows.Read(new(id),Options()));Ok(await api.Workflows.Resume(new(id,saved.Revision,"v1"),Options()));
                Check(TextResult.Read((await Terminal(api,id,OperationState.Completed)).Result).Text=="after-interruption");
            }
        }
        finally{Directory.Delete(root,true);}
    }
    private static EventEnvelope Event(string text)=>new(Guid.NewGuid(),new("clock:event"),Abi100Schemas.TextResult,null,new("v1"),DateTimeOffset.UtcNow,Guid.NewGuid(),null,"ignored",Scope,TextResult.Write(new(text)));
    private static async Task Stage(Abi100Context api,EntityId id,string stage)
    {using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));while(Ok(await api.Workflows.Read(new(id),Options())).Stage!=stage)await Task.Delay(5,timeout.Token);}
    private static async Task<OperationRecord> Terminal(Abi100Context api,EntityId id,OperationState state)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while(true){var result=Ok(await api.Workflows.Read(new(id),Options()));if(result.State==state)return result;if(result.State is OperationState.Failed or OperationState.OutcomeUnknown)throw new InvalidOperationException(result.Failure?.Code);await Task.Delay(5,timeout.Token);}
    }
    private static async Task Until(Func<bool> condition){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));while(!condition())await Task.Delay(5,timeout.Token);}
    private static string Temp(){string path=Path.Combine(Path.GetTempPath(),"nexa-schedule-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);return path;}
    private static void OkLoad(ImmutableArray<PluginLoadReport> reports){foreach(var report in reports)if(report.State!=LoadState.Active)throw new InvalidOperationException(report.Error?.Code+" "+report.Error?.Operation);}
    private sealed class Clock:TimeProvider
    {private DateTimeOffset _now=Anchor;public override DateTimeOffset GetUtcNow()=>_now;internal void Advance(TimeSpan amount)=>_now+=amount;internal void Set(DateTimeOffset value)=>_now=value;}
    private sealed class Interaction:IWorkflowInteraction
    {
        internal System.Threading.Channels.Channel<WorkflowInteractionRequest> Requests=System.Threading.Channels.Channel.CreateUnbounded<WorkflowInteractionRequest>();
        internal TaskCompletionSource<AbiReply<AbiValue>> Answer=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<AbiReply<AbiValue>> AskAsync(WorkflowInteractionRequest request,CancellationToken cancellation){Requests.Writer.TryWrite(request);return new(Answer.Task.WaitAsync(cancellation));}
    }
    private sealed class Entry(string id="clock",bool provider=true,bool schedules=false):IPluginBootstrapV1
    {
        internal ABIContext? Context;internal PluginIdentity? Identity;internal bool AllowWorkflowExecution=true,DurableEvents;
        internal int ScheduledCalls,EventCalls;internal string? LastEvent;
        internal System.Threading.Channels.Channel<TaskCompletionSource> Blocked=System.Threading.Channels.Channel.CreateUnbounded<TaskCompletionSource>();
        private ulong _block,_scheduled,_event;
        private static readonly string[] Groups=["Actions","Workflows","Events","Schedules","Tasks"];
        private ImmutableArray<string> Permissions=>Abi100Catalog.Operations.Where(o=>Groups.Contains(o.Group)).Select(o=>o.Permission).Append("nexa.ui.write").Where(p=>AllowWorkflowExecution||p!="nexa.workflows.write").Distinct().ToImmutableArray();
        internal PluginLoadSource Source()=>new(new(1,id,"1.0.0",1,new("test","test"),new([],[],"test"),[100],[],[],[],Permissions,[]),Permissions,_=>ValueTask.FromResult<IPluginBootstrapV1>(this),()=>ValueTask.CompletedTask);
        public async ValueTask<AbiReply<PreInitResult>> OnPreInitialize(PreInitContext context)
        {
            Identity=context.Identity;
            _block=context.Callbacks.Register("block",async(value,options)=>{var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);Blocked.Writer.TryWrite(release);await release.Task.WaitAsync(options.Cancellation);return AbiReply<AbiValue>.Ok(ActionRequest.Read(value).Inputs);}).Value;
            _scheduled=context.Callbacks.Register("scheduled",(value,options)=>{Interlocked.Increment(ref ScheduledCalls);return ValueTask.FromResult(AbiReply<AbiValue>.Ok(Empty.Write(new())));}).Value;
            _event=context.Callbacks.Register("event",(value,options)=>{LastEvent=TextResult.Read(ActionRequest.Read(value).Inputs).Text;Interlocked.Increment(ref EventCalls);return ValueTask.FromResult(AbiReply<AbiValue>.Ok(ActionRequest.Read(value).Inputs));}).Value;
            var draft=new Abi100Draft(Ok(await context.Negotiator.GetABIContext(100,Options())));
            foreach(var contract in Abi100Catalog.Operations.Where(o=>Groups.Contains(o.Group)).Select(o=>o.ContractId).Distinct())draft=draft.Require(contract);
            Context=Ok(await draft.Make(context.Negotiator,Guid.NewGuid(),Permissions,default));return AbiReply<PreInitResult>.Ok(new(Context,[],[],[],[]));
        }
        public async ValueTask<AbiReply<Unit>> OnInitialize(ABIContext context,StageContext stage)
        {
            using var api=context.ForStage100(stage);
            if(provider)
            {
                foreach(var (name,schema,handler) in new[]{("block",Abi100Schemas.TextResult,_block),("scheduled",Abi100Schemas.Empty,_scheduled),("eventaction",Abi100Schemas.TextResult,_event)})
                    Ok(await api.Actions.Register(new(new("clock:"+name),schema,schema,[],[],IdempotencyMode.ReadOnly,true,false,ConfirmationRequirement.None,null,new(name,[]),handler),Options()));
                foreach(var mode in Enum.GetValues<ConcurrencyPolicy>())
                    Ok(await api.Workflows.Register(Flow(mode.ToString().ToLowerInvariant(),Abi100Schemas.TextResult,Abi100Schemas.TextResult,[Node("block",WorkflowNodeKind.Action,Abi100Schemas.TextResult) with {Target=new("clock:block")}],mode),Options()));
                Ok(await api.Workflows.Register(Flow("scheduled",Abi100Schemas.Empty,Abi100Schemas.Empty,[Node("call",WorkflowNodeKind.Action,Abi100Schemas.Empty) with {Target=new("clock:scheduled")}]),Options()));
                Ok(await api.Workflows.Register(Flow("eventflow",Abi100Schemas.TextResult,Abi100Schemas.TextResult,[Node("call",WorkflowNodeKind.Action,Abi100Schemas.TextResult) with {Target=new("clock:eventaction")}]),Options()));
                Ok(await api.Events.Define(new(new("clock:event"),Abi100Schemas.TextResult,"scope",DeliveryGuarantee.BestEffortAtMostOnce,DurableEvents,16),Options()));
                Ok(await api.Workflows.Register(Flow("wait",Abi100Schemas.Empty,Abi100Schemas.TextResult,[Node("wait",WorkflowNodeKind.WaitEvent,Abi100Schemas.ScopedTarget) with {Target=new("clock:event"),Inputs=ScopedTarget.Write(Scope),OutputSchema=Abi100Schemas.TextResult}],persistent:true),Options()));
                Ok(await api.Workflows.Register(Flow("delay",Abi100Schemas.Empty,Abi100Schemas.Empty,[Node("delay",WorkflowNodeKind.Delay,Abi100Schemas.Empty) with {BackoffMilliseconds=300000}],persistent:true),Options()));
                Ok(await api.Workflows.Register(Flow("ask",Abi100Schemas.Empty,Abi100Schemas.TextResult,[Node("question",WorkflowNodeKind.AskUser,Abi100Schemas.UIDialog) with {Inputs=UIDialog.Write(new(new("clock:question"),new("clock:page"),Empty.Write(new()))),OutputSchema=Abi100Schemas.TextResult}],persistent:true),Options()));
            }
            if(schedules)
            {
                foreach(var (name,missed) in new[]{("skip",MissedSchedulePolicy.Skip),("coalesce",MissedSchedulePolicy.Coalesce),("replay",MissedSchedulePolicy.ReplayBounded)})
                    Ok(await api.Schedules.Register(new(new(id+":"+name),new("clock:scheduled"),ScheduleTrigger.Interval,"UTC",null,1000,null,missed,3,0),Options()));
                Ok(await api.Schedules.Register(new(new(id+":at"),new("clock:scheduled"),ScheduleTrigger.AtTime,"UTC",Anchor.AddMinutes(1),null,null,MissedSchedulePolicy.Coalesce,1,0),Options()));
                Ok(await api.Schedules.Register(new(new(id+":event"),new("clock:eventflow"),ScheduleTrigger.Event,"UTC",null,null,new("clock:event"),MissedSchedulePolicy.Coalesce,1,200),Options()));
            }
            return AbiReply<Unit>.Ok(new());
        }
        public ValueTask<AbiReply<Unit>> OnPostInitialize(ABIContext abi,StageContext stage)=>Done();
        public ValueTask<AbiReply<CheckupResult>> OnCheckup(ABIContext abi,StageContext stage)=>ValueTask.FromResult(AbiReply<CheckupResult>.Ok(new(CheckupStatus.Passed,[],[])));
        public ValueTask<AbiReply<Unit>> OnFinalize(ABIContext abi,StageContext stage)=>Done();
        public ValueTask<AbiReply<Unit>> OnActivated(ABIContext abi,InvocationContext context)=>Done();
        public ValueTask<AbiReply<Unit>> OnStop(StopContext context)=>Done();public ValueTask<AbiReply<Unit>> OnAbort(AbortContext context)=>Done();
        private static ValueTask<AbiReply<Unit>> Done()=>ValueTask.FromResult(AbiReply<Unit>.Ok(new()));
    }
}
