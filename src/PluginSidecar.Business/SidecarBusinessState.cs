using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Sidecar.Host;
using Nexa.Xsr;

namespace PluginSidecar.Business;

/// <summary>Session-local public projections. Host revisions remain authoritative for CAS.</summary>
public sealed class SidecarBusinessState : IAsyncDisposable
{
    private readonly Func<CancellationToken, ValueTask<XsrResult<SidecarPublicState>>> _read;
    private readonly object _lifetime = new();
    private Task? _dispose;
    public SidecarBusinessState(SidecarBusinessClient client) : this(client.ReadStateAsync) { }
    public SidecarBusinessState(Func<CancellationToken, ValueTask<XsrResult<SidecarPublicState>>> read)
        => _read = read ?? throw new ArgumentNullException(nameof(read));
    private SidecarPublicState? _current;
    public bool TryRead(out SidecarPublicState? snapshot) { snapshot = Volatile.Read(ref _current); return snapshot is not null; }
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _poll;
    public event Action? Changed;
    public SidecarPublicState Current => Volatile.Read(ref _current) ?? throw new AbiHostException(AbiErrors.CAPABILITY_UNAVAILABLE);
    public async ValueTask RefreshAsync(CancellationToken token = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        await _refresh.WaitAsync(lifetime.Token).ConfigureAwait(false);
        bool changed = false;
        try
        {
            var result = await _read(lifetime.Token).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                changed = Interlocked.Exchange(ref _current, null) is not null;
                throw new AbiHostException(AbiErrors.CAPABILITY_UNAVAILABLE);
            }
            var previous = Interlocked.Exchange(ref _current, result.Value!);
            var next = result.Value!;
            changed = previous is null || previous.LibraryRevision != next.LibraryRevision || previous.AccountsRevision != next.AccountsRevision
                || previous.ProcessesRevision != next.ProcessesRevision || previous.InstallRevision != next.InstallRevision
                || previous.LoginRevision != next.LoginRevision || previous.CharactersRevision != next.CharactersRevision || previous.SettingsRevision != next.SettingsRevision;
        }
        catch
        {
            changed |= Interlocked.Exchange(ref _current, null) is not null;
            throw;
        }
        finally { _refresh.Release(); if (changed) Changed?.Invoke(); }
    }
    public void Start()
    {
        lock (_lifetime)
        {
            ObjectDisposedException.ThrowIf(_dispose is not null, this);
            if (_poll is not null) throw new InvalidOperationException("State polling already started.");
            _poll = PollAsync();
        }
    }
    private async Task PollAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                try { await RefreshAsync(_stop.Token).ConfigureAwait(false); }
                catch (Exception error) when (error is AbiHostException or IOException or OperationCanceledException or ObjectDisposedException) { }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    public ValueTask DisposeAsync() { lock (_lifetime) return new(_dispose ??= DisposeCore()); }
    private async Task DisposeCore()
    {
        _stop.Cancel();
        if (_poll is not null) await _poll.ConfigureAwait(false);
        // Serialize with an outstanding refresh before disposing its semaphore.
        await _refresh.WaitAsync().ConfigureAwait(false);
        _refresh.Release();
        _stop.Dispose();
    }
}

public sealed record HostCollectionSnapshot<T>(long Revision, IReadOnlyList<T> Items, XsrStateAvailability Availability);
