using Nexa.Plugin.Bootstrap.V1;
namespace PluginSidecar.WasmRuntime;

/// <summary>Execution port. The embedding Host retains all capability authority.</summary>
public interface IWasmPluginMachine:IAsyncDisposable
{
    ValueTask<AbiValue> RunDetailedAsync(AbiValue invocation,
        Func<WasmHostCall,CancellationToken,ValueTask<AbiReply<AbiValue>>> invoke,
        TimeSpan budget,CancellationToken cancellation=default);
}

/// <summary>Authenticated, asynchronous binary exchange. Transport adapters must
/// bind this port to one admitted Sidecar session and never reconnect implicitly.</summary>
public interface IPluginBinaryExchange
{
    ValueTask<byte[]> ExchangeAsync(ReadOnlyMemory<byte> request,CancellationToken cancellation);
}
