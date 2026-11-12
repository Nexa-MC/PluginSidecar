using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100.Data;
namespace Nexa.Plugin.Hosting;

public sealed partial class PluginLoadCoordinator
{
    private sealed record HostCleanup(RegistrationRef Reference, Func<CancellationToken,ValueTask> Release);
    internal RegistrationRef RegisterHostCleanup(HostInvocation caller, Func<CancellationToken,ValueTask> cleanup)
    {
        lock(authority.PublicationGate)
        {
            caller.Options.Cancellation.ThrowIfCancellationRequested();
            if(!_instances.TryGetValue(caller.Identity.LoadInstanceId,out var member)||member.Identity!=caller.Identity)throw new AbiHostException(AbiErrors.HANDLE_OWNER_MISMATCH);
            lock(member.Callbacks)
            {
                if(member.Invocations.IsCancellationRequested)throw new AbiHostException(AbiErrors.PLUGIN_STOPPED);
                if(member.HostCleanups.Count>=256)throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
                ulong handle=authority.AllocateRegistrationHandle();
                var reference=new RegistrationRef(handle,caller.Identity.PluginId,caller.Identity.LoadInstanceId,new(caller.Identity.PluginId+":host-resource."+handle),RegistrationState.Active);
                member.HostCleanups.Add(handle,new(reference,cleanup));return reference;
            }
        }
    }
    private static async ValueTask<AbiReply<AbiValue>> InvokeHostCleanup(Attempt member,HostCleanup cleanup,CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var pending=Task.Run(async()=>await cleanup.Release(token).ConfigureAwait(false),token);
            _=Observe(pending);await pending.WaitAsync(token).ConfigureAwait(false);return AbiReply<AbiValue>.Ok(Empty.Write(new()));
        }
        catch(Exception error)when(Recoverable(error))
        {return AbiReply<AbiValue>.Fail(Error(member,error is OperationCanceledException?AbiErrors.OUTCOME_UNKNOWN:AbiErrors.PLUGIN_EXCEPTION));}
    }
    private static RegistrationRef RegisterCleanupCore(Attempt member,CapabilityAuthority authority,ulong callback)
    {
        lock(authority.PublicationGate)
        lock(member.Callbacks)
        {
            if(member.Invocations.IsCancellationRequested)throw new AbiHostException(AbiErrors.PLUGIN_STOPPED);
            if(callback==0||!member.Callbacks.ContainsKey(callback)&&member.Entry is not IPluginCleanupInvocationV1)throw new AbiHostException(AbiErrors.HANDLE_INVALID);
            if(member.CleanupRegistrations.TryGetValue(callback,out var old))return old;
            if(member.CleanupCallbacks.Count>=256)throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
            var reference=new RegistrationRef(authority.AllocateRegistrationHandle(),member.Identity.PluginId,member.Identity.LoadInstanceId,
                new(member.Identity.PluginId+":cleanup."+callback),RegistrationState.Staged);
            member.CleanupCallbacks.Add(new(callback));member.CleanupRegistrations.Add(callback,reference);return reference;
        }
    }
    internal RegistrationRef RegisterCleanup(HostInvocation caller,ulong callback)
    {
        lock(authority.PublicationGate)
        {
            caller.Options.Cancellation.ThrowIfCancellationRequested();
            if(!_instances.TryGetValue(caller.Identity.LoadInstanceId,out var member)||member.Identity!=caller.Identity)throw new AbiHostException(AbiErrors.HANDLE_OWNER_MISMATCH);
            return RegisterCleanupCore(member,authority,callback);
        }
    }
    internal async ValueTask<bool> ReleaseCleanup(HostInvocation caller,RegistrationRef reference)
    {
        Attempt member;ulong callback=0;HostCleanup? hostCleanup=null;
        lock(authority.PublicationGate)
        {
            if(!_instances.TryGetValue(caller.Identity.LoadInstanceId,out member!)||member.Identity!=caller.Identity)throw new AbiHostException(AbiErrors.HANDLE_OWNER_MISMATCH);
            lock(member.Callbacks)
            {
                if(member.HostCleanups.TryGetValue(reference.Handle,out hostCleanup))
                {
                    if(hostCleanup.Reference!=reference)throw new AbiHostException(AbiErrors.HANDLE_OWNER_MISMATCH);
                    member.HostCleanups.Remove(reference.Handle);
                }
                else
                {
                var item=member.CleanupRegistrations.SingleOrDefault(p=>p.Value.Handle==reference.Handle);
                if(item.Value is null)return false;
                if(item.Value.Owner!=reference.Owner||item.Value.LoadInstanceId!=reference.LoadInstanceId||item.Value.DefinitionId!=reference.DefinitionId)throw new AbiHostException(AbiErrors.HANDLE_OWNER_MISMATCH);
                callback=item.Key;member.CleanupRegistrations.Remove(callback);member.CleanupCallbacks.Remove(new(callback));
                }
            }
        }
        var result=hostCleanup is null?await InvokeCleanup(member,callback,caller.Phase,caller.Options).ConfigureAwait(false):await InvokeHostCleanup(member,hostCleanup,caller.Options.Cancellation).ConfigureAwait(false);
        if(!result.Success)throw new AbiHostException(result.Error!.Code);return true;
    }
    private static async ValueTask<AbiReply<AbiValue>> InvokeCleanup(Attempt member,ulong handle,string phase,InvocationOptions options)
    {
        try
        {
            Func<AbiValue,InvocationOptions,ValueTask<AbiReply<AbiValue>>>? callback;
            lock(member.Callbacks)member.Callbacks.TryGetValue(handle,out callback);
            Task<AbiReply<AbiValue>> pending;
            if(callback is not null)pending=Task.Run(async()=>await callback(new AbiValue.Null(),options).ConfigureAwait(false),options.Cancellation);
            else if(member.Entry is IPluginCleanupInvocationV1 remote&&(member.Final??(member.Admission?.IsReady==true?member.Admission.Context:null)) is { } context)
                pending=Task.Run(async()=>await remote.InvokeCleanup(context,handle,phase,options).ConfigureAwait(false),options.Cancellation);
            else return AbiReply<AbiValue>.Fail(Error(member,AbiErrors.HANDLE_INVALID));
            _=Observe(pending);return await pending.WaitAsync(options.Cancellation).ConfigureAwait(false);
        }
        catch(Exception error)when(Recoverable(error))
        {return AbiReply<AbiValue>.Fail(Error(member,error is OperationCanceledException?AbiErrors.OUTCOME_UNKNOWN:AbiErrors.PLUGIN_EXCEPTION));}
    }
}
