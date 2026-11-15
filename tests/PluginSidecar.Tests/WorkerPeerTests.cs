using System.Net;
using System.Net.Sockets;
using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;
using PluginSidecar;

internal static class WorkerPeerTests
{
    internal static async Task Run()
    {
        await DuplexActivationAndRetirement();
        await CancelledCommand();
        await NativeQuery(false);
        await NativeQuery(true);
        await ShutdownWithoutReply();
        await FailureWithoutAnotherFrame();
        await BackgroundCompletion();
        await RejectAfterRetirement();
        await DrainDeadline();
        Console.WriteLine("PASS: worker single reader, independent command/query dispatch, bidirectional activation/retirement, cancellation, shutdown without reply, background failure, sealed admission and bounded single disposal.");
    }

    private static async Task DuplexActivationAndRetirement()
    {
        var runtime = new Runtime { QueryDuringActivation = true, QueryDuringDisposal = true };
        await WithPeer(runtime, async (host, run, token) =>
        {
            var activation = await Read(host, SidecarMessageType.QueryRequest, token);
            var command = SidecarCorrelationId.Create();
            await Send(host, SidecarMessageType.CommandRequest, SidecarDataMessages.EncodeBinaryRequest(1,
                new(SidecarWireCodecs.GeneratedDto, new byte[] { 7 })), token, command);
            await Ping(host, token); // Activation is waiting on the same reader.
            Check(runtime.Executions == 0);
            await Reply(host, activation, true, token);
            await runtime.Activated.Task.WaitAsync(token);
            var commandQuery = await Read(host, SidecarMessageType.QueryRequest, token);
            await Reply(host, commandQuery, true, token);
            Check((await Read(host, SidecarMessageType.CommandResult, token)).CorrelationId == command);
            await Send(host, SidecarMessageType.Deactivate, ReadOnlyMemory<byte>.Empty, token);
            var unregister = SidecarCorrelationId.Create();
            await Send(host, SidecarMessageType.Unregister, SidecarControlMessages.EncodeUnregister("test"), token, unregister);
            var cleanup = await Read(host, SidecarMessageType.QueryRequest, token);
            Check(!runtime.Disposed.Task.IsCompleted);
            await Ping(host, token); // No early Unregistered; disposal has not received its reply.
            await Reply(host, cleanup, false, token); // Real Hosts may deny effects after deactivation.
            var acknowledgement = await Read(host, SidecarMessageType.Unregistered, token);
            Check(acknowledgement.CorrelationId == unregister && runtime.Disposed.Task.IsCompleted);
            await Ping(host, token);
            await Send(host, SidecarMessageType.Shutdown, ReadOnlyMemory<byte>.Empty, token);
            await run.WaitAsync(token);
            Check(runtime.DisposeCount == 1);
        });
    }

    private static async Task CancelledCommand()
    {
        var runtime = new Runtime();
        await WithPeer(runtime, async (host, run, token) =>
        {
            await runtime.Activated.Task.WaitAsync(token);
            var command = SidecarCorrelationId.Create();
            await Send(host, SidecarMessageType.CommandRequest, SidecarDataMessages.EncodeBinaryRequest(1,
                new(SidecarWireCodecs.GeneratedDto, new byte[] { 7 })), token, command);
            var query = await Read(host, SidecarMessageType.QueryRequest, token);
            await Send(host, SidecarMessageType.Cancel, SidecarControlMessages.EncodeCancel(command.Value, "test"), token);
            var cancel = await Read(host, SidecarMessageType.Cancel, token);
            Check(SidecarControlMessages.DecodeCancel(cancel.Payload.Span).CorrelationId == query.CorrelationId.Value);
            var reply = await Read(host, SidecarMessageType.CommandResult, token);
            var value = SidecarDataMessages.DecodeBinaryResultDetails(reply.Payload.Span);
            Check(reply.CorrelationId == command && !value.Success && value.ErrorCode == "plugin.execution.cancelled");
            await Reply(host, query, true, token); // Valid late reply releases the original request slot.
            await Ping(host, token);
            await Send(host, SidecarMessageType.Shutdown, ReadOnlyMemory<byte>.Empty, token);
            await run.WaitAsync(token);
            Check(runtime.Executions == 1 && runtime.DisposeCount == 1);
        });
    }

    private static async Task NativeQuery(bool cancel)
    {
        var runtime = new Runtime();
        await WithPeer(runtime, async (host, run, token) =>
        {
            await runtime.Activated.Task.WaitAsync(token);
            var correlation = SidecarCorrelationId.Create();
            await Send(host, SidecarMessageType.QueryRequest, SidecarDataMessages.EncodeBinaryRequest(1,
                new(SidecarWireCodecs.GeneratedDto, new byte[] { 9 })), token, correlation);
            var outbound = await Read(host, SidecarMessageType.QueryRequest, token);
            await Ping(host, token); // A service awaiting the Host must not block the reader.
            if (cancel)
            {
                await Send(host, SidecarMessageType.Cancel, SidecarControlMessages.EncodeCancel(correlation.Value, "test"), token);
                var stopped = await Read(host, SidecarMessageType.Cancel, token);
                Check(SidecarControlMessages.DecodeCancel(stopped.Payload.Span).CorrelationId == outbound.CorrelationId.Value);
            }
            else await Reply(host, outbound, true, token);
            var frame = await Read(host, SidecarMessageType.QueryResult, token);
            var reply = SidecarDataMessages.DecodeBinaryResultDetails(frame.Payload.Span);
            Check(frame.CorrelationId == correlation && reply.Success == !cancel);
            if (cancel)
            {
                Check(reply.ErrorCode == "plugin.execution.cancelled");
                await Reply(host, outbound, true, token);
            }
            else Check(reply.Value.ToArray().SequenceEqual(new byte[] { 2 }));
            Check(runtime.Queries == 1 && runtime.Executions == 0);
            await Send(host, SidecarMessageType.QueryRequest, SidecarDataMessages.EncodeBinaryRequest(99,
                new(SidecarWireCodecs.GeneratedDto, new byte[] { 7 })), token);
            Check(!SidecarDataMessages.DecodeBinaryResultDetails((await Read(host, SidecarMessageType.QueryResult, token)).Payload.Span).Success);
            Check(runtime.Executions == 0 && runtime.Queries == 1);
            await Ping(host, token);
            await Send(host, SidecarMessageType.Shutdown, ReadOnlyMemory<byte>.Empty, token);
            await run.WaitAsync(token);
            Check(runtime.DisposeCount == 1);
        });
    }

    private static async Task ShutdownWithoutReply()
    {
        var runtime = new Runtime { QueryDuringDisposal = true };
        await WithPeer(runtime, async (host, run, token) =>
        {
            await runtime.Activated.Task.WaitAsync(token);
            await Send(host, SidecarMessageType.Deactivate, ReadOnlyMemory<byte>.Empty, token);
            _ = await Read(host, SidecarMessageType.QueryRequest, token);
            await Send(host, SidecarMessageType.Shutdown, ReadOnlyMemory<byte>.Empty, token);
            await run.WaitAsync(token);
            Check(runtime.DisposalDisconnected && runtime.DisposeCount == 1);
        });
    }

    private static async Task FailureWithoutAnotherFrame()
    {
        var runtime = new Runtime { FailActivation = true };
        await WithPeer(runtime, async (_, run, token) =>
        {
            await Reject<InvalidOperationException>(run.WaitAsync(token));
            Check(runtime.DisposeCount == 1);
        });
    }

    private static async Task RejectAfterRetirement()
    {
        var runtime = new Runtime();
        await WithPeer(runtime, async (host, run, token) =>
        {
            await runtime.Activated.Task.WaitAsync(token);
            await Send(host, SidecarMessageType.Deactivate, ReadOnlyMemory<byte>.Empty, token);
            await runtime.Disposed.Task.WaitAsync(token);
            await Send(host, SidecarMessageType.CommandRequest, SidecarDataMessages.EncodeBinaryRequest(1,
                new(SidecarWireCodecs.GeneratedDto, new byte[] { 7 })), token);
            await Reject<InvalidDataException>(run.WaitAsync(token));
            Check(runtime.Executions == 0 && runtime.DisposeCount == 1);
        });
    }

    private static async Task DrainDeadline()
    {
        using var release = new ManualResetEventSlim();
        var runtime = new Runtime { BeforeDisposal = () => release.Wait() };
        try
        {
            await WithPeer(runtime, async (host, run, token) =>
            {
                await Send(host, SidecarMessageType.Deactivate, ReadOnlyMemory<byte>.Empty, token);
                await runtime.Disposing.Task.WaitAsync(token);
                await Ping(host, token);
                await Reject<TimeoutException>(run.WaitAsync(token));
                Check(runtime.DisposeCount == 1 && !runtime.Disposed.Task.IsCompleted);
            });
        }
        finally { release.Set(); }
        await runtime.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static async Task BackgroundCompletion()
    {
        foreach (bool fail in new[] { false, true })
        {
            var runtime = new Runtime();
            await WithPeer(runtime, async (host, run, token) =>
            {
                await runtime.Activated.Task.WaitAsync(token);
                await Ping(host, token);
                if (fail) runtime.Background.TrySetException(new IOException("Publication failed without another frame."));
                else runtime.Background.TrySetResult();
                await Reject<Exception>(run.WaitAsync(token));
                Check(runtime.DisposeCount == 1);
            });
        }
    }

    private sealed class Runtime : IPluginWorkerRuntime, IPluginWorkerBackground
    {
        internal bool QueryDuringActivation, QueryDuringDisposal, FailActivation, DisposalDisconnected;
        internal int DisposeCount, Executions, Queries;
        internal readonly TaskCompletionSource Background = Signal();
        public Task Completion => Background.Task;
        internal Action? BeforeDisposal;
        internal readonly TaskCompletionSource Activated = Signal(), Disposing = Signal(), Disposed = Signal();
        private SidecarHostRequestClient? _requests;
        public SidecarFeatures Features => SidecarFeatures.BinaryPayloads | SidecarFeatures.HostRequests | SidecarFeatures.Health | SidecarFeatures.Unregistration;
        public IReadOnlyList<SidecarRegistrationItem> Registrations { get; } =
            [new(SidecarRegistrationKind.Command, "worker.test.invoke", 0, SidecarWireCodecs.GeneratedDto) { ResultCodecId = SidecarWireCodecs.GeneratedDto },
             new(SidecarRegistrationKind.Query, "sidecar.service.test.query", 0, SidecarWireCodecs.GeneratedDto) { ResultCodecId = SidecarWireCodecs.GeneratedDto }];
        public async ValueTask ActivateAsync(SidecarHostRequestClient requests, CancellationToken token)
        {
            _requests = requests;
            if (FailActivation) throw new InvalidOperationException("Activation failed without more input.");
            if (QueryDuringActivation) await Query(token);
            Activated.TrySetResult();
        }
        public async ValueTask<byte[]> ExecuteAsync(uint contract, SidecarBinaryValue input, CancellationToken token)
        {
            Interlocked.Increment(ref Executions);
            Check(contract == 1 && input.ToArray().SequenceEqual(new byte[] { 7 }));
            await Query(token);
            return [1];
        }
        public async ValueTask<byte[]> QueryAsync(uint contract, SidecarBinaryValue input, CancellationToken token)
        {
            if (contract != 1) throw new InvalidDataException("Unknown query.");
            Interlocked.Increment(ref Queries);
            Check(input.ToArray().SequenceEqual(new byte[] { 9 }));
            await Query(token);
            return [2];
        }
        private ValueTask<SidecarHostReply> Query(CancellationToken token) => _requests!.InvokeAsync(255, true,
            new(SidecarWireCodecs.GeneratedDto, []), SidecarWireCodecs.GeneratedDto, TimeSpan.FromSeconds(20), token);
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCount);
            Disposing.TrySetResult();
            BeforeDisposal?.Invoke();
            if (QueryDuringDisposal)
            {
                try { await Query(default); }
                catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException)
                { DisposalDisconnected = true; }
            }
            Disposed.TrySetResult();
        }
    }

    private static async Task WithPeer(Runtime runtime, Func<SidecarFrameTransport, Task, CancellationToken, Task> test)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        var accept = listener.AcceptTcpClientAsync(token);
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, token);
        using var server = await accept;
        using var host = new SidecarFrameTransport(client.GetStream());
        using var wire = new SidecarFrameTransport(server.GetStream());
        Task run = new PluginExecutionPeer(wire, runtime).RunAsync(token);
        try
        {
            await Send(host, SidecarMessageType.Hello, SidecarHandshake.EncodeHello(SidecarProtocol.Version, "worker-tests", SidecarFeatures.All), token);
            var welcome = await Read(host, SidecarMessageType.Welcome, token);
            Check(SidecarHandshake.DecodeWelcomeDetails(welcome.Payload.Span).Features == runtime.Features);
            await Read(host, SidecarMessageType.RegisterBegin, token);
            foreach (var _ in runtime.Registrations) await Read(host, SidecarMessageType.RegisterItem, token);
            await Read(host, SidecarMessageType.RegisterEnd, token);
            await Read(host, SidecarMessageType.StateSnapshotBegin, token);
            await Read(host, SidecarMessageType.StateSnapshotEnd, token);
            await Send(host, SidecarMessageType.Ready, ReadOnlyMemory<byte>.Empty, token);
            await Send(host, SidecarMessageType.Activate, ReadOnlyMemory<byte>.Empty, token);
            await test(host, run, token);
        }
        finally
        {
            await deadline.CancelAsync();
            client.Close(); server.Close();
            try { await run.WaitAsync(TimeSpan.FromSeconds(7)); }
            catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException) { }
        }
    }
    private static async Task Ping(SidecarFrameTransport host, CancellationToken token)
    {
        var id = SidecarCorrelationId.Create();
        await Send(host, SidecarMessageType.HealthPing, new byte[] { 1, 2, 3 }, token, id);
        var pong = await Read(host, SidecarMessageType.HealthPong, token);
        Check(pong.CorrelationId == id && pong.Payload.Span.SequenceEqual(new byte[] { 1, 2, 3 }));
    }
    private static ValueTask Reply(SidecarFrameTransport host, SidecarFrame query, bool success, CancellationToken token)
        => Send(host, SidecarMessageType.QueryResult, SidecarDataMessages.EncodeBinaryResult(success,
            new(SidecarWireCodecs.GeneratedDto, []), success ? null : "xsr.rejected"), token, query.CorrelationId);
    private static ValueTask Send(SidecarFrameTransport host, SidecarMessageType type, ReadOnlyMemory<byte> bytes,
        CancellationToken token, SidecarCorrelationId? correlation = null)
        => host.SendAsync(new(SidecarProtocol.Version, type, SidecarFrameTraits.None, correlation ?? SidecarCorrelationId.Create(), bytes), token);
    private static async Task<SidecarFrame> Read(SidecarFrameTransport host, SidecarMessageType type, CancellationToken token)
    {
        var frame = await host.ReceiveAsync(token);
        Check(frame.MessageType == type, $"Expected {type}, received {frame.MessageType}.");
        return frame;
    }
    private static async Task Reject<T>(Task task) where T : Exception
    {
        try { await task; }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value, string message = "Worker lifecycle assertion failed.")
    { if (!value) throw new InvalidOperationException(message); }
}
