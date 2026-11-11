using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100.Data;
using Wasmtime;
namespace PluginSidecar.WasmRuntime;

public sealed record WasmHostCall(string Contract, uint Method, AbiValue Arguments, Guid OperationId, string? IdempotencyKey, string? ExpectedRevision, Guid? ExperimentId = null, TimeSpan? Budget = null);

public sealed record WasmLimits(long MemoryBytes = 32 * 1024 * 1024, ulong FuelPerCallback = 10_000_000, int MaximumHostCalls = 1024);

/// <summary>Import-free, bounded guest machine. All effects are asynchronous continuations.</summary>
public sealed class WasmPluginMachine : IAsyncDisposable
{
    private readonly Engine _engine;
    private readonly Module _module;
    private readonly Linker _linker;
    private readonly Store _store;
    private readonly Memory _memory;
    private readonly Function _allocate;
    private readonly Function _resume;
    private readonly WasmLimits _limits;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed, _faulted;

    public WasmPluginMachine(ReadOnlySpan<byte> verifiedWasm, WasmLimits? limits = null)
    {
        _limits = limits ?? new();
        if (_limits.MemoryBytes is < 65536 or > 256 * 1024 * 1024 || _limits.FuelPerCallback is < 1 or > 100_000_000 || _limits.MaximumHostCalls is < 1 or > 16384)
            throw new ArgumentOutOfRangeException(nameof(limits));
        if (verifiedWasm.Length is < 8 or > 25 * 1024 * 1024) throw new InvalidDataException(AbiErrors.PACKAGE_INVALID);
        _engine = new Engine(new Config().WithFuelConsumption(true));
        try
        {
            _module = Module.FromBytes(_engine, "plugin", verifiedWasm);
            if (_module.Imports.Count != 0) throw new InvalidDataException("Plugin WebAssembly imports are forbidden.");
            _store = new Store(_engine);
            _store.SetLimits(memorySize: _limits.MemoryBytes, tableElements: 4096, instances: 1, tables: 1, memories: 1);
            _store.Fuel = _limits.FuelPerCallback;
            _linker = new Linker(_engine); // Intentionally no DefineWasi or host imports.
            var instance = _linker.Instantiate(_store, _module);
            _memory = instance.GetMemory("memory") ?? throw new InvalidDataException("Missing plugin memory export.");
            _allocate = instance.GetFunction("nexa_alloc") ?? throw new InvalidDataException("Missing allocator export.");
            _resume = instance.GetFunction("nexa_resume") ?? throw new InvalidDataException("Missing continuation export.");
            if (!_allocate.Parameters.SequenceEqual([ValueKind.Int32]) || !_allocate.Results.SequenceEqual([ValueKind.Int32]) ||
                !_resume.Parameters.SequenceEqual([ValueKind.Int32, ValueKind.Int32]) || !_resume.Results.SequenceEqual([ValueKind.Int64]))
                throw new InvalidDataException("Invalid guest export signature.");
        }
        catch { _store?.Dispose(); _linker?.Dispose(); _module?.Dispose(); _engine.Dispose(); throw; }
    }

    public ValueTask<AbiValue> RunAsync(AbiValue invocation,
        Func<string, uint, AbiValue, CancellationToken, ValueTask<AbiReply<AbiValue>>> invoke,
        TimeSpan budget, CancellationToken cancellation = default) =>
        RunDetailedAsync(invocation, (call, token) => invoke(call.Contract, call.Method, call.Arguments, token), budget, cancellation);

    public async ValueTask<AbiValue> RunDetailedAsync(AbiValue invocation,
        Func<WasmHostCall, CancellationToken, ValueTask<AbiReply<AbiValue>>> invoke,
        TimeSpan budget, CancellationToken cancellation = default)
    {
        if (budget <= TimeSpan.Zero || budget > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(budget));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(budget);
        await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_faulted) throw new InvalidOperationException("A failed guest machine must be reloaded.");
            _store.Fuel = _limits.FuelPerCallback; // Shared across all continuations in this callback.
            AbiValue input = invocation;
            for (int calls = 0; calls <= _limits.MaximumHostCalls; calls++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                byte[] message = AbiValueBinary.Encode(input);
                // Guest CPU work never blocks the IPC receive loop. Fuel bounds the worker.
                AbiValue output = await Task.Run(() => Step(message), deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                var fields = DataCodec.ReadMap(output);
                string kind = DataCodec.ReadString(DataCodec.Field(fields, "kind"));
                if (kind == "complete") return DataCodec.Field(fields, "value");
                if (kind != "invoke" || calls == _limits.MaximumHostCalls) throw new InvalidDataException("Invalid guest continuation or request budget exceeded.");
                string contract = DataCodec.ReadString(DataCodec.Field(fields, "contract"));
                uint method = checked((uint)DataCodec.ReadUInt64(DataCodec.Field(fields, "method")));
                var call = new WasmHostCall(contract, method, DataCodec.Field(fields, "arguments"),
                    fields.TryGetValue("operationId", out var operation) ? DataCodec.ReadGuid(operation) : Guid.NewGuid(),
                    fields.TryGetValue("idempotencyKey", out var key) && key is not AbiValue.Null ? DataCodec.ReadString(key) : null,
                    fields.TryGetValue("expectedRevision", out var revision) && revision is not AbiValue.Null ? DataCodec.ReadString(revision) : null,
                    fields.TryGetValue("experimentId", out var experiment) && experiment is not AbiValue.Null ? DataCodec.ReadGuid(experiment) : null,
                    fields.TryGetValue("budgetMilliseconds", out var milliseconds) ? CallBudget(milliseconds) : null);
                var pending = invoke(call, deadline.Token).AsTask();
                AbiReply<AbiValue> result;
                try { result = await pending.WaitAsync(deadline.Token).ConfigureAwait(false); }
                catch { _ = Observe(pending); throw; }
                input = DataCodec.Map(("kind", DataCodec.WriteString("reply")), ("success", new AbiValue.Bool(result.Success)),
                    ("value", result.Success ? result.Value : new AbiValue.Null()), ("error", result.Success ? new AbiValue.Null() : EncodeError(result.Error!)));
            }
            throw new InvalidOperationException("Unreachable continuation state.");
        }
        catch { _faulted = true; throw; }
        finally { _gate.Release(); }
    }
    private static async Task Observe(Task task)
    { try { await task.ConfigureAwait(false); } catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException) { } }
    private static TimeSpan CallBudget(AbiValue value)
    {
        ulong milliseconds = DataCodec.ReadUInt64(value);
        if (milliseconds is < 1 or > 300000) throw new FormatException("Invalid guest call budget.");
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private AbiValue Step(byte[] message)
    {
        uint address = unchecked((uint)(int)_allocate.Invoke(message.Length)!);
        message.CopyTo(_memory.GetSpan(address, message.Length));
        ulong reply = unchecked((ulong)(long)_resume.Invoke(unchecked((int)address), message.Length)!);
        uint outputAddress = (uint)(reply >> 32), size = (uint)reply;
        if (size > DataCodec.MaximumBytes) throw new InvalidDataException("Guest reply exceeds its byte budget.");
        return AbiValueBinary.Decode(_memory.GetSpan(outputAddress, checked((int)size)));
    }
    private static AbiValue EncodeError(AbiError error) => DataCodec.Map(("code", DataCodec.WriteString(error.Code)),
        ("message", DataCodec.WriteString(error.Message)), ("stage", DataCodec.WriteString(error.Stage)),
        ("operation", DataCodec.WriteString(error.Operation)), ("pluginId", DataCodec.WriteString(error.PluginId)), ("loadInstanceId", DataCodec.WriteGuid(error.LoadInstanceId)),
        ("messageKey", DataCodec.WriteString(error.MessageKey)), ("operationId", DataCodec.WriteGuid(error.OperationId)),
        ("correlationId", DataCodec.WriteGuid(error.CorrelationId)), ("retryable", DataCodec.WriteBoolean(error.Retryable)),
        ("parameters", new AbiValue.Map(error.Parameters.ToImmutableDictionary(pair => pair.Key, pair => DataCodec.WriteString(pair.Value)))),
        ("details", new AbiValue.Map(error.Details.ToImmutableDictionary(pair => pair.Key, pair => DataCodec.WriteString(pair.Value)))));
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { if (_disposed) return; _disposed = true; _store.Dispose(); _linker.Dispose(); _module.Dispose(); _engine.Dispose(); }
        finally { _gate.Release(); }
    }
}
