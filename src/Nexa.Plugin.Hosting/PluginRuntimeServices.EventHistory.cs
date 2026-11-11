using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100.Data;
namespace Nexa.Plugin.Hosting;

public sealed partial class PluginRuntimeServices
{
    private sealed class EventHistory(EventDescriptor descriptor,Guid stream)
    {
        internal EventDescriptor Descriptor=descriptor;
        internal Guid Stream=stream;
        internal long Sequence;
        internal List<(long Sequence,EventEnvelope Envelope)> Events=[];
    }
    private sealed record EventCheckpoint(Guid Id,string Owner,Guid Stream,QualifiedId Event,ScopedTarget Scope,long Sequence,bool Gap);
    private readonly Dictionary<QualifiedId,EventHistory> _eventHistory=[];
    private string EventStore=>Path.Combine(Path.GetDirectoryName(_workflowStore)!,"events");
    private string EventPath(QualifiedId id)=>Path.Combine(EventStore,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id.Value)))+".events");
    private string CheckpointPath(Guid id)=>Path.Combine(EventStore,id.ToString("N")+".cursor");
    private static AbiValue WriteCheckpoint(EventCheckpoint c)=>DataCodec.Map(("id",DataCodec.WriteGuid(c.Id)),("owner",DataCodec.WriteString(c.Owner)),
        ("stream",DataCodec.WriteGuid(c.Stream)),("event",QualifiedId.Write(c.Event)),("scope",ScopedTarget.Write(c.Scope)),("sequence",DataCodec.WriteInt64(c.Sequence)),("gap",DataCodec.WriteBoolean(c.Gap)));
    private static EventCheckpoint ReadCheckpoint(AbiValue value)
    {
        var m=DataCodec.ReadMap(value);AbiValue F(string key)=>DataCodec.Field(m,key);
        return new(DataCodec.ReadGuid(F("id")),DataCodec.ReadString(F("owner")),DataCodec.ReadGuid(F("stream")),QualifiedId.Read(F("event")),ScopedTarget.Read(F("scope")),DataCodec.ReadInt64(F("sequence")),DataCodec.ReadBoolean(F("gap")));
    }
    private void SaveEventFile(string path,AbiValue value,int maxBytes,int maxFiles)
    {
        byte[] bytes=AbiValueBinary.Encode(value);if(bytes.Length>maxBytes)throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
        Directory.CreateDirectory(EventStore);
        string extension=Path.GetExtension(path);
        if(!File.Exists(path)&&Directory.EnumerateFiles(EventStore,"*"+extension).Take(maxFiles).Count()>=maxFiles)throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){file.Write(bytes);file.Flush(true);}File.Move(temp,path,true);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    private static AbiValue ReadEventFile(string path,int maxBytes)
    {if(new FileInfo(path).Length>maxBytes)throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);return AbiValueBinary.Decode(File.ReadAllBytes(path));}
    private static AbiValue WriteHistory(EventHistory h)=>DataCodec.Map(("descriptor",EventDescriptor.Write(h.Descriptor)),("stream",DataCodec.WriteGuid(h.Stream)),
        ("sequence",DataCodec.WriteInt64(h.Sequence)),("events",new AbiValue.Array(h.Events.Select(e=>DataCodec.Map(("sequence",DataCodec.WriteInt64(e.Sequence)),("envelope",EventEnvelope.Write(e.Envelope)))).ToImmutableArray())));
    private EventHistory History(EventDescriptor descriptor)
    {
        if(_eventHistory.TryGetValue(descriptor.Id,out var known))
        {
            if(!AbiValueBinary.Encode(EventDescriptor.Write(known.Descriptor)).SequenceEqual(AbiValueBinary.Encode(EventDescriptor.Write(descriptor))))throw new AbiHostException(AbiErrors.PLAN_STALE);
            return known;
        }
        if(_eventHistory.Count>=64)throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
        var history=new EventHistory(descriptor,Guid.NewGuid());string path=EventPath(descriptor.Id);
        if(File.Exists(path))
        {
            var m=DataCodec.ReadMap(ReadEventFile(path,1024*1024));
            if(!AbiValueBinary.Encode(DataCodec.Field(m,"descriptor")).SequenceEqual(AbiValueBinary.Encode(EventDescriptor.Write(descriptor))))throw new AbiHostException(AbiErrors.PLAN_STALE);
            history.Stream=DataCodec.ReadGuid(DataCodec.Field(m,"stream"));history.Sequence=DataCodec.ReadInt64(DataCodec.Field(m,"sequence"));
            foreach(var value in DataCodec.ReadArray(DataCodec.Field(m,"events"),(v,_)=>v,0))
            {
                var item=DataCodec.ReadMap(value);long sequence=DataCodec.ReadInt64(DataCodec.Field(item,"sequence"));var envelope=EventEnvelope.Read(DataCodec.Field(item,"envelope"));
                if(sequence<1||sequence>history.Sequence||history.Events.Count>0&&sequence!=history.Events[^1].Sequence+1||envelope.Event!=descriptor.Id||envelope.Schema!=descriptor.Schema)throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
                Validate(envelope.Schema,envelope.Payload);history.Events.Add((sequence,envelope));
            }
            if(history.Stream==Guid.Empty||history.Sequence<0||history.Events.Count==0&&history.Sequence!=0||history.Events.Count>Math.Min(256,descriptor.QueueLimit)||history.Events.Count>0&&history.Events[^1].Sequence!=history.Sequence)throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
        }
        else SaveEventFile(path,WriteHistory(history),1024*1024,64);
        _eventHistory.Add(descriptor.Id,history);return history;
    }
    private bool AppendHistory(EventDescriptor descriptor,EventEnvelope envelope)
    {
        if(!descriptor.Durable)return true;
        var history=History(descriptor);
        var duplicate=history.Events.FirstOrDefault(e=>e.Envelope.EventId==envelope.EventId);
        if(duplicate.Envelope is not null)
        {
            if(duplicate.Envelope.Scope!=envelope.Scope||!AbiValueBinary.Encode(duplicate.Envelope.Payload).SequenceEqual(AbiValueBinary.Encode(envelope.Payload)))throw new AbiHostException(AbiErrors.REQUEST_ID_REUSED);
            return false;
        }
        if(AbiValueBinary.Encode(EventEnvelope.Write(envelope)).Length>262144)throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
        long before=history.Sequence;var previous=history.Events;
        history.Sequence=checked(before+1);history.Events=[..previous,(history.Sequence,envelope)];
        try
        {
            while(history.Events.Count>Math.Min(256,descriptor.QueueLimit)||AbiValueBinary.Encode(WriteHistory(history)).Length>1024*1024-4096)history.Events.RemoveAt(0);
            SaveEventFile(EventPath(descriptor.Id),WriteHistory(history),1024*1024,64);
        }
        catch{history.Sequence=before;history.Events=previous;throw;}
        return true;
    }
    private EventCheckpoint OpenCheckpoint(HostInvocation caller,EventSubscribeRequest request,EventHistory history)
    {
        EventCheckpoint checkpoint;
        if(request.Cursor is null)
        {
            checkpoint=new(Guid.NewGuid(),caller.Identity.PluginId,history.Stream,request.Event,request.Scope,history.Sequence,false);
            SaveEventFile(CheckpointPath(checkpoint.Id),WriteCheckpoint(checkpoint),65536,4096);
        }
        else
        {
            if(!Guid.TryParseExact(request.Cursor,"N",out var id))throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
            string path=CheckpointPath(id);if(!File.Exists(path))throw new AbiHostException(AbiErrors.RESYNC_REQUIRED);
            checkpoint=ReadCheckpoint(ReadEventFile(path,65536));
            if(checkpoint.Id!=id||checkpoint.Owner!=caller.Identity.PluginId||checkpoint.Event!=request.Event||checkpoint.Scope!=request.Scope)throw new AbiHostException(AbiErrors.HANDLE_OWNER_MISMATCH);
            if(checkpoint.Stream!=history.Stream||checkpoint.Sequence>history.Sequence||checkpoint.Sequence<0)throw new AbiHostException(AbiErrors.RESYNC_REQUIRED);
            if(_subscriptions.Values.Any(s=>s.Checkpoint?.Id==id))throw new AbiHostException(AbiErrors.CONFLICT);
            if(history.Events.Count>0&&checkpoint.Sequence<history.Events[0].Sequence-1)checkpoint=checkpoint with {Gap=true};
        }
        return checkpoint;
    }
    private void Acknowledge(Subscription sub,EventEnvelope envelope)
    {
        if(sub.Checkpoint is not { } checkpoint)return;
        sub.Pending.Remove(envelope.EventId,out long acknowledged);
        var history=_eventHistory[checkpoint.Event];
        long next=sub.Pending.Count>0?sub.Pending.Values.Min()-1:history.Sequence;
        var updated=checkpoint with {Sequence=Math.Max(checkpoint.Sequence,next),Gap=checkpoint.Gap||sub.Gap};
        try{SaveEventFile(CheckpointPath(updated.Id),WriteCheckpoint(updated),65536,4096);sub.Checkpoint=updated;}
        catch{if(acknowledged>0)sub.Pending[envelope.EventId]=acknowledged;throw;}
    }
}
