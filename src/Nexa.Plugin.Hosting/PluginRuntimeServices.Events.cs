using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;
namespace Nexa.Plugin.Hosting;

public sealed record HostSubscriptionBatch(ImmutableArray<AbiValue> Items,bool ResyncRequired);
public sealed partial class PluginRuntimeServices
{
    private sealed class Subscription(PluginIdentity owner,string kind,string key,Guid group,int limit,EventSubscribeRequest? handler)
    {
        internal PluginIdentity Owner=owner;internal string Kind=kind,Key=key;internal Guid Group=group;
        internal int Limit=limit;internal EventSubscribeRequest? Handler=handler;
        internal Queue<AbiValue> Queue=[]; internal HashSet<Guid> Seen=[];internal bool Gap,Closed,Active;
        internal int Workers;internal bool Retry;
        internal EventCheckpoint? Checkpoint;
        internal ServiceLookup? ServiceLookup;
        internal CapabilityDescription? LastAvailability;
        internal Dictionary<Guid,long> Pending=[];
        internal CancellationTokenSource Cancellation=new();
    }
    private readonly Dictionary<Guid,Subscription> _subscriptions=[];
    private long _queuedBytes;
    private int _eventWorkers;
    private bool Enqueue(Subscription sub,AbiValue value)
    { int size=AbiValueBinary.Encode(value).Length;try{DataCodec.Validate(SubscriptionBatch.Write(new([value],false)));}catch(FormatException){sub.Gap=true;return false;}if(size>DataCodec.MaximumBytes-4096||sub.Queue.Count>=sub.Limit||_queuedBytes+size>16*1024*1024){sub.Gap=true;return false;}sub.Queue.Enqueue(value);_queuedBytes+=size;return true; }
    private void Consumed(AbiValue value) => _queuedBytes-=AbiValueBinary.Encode(value).Length;
    private void Clear(Subscription sub) { while(sub.Queue.TryDequeue(out var value))Consumed(value); }
    private SubscriptionRef Subscribe(HostInvocation c,string kind,string key,int limit=256,EventSubscribeRequest? handler=null)
    {
        lock(_gate)
        {
            if(limit is <1 or >4096 ||_subscriptions.Count>=4096)throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
            var token=Guid.NewGuid();_subscriptions.Add(token,new(c.Identity,kind,key,c.GroupId,limit,handler){Active=c.Phase=="Active"});
            _=Lifetime(c.Identity);return new(token,c.Identity.LoadInstanceId,null,false);
        }
    }
    /// <summary>Trusted transport port: caller identity must come from the authenticated session.</summary>
    public HostSubscriptionBatch ReadSubscription(PluginIdentity owner,SubscriptionRef reference,int limit=256)
    {
        lock(_gate)
        {
            if(limit is <1 or >256)throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
            if(!_subscriptions.TryGetValue(reference.Token,out var sub))throw new AbiHostException(AbiErrors.HANDLE_EXPIRED);
            if(sub.Owner!=owner||reference.LoadInstanceId!=owner.LoadInstanceId)throw new AbiHostException(AbiErrors.HANDLE_OWNER_MISMATCH);
            if(sub.Handler is not null){bool missed=sub.Gap;sub.Gap=false;return new([],missed);}
            var values=ImmutableArray.CreateBuilder<AbiValue>();while(values.Count<limit&&sub.Queue.TryPeek(out var value))
            {
                values.Add(value);
                try{DataCodec.Validate(SubscriptionBatch.Write(new(values.ToImmutable(),sub.Gap)));if(AbiValueBinary.Encode(SubscriptionBatch.Write(new(values.ToImmutable(),sub.Gap))).Length>DataCodec.MaximumBytes-1024)throw new FormatException();}
                catch(FormatException){values.RemoveAt(values.Count-1);break;}
                sub.Queue.Dequeue();Consumed(value);
            }
            bool gap=sub.Gap;sub.Gap=false;return new(values.ToImmutable(),gap);
        }
    }
    public void ReleaseSubscription(PluginIdentity owner,SubscriptionRef reference)
    {
        lock(_gate)
        {
            if(!_subscriptions.TryGetValue(reference.Token,out var sub))return;
            if(sub.Owner!=owner||reference.LoadInstanceId!=owner.LoadInstanceId)throw new AbiHostException(AbiErrors.HANDLE_OWNER_MISMATCH);
            if(sub.Checkpoint is { } checkpoint)File.Delete(CheckpointPath(checkpoint.Id));
            Close(sub);_subscriptions.Remove(reference.Token);
        }
    }
    private void RetireSubscriptions(Guid instance)
    {foreach(var pair in _subscriptions.Where(p=>p.Value.Owner.LoadInstanceId==instance).ToArray()){Close(pair.Value);_subscriptions.Remove(pair.Key);}}
    private void Close(Subscription sub)
    {sub.Closed=true;sub.Cancellation.Cancel();Clear(sub);if(sub.Workers==0)sub.Cancellation.Dispose();}
    private void Notify(string kind,string key,AbiValue value,Guid? owner=null)
    {
        lock(_gate)
        foreach(var sub in _subscriptions.Values.Where(s=>s.Active&&s.Kind==kind&&s.Key==key&&(!owner.HasValue||s.Owner.LoadInstanceId==owner)))
        {
            Enqueue(sub,value);
        }
    }
    private Empty PublishEvent(HostInvocation c,EventEnvelope v)
    {
        lock(_gate)
        {
            var entry=Find(c,"event",v.Event);var descriptor=EventDescriptor.Read(entry.Value);
            if(entry.Owner!=c.Identity)throw new AbiHostException(AbiErrors.HANDLE_OWNER_MISMATCH);
            if(descriptor.Schema!=v.Schema||v.EventId==Guid.Empty)throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
            Validate(v.Schema,v.Payload);
            var envelope=v with {Origin=c.Identity.PluginId,CorrelationId=c.Options.CorrelationId,CausationId=c.Options.CausationId};
            if(!AppendHistory(descriptor,envelope))return new();
            foreach(var sub in _subscriptions.Values.Where(s=>s.Active&&s.Kind=="event"&&s.Key==v.Event.Value))
            {
                QueueEvent(sub,envelope);
            }
            DeliverWaiters(envelope);OnScheduledEvent(envelope);
            return new();
        }
    }
    private void QueueEvent(Subscription sub,EventEnvelope envelope)
    {
        var request=sub.Handler!;
        if(request.Scope!=envelope.Scope||request.IgnoreSelf&&sub.Owner.PluginId==envelope.Origin)return;
        if(request.Deduplicate&&sub.Seen.Contains(envelope.EventId))return;
        if(request.Reentry==ReentryPolicy.Reject&&(sub.Workers>0||sub.Queue.Count>0)){sub.Gap=true;return;}
        if(!Enqueue(sub,EventEnvelope.Write(envelope)))return;
        if(request.Deduplicate){if(sub.Seen.Count>=4096)sub.Seen.Clear();sub.Seen.Add(envelope.EventId);}
        if(sub.Checkpoint is { } checkpoint)
            sub.Pending[envelope.EventId]=_eventHistory[checkpoint.Event].Events.Single(e=>e.Envelope.EventId==envelope.EventId).Sequence;
        StartDrains(sub);
    }
    private void StartDrains(Subscription sub)
    {
        if(!sub.Active||sub.Closed||!_runtimeReady.Contains(sub.Owner.LoadInstanceId))return;
        int maximum=sub.Handler!.Reentry==ReentryPolicy.AllowBounded?4:1;
        int count=Math.Min(64-_eventWorkers,Math.Min(maximum-sub.Workers,sub.Queue.Count));
        for(int i=0;i<count;i++)
        {sub.Workers++;_eventWorkers++;using(ExecutionContext.SuppressFlow())_=Task.Run(()=>Drain(sub));}
    }
    private async Task Drain(Subscription sub)
    {
        AbiValue? retained=null;
        try
        {
            while(true)
            {
                AbiValue value;
                lock(_gate)
                {
                    if(sub.Closed||!sub.Queue.TryDequeue(out value!))return;
                    retained=value;
                }
                var envelope=EventEnvelope.Read(value);
                while(true)
                {
                    bool succeeded=false;
                    try
                    {
                        var reply=await _dispatch(sub.Owner,sub.Handler!.Handler,value,new(Guid.NewGuid(),TimeSpan.FromSeconds(5),sub.Cancellation.Token)
                            {CorrelationId=envelope.CorrelationId,CausationId=envelope.EventId}).ConfigureAwait(false);
                        succeeded=reply.Success;
                        lock(_gate)
                        {
                            if(sub.Closed)return;
                            if(succeeded||!sub.Retry){if(!succeeded)sub.Gap=true;Acknowledge(sub,envelope);break;}
                        }
                    }
                    catch(Exception e)when(e is not OutOfMemoryException and not AccessViolationException)
                    {lock(_gate){if(sub.Closed)return;sub.Gap=true;}}
                    if(!sub.Retry){lock(_gate)sub.Gap=true;break;}
                    await Task.Delay(TimeSpan.FromMilliseconds(250),sub.Cancellation.Token).ConfigureAwait(false);
                }
                lock(_gate){Consumed(value);retained=null;}
            }
        }
        catch(OperationCanceledException)when(sub.Cancellation.IsCancellationRequested){}
        finally
        {
            lock(_gate)
            {
                if(retained is not null)Consumed(retained);
                sub.Workers--;_eventWorkers--;if(sub.Closed&&sub.Workers==0)sub.Cancellation.Dispose();
                foreach(var waiting in _subscriptions.Values.Where(s=>s.Handler is not null))StartDrains(waiting);
            }
        }
    }
    private SubscriptionRef SubscribeEvent(HostInvocation c,EventSubscribeRequest request)
    {
        lock(_gate)
        {
            var descriptor=EventDescriptor.Read(Find(c,"event",request.Event).Value);
            if(descriptor.Schema!=request.Schema||request.Handler==0||request.QueueLimit is <1 or >4096)throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
            if(request.Lifetime==SubscriptionLifetime.Transient&&request.Cursor is not null||request.Lifetime==SubscriptionLifetime.Resumable&&!descriptor.Durable)throw new AbiHostException(AbiErrors.RESYNC_REQUIRED);
            EventHistory? history=descriptor.Durable?History(descriptor):null;
            EventCheckpoint? checkpoint=request.Lifetime==SubscriptionLifetime.Resumable?OpenCheckpoint(c,request,history!):null;
            var reference=Subscribe(c,"event",request.Event.Value,(int)Math.Min(request.QueueLimit,descriptor.QueueLimit),request);
            var sub=_subscriptions[reference.Token];sub.Retry=descriptor.Delivery==DeliveryGuarantee.AtLeastOnce;sub.Checkpoint=checkpoint;
            if(checkpoint is not null)
            {
                sub.Gap=checkpoint.Gap;
                foreach(var item in history!.Events.Where(e=>e.Sequence>checkpoint.Sequence))QueueEvent(sub,item.Envelope);
                reference=reference with {Cursor=checkpoint.Id.ToString("N"),ResyncRequired=sub.Gap};
            }
            return reference;
        }
    }
    private IEnumerable<HostCapabilityRoute> EventRoutes => [
        HostRoutes.Typed<SubscriptionRead,SubscriptionBatch>("Resources","ReadSubscription",(c,v)=>
        {if(v.Limit is <1 or >256)throw new AbiHostException(AbiErrors.SCHEMA_INVALID);var batch=ReadSubscription(c.Identity,v.Subscription,(int)v.Limit);return ValueTask.FromResult(new SubscriptionBatch(batch.Items,batch.ResyncRequired));}),
        HostRoutes.Typed<SubscriptionRef,Empty>("Resources","ReleaseSubscription",(c,v)=>
        {ReleaseSubscription(c.Identity,v);return ValueTask.FromResult(new Empty());}),
        HostRoutes.Typed<EventDescriptor,RegistrationRef>("Events","Define",(c,v)=>
        {if(v.QueueLimit is <1 or >4096)throw new AbiHostException(AbiErrors.SCHEMA_INVALID);lock(_gate){Own(c,v.Id);if(v.Durable)History(v);return ValueTask.FromResult(Stage(c,"event",v.Id,v));}}),
        HostRoutes.Typed<QualifiedRequest,EventDescriptor>("Events","Describe",(c,v)=>ValueTask.FromResult(EventDescriptor.Read(Find(c,"event",v.Id).Value))),
        HostRoutes.Typed<EventSubscribeRequest,SubscriptionRef>("Events","Subscribe",(c,v)=>ValueTask.FromResult(SubscribeEvent(c,v))),
        HostRoutes.Typed<EventEnvelope,Empty>("Events","Publish",(c,v)=>ValueTask.FromResult(PublishEvent(c,v))),
        HostRoutes.Typed<StateReadRequest,SubscriptionRef>("State","Watch",(c,v)=>
        {ReadState(c,v);return ValueTask.FromResult(Subscribe(c,"state",v.Id.Value));}),
        HostRoutes.Typed<WatchRequest,SubscriptionRef>("Tasks","Watch",(c,v)=>
        {if(v.Schema!=Abi100Schemas.OperationRecord)throw new AbiHostException(AbiErrors.SCHEMA_INVALID);GetOperation(c,v.Target);return ValueTask.FromResult(Subscribe(c,"operation",v.Target.Value) with {ResyncRequired=v.Cursor is not null});})
    ];
}
