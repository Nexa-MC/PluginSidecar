using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;
namespace Nexa.Plugin.Hosting;

/// <summary>A trusted Host method table; these descriptors never cross the worker boundary.</summary>
public sealed record BusinessProviderMethod(string Name,SchemaRef Input,SchemaRef Output);

public sealed partial class PluginRuntimeServices
{
    private sealed class ProviderAdmission(int slots)
    {
        internal readonly SemaphoreSlim Semaphore=new(slots,slots);
        internal int Calls;
    }
    private readonly Dictionary<ulong,ProviderAdmission> _providerAdmissions=[];

    /// <summary>Stages an authenticated registration. The outer Host capability route owns permission checks.</summary>
    public RegistrationRef RegisterHostProvider(HostInvocation caller,QualifiedId kind,ProviderDescriptor descriptor,
        IReadOnlyList<BusinessProviderMethod> methods)
    {
        if(caller.Phase!="Initialize")throw new AbiHostException(AbiErrors.STAGE_DENIED);
        if(descriptor.Kind!=kind||descriptor.ContractVersion!=100||descriptor.SchemaHash!=Abi100Schemas.BusinessProviderCall.Digest
            ||descriptor.Configuration!=Abi100Schemas.Empty||descriptor.Handler==0||descriptor.DeadlineMilliseconds is <1 or >30000
            ||descriptor.Selection!=SelectionPolicy.ExplicitRequest||descriptor.FailurePolicy==ProviderFailurePolicy.RetryBounded
            ||descriptor.Concurrency==ConcurrencyPolicy.ReplacePending||descriptor.SupportedScopes.Length!=1||descriptor.SupportedScopes[0]!="application"
            ||methods.Count is <1 or >32||methods.Select(m=>m.Name).Distinct(StringComparer.Ordinal).Count()!=methods.Count
            ||descriptor.Capabilities.Length is <1 or >32||descriptor.Capabilities.Distinct(StringComparer.Ordinal).Count()!=descriptor.Capabilities.Length
            ||descriptor.Capabilities.Any(name=>!methods.Any(m=>m.Name==name)))throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
        return Stage(caller,"business-provider/"+kind.Value,descriptor.Id,descriptor);
    }

    /// <summary>Lists only atomically published provider registrations, never a different load's staged entries.</summary>
    public ImmutableArray<ProviderDescriptor> HostProviders(QualifiedId kind)
    {
        lock(_gate)return _entries.Where(p=>p.Key.Item1=="business-provider/"+kind.Value&&p.Value.Registration.State==RegistrationState.Active)
            .OrderBy(p=>p.Key.Item2.Value,StringComparer.Ordinal).Select(p=>ProviderDescriptor.Read(p.Value.Value)).ToImmutableArray();
    }

    /// <summary>Calls a published read provider through the query-only callback boundary, retaining its load and deadline.</summary>
    public async ValueTask<AbiValue> QueryHostProvider(HostInvocation caller,QualifiedId kind,QualifiedId provider,
        BusinessProviderMethod method,AbiValue input)
    {
        Validate(method.Input,input);
        Entry entry;ProviderDescriptor descriptor;ProviderAdmission admission;CancellationToken providerLifetime,callerLifetime;
        lock(_gate)
        {
            entry=Find(caller,"business-provider/"+kind.Value,provider);
            if(entry.Registration.State!=RegistrationState.Active)throw new AbiHostException(AbiErrors.SERVICE_NOT_READY);
            descriptor=ProviderDescriptor.Read(entry.Value);
            if(!descriptor.Capabilities.Contains(method.Name))throw new AbiHostException(AbiErrors.CAPABILITY_UNAVAILABLE);
            if(!_providerAdmissions.TryGetValue(entry.Registration.Handle,out admission!))
                _providerAdmissions.Add(entry.Registration.Handle,admission=new(descriptor.Concurrency==ConcurrencyPolicy.Parallel?4:1));
            if(admission.Calls>=32||descriptor.Concurrency==ConcurrencyPolicy.Parallel&&admission.Calls>=4
                ||descriptor.Concurrency==ConcurrencyPolicy.RejectDuplicate&&admission.Calls!=0)throw new AbiHostException(AbiErrors.CONFLICT);
            HoldRegistration(entry);admission.Calls++;providerLifetime=Lifetime(entry.Owner);callerLifetime=Lifetime(caller.Identity);
        }
        bool entered=false;
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(caller.Options.Cancellation,providerLifetime,callerLifetime);
        var budget=TimeSpan.FromMilliseconds(Math.Min(caller.Options.Budget.TotalMilliseconds,descriptor.DeadlineMilliseconds));
        timeout.CancelAfter(budget);
        try
        {
            await admission.Semaphore.WaitAsync(timeout.Token).ConfigureAwait(false);entered=true;
            timeout.Token.ThrowIfCancellationRequested();
            var request=new BusinessProviderCall(provider,kind,method.Name,method.Input,method.Output,input);
            var reply=await DispatchProbe(caller,entry.Owner,descriptor.Handler,BusinessProviderCall.Write(request),caller.Options with
            {OperationId=Guid.NewGuid(),CausationId=caller.Options.OperationId,Budget=budget,Cancellation=timeout.Token}).ConfigureAwait(false);
            if(!reply.Success)throw new AbiHostException(descriptor.FailurePolicy==ProviderFailurePolicy.Unavailable
                ?AbiErrors.PROVIDER_UNAVAILABLE:reply.Error!.Code);
            timeout.Token.ThrowIfCancellationRequested();Validate(method.Output,reply.Value);DataCodec.Validate(reply.Value);return reply.Value;
        }
        catch(OperationCanceledException) when(!caller.Options.Cancellation.IsCancellationRequested&&!providerLifetime.IsCancellationRequested&&!callerLifetime.IsCancellationRequested)
        {throw new AbiHostException(AbiErrors.DEADLINE_EXCEEDED);}
        finally
        {
            lock(_gate)
            {
                if(entered)admission.Semaphore.Release();
                admission.Calls--;DropRegistration(entry);
                if(admission.Calls==0){_providerAdmissions.Remove(entry.Registration.Handle);admission.Semaphore.Dispose();}
            }
        }
    }
}
