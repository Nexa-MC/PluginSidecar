using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100.Data;
namespace Nexa.Plugin.Hosting;

public sealed partial class PluginRuntimeServices
{
    internal Func<HostInvocation,Func<CancellationToken,ValueTask>,RegistrationRef> RegisterHostCleanupCore = null!;
    /// <summary>Registers an owned Host resource for explicit release and automatic load cleanup.</summary>
    public RegistrationRef RegisterHostCleanup(HostInvocation caller, string permission, Func<CancellationToken,ValueTask> cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        if (!_authorize(caller.Identity, permission)) throw new AbiHostException(AbiErrors.PERMISSION_DENIED);
        return RegisterHostCleanupCore(caller, cleanup);
    }
    /// <summary>Releases an owned Host resource through the same cleanup ledger as Resources.Release.</summary>
    public async ValueTask ReleaseHostCleanup(HostInvocation caller, string permission, RegistrationRef reference)
    {
        if (!_authorize(caller.Identity, permission)) throw new AbiHostException(AbiErrors.PERMISSION_DENIED);
        if (!await ReleaseCleanup(caller, reference).ConfigureAwait(false)) throw new AbiHostException(AbiErrors.HANDLE_EXPIRED);
    }

    /// <summary>Trusted Host route only. The outer capability handler already checked this method's permission.</summary>
    public SubscriptionRef CreateHostSubscription(HostInvocation caller, string contract, string permission,
        AbiValue initial, int limit = 256)
    {
        if (!_capabilities(caller, [contract])) throw new AbiHostException(AbiErrors.CAPABILITY_DENIED);
        lock (_gate)
        {
            var reference = Subscribe(caller, "host", permission, limit);
            Enqueue(_subscriptions[reference.Token], initial);
            return reference;
        }
    }

    /// <summary>Publishes only to the original active owner. False means retired or released.</summary>
    public bool PublishHostSubscription(PluginIdentity owner, SubscriptionRef reference, AbiValue snapshot)
    {
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(reference.Token, out var sub)) return false;
            if (sub.Owner != owner || reference.LoadInstanceId != owner.LoadInstanceId || sub.Kind != "host")
                throw new AbiHostException(AbiErrors.HANDLE_OWNER_MISMATCH);
            if (!_authorize(owner, sub.Key) || sub.Closed) return false;
            Enqueue(sub, snapshot); return true;
        }
    }

    /// <summary>Runs an authorized application operation in the existing Tasks ledger and cancellation lifetime.</summary>
    public OperationRef StartHostOperation(HostInvocation caller, string permission, SchemaRef resultSchema,
        Func<CancellationToken, ValueTask<AbiReply<AbiValue>>> operation)
    {
        if (!_authorize(caller.Identity, permission)) throw new AbiHostException(AbiErrors.PERMISSION_DENIED);
        caller.Options.Cancellation.ThrowIfCancellationRequested();
        return StartOperation(caller, resultSchema, operation);
    }

    /// <summary>Returns an owned, published workflow for a Host-admitted template plan.</summary>
    public WorkflowDefinition HostWorkflow(HostInvocation caller, QualifiedId id, SchemaRef input, SchemaRef output)
    {
        Own(caller,id); var entry=Find(caller,"workflow",id);
        if(entry.Owner!=caller.Identity||entry.Registration.State!=RegistrationState.Active)throw new AbiHostException(AbiErrors.SERVICE_NOT_READY);
        var definition=WorkflowDefinition.Read(entry.Value);
        if(definition.Input!=input||definition.Output!=output)throw new AbiHostException(AbiErrors.SERVICE_CONTRACT_MISMATCH);
        return definition;
    }
    /// <summary>Executes a template workflow with the same owned Tasks ledger and effect journal as Workflows.Start.</summary>
    public async ValueTask<AbiValue> ExecuteHostWorkflow(HostInvocation caller, string permission, WorkflowRequest request)
    {
        if(!_authorize(caller.Identity,permission))throw new AbiHostException(AbiErrors.PERMISSION_DENIED);
        Own(caller,request.Workflow);caller.Options.Cancellation.ThrowIfCancellationRequested();
        var reference=StartWorkflow(caller,request);var operation=GetOperation(caller,reference.Id);
        using var cancellation=caller.Options.Cancellation.Register(()=>Cancel(caller,new(reference.Id)));
        await operation.Worker!.ConfigureAwait(false);
        if(operation.Record.State!=OperationState.Completed)throw new AbiHostException(operation.Record.State==OperationState.OutcomeUnknown?AbiErrors.OUTCOME_UNKNOWN:operation.Record.Failure?.Code??AbiErrors.CANCELLED);
        return operation.Record.Result;
    }

    /// <summary>Invokes a pure callback while checking its published definition before and after execution.</summary>
    public async ValueTask<AbiValue> QueryDefinitionCallback(HostInvocation caller, PublishedDefinition definition, string permission, ulong handler, AbiValue input, SchemaRef output)
    {
        void CheckPublished()
        {
            if (ContentRegistry is null || !ContentRegistry.Entries.Any(e => e.Owner == definition.Owner && e.Registration == definition.Registration && e.Revision == definition.Revision)) throw new AbiHostException(AbiErrors.PROVIDER_GONE);
            if (!_authorize(definition.Owner, permission)) throw new AbiHostException(AbiErrors.PERMISSION_DENIED);
        }
        CheckPublished();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(caller.Options.Cancellation, Lifetime(caller.Identity), Lifetime(definition.Owner));
        lifetime.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(caller.Options.Budget.TotalMilliseconds, 30000)));
        var reply = await (caller.Identity == definition.Owner ? DispatchOwnProbe : DispatchProbe)(caller, definition.Owner, handler, input, caller.Options with { Cancellation = lifetime.Token }).ConfigureAwait(false);
        lifetime.Token.ThrowIfCancellationRequested(); CheckPublished();
        if (!reply.Success) throw new AbiHostException(reply.Error!.Code);
        Validate(output, reply.Value); return reply.Value;
    }

    /// <summary>Runs an owner's pure transformation without allowing mutation reentrancy.</summary>
    public async ValueTask<AbiValue> QueryOwnHostCallback(HostInvocation caller, string permission, ulong handler, AbiValue input, SchemaRef output)
    {
        if (!_authorize(caller.Identity, permission)) throw new AbiHostException(AbiErrors.PERMISSION_DENIED);
        if (handler == 0) throw new AbiHostException(AbiErrors.HANDLE_INVALID);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(caller.Options.Cancellation, Lifetime(caller.Identity));
        lifetime.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(caller.Options.Budget.TotalMilliseconds, 30000)));
        var reply = await DispatchOwnProbe(caller, caller.Identity, handler, input, caller.Options with { Cancellation = lifetime.Token }).ConfigureAwait(false);
        lifetime.Token.ThrowIfCancellationRequested();
        if (!reply.Success) throw new AbiHostException(reply.Error!.Code);
        Validate(output, reply.Value); return reply.Value;
    }

    /// <summary>Invokes a callback registered by the admitted plugin, retaining its original capability context.</summary>
    public ValueTask<AbiReply<AbiValue>> DispatchHostCallback(PluginIdentity owner, string permission, ulong handler,
        AbiValue input, InvocationOptions options)
    {
        if (!_authorize(owner, permission)) throw new AbiHostException(AbiErrors.PERMISSION_DENIED);
        return _dispatch(owner, handler, input, options);
    }
}
