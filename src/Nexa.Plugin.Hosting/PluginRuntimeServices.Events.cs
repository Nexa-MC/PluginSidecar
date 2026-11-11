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
        internal Queue<AbiValue> Queue=[]; internal HashSet<Guid> Seen=[];internal bool Gap,Draining,Closed,Active;
    }
    private readonly Dictionary<Guid,Subscription> _subscriptions=[];
    private long _queuedBytes;
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
            sub.Closed=true;Clear(sub);_subscriptions.Remove(reference.Token);
        }
    }
    private void RetireSubscriptions(Guid instance)
    {foreach(var pair in _subscriptions.Where(p=>p.Value.Owner.LoadInstanceId==instance).ToArray()){pair.Value.Closed=true;Clear(pair.Value);_subscriptions.Remove(pair.Key);}}
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
            foreach(var sub in _subscriptions.Values.Where(s=>s.Active&&s.Kind=="event"&&s.Key==v.Event.Value))
            {
                var request=sub.Handler!;
                if(request.Scope!=v.Scope ||request.IgnoreSelf&&sub.Owner==c.Identity)continue;
                if(request.Deduplicate&&!sub.Seen.Add(v.EventId))continue;
                if(sub.Seen.Count>4096){sub.Seen.Clear();sub.Seen.Add(v.EventId);}
                if(!Enqueue(sub,EventEnvelope.Write(envelope)))continue;
                if(!sub.Draining){sub.Draining=true;using(ExecutionContext.SuppressFlow())_=Task.Run(()=>Drain(sub));}
            }
            return new();
        }
    }
    private async Task Drain(Subscription sub)
    {
        while(true)
        {
            AbiValue value;CancellationToken lifetime;
            lock(_gate)
            {
                if(sub.Closed||!sub.Queue.TryDequeue(out value!)){sub.Draining=false;return;}
                Consumed(value);lifetime=Lifetime(sub.Owner);
            }
            try
            {
                var envelope=EventEnvelope.Read(value);
                var reply=await _dispatch(sub.Owner,sub.Handler!.Handler,value,new(Guid.NewGuid(),TimeSpan.FromSeconds(5),lifetime)
                    {CorrelationId=envelope.CorrelationId,CausationId=envelope.EventId}).ConfigureAwait(false);
                if(!reply.Success)lock(_gate)sub.Gap=true;
            }
            catch(Exception e) when(e is not OutOfMemoryException and not AccessViolationException){lock(_gate)sub.Gap=true;}
        }
    }
    private IEnumerable<HostCapabilityRoute> EventRoutes => [
        HostRoutes.Typed<SubscriptionRead,SubscriptionBatch>("Resources","ReadSubscription",(c,v)=>
        {if(v.Limit is <1 or >256)throw new AbiHostException(AbiErrors.SCHEMA_INVALID);var batch=ReadSubscription(c.Identity,v.Subscription,(int)v.Limit);return ValueTask.FromResult(new SubscriptionBatch(batch.Items,batch.ResyncRequired));}),
        HostRoutes.Typed<SubscriptionRef,Empty>("Resources","ReleaseSubscription",(c,v)=>
        {ReleaseSubscription(c.Identity,v);return ValueTask.FromResult(new Empty());}),
        HostRoutes.Typed<EventDescriptor,RegistrationRef>("Events","Define",(c,v)=>
        {if(v.Durable||v.Delivery!=DeliveryGuarantee.BestEffortAtMostOnce)throw new AbiHostException(AbiErrors.CAPABILITY_UNAVAILABLE);if(v.QueueLimit is <1 or >4096)throw new AbiHostException(AbiErrors.SCHEMA_INVALID);return ValueTask.FromResult(Stage(c,"event",v.Id,v));}),
        HostRoutes.Typed<QualifiedRequest,EventDescriptor>("Events","Describe",(c,v)=>ValueTask.FromResult(EventDescriptor.Read(Find(c,"event",v.Id).Value))),
        HostRoutes.Typed<EventSubscribeRequest,SubscriptionRef>("Events","Subscribe",(c,v)=>
        {
            var descriptor=EventDescriptor.Read(Find(c,"event",v.Event).Value);
            if(descriptor.Schema!=v.Schema||v.Handler==0)throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
            if(v.Lifetime!=SubscriptionLifetime.Transient||v.Cursor is not null||v.Reentry!=ReentryPolicy.Queue)throw new AbiHostException(AbiErrors.CAPABILITY_UNAVAILABLE);
            return ValueTask.FromResult(Subscribe(c,"event",v.Event.Value,checked((int)Math.Min(v.QueueLimit,descriptor.QueueLimit)),v));
        }),
        HostRoutes.Typed<EventEnvelope,Empty>("Events","Publish",(c,v)=>ValueTask.FromResult(PublishEvent(c,v))),
        HostRoutes.Typed<StateReadRequest,SubscriptionRef>("State","Watch",(c,v)=>
        {ReadState(c,v);return ValueTask.FromResult(Subscribe(c,"state",v.Id.Value));}),
        HostRoutes.Typed<WatchRequest,SubscriptionRef>("Tasks","Watch",(c,v)=>
        {if(v.Schema!=Abi100Schemas.OperationRecord)throw new AbiHostException(AbiErrors.SCHEMA_INVALID);GetOperation(c,v.Target);return ValueTask.FromResult(Subscribe(c,"operation",v.Target.Value) with {ResyncRequired=v.Cursor is not null});})
    ];
}
