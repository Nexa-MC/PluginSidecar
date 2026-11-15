using System.Collections.Concurrent;
using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;

namespace PluginSidecar;

internal sealed class PluginExecutionPeer(SidecarFrameTransport wire, IPluginWorkerRuntime runtime)
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _calls = new();
    private readonly List<Task> _workers = [];
    private readonly TaskCompletionSource<Exception> _failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _active, _retired, _unregistering;
    private Task? _retirement;
    private Task _activation = Task.CompletedTask;
    private SidecarFeatures _features;
    internal static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    internal async Task RunAsync(CancellationToken cancellation)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        using var active = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        using var requests = new SidecarHostRequestClient(wire.SendAsync);
        Task<SidecarFrame>? receiving = null;
        try
        {
            using (var startup = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                startup.CancelAfter(TimeSpan.FromSeconds(15));
                await Register(startup.Token).ConfigureAwait(false);
            }
            while (!lifetime.IsCancellationRequested)
            {
                receiving = wire.ReceiveAsync(lifetime.Token).AsTask();
                if (await Task.WhenAny(receiving, _failure.Task).ConfigureAwait(false) == _failure.Task)
                    throw await _failure.Task.ConfigureAwait(false);
                var frame = await receiving.ConfigureAwait(false);
                receiving = null;
                _workers.RemoveAll(task => task.IsCompletedSuccessfully);
                switch (frame.MessageType)
                {
                    case SidecarMessageType.Activate:
                        if (_active || _retired) throw new InvalidDataException("Duplicate activation.");
                        _active = true;
                        if (_features.HasFlag(SidecarFeatures.HostRequests)) requests.Activate(_features);
                        _activation = Start(async () =>
                        {
                            try { await runtime.ActivateAsync(requests, active.Token).ConfigureAwait(false); }
                            catch (OperationCanceledException) when (active.IsCancellationRequested) { }
                        });
                        if (runtime is IPluginWorkerBackground background)
                            _ = Start(async () =>
                            {
                                try
                                {
                                    await _activation.ConfigureAwait(false);
                                    await background.Completion.WaitAsync(active.Token).ConfigureAwait(false);
                                    if (!active.IsCancellationRequested) throw new IOException("Worker background loop ended before retirement.");
                                }
                                catch (OperationCanceledException) when (active.IsCancellationRequested) { }
                            });
                        break;
                    case SidecarMessageType.Deactivate:
                        _ = Retire(active);
                        break;
                    case SidecarMessageType.HealthPing:
                        if (!_features.HasFlag(SidecarFeatures.Health)) throw new InvalidDataException("Health was not negotiated.");
                        await Send(SidecarMessageType.HealthPong, frame.Payload, frame.CorrelationId, lifetime.Token).ConfigureAwait(false);
                        break;
                    case SidecarMessageType.Cancel:
                        var cancelled = SidecarControlMessages.DecodeCancel(frame.Payload.Span);
                        if (_calls.TryGetValue(cancelled.CorrelationId, out var pending) && !pending.IsCancellationRequested)
                        {
                            Task cancelling = Cancel(pending);
                            _ = Start(() => cancelling);
                        }
                        break;
                    case SidecarMessageType.CommandRequest:
                    case SidecarMessageType.QueryRequest:
                        if (!_active || !frame.CorrelationId.IsAssigned || _calls.Count >= 32)
                            throw new InvalidDataException("Execution request not admitted.");
                        var stop = CancellationTokenSource.CreateLinkedTokenSource(active.Token);
                        stop.CancelAfter(TimeSpan.FromMinutes(5));
                        if (!_calls.TryAdd(frame.CorrelationId.Value, stop))
                        { stop.Dispose(); throw new InvalidDataException("Duplicate correlation."); }
                        _ = Start(() => Execute(frame, stop, lifetime.Token));
                        break;
                    case SidecarMessageType.CommandResult:
                    case SidecarMessageType.QueryResult:
                        if (!_features.HasFlag(SidecarFeatures.HostRequests)) throw new InvalidDataException("Host requests were not negotiated.");
                        // Unknown and late correlations do not create a new request or replay an effect.
                        requests.TryAcceptReply(frame);
                        break;
                    case SidecarMessageType.Unregister:
                        if (!_features.HasFlag(SidecarFeatures.Unregistration) || _unregistering || !frame.CorrelationId.IsAssigned)
                            throw new InvalidDataException("Unregistration not admitted.");
                        _ = SidecarControlMessages.DecodeUnregister(frame.Payload.Span);
                        _unregistering = true;
                        Task retirement = Retire(active);
                        _ = Start(async () =>
                        {
                            await retirement.ConfigureAwait(false);
                            try
                            {
                                await Send(SidecarMessageType.Unregistered, SidecarControlMessages.EncodeUnregister("Retired"),
                                    frame.CorrelationId, lifetime.Token).ConfigureAwait(false);
                            }
                            catch (Exception error) when (lifetime.IsCancellationRequested && error is IOException or OperationCanceledException) { }
                        });
                        break;
                    case SidecarMessageType.Shutdown:
                        return;
                    default:
                        throw new InvalidDataException("Unexpected Host frame.");
                }
            }
        }
        finally
        {
            _active = false;
            // No reader remains after this point. Release reply waiters before draining the runtime.
            requests.Dispose();
            Task cancel = lifetime.CancelAsync();
            Task retirement = Retire(active);
            if (receiving is not null)
            {
                try { await receiving.ConfigureAwait(false); }
                catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException) { }
            }
            await Task.WhenAll(cancel, retirement, Task.WhenAll(_workers)).WaitAsync(DrainTimeout).ConfigureAwait(false);
        }
    }

    private Task Start(Func<Task> work)
    {
        // Even synchronous initialization/cleanup and cancellation callbacks must leave the reader free.
        Task task = Task.Run(async () =>
        {
            try { await work().ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
            { _failure.TrySetResult(error); throw; }
        });
        _workers.Add(task);
        return task;
    }

    private Task Retire(CancellationTokenSource active)
    {
        _active = false;
        _retired = true;
        if (_retirement is not null) return _retirement;
        Task[] admitted = [.. _workers];
        _retirement = Start(async () =>
        {
            Task drain = Task.Run(Drain);
            // Observe eventual failure even when a broken runtime exceeds the local limit.
            _ = ObserveLate(drain);
            await drain.WaitAsync(DrainTimeout).ConfigureAwait(false);
            async Task Drain()
            {
                try
                {
                    await active.CancelAsync().ConfigureAwait(false);
                    await Task.WhenAll(admitted).ConfigureAwait(false);
                }
                finally { await runtime.DisposeAsync().ConfigureAwait(false); }
            }
        });
        return _retirement;
    }

    private static async Task ObserveLate(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException) { }
    }

    private async Task Register(CancellationToken token)
    {
        var hello = await wire.ReceiveAsync(token).ConfigureAwait(false);
        if (hello.MessageType != SidecarMessageType.Hello) throw new InvalidDataException();
        var offered = SidecarHandshake.DecodeHelloDetails(hello.Payload.Span);
        if (offered.ProtocolVersion != SidecarProtocol.Version || !offered.Features.HasFlag(SidecarFeatures.BinaryPayloads))
            throw new InvalidDataException();
        _features = offered.Features & runtime.Features;
        if (!_features.HasFlag(SidecarFeatures.BinaryPayloads)) throw new InvalidDataException("Binary execution is required.");
        await Send(SidecarMessageType.Welcome, SidecarHandshake.EncodeWelcome(SidecarProtocol.Version, Guid.NewGuid(), null, _features), hello.CorrelationId, token).ConfigureAwait(false);
        var registrations = runtime.Registrations;
        await Send(SidecarMessageType.RegisterBegin, SidecarRegistration.EncodeBegin(checked((uint)registrations.Count)), SidecarCorrelationId.Create(), token).ConfigureAwait(false);
        foreach (var item in registrations)
            await Send(SidecarMessageType.RegisterItem, SidecarRegistration.EncodeItem(item), SidecarCorrelationId.Create(), token).ConfigureAwait(false);
        await Send(SidecarMessageType.RegisterEnd, SidecarRegistration.EncodeEnd(), SidecarCorrelationId.Create(), token).ConfigureAwait(false);
        await Send(SidecarMessageType.StateSnapshotBegin, SidecarStateSnapshot.EncodeBegin(0), SidecarCorrelationId.Create(), token).ConfigureAwait(false);
        await Send(SidecarMessageType.StateSnapshotEnd, SidecarStateSnapshot.EncodeEnd(), SidecarCorrelationId.Create(), token).ConfigureAwait(false);
        if ((await wire.ReceiveAsync(token).ConfigureAwait(false)).MessageType != SidecarMessageType.Ready)
            throw new InvalidDataException("Host rejected registration.");
    }

    private async Task Execute(SidecarFrame frame, CancellationTokenSource stop, CancellationToken lifetime)
    {
        try
        {
            byte[] bytes = [];
            string? error = null;
            try
            {
                await _activation.WaitAsync(stop.Token).ConfigureAwait(false);
                stop.Token.ThrowIfCancellationRequested();
                var input = SidecarDataMessages.DecodeBinaryRequestDetails(frame.Payload.Span);
                if (!input.IsBinary || input.Value.CodecId != SidecarWireCodecs.GeneratedDto
                    || input.Value.Length > SidecarWireCodecs.MaximumValueLength && !_features.HasFlag(SidecarFeatures.LargeGeneratedDtos))
                    throw new InvalidDataException();
                bytes = await (frame.MessageType == SidecarMessageType.QueryRequest
                    ? runtime.QueryAsync(input.ContractId, input.Value, stop.Token)
                    : runtime.ExecuteAsync(input.ContractId, input.Value, stop.Token)).ConfigureAwait(false);
                if (bytes.Length > SidecarWireCodecs.MaximumValueLength && !_features.HasFlag(SidecarFeatures.LargeGeneratedDtos))
                { bytes = []; throw new InvalidDataException("Large replies were not negotiated."); }
            }
            catch (OperationCanceledException) { error = "plugin.execution.cancelled"; }
            catch (Exception failure) when (failure is not OutOfMemoryException and not AccessViolationException)
            { error = "plugin.execution.failed"; }
            await Send(frame.MessageType == SidecarMessageType.QueryRequest ? SidecarMessageType.QueryResult : SidecarMessageType.CommandResult, SidecarDataMessages.EncodeBinaryResult(error is null,
                new(SidecarWireCodecs.GeneratedDto, bytes), error), frame.CorrelationId, lifetime).ConfigureAwait(false);
        }
        catch (Exception error) when (lifetime.IsCancellationRequested && error is IOException or OperationCanceledException) { }
        finally { _calls.TryRemove(frame.CorrelationId.Value, out _); stop.Dispose(); }
    }

    private ValueTask Send(SidecarMessageType type, ReadOnlyMemory<byte> bytes, SidecarCorrelationId correlation, CancellationToken token)
        => wire.SendAsync(new(SidecarProtocol.Version, type, SidecarFrameTraits.None, correlation, bytes), token);
    private static async Task Cancel(CancellationTokenSource source)
    {
        try { await source.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
    }
}
