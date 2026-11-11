using System.Security.Cryptography;
using System.Text;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;
namespace Nexa.Plugin.Hosting;

public sealed partial class PluginRuntimeServices
{
    private sealed record EventWaiter(PluginIdentity Owner,QualifiedId Event,ScopedTarget Scope,
        TaskCompletionSource<EventEnvelope> Completion);
    private readonly Dictionary<Guid,EventWaiter> _eventWaiters=[];
    private async ValueTask WaitDelay(Run run,string path,long milliseconds,CancellationToken token)
    {
        DateTimeOffset due;
        lock(_gate)
        {
            if(run.Waits.TryGetValue(path,out var stored))due=DataCodec.ReadTimestamp(stored);
            else{due=_options.Clock.GetUtcNow().AddMilliseconds(milliseconds);run.Waits[path]=DataCodec.WriteTimestamp(due);PersistRun(run.Operation!);}
        }
        lock(_gate)Change(run.Operation!,run.Operation!.Record with {Stage="waiting:delay"});
        TimeSpan remaining=due-_options.Clock.GetUtcNow();
        if(remaining>TimeSpan.Zero)await Task.Delay(remaining,_options.Clock,token).ConfigureAwait(false);
    }
    private async ValueTask<AbiValue> WaitEvent(HostInvocation caller,Run run,string path,WorkflowNode node,AbiValue input,CancellationToken token)
    {
        if(!_authorize(caller.Identity,"nexa.events.read"))throw new AbiHostException(AbiErrors.PERMISSION_DENIED);
        var scope=ScopedTarget.Read(input);Guid id=Guid.NewGuid();EventWaiter waiter;EventDescriptor descriptor;
        lock(_gate)
        {
            descriptor=EventDescriptor.Read(Find(caller,"event",node.Target!).Value);
            if(node.OutputSchema!=descriptor.Schema&&node.OutputSchema!=Abi100Schemas.EventEnvelope)throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
            if(_eventWaiters.Count>=256)throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
            if(descriptor.Durable)
            {
                var history=History(descriptor);
                if(run.Waits.TryGetValue(path,out var prior))
                {
                    var marker=DataCodec.ReadMap(prior);long sequence=DataCodec.ReadInt64(DataCodec.Field(marker,"sequence"));
                    if(DataCodec.ReadGuid(DataCodec.Field(marker,"stream"))!=history.Stream||sequence>history.Sequence||sequence<0||history.Events.Count>0&&sequence<history.Events[0].Sequence-1)throw new AbiHostException(AbiErrors.RESYNC_REQUIRED);
                    var saved=history.Events.FirstOrDefault(e=>e.Sequence>sequence&&e.Envelope.Scope==scope).Envelope;
                    if(saved is not null)return node.OutputSchema==Abi100Schemas.EventEnvelope?EventEnvelope.Write(saved):saved.Payload;
                }
                else run.Waits[path]=DataCodec.Map(("stream",DataCodec.WriteGuid(history.Stream)),("sequence",DataCodec.WriteInt64(history.Sequence)));
            }
            else
            {
                if(run.Waits.TryGetValue(path,out var prior)&&DataCodec.ReadGuid(prior)!=caller.Identity.LoadInstanceId)throw new AbiHostException(AbiErrors.RESYNC_REQUIRED);
                run.Waits[path]=DataCodec.WriteGuid(caller.Identity.LoadInstanceId);
            }
            PersistRun(run.Operation!);
            waiter=new(caller.Identity,node.Target!,scope,new(TaskCreationOptions.RunContinuationsAsynchronously));_eventWaiters.Add(id,waiter);
            Change(run.Operation!,run.Operation!.Record with {Stage="waiting:event"});
        }
        try
        {
            var envelope=await waiter.Completion.Task.WaitAsync(token).ConfigureAwait(false);
            return node.OutputSchema==Abi100Schemas.EventEnvelope?EventEnvelope.Write(envelope):envelope.Payload;
        }
        finally{lock(_gate)_eventWaiters.Remove(id);}
    }
    private void DeliverWaiters(EventEnvelope envelope)
    {
        foreach(var waiter in _eventWaiters.Values.Where(w=>w.Event==envelope.Event&&w.Scope==envelope.Scope))
            if(_authorize(waiter.Owner,"nexa.events.read"))waiter.Completion.TrySetResult(envelope);
    }
    private void RetireWaiters(Guid instance)
    {
        foreach(var pair in _eventWaiters.Where(p=>p.Value.Owner.LoadInstanceId==instance).ToArray())
        {pair.Value.Completion.TrySetException(new AbiHostException(AbiErrors.PLUGIN_STOPPED));_eventWaiters.Remove(pair.Key);}
    }
    private async ValueTask<AbiValue> AskUser(HostInvocation caller,Run run,string path,WorkflowNode node,AbiValue input,CancellationToken token)
    {
        if(!_authorize(caller.Identity,"nexa.ui.write"))throw new AbiHostException(AbiErrors.PERMISSION_DENIED);
        if(_options.Interaction is not { } interaction)throw new AbiHostException(AbiErrors.CAPABILITY_UNAVAILABLE);
        var dialog=UIDialog.Read(input);
        byte[] digest=SHA256.HashData(Encoding.UTF8.GetBytes(caller.Identity.PluginId+"/"+run.Id.Value+"/"+path));
        var request=new WorkflowInteractionRequest(new Guid(digest.AsSpan(0,16)),caller.Identity,run.Id,node.Id,dialog,node.OutputSchema,caller.Options.CorrelationId);
        lock(_gate){run.Waits[path]=DataCodec.WriteGuid(request.RequestId);PersistRun(run.Operation!);}
        var pending=interaction.AskAsync(request,token).AsTask();
        _=ObserveInteraction(pending);
        var reply=await pending.WaitAsync(token).ConfigureAwait(false);
        if(!reply.Success)throw new AbiHostException(reply.Error!.Code);
        token.ThrowIfCancellationRequested();Validate(node.OutputSchema,reply.Value);return reply.Value;
    }
    private static async Task ObserveInteraction(Task task)
    {try{await task.ConfigureAwait(false);}catch(Exception e)when(e is not OutOfMemoryException and not AccessViolationException){}}
}
