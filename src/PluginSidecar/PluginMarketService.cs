using Nexa.Plugin.MarketDelivery;
using Nexa.Sidecar.Host;
using Nexa.Sidecar.Protocol;

namespace PluginSidecar;

/// <summary>Authenticated native transfer ownership. Guest plugins cannot call this service directly.</summary>
internal sealed class PluginMarketService : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<(Guid Caller, Guid Operation), Entry> _entries = [];
    private readonly byte[] _publicKey;
    private readonly Func<EncryptedDelivery, string, string, string, CancellationToken, ValueTask<bool>> _install;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Timer _expiry;
    private Task? _disposal;
    private Exception? _cleanupFailure;
    private sealed class Entry(MarketTransferRequest request, DateTimeOffset used) : IDisposable
    {
        internal readonly MarketTransferRequest Request = request;
        internal readonly CancellationTokenSource Stop = new();
        internal MemoryStream? Upload = new();
        internal int Offset;
        internal DateTimeOffset Used = used;
        internal MarketTransferPhase Phase = MarketTransferPhase.Receiving;
        internal string Error = "";
        internal Task? Work, Retirement;
        internal bool Reserved => Phase is MarketTransferPhase.Receiving or MarketTransferPhase.Installing || Work is { IsCompleted: false } || Retirement is { IsCompleted: false };
        public void Dispose() { Upload?.Dispose(); Upload = null; Stop.Dispose(); }
    }

    internal PluginMarketService(byte[] devicePublicKey,
        Func<EncryptedDelivery, string, string, string, CancellationToken, ValueTask<bool>> install, Func<DateTimeOffset>? clock = null)
    {
        if (devicePublicKey.Length is < 1 or > 4096) throw new ArgumentException("Invalid device public key.", nameof(devicePublicKey));
        _publicKey = devicePublicKey.ToArray(); _install = install ?? throw new ArgumentNullException(nameof(install)); _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _expiry = new(_ => Sweep(), null, Timeout.Infinite, Timeout.Infinite);
    }

    internal async ValueTask<byte[]> InvokeAsync(SidecarBinaryValue input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var envelope = SidecarPeerService.DecodeRequest(input);
        if (envelope.CallerPackage != PluginMarketWire.Caller) throw new UnauthorizedAccessException("Market caller required.");
        Guid caller = Guid.ParseExact(envelope.CallerSession, "N");
        var request = PluginMarketWire.Decode(envelope.Payload); var key = (caller, request.Operation);
        Entry entry; Task? retiring = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            token.ThrowIfCancellationRequested();
            if (request.Verb == MarketTransferVerb.Begin)
            {
                if (_entries.ContainsKey(key) || _entries.Count >= 16 || _entries.Values.Count(value => value.Reserved) >= 2)
                    throw new InvalidOperationException("Market transfer budget exceeded.");
                entry = new(request, _clock()); _entries.Add(key, entry); _expiry.Change(1000, 1000);
            }
            else
            {
                if (!_entries.TryGetValue(key, out entry!) || _clock() - entry.Used >= TimeSpan.FromMinutes(2))
                    throw new InvalidOperationException("Market transfer retired.");
                entry.Used = _clock();
                switch (request.Verb)
                {
                    case MarketTransferVerb.Append:
                        if (entry.Retirement is not null || entry.Phase != MarketTransferPhase.Receiving || request.Offset != entry.Offset
                            || request.Chunk!.Length > PluginMarketWire.PacketLimit - entry.Offset)
                            throw new InvalidDataException("Market chunk is not sequential or exceeds its budget.");
                        entry.Upload!.Write(request.Chunk); entry.Offset += request.Chunk.Length;
                        break;
                    case MarketTransferVerb.Finish:
                        if (entry.Retirement is not null || entry.Phase != MarketTransferPhase.Receiving
                            || _entries.Values.Any(value => value.Work is { IsCompleted: false }))
                            throw new InvalidOperationException("Market installation not admitted.");
                        EncryptedDelivery delivery;
                        try { delivery = PluginMarketWire.DecodeDelivery(entry.Upload!.ToArray()); }
                        catch (Exception error) when (error is IOException or InvalidDataException)
                        { entry.Phase = MarketTransferPhase.Failed; entry.Error = "plugin.market.delivery-invalid"; entry.Upload?.Dispose(); entry.Upload = null; break; }
                        entry.Upload.Dispose(); entry.Upload = null; entry.Phase = MarketTransferPhase.Installing;
                        entry.Work = Task.Run(() => InstallAsync(entry, delivery));
                        break;
                    case MarketTransferVerb.Abort:
                        retiring = Retire(key, entry);
                        break;
                    case MarketTransferVerb.Status: break;
                    default: throw new InvalidDataException("Unknown Market operation.");
                }
            }
            if (retiring is null) return Reply(entry);
        }
        await retiring.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        lock (_gate) return Reply(entry);
    }

    private byte[] Reply(Entry entry) => PluginMarketWire.Encode(new MarketTransferReply(entry.Phase, entry.Offset,
        entry.Phase == MarketTransferPhase.Receiving ? _publicKey : [], entry.Error));

    private async Task InstallAsync(Entry entry, EncryptedDelivery delivery)
    {
        MarketTransferPhase phase; string error = "";
        try
        {
            entry.Stop.Token.ThrowIfCancellationRequested();
            bool installed = await _install(delivery, entry.Request.Id, entry.Request.Version, entry.Request.Subject, entry.Stop.Token).ConfigureAwait(false);
            phase = installed ? MarketTransferPhase.Installed : MarketTransferPhase.Declined;
        }
        catch (OperationCanceledException) { phase = MarketTransferPhase.Aborted; }
        catch (Exception failure) when (failure is not OutOfMemoryException and not AccessViolationException)
        { phase = MarketTransferPhase.Failed; error = "plugin.market.install-failed"; }
        lock (_gate) { entry.Phase = phase; entry.Error = error; }
    }

    private Task Retire((Guid, Guid) key, Entry entry)
    {
        if (entry.Retirement is not null) return entry.Retirement;
        if (entry.Phase == MarketTransferPhase.Receiving) entry.Phase = MarketTransferPhase.Aborted;
        Task admitted = entry.Work ?? Task.CompletedTask;
        entry.Retirement = Task.Run(async () =>
        {
            try { await Task.WhenAll(entry.Stop.CancelAsync(), admitted).ConfigureAwait(false); }
            finally
            {
                lock (_gate)
                {
                    _entries.Remove(key);
                    if (_entries.Count == 0 && _disposal is null) _expiry.Change(Timeout.Infinite, Timeout.Infinite);
                }
                entry.Dispose();
            }
        });
        _ = ObserveAsync(entry.Retirement); return entry.Retirement;
    }
    private async Task ObserveAsync(Task retirement)
    {
        try { await retirement.ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        { lock (_gate) _cleanupFailure ??= error; }
    }
    private void Sweep()
    {
        lock (_gate)
        {
            if (_disposal is not null) return;
            foreach (var (key, entry) in _entries)
                if (_clock() - entry.Used >= TimeSpan.FromMinutes(2)) Retire(key, entry);
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposal is not null) return new(_disposal);
            _expiry.Dispose();
            Task[] draining = _entries.Select(pair => Retire(pair.Key, pair.Value)).ToArray();
            return new(_disposal = Task.Run(async () =>
            {
                await Task.WhenAll(draining).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                if (_cleanupFailure is { } error) throw new IOException("Market transfer cleanup failed.", error);
            }));
        }
    }
}
