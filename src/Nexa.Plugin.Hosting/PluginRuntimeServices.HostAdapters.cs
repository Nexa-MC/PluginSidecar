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

    /// <summary>Invokes a callback registered by the admitted plugin, retaining its original capability context.</summary>
    public ValueTask<AbiReply<AbiValue>> DispatchHostCallback(PluginIdentity owner, string permission, ulong handler,
        AbiValue input, InvocationOptions options)
    {
        if (!_authorize(owner, permission)) throw new AbiHostException(AbiErrors.PERMISSION_DENIED);
        return _dispatch(owner, handler, input, options);
    }
}
