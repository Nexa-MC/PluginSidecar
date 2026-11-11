using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100.Data;
namespace Nexa.Plugin.Hosting;

public sealed partial class PluginLoadCoordinator
{
    private static readonly AsyncLocal<ImmutableStack<Guid>?> CallbackChain = new();

    internal async ValueTask<AbiReply<AbiValue>> InvokeHandler(PluginIdentity owner, ulong handle, AbiValue input, InvocationOptions options)
    {
        Attempt? target;
        lock (authority.PublicationGate) target = _active.GetValueOrDefault(owner.PluginId);
        if (target?.Identity != owner || target.State != LoadState.Active) return Failure(AbiErrors.PLUGIN_STOPPED);
        if (handle == 0) return Failure(AbiErrors.HANDLE_INVALID);
        var chain = CallbackChain.Value ?? ImmutableStack<Guid>.Empty;
        if (chain.Contains(owner.LoadInstanceId) || chain.Count() >= 16) return Failure(AbiErrors.CONFLICT);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(options.Cancellation, target.Invocations.Token);
        timeout.CancelAfter(options.Budget);
        var cancellation=timeout.Token;
        CallbackChain.Value = chain.Push(owner.LoadInstanceId);
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            DataCodec.Validate(input);
            var pending=Task.Run(async()=>
            {
                cancellation.ThrowIfCancellationRequested();
                if(target.Entry is IPluginInvocationV1 entry)
                    return await entry.InvokeHandler(target.Final!,handle,input,new(owner,options.OperationId,options.CorrelationId,options.CausationId,cancellation,options.Budget)).ConfigureAwait(false);
                Func<AbiValue, InvocationOptions, ValueTask<AbiReply<AbiValue>>>? handler;
                lock (target.Callbacks) target.Callbacks.TryGetValue(handle, out handler);
                if (handler is null) return Failure(AbiErrors.HANDLE_INVALID);
                return await handler(input,options with{Cancellation=cancellation}).ConfigureAwait(false);
            },cancellation);
            _ = Observe(pending);
            var reply = await pending.WaitAsync(timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            if (reply.Success) DataCodec.Validate(reply.Value);
            return reply;
        }
        catch (OperationCanceledException) { return Failure(target.Invocations.IsCancellationRequested ? AbiErrors.PLUGIN_STOPPED : options.Cancellation.IsCancellationRequested ? AbiErrors.CANCELLED : AbiErrors.DEADLINE_EXCEEDED); }
        catch (Exception error) when (Recoverable(error)) { return Failure(AbiErrors.PLUGIN_EXCEPTION); }
        finally { CallbackChain.Value = chain; }
        AbiReply<AbiValue> Failure(string code) => AbiReply<AbiValue>.Fail(new(code, code, "Active", "plugin.handler", owner.PluginId, owner.LoadInstanceId,
            options.CorrelationId, false, ImmutableDictionary<string,string>.Empty) { OperationId = options.OperationId });
    }
}
