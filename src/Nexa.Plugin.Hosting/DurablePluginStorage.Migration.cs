using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;
namespace Nexa.Plugin.Hosting;

public sealed partial class DurablePluginStorage
{
    internal PluginRuntimeServices? Runtime;
    private ValueTask<OperationRef> Migrate(HostInvocation caller,StorageMigration request)
    {
        if(request.ToVersion<=request.FromVersion)throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
        RequireOwner(caller.Identity,request.Action);
        var runtime=Runtime??throw new AbiHostException(AbiErrors.CAPABILITY_UNAVAILABLE);
        return ValueTask.FromResult(runtime.StartHostOperation(caller,"nexa.storage.write",Abi100Schemas.StorageNamespace,async token=>
        {
            var active=caller with {Options=caller.Options with {Cancellation=token}};
            NamespaceLease lease;Revision before;StorageMigrationInput input;
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                lease=Resolve(active,request.Namespace);before=lease.State.Revision;
                if(lease.State.SchemaVersion!=request.FromVersion||request.Namespace.Revision!=before)throw new AbiHostException(AbiErrors.STALE_REVISION);
                if(lease.State.Values.Count>256)throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
                input=new(request.Namespace.Namespace,request.FromVersion,request.ToVersion,lease.State.Values.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>new StorageEntry(p.Key,p.Value)).ToImmutableArray());
                DataCodec.Validate(StorageMigrationInput.Write(input));
            }
            finally{_gate.Release();}
            var output=await runtime.TransformStorage(active,request.Action,input).ConfigureAwait(false);
            if(output.Entries.Length>256||output.Entries.Select(e=>e.Key).Distinct(StringComparer.Ordinal).Count()!=output.Entries.Length)throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
            var values=ImmutableDictionary.CreateBuilder<string,StorageValue>(StringComparer.Ordinal);
            foreach(var entry in output.Entries)
            {
                ValidateKey(entry.Key);DataCodec.Validate(entry.Value.Value);
                if(!validateSchema(entry.Value.Schema,entry.Value.Value))throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
                values.Add(entry.Key,entry.Value with {Revision=NewRevision()});
            }
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if(Resolve(active,request.Namespace)!=lease||lease.State.Revision!=before||lease.State.SchemaVersion!=request.FromVersion)throw new AbiHostException(AbiErrors.STALE_REVISION);
                var revision=NewRevision();var next=values.ToImmutable();
                await Save(new(lease.State.Path,request.ToVersion,revision,next),next,revision,token).ConfigureAwait(false);
                lease.State.SchemaVersion=request.ToVersion;lease.State.Values=next;lease.State.Revision=revision;
                foreach(var id in _transactions.Where(p=>ReferenceEquals(p.Value.Namespace.State,lease.State)).Select(p=>p.Key).ToArray())_transactions.Remove(id);
                foreach(var id in _namespaces.Where(p=>ReferenceEquals(p.Value.State,lease.State)).Select(p=>p.Key).ToArray())_namespaces.Remove(id);
                var reference=new StorageNamespace(Guid.NewGuid(),caller.Identity.LoadInstanceId,request.Namespace.Namespace,revision);
                _namespaces.Add(reference.Token,new(caller.Identity,reference,lease.State));
                return AbiReply<AbiValue>.Ok(StorageNamespace.Write(reference));
            }
            finally{_gate.Release();}
        }));
    }
}

public sealed partial class PluginRuntimeServices
{
    internal async ValueTask<StorageMigrationOutput> TransformStorage(HostInvocation caller,QualifiedId id,StorageMigrationInput input)
    {
        Own(caller,id);var entry=Find(caller,"action",id);var action=ActionDescriptor.Read(entry.Value);
        if(entry.Owner!=caller.Identity||action.Input!=Abi100Schemas.StorageMigrationInput||action.Output!=Abi100Schemas.StorageMigrationOutput
            ||action.Idempotency!=IdempotencyMode.ReadOnly||action.Confirmation!=ConfirmationRequirement.None)throw new AbiHostException(AbiErrors.SERVICE_CONTRACT_MISMATCH);
        var request=new ActionRequest(id,new(new("nexa:storage"),null,"storage-migration"),StorageMigrationInput.Write(input),null,caller.Options.OperationId.ToString("N"));
        AdmitAction(caller,action,request,"nexa.storage.write");HoldRegistration(entry);
        try
        {
            var reply=await _dispatch(entry.Owner,action.Handler!.Value,ActionRequest.Write(request),caller.Options).ConfigureAwait(false);
            if(!reply.Success)throw new AbiHostException(reply.Error!.Code);
            Validate(Abi100Schemas.StorageMigrationOutput,reply.Value);return StorageMigrationOutput.Read(reply.Value);
        }
        finally{DropRegistration(entry);}
    }
}
