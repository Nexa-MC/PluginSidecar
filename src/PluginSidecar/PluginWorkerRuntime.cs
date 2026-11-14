using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;
using PluginSidecar.WasmRuntime;

namespace PluginSidecar;

/// <summary>Internal session composition; no CLR runtime object crosses the wire.</summary>
internal interface IPluginWorkerRuntime : IAsyncDisposable
{
    SidecarFeatures Features { get; }
    IReadOnlyList<SidecarRegistrationItem> Registrations { get; }
    ValueTask ActivateAsync(SidecarHostRequestClient requests, CancellationToken token);
    ValueTask<byte[]> ExecuteAsync(uint contract, SidecarBinaryValue input, CancellationToken token);
}

internal sealed class RemoteExecutionRuntime : IPluginWorkerRuntime
{
    private readonly GuestExecutionServer _execution = new();
    private readonly ChunkedPluginEndpoint _endpoint;
    internal RemoteExecutionRuntime() => _endpoint = new(_execution);
    public SidecarFeatures Features => SidecarFeatures.BinaryPayloads | SidecarFeatures.Health | SidecarFeatures.Unregistration;
    public IReadOnlyList<SidecarRegistrationItem> Registrations { get; } =
    [new(SidecarRegistrationKind.Command, RemoteWasmPluginMachine.ExecutionContract, 0, SidecarWireCodecs.GeneratedDto)
        { ResultCodecId = SidecarWireCodecs.GeneratedDto }];
    public ValueTask ActivateAsync(SidecarHostRequestClient requests, CancellationToken token) => ValueTask.CompletedTask;
    public ValueTask<byte[]> ExecuteAsync(uint contract, SidecarBinaryValue input, CancellationToken token)
    {
        if (contract != 1 || input.CodecId != SidecarWireCodecs.GeneratedDto) throw new InvalidDataException();
        return _endpoint.ExchangeAsync(input.ToArray(), token);
    }
    public async ValueTask DisposeAsync()
    {
        try { await _execution.DisposeAsync().ConfigureAwait(false); }
        finally { _endpoint.Dispose(); }
    }
}
