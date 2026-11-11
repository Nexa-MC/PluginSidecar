using System.Collections.Immutable;
using System.Threading.Channels;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;

internal static class DurableEventTests
{
    private static readonly ScopedTarget Scope=new(new("nexa:global"),null,"test");
    private static InvocationOptions Options()=>new(Guid.NewGuid(),TimeSpan.FromSeconds(5),default){IdempotencyKey=Guid.NewGuid().ToString("N")};
    private static T Ok<T>(AbiReply<T> reply)=>reply.Success?reply.Value:throw new InvalidOperationException(reply.Error!.Code+" "+reply.Error.Operation);
    private static void Check(bool value,[System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))]string? expression=null)
    {if(!value)throw new InvalidOperationException("Durable event assertion: "+expression);}
    internal static async Task Run()
    {
        await Recovery();await Reentry();await ParallelCheckpoint();await ActivationReplay();
        Console.WriteLine("PASS: durable event restart, callback acknowledgement/retry, cursor ownership, retention gaps, staged replay, bounded reentry and release cancellation.");
    }
    private static PluginHostRuntime Host(string path)=>new(path,_=>true,Abi100Schemas.Validate,options:new(){AutomaticSchedules=false});
    private static string Temp(){string path=Path.Combine(Path.GetTempPath(),"nexa-events-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);return path;}
    private static void Loaded(ImmutableArray<PluginLoadReport> reports)
    {foreach(var r in reports)if(r.State!=LoadState.Active)throw new InvalidOperationException(r.Error?.Code+" "+r.Error?.Operation+" "+r.Error?.Stage+" "+r.Error?.PluginId);}
    private static EventEnvelope Event(string value)=>new(Guid.NewGuid(),new("publisher:event"),Abi100Schemas.TextResult,null,new("v1"),DateTimeOffset.UtcNow,Guid.NewGuid(),null,"guest-origin",Scope,TextResult.Write(new(value)));
    private static async Task Until(Func<bool> condition)
    {using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8));while(!condition())await Task.Delay(5,timeout.Token);}
    private static async Task<string> Receive(Entry entry)=>await entry.Received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
    private static void Checkpoint(string root,string cursor,long sequence)=>Check(ReadCheckpoint(root,cursor)==sequence);
    private static long ReadCheckpoint(string root,string cursor)=>DataCodec.ReadInt64(DataCodec.Field(DataCodec.ReadMap(AbiValueBinary.Decode(File.ReadAllBytes(Path.Combine(root,"events",cursor+".cursor")))),"sequence"));
    private static async Task Recovery()
    {
        string root=Temp();string cursor;
        try
        {
            await using(var host=Host(root))
            {
                var publisher=new Entry("publisher",true);var listener=new Entry("listener"){Failures=1,SubscribeOnLoad=true};
                Loaded(await host.LoadAsync([publisher.Source()]));Loaded(await host.LoadAsync([listener.Source()]));cursor=listener.Subscription!.Cursor!;Check(cursor.Length==32);
                using var p=publisher.Context!.AsAbi100();
                var first=Event("one");Ok(await p.Events.Publish(first,Options()));Check(await Receive(listener)=="one");Check(listener.Calls==2);
                await Until(()=>ReadCheckpoint(root,cursor)==1);
                Ok(await p.Events.Publish(first,Options()));Check(listener.Calls==2);
                Check((await p.Events.Publish(first with {Payload=TextResult.Write(new("changed"))},Options())).Error?.Code==AbiErrors.REQUEST_ID_REUSED);
                await host.StopAsync("listener");Ok(await p.Events.Publish(Event("two"),Options()));Ok(await p.Events.Publish(Event("three"),Options()));
            }
            await using(var host=Host(root))
            {
                var publisher=new Entry("publisher",true);var listener=new Entry("listener"){Cursor=cursor,SubscribeOnLoad=true,CheckDuplicate=true};
                Loaded(await host.LoadAsync([publisher.Source()]));Loaded(await host.LoadAsync([listener.Source()]));Check(!listener.Subscription!.ResyncRequired);
                Check(await Receive(listener)=="two");Check(await Receive(listener)=="three");await Until(()=>ReadCheckpoint(root,cursor)==3);Check(!listener.BeforeActivation);
                using var api=listener.Context!.AsAbi100();
                Check(listener.DuplicateError==AbiErrors.CONFLICT&&listener.ScopeError==AbiErrors.HANDLE_OWNER_MISMATCH);
                Check((await api.Events.Subscribe(listener.Request(),Options())).Error?.Code==AbiErrors.STAGE_DENIED);
                var stranger=new Entry("stranger"){SubscribeOnLoad=true,AcceptFailure=true,Cursor=cursor};Loaded(await host.LoadAsync([stranger.Source()]));
                Check(stranger.SubscribeError==AbiErrors.HANDLE_OWNER_MISMATCH);
                await host.StopAsync("listener");using var p=publisher.Context!.AsAbi100();
                foreach(string value in new[]{"four","five","six"})Ok(await p.Events.Publish(Event(value),Options()));
            }
            await using(var host=Host(root))
            {
                var publisher=new Entry("publisher",true);var listener=new Entry("listener"){Cursor=cursor,SubscribeOnLoad=true};
                Loaded(await host.LoadAsync([publisher.Source()]));Loaded(await host.LoadAsync([listener.Source()]));Check(listener.Subscription!.ResyncRequired);
                Check(await Receive(listener)=="five");Check(await Receive(listener)=="six");await Until(()=>ReadCheckpoint(root,cursor)==6);
                using var api=listener.Context!.AsAbi100();var batch=Ok(await api.Resources.ReadSubscription(new(listener.Subscription,256),Options()));Check(batch.ResyncRequired);
                Ok(await api.Resources.ReleaseSubscription(listener.Subscription,Options()));Check(!File.Exists(Path.Combine(root,"events",cursor+".cursor")));
            }
        }
        finally{Directory.Delete(root,true);}
    }
    private static async Task Reentry()
    {
        foreach(var policy in Enum.GetValues<ReentryPolicy>())
        {
            string root=Temp();
            try
            {
                await using var host=Host(root);var publisher=new Entry("publisher",true){Durable=false,QueueLimit=16};var listener=new Entry("listener"){Hold=true,SubscribeOnLoad=true,SubscriptionLifetime=SubscriptionLifetime.Transient,Policy=policy,SubscriptionQueueLimit=policy==ReentryPolicy.AllowBounded?16:1};
                Loaded(await host.LoadAsync([publisher.Source()]));Loaded(await host.LoadAsync([listener.Source()]));using var api=listener.Context!.AsAbi100();using var p=publisher.Context!.AsAbi100();
                var subscription=listener.Subscription!;
                Ok(await p.Events.Publish(Event("0"),Options()));await Until(()=>listener.Active==1);
                int count=policy==ReentryPolicy.AllowBounded?6:3;
                for(int i=1;i<count;i++)Ok(await p.Events.Publish(Event(i.ToString()),Options()));
                if(policy==ReentryPolicy.AllowBounded)await Until(()=>listener.Active==4);
                else Check(Ok(await api.Resources.ReadSubscription(new(subscription,256),Options())).ResyncRequired);
                Check(listener.MaximumActive==(policy==ReentryPolicy.AllowBounded?4:1));
                Ok(await api.Resources.ReleaseSubscription(subscription,Options()));await Until(()=>listener.Active==0);
                int before=listener.Calls;Ok(await p.Events.Publish(Event("after-release"),Options()));await Task.Delay(300);Check(listener.Calls==before);
            }
            finally{Directory.Delete(root,true);}
        }
    }
    private static async Task ParallelCheckpoint()
    {
        string root=Temp();string cursor;
        try
        {
            await using(var host=Host(root))
            {
                var publisher=new Entry("publisher",true){QueueLimit=16};
                var listener=new Entry("listener"){SubscribeOnLoad=true,Hold=true,Policy=ReentryPolicy.AllowBounded};
                Loaded(await host.LoadAsync([publisher.Source()]));Loaded(await host.LoadAsync([listener.Source()]));cursor=listener.Subscription!.Cursor!;
                var savedAt=File.GetLastWriteTimeUtc(Path.Combine(root,"events",cursor+".cursor"));await Task.Delay(30);
                using var p=publisher.Context!.AsAbi100();
                Ok(await p.Events.Publish(Event("first"),Options()));Ok(await p.Events.Publish(Event("second"),Options()));
                await Until(()=>listener.Blocks.Count==2);
                listener.Blocks["second"].TrySetResult();Check(await Receive(listener)=="second");
                await Until(()=>listener.Active==1&&File.GetLastWriteTimeUtc(Path.Combine(root,"events",cursor+".cursor"))!=savedAt);Checkpoint(root,cursor,0);
                // Restart while the earlier delivery is still unacknowledged. The later
                // success must not cause that earlier event to disappear from replay.
            }
            await using(var host=Host(root))
            {
                var publisher=new Entry("publisher",true){QueueLimit=16};var listener=new Entry("listener"){SubscribeOnLoad=true,Cursor=cursor};
                Loaded(await host.LoadAsync([publisher.Source()]));Loaded(await host.LoadAsync([listener.Source()]));
                Check(await Receive(listener)=="first");Check(await Receive(listener)=="second");await Until(()=>ReadCheckpoint(root,cursor)==2);
            }
        }
        finally{Directory.Delete(root,true);}
    }
    private static async Task ActivationReplay()
    {
        string root=Temp();
        try
        {
            await using var host=Host(root);var publisher=new Entry("publisher",true);
            var listener=new Entry("listener"){SubscribeOnLoad=true,CheckupGate=new(TaskCreationOptions.RunContinuationsAsynchronously)};
            Loaded(await host.LoadAsync([publisher.Source()]));var loading=host.LoadAsync([listener.Source()]).AsTask();
            await listener.InCheckup.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var p=publisher.Context!.AsAbi100();Ok(await p.Events.Publish(Event("during-checkup"),Options()));Check(listener.Calls==0);
            listener.CheckupGate.TrySetResult();Loaded(await loading);Check(await Receive(listener)=="during-checkup"&&!listener.BeforeActivation);
        }
        finally{Directory.Delete(root,true);}
    }
    private sealed class Entry(string id,bool provider=false):IPluginBootstrapV1
    {
        internal ABIContext? Context;internal SubscriptionRef? Subscription;internal string? Cursor;
        internal bool SubscribeOnLoad,Durable=true,Hold,BeforeActivation,Activated,AcceptFailure,CheckDuplicate;internal long QueueLimit=2,SubscriptionQueueLimit=16;
        internal string? SubscribeError,DuplicateError,ScopeError;
        internal SubscriptionLifetime SubscriptionLifetime=SubscriptionLifetime.Resumable;
        internal ReentryPolicy Policy=ReentryPolicy.Queue;
        internal int Failures,Calls,Active,MaximumActive;
        internal TaskCompletionSource? CheckupGate;internal TaskCompletionSource InCheckup=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Channel<string> Received=Channel.CreateUnbounded<string>();
        internal System.Collections.Concurrent.ConcurrentDictionary<string,TaskCompletionSource> Blocks=[];private ulong _handler;
        private static readonly string[] Groups=["Events","Resources"];
        private ImmutableArray<string> Permissions=>Abi100Catalog.Operations.Where(o=>Groups.Contains(o.Group)).Select(o=>o.Permission).Where(p=>!string.IsNullOrEmpty(p)).Distinct().ToImmutableArray();
        internal PluginLoadSource Source()=>new(new(1,id,"1.0.0",1,new("test","test"),new([],[],"test"),[100],[],[],[],Permissions,[]),Permissions,_=>ValueTask.FromResult<IPluginBootstrapV1>(this),()=>ValueTask.CompletedTask);
        internal EventSubscribeRequest Request()=>new(new("publisher:event"),Abi100Schemas.TextResult,Cursor,_handler,false,true,Scope,SubscriptionLifetime,Policy,SubscriptionQueueLimit);
        public async ValueTask<AbiReply<PreInitResult>> OnPreInitialize(PreInitContext context)
        {
            _handler=context.Callbacks.Register("event",async(value,options)=>
            {
                Interlocked.Increment(ref Calls);int active=Interlocked.Increment(ref Active);MaximumActive=Math.Max(MaximumActive,active);BeforeActivation|=!Activated;
                try
                {
                    if(Hold)
                    {
                        var signal=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        Blocks[TextResult.Read(EventEnvelope.Read(value).Payload).Text]=signal;
                        await signal.Task.WaitAsync(options.Cancellation);
                    }
                    if(Interlocked.Decrement(ref Failures)>=0)return AbiReply<AbiValue>.Fail(new(AbiErrors.PROVIDER_UNAVAILABLE,"retry","Active","event",id,context.Identity.LoadInstanceId,options.CorrelationId,true,ImmutableDictionary<string,string>.Empty));
                    var envelope=EventEnvelope.Read(value);Check(envelope.Origin=="publisher");Received.Writer.TryWrite(TextResult.Read(envelope.Payload).Text);
                    return AbiReply<AbiValue>.Ok(Empty.Write(new()));
                }
                finally{Interlocked.Decrement(ref Active);}
            }).Value;
            var draft=new Abi100Draft(Ok(await context.Negotiator.GetABIContext(100,Options())));
            foreach(var contract in Abi100Catalog.Operations.Where(o=>Groups.Contains(o.Group)).Select(o=>o.ContractId).Distinct())draft=draft.Require(contract);
            Context=Ok(await draft.Make(context.Negotiator,Guid.NewGuid(),Permissions,default));return AbiReply<PreInitResult>.Ok(new(Context,[],[],[],[]));
        }
        public async ValueTask<AbiReply<Unit>> OnInitialize(ABIContext context,StageContext stage)
        {
            if(provider){using var api=context.ForStage100(stage);Ok(await api.Events.Define(new(new("publisher:event"),Abi100Schemas.TextResult,"scope",DeliveryGuarantee.AtLeastOnce,Durable,QueueLimit),Options()));}
            if(SubscribeOnLoad)
            {
                using var api=context.ForStage100(stage);var reply=await api.Events.Subscribe(Request(),Options());SubscribeError=reply.Error?.Code;
                if(!AcceptFailure)Subscription=Ok(reply);
                if(CheckDuplicate)
                {
                    DuplicateError=(await api.Events.Subscribe(Request(),Options())).Error?.Code;
                    ScopeError=(await api.Events.Subscribe(Request() with {Scope=Scope with {Scope="different"}},Options())).Error?.Code;
                }
            }
            return AbiReply<Unit>.Ok(new());
        }
        public ValueTask<AbiReply<Unit>> OnPostInitialize(ABIContext context,StageContext stage)=>Done();
        public async ValueTask<AbiReply<CheckupResult>> OnCheckup(ABIContext abi,StageContext stage)
        {InCheckup.TrySetResult();if(CheckupGate is not null)await CheckupGate.Task.WaitAsync(stage.Cancellation);return AbiReply<CheckupResult>.Ok(new(CheckupStatus.Passed,[],[]));}
        public ValueTask<AbiReply<Unit>> OnFinalize(ABIContext abi,StageContext stage)=>Done();
        public ValueTask<AbiReply<Unit>> OnActivated(ABIContext abi,InvocationContext context){Activated=true;return Done();}
        public ValueTask<AbiReply<Unit>> OnStop(StopContext context)=>Done();public ValueTask<AbiReply<Unit>> OnAbort(AbortContext context)=>Done();
        private static ValueTask<AbiReply<Unit>> Done()=>ValueTask.FromResult(AbiReply<Unit>.Ok(new()));
    }
}
