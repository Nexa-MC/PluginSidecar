using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100.Data;
namespace Nexa.Plugin.Hosting;

public sealed partial class PluginLoadCoordinator
{
    internal async ValueTask<AbiReply<AbiValue>> InvokeProbe(HostInvocation caller,PluginIdentity owner,ulong handle,AbiValue input,InvocationOptions options,bool allowOwnRoot=false)
    {
        Attempt? target;
        lock(authority.PublicationGate)
        {
            target=_instances.GetValueOrDefault(owner.LoadInstanceId);
            if(target?.Identity!=owner||target.Final is null||target.Invocations.IsCancellationRequested||
                target.State!=LoadState.Active&&(target.GroupId!=caller.GroupId||!target.Completed.Contains(LoadStage.PostInitialize)))
                return Failure(AbiErrors.SERVICE_NOT_READY);
        }
        var chain=CallbackChain.Value??ImmutableStack<Guid>.Empty;
        // A Host-owned migration may enter its owner once; nested callbacks still cannot cycle.
        if(chain.IsEmpty&&(!allowOwnRoot||caller.Identity!=owner))chain=chain.Push(caller.Identity.LoadInstanceId);
        if(chain.Contains(owner.LoadInstanceId)||chain.Count()>=16)return Failure(AbiErrors.CONFLICT);
        if(handle==0)return Failure(AbiErrors.HANDLE_INVALID);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(options.Cancellation,target.Invocations.Token);
        timeout.CancelAfter(options.Budget);var token=timeout.Token;
        using var scope=new ProbeExecution(authority,owner,token);
        scope.Enter();var previous=CallbackChain.Value;CallbackChain.Value=chain.Push(owner.LoadInstanceId);
        try
        {
            token.ThrowIfCancellationRequested();DataCodec.Validate(input);
            var pending=Task.Run(async()=>
            {
                token.ThrowIfCancellationRequested();
                if(target.Entry is IPluginProbeInvocationV1 adapter)
                    return await adapter.InvokeProbe(target.Final,handle,input,new(owner,options.OperationId,options.CorrelationId,options.CausationId,token,options.Budget)).ConfigureAwait(false);
                Func<AbiValue,InvocationOptions,ValueTask<AbiReply<AbiValue>>>? callback;
                lock(target.Callbacks)target.Callbacks.TryGetValue(handle,out callback);
                return callback is null?Failure(AbiErrors.HANDLE_INVALID):await callback(input,options with{Cancellation=token,StageInvocationId=Guid.Empty}).ConfigureAwait(false);
            },token);
            _=Observe(pending);
            var reply=await pending.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if(reply.Success)DataCodec.Validate(reply.Value);
            return reply;
        }
        catch(OperationCanceledException){return Failure(options.Cancellation.IsCancellationRequested?AbiErrors.CANCELLED:AbiErrors.DEADLINE_EXCEEDED);}
        catch(Exception error)when(Recoverable(error)){return Failure(AbiErrors.PLUGIN_EXCEPTION);}
        finally{CallbackChain.Value=previous;}
        AbiReply<AbiValue> Failure(string code)=>AbiReply<AbiValue>.Fail(new(code,code,"Checkup","service.probe",owner.PluginId,owner.LoadInstanceId,options.CorrelationId,false,ImmutableDictionary<string,string>.Empty){OperationId=options.OperationId});
    }
}
