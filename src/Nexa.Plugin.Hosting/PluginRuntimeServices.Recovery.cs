using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100.Data;
namespace Nexa.Plugin.Hosting;

public sealed partial class PluginRuntimeServices
{
    private string JournalPath(string owner,EntityId id)
    {
        if(!Guid.TryParseExact(id.Value,"N",out _))throw new AbiHostException(AbiErrors.NOT_FOUND);
        return Path.Combine(_workflowStore,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner))),id.Value+".run");
    }
    private void PersistRun(Operation operation)
    {
        if(!_runs.TryGetValue(operation.Record.Id,out var run)||!run.Definition.Persistent)return;
        var journal=DataCodec.Map(("version",DataCodec.WriteUInt64(1)),("owner",DataCodec.WriteString(operation.Owner.PluginId)),
            ("pluginVersion",DataCodec.WriteString(operation.Owner.PluginVersion)),("operation",OperationRecord.Write(operation.Record)),
            ("definition",WorkflowDefinition.Write(run.Definition)),("request",WorkflowRequest.Write(run.Request)),
            ("trace",WorkflowTrace.Write(new(run.Id,run.Definition.DefinitionDigest,run.Steps.ToImmutableArray()))),
            ("waits",new AbiValue.Map(run.Waits.ToImmutableDictionary())),("returns",new AbiValue.Array(run.Returns.Select(DataCodec.WriteString).ToImmutableArray())),("outcomes",new AbiValue.Map(run.Outcomes.ToImmutableDictionary())),("pendingEffects",new AbiValue.Array(run.PendingEffects.Select(DataCodec.WriteString).ToImmutableArray())));
        byte[] bytes=AbiValueBinary.Encode(journal);
        string path=JournalPath(operation.Owner.PluginId,run.Id);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var journals=Directory.EnumerateFiles(_workflowStore,"*.run",SearchOption.AllDirectories).Take(257).Select(p=>new FileInfo(p)).ToArray();
        if(journals.Length>256 || !File.Exists(path)&&journals.Length==256 || journals.Sum(f=>f.Length)+bytes.Length-(File.Exists(path)?new FileInfo(path).Length:0)>64L*1024*1024)
            throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){stream.Write(bytes);stream.Flush(true);}
            File.Move(temporary,path,true);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    private void RestoreOwnRuns(HostInvocation caller)
    {
        string directory=Path.GetDirectoryName(JournalPath(caller.Identity.PluginId,new(Guid.Empty.ToString("N"))))!;
        if(!Directory.Exists(directory))return;
        foreach(string path in Directory.EnumerateFiles(directory,"*.run").Take(257))
        {
            var id=new EntityId(Path.GetFileNameWithoutExtension(path));
            if(!_operations.ContainsKey(id))RestoreRun(caller,id);
        }
    }
    private Operation RestoreRun(HostInvocation caller,EntityId id)
    {
        string path=JournalPath(caller.Identity.PluginId,id);
        if(!File.Exists(path))throw new AbiHostException(AbiErrors.NOT_FOUND);
        if(_operations.Count>=256||new FileInfo(path).Length>DataCodec.MaximumBytes)throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
        var map=DataCodec.ReadMap(AbiValueBinary.Decode(File.ReadAllBytes(path)));
        AbiValue Field(string key)=>DataCodec.Field(map,key);
        if(DataCodec.ReadUInt64(Field("version"))!=1||DataCodec.ReadString(Field("owner"))!=caller.Identity.PluginId||DataCodec.ReadString(Field("pluginVersion"))!=caller.Identity.PluginVersion)
            throw new AbiHostException(AbiErrors.HANDLE_OWNER_MISMATCH);
        var record=OperationRecord.Read(Field("operation"));if(record.Id!=id)throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
        var run=new Run(WorkflowDefinition.Read(Field("definition")),WorkflowRequest.Read(Field("request")))
        {
            Waits=map.TryGetValue("waits",out var waits)?DataCodec.ReadMap(waits).ToDictionary():[],
            Id=id,Steps=WorkflowTrace.Read(Field("trace")).Steps.ToList(),Outcomes=DataCodec.ReadMap(Field("outcomes")).ToDictionary(),
            Returns=DataCodec.ReadArray(Field("returns"),(v,_)=>DataCodec.ReadString(v),0).ToHashSet(StringComparer.Ordinal),
            PendingEffects=DataCodec.ReadArray(Field("pendingEffects"),(v,_)=>DataCodec.ReadString(v),0).ToHashSet(StringComparer.Ordinal)
        };
        if(run.PendingEffects.Count>0||!Terminal(record.State))record=record with {State=run.PendingEffects.Count>0?OperationState.OutcomeUnknown:OperationState.Failed,
            Failure=new(run.PendingEffects.Count>0?AbiErrors.OUTCOME_UNKNOWN:AbiErrors.SESSION_LOST,Text("workflow.interrupted"),Guid.NewGuid(),false,[]),Revision=NewRevision()};
        run.Bytes=run.Outcomes.Values.Sum(v=>(long)AbiValueBinary.Encode(v).Length);if(run.Bytes>2*1024*1024||_workflowBytes+run.Bytes>32*1024*1024)throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
        var operation=new Operation(caller.Identity,record,true,CancellationTokenSource.CreateLinkedTokenSource(Lifetime(caller.Identity)));
        _workflowBytes+=run.Bytes;run.Operation=operation;_runs.Add(id,run);_operations.Add(id,operation);return operation;
    }
    private OperationRef Resume(HostInvocation c,WorkflowRunRequest request)
    {
        lock(_gate)
        {
            var run=FindRun(c,request.Run);var operation=GetOperation(c,request.Run);
            if(operation.Record.Revision!=request.ExpectedRevision||request.DefinitionDigest!=run.Definition.DefinitionDigest)throw new AbiHostException(AbiErrors.PLAN_STALE);
            if(operation.Running||operation.Record.State==OperationState.Completed)throw new AbiHostException(AbiErrors.CONFLICT);
            if(run.PendingEffects.Count!=0)throw new AbiHostException(AbiErrors.OUTCOME_UNKNOWN);
            var current=ResolveWorkflow(c,run.Request);
            if(!AbiValueBinary.Encode(WorkflowDefinition.Write(current)).SequenceEqual(AbiValueBinary.Encode(WorkflowDefinition.Write(run.Definition))))throw new AbiHostException(AbiErrors.PLAN_STALE);
            operation.Lifetime.Dispose();operation.Lifetime=CancellationTokenSource.CreateLinkedTokenSource(Lifetime(c.Identity));run.Work=0;
            ReserveWorkflow(run);
            var previous=operation.Record;
            try { Change(operation,operation.Record with {State=OperationState.Pending,Failure=null,Stage="resuming"}); }
            catch { operation.Record=previous;ReleaseWorkflow(run);throw; }
            RunOperation(c,operation,token=>ExecuteRun(c,run,token),token=>EnterWorkflow(run,token),()=>ReleaseWorkflow(run));
            return new(operation.Record.Id,OperationState.Pending);
        }
    }
}
