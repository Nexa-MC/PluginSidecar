using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;
namespace PluginSidecar.Tests;

internal static class StorageMigrationTests
{
    private static InvocationOptions Options()=>new(Guid.NewGuid(),TimeSpan.FromSeconds(5),default){IdempotencyKey=Guid.NewGuid().ToString("N")};
    private static T Success<T>(AbiReply<T> reply)=>reply.Success?reply.Value:throw new InvalidOperationException(reply.Error!.Code);
    private static void Check(bool value,[System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))]string? expression=null)
    {if(!value)throw new InvalidOperationException("Storage migration: "+expression);}
    private static async Task<OperationRecord> Terminal(Abi100Context api,OperationRef operation)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while(true)
        {
            var record=Success(await api.Tasks.Read(new(operation.Id),Options()));
            if(record.State is OperationState.Completed or OperationState.Failed or OperationState.Cancelled)return record;
            await Task.Delay(10,timeout.Token);
        }
    }
    internal static async Task Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"nexa-migration-"+Guid.NewGuid().ToString("N"));
        try
        {
            await using(var runtime=new PluginHostRuntime(root,_=>true,Abi100Schemas.Validate))
            {
                var entry=new Entry();Check((await runtime.LoadAsync([entry.Source()])).Single().State==LoadState.Active);
                using var api=entry.Context!.AsAbi100();
                var initial=Success(await api.Storage.OpenNamespace(new(new("migrate:data"),1),Options()));
                Success(await api.Storage.Write(new(initial,"name",TextResult.Write(new("original")),Abi100Schemas.TextResult,null),Options()));
                var stale=await Terminal(api,Success(await api.Storage.Migrate(new(initial,1,2,new("migrate:transform")),Options())));
                Check(stale.Failure?.Code==AbiErrors.STALE_REVISION&&entry.Calls==0);
                var current=Success(await api.Storage.OpenNamespace(new(new("migrate:data"),1),Options()));
                entry.Duplicate=true;
                var invalid=await Terminal(api,Success(await api.Storage.Migrate(new(current,1,2,new("migrate:transform")),Options())));
                Check(invalid.Failure?.Code==AbiErrors.SCHEMA_INVALID);
                Check(TextResult.Read(Success(await api.Storage.Read(new(current,"name"),Options())).Value).Text=="original");
                entry.Duplicate=false;entry.Block=true;
                var racing=Success(await api.Storage.Migrate(new(current,1,2,new("migrate:transform")),Options()));
                await entry.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Success(await api.Storage.Write(new(current,"concurrent",TextResult.Write(new("preserved")),Abi100Schemas.TextResult,null),Options()));
                entry.Release.SetResult();Check((await Terminal(api,racing)).Failure?.Code==AbiErrors.STALE_REVISION);
                entry.Block=false;current=Success(await api.Storage.OpenNamespace(new(new("migrate:data"),1),Options()));
                var transaction=Success(await api.Storage.BeginTransaction(current,Options()));
                var complete=await Terminal(api,Success(await api.Storage.Migrate(new(current,1,2,new("migrate:transform")),Options())));
                Check(complete.State==OperationState.Completed);
                var migrated=StorageNamespace.Read(complete.Result);
                Check(TextResult.Read(Success(await api.Storage.Read(new(migrated,"name"),Options())).Value).Text=="original-v2");
                Check(!(await api.Storage.Read(new(current,"name"),Options())).Success);
                Check(!(await api.Storage.Commit(new(transaction,[]),Options())).Success);
                Check(!(await api.Storage.OpenNamespace(new(new("migrate:data"),1),Options())).Success);
            }
            await using(var restarted=new PluginHostRuntime(root,_=>true,Abi100Schemas.Validate))
            {
                var entry=new Entry();Check((await restarted.LoadAsync([entry.Source()])).Single().State==LoadState.Active);
                using var api=entry.Context!.AsAbi100();
                var migrated=Success(await api.Storage.OpenNamespace(new(new("migrate:data"),2),Options()));
                Check(TextResult.Read(Success(await api.Storage.Read(new(migrated,"name"),Options())).Value).Text=="original-v2");
                Check(TextResult.Read(Success(await api.Storage.Read(new(migrated,"concurrent"),Options())).Value).Text=="preserved-v2");
            }
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
        Console.WriteLine("PASS: typed storage migration, atomic version commit, stale and concurrent writer rejection, invalid transform rollback, retired handles and restart persistence.");
    }
    private sealed class Entry:IPluginBootstrapV1
    {
        internal ABIContext? Context;internal int Calls;internal bool Duplicate,Block;private ulong _handler;
        internal TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static readonly string[] Groups=["Actions","Storage","Tasks"];
        private static readonly ImmutableArray<string> Permissions=Abi100Catalog.Operations.Where(o=>Groups.Contains(o.Group)).Select(o=>o.Permission).Where(p=>p.Length>0).Distinct().ToImmutableArray();
        internal PluginLoadSource Source()=>new(new(1,"migrate","1.0.0",1,new("test","test"),new([],[],"fixture"),[100],[],[],[],Permissions,[]),Permissions,_=>ValueTask.FromResult<IPluginBootstrapV1>(this),()=>ValueTask.CompletedTask);
        public async ValueTask<AbiReply<PreInitResult>> OnPreInitialize(PreInitContext context)
        {
            _handler=context.Callbacks.Register("transform",async(input,options)=>
            {
                Calls++;var request=StorageMigrationInput.Read(ActionRequest.Read(input).Inputs);
                if(Block){Entered.SetResult();await Release.Task.WaitAsync(options.Cancellation);}
                var entries=request.Entries.Select(e=>e with{Value=e.Value with{Value=TextResult.Write(new(TextResult.Read(e.Value.Value).Text+"-v2"))}}).ToImmutableArray();
                if(Duplicate)entries=entries.Add(entries[0]);
                return AbiReply<AbiValue>.Ok(StorageMigrationOutput.Write(new(entries)));
            }).Value;
            var draft=new Abi100Draft(Success(await context.Negotiator.GetABIContext(100,Options())));
            foreach(var contract in Abi100Catalog.Operations.Where(o=>Groups.Contains(o.Group)).Select(o=>o.ContractId).Distinct())draft=draft.Require(contract);
            Context=Success(await draft.Make(context.Negotiator,Guid.NewGuid(),Permissions,default));return AbiReply<PreInitResult>.Ok(new(Context,[],[],[],[]));
        }
        public async ValueTask<AbiReply<Unit>> OnInitialize(ABIContext context,StageContext stage)
        {
            using var api=context.ForStage100(stage);
            Success(await api.Actions.Register(new(new("migrate:transform"),Abi100Schemas.StorageMigrationInput,Abi100Schemas.StorageMigrationOutput,[],[],IdempotencyMode.ReadOnly,true,false,ConfirmationRequirement.None,null,new("transform",[]),_handler),Options()));
            return AbiReply<Unit>.Ok(new());
        }
        public ValueTask<AbiReply<Unit>> OnPostInitialize(ABIContext context,StageContext stage)=>Ok();
        public ValueTask<AbiReply<CheckupResult>> OnCheckup(ABIContext context,StageContext stage)=>ValueTask.FromResult(AbiReply<CheckupResult>.Ok(new(CheckupStatus.Passed,[],[])));
        public ValueTask<AbiReply<Unit>> OnFinalize(ABIContext context,StageContext stage)=>Ok();
        public ValueTask<AbiReply<Unit>> OnActivated(ABIContext context,InvocationContext invocation)=>Ok();
        public ValueTask<AbiReply<Unit>> OnStop(StopContext context)=>Ok();
        public ValueTask<AbiReply<Unit>> OnAbort(AbortContext context)=>Ok();
        private static ValueTask<AbiReply<Unit>> Ok()=>ValueTask.FromResult(AbiReply<Unit>.Ok(new()));
    }
}
