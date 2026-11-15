using System.Threading.Channels;
using System.Security.Cryptography;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100.Data;
using Nexa.Sidecar.Host;
using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using PluginSidecar.Application;
using PluginSidecar.Business;
using PluginSidecar.Presentation;
using PluginSidecar.Protection;
using PluginSidecar.WasmRuntime;

namespace PluginSidecar;

/// <summary>One native session owns the application, all guest effects and all presentation.</summary>
internal sealed class StandalonePluginRuntime(
    Func<SidecarBusinessClient, CancellationToken, ValueTask<HttpClient>>? network,
    Action<string> report, Func<PluginMarketTrust>? publisherTrust = null,
    Func<PlatformPluginKeyProtector>? protection = null) : IPluginWorkerRuntime, IPluginWorkerBackground
{
    private readonly CancellationTokenSource _stop = new();
    private readonly object _lifecycle = new();
    private readonly Guid _session = Guid.NewGuid();
    private readonly Channel<(PluginIdentity Owner, QualifiedId Page)> _navigation = Channel.CreateBounded<(PluginIdentity, QualifiedId)>(16);
    private Task? _activation, _disposal;
    private Task _pump = Task.CompletedTask;
    private CancellationTokenSource? _active;
    private PluginApplication? _application;
    private PluginBusinessRoutes? _business;
    private SidecarHttpHandler? _http;
    internal StandalonePluginRuntime(Action<string> report) : this(null, report) { }
    private SidecarBusinessState? _state;
    private PluginPagePublisher? _publisher;
    private PluginManagementController? _management;
    private SidecarRasterHost? _rasters;
    private PlatformPluginKeyProtector? _keys;
    private PluginOfflineCatalog? _catalog;
    private PluginMarketTrust? _marketTrust;
    private RSA? _deviceKey;
    private PluginMarketCatalog? _marketCatalog;
    private PluginMarketService? _marketService;
    private bool _ready;

    public SidecarFeatures Features => SidecarFeatures.BinaryPayloads | SidecarFeatures.HostRequests
        | SidecarFeatures.LargeGeneratedDtos | SidecarFeatures.Nxml | SidecarFeatures.UiForms | SidecarFeatures.Health | SidecarFeatures.Unregistration;
    public IReadOnlyList<SidecarRegistrationItem> Registrations { get; } =
    [Command(PluginViewCompiler.ActionCommand), Command(PluginPagePublisher.ManagementCommand), Command(PluginViewCompiler.DrawingInputCommand),
        new(SidecarRegistrationKind.Query, PluginMarketWire.Service, 0, SidecarWireCodecs.GeneratedDto)
        { ResultCodecId = SidecarWireCodecs.GeneratedDto }];
    public Task Completion => _pump;
    private static SidecarRegistrationItem Command(string semantic) => new(SidecarRegistrationKind.Command,
        semantic, 0, SidecarWireCodecs.GeneratedDto)
    { ResultCodecId = SidecarWireCodecs.GeneratedDto };

    public ValueTask ActivateAsync(SidecarHostRequestClient requests, CancellationToken token)
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            if (_activation is not null) throw new InvalidOperationException("Worker already activated.");
            _active = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
            return new(_activation = InitializeAsync(requests, _active.Token));
        }
    }

    private async Task InitializeAsync(SidecarHostRequestClient requests, CancellationToken token)
    {
        Observer observer = new();
        SidecarContextClient context = new(requests, observer);
        Check(await context.DiscoverAsync(token).ConfigureAwait(false));
        var facts = await new SidecarRuntimeContext(context).WaitReadyAsync(true, TimeSpan.FromMinutes(2), token).ConfigureAwait(false);
        SidecarBusinessClient client = new(requests, observer);
        Check(await client.DiscoverAsync(token).ConfigureAwait(false));
        SidecarEffectsClient effects = new(requests, observer); Check(await effects.DiscoverAsync(token).ConfigureAwait(false));
        SidecarShellClient shell = new(requests, observer); Check(await shell.DiscoverAsync(token).ConfigureAwait(false));
        SidecarThemeOptionsClient themeOptions = new(requests, observer); Check(await themeOptions.DiscoverAsync(token).ConfigureAwait(false));
        SidecarThemeActivationClient themes = new(requests, observer); Check(await themes.DiscoverAsync(token).ConfigureAwait(false));
        SidecarClipboardClient clipboard = new(requests, observer); Check(await clipboard.DiscoverAsync(token).ConfigureAwait(false));
        SidecarFilePickerClient picker = new(requests, observer); Check(await picker.DiscoverAsync(token).ConfigureAwait(false));
        var confirmation = await SidecarActionConfirmation.CreateAsync(requests, observer, token).ConfigureAwait(false);
        var notices = await SidecarNotifications.CreateAsync(requests, observer, token).ConfigureAwait(false);
        var interaction = await SidecarDeveloperPackageInteraction.CreateAsync(requests, observer, token).ConfigureAwait(false);
        var views = await SidecarPresentationHost.CreateAsync(requests, observer, token).ConfigureAwait(false);
        var dialogs = await SidecarDialogHost.CreateAsync(requests, observer, token).ConfigureAwait(false);
        var shortcuts = await SidecarShortcutHost.CreateAsync(requests, observer, token).ConfigureAwait(false);
        _rasters = await SidecarRasterHost.CreateAsync(requests, observer, token).ConfigureAwait(false);
        _state = new(client);
        await _state.RefreshAsync(token).ConfigureAwait(false);
        _business = new(_state, client.Commands, client.Commands, client.Commands, client.Queries, client.Commands, client.Queries);
        _business.BindEffects(effects);
        _business.BindShell(shell);
        _business.BindThemeOptions(themeOptions);
        _business.BindThemeActivation(themes, error => report(error.Code.Value));
        _business.BindClipboard(clipboard, error => report("plugin.clipboard.cleanup." + error.GetType().Name));
        _business.BindFilePicker(picker);
        _business.BindCatalog(client.Commands, client.Queries);
        _business.BindInstall(client.Commands, client.Queries, client.Commands, client.Queries, client.Queries);
        _business.BindContentImport(client.Commands);
        _business.BindAccounts(client.Queries);
        _business.BindConfirmation(confirmation);
        if (network is not null) _business.BindNetworkHttp(await network(client, token).ConfigureAwait(false));
        else
        {
            _http = await SidecarHttpHandler.CreateAsync(requests, observer, report, token).ConfigureAwait(false);
            _business.BindNetworkHttp(new HttpClient(_http) { Timeout = TimeSpan.FromSeconds(60) });
        }
        token.ThrowIfCancellationRequested();
        string storage = Path.Combine(facts.DataRoot, "plugins", "storage");
        _application = new(storage, (bytes, cancellation) =>
        {
            cancellation.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IWasmPluginMachine>(new WasmPluginMachine(bytes.Span));
        }, _business.Routes, confirmation, notices.NotifyAsync, (owner, page) =>
        {
            if (!_navigation.Writer.TryWrite((owner, page))) throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
        }, PluginViewCompiler.ValidatePage, PluginViewCompiler.ValidateComponent);
        _business.BindApplication(_application);
        _business.Bind(_application.Runtime.Services);
        _keys = protection?.Invoke() ?? new(); _catalog = new(storage + ".installed", _keys);
        _marketTrust = publisherTrust?.Invoke() ?? PluginMarketTrust.Load();
        if (_marketTrust.Available)
        {
            try
            {
                _deviceKey = await new PluginMarketDeviceKey(storage + ".device", _keys).OpenAsync(token).ConfigureAwait(false);
                _marketCatalog = PluginMarketCatalog.CreateWithVerifier(storage + ".market", _keys,
                    (delivery, id, version, subject) => _marketTrust.Open(_deviceKey, delivery, id, version, subject));
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { report("plugin.market.device-unavailable"); _deviceKey?.Dispose(); _deviceKey = null; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException
                or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
            { report("plugin.market.device-unavailable"); _deviceKey?.Dispose(); _deviceKey = null; }
        }
        else report("plugin.market.publisher-not-configured");
        _management = new(_application, _catalog, Policy, interaction.PickAsync, interaction.ConfirmAsync, marketCatalog: _marketCatalog);
        if (_marketCatalog is not null)
            _marketService = new(_deviceKey!.ExportSubjectPublicKeyInfo(), _management.InstallMarketAsync);
        _publisher = new(_application, views, new(() =>
        {
            var policy = Policy();
            return new(policy.DeveloperMode, policy.SafeMode, policy.Ready, _management.IsBusy, "");
        }, _management), dialogs, shortcuts, _rasters);
        _ready = true;
        // Do not await the infinite loop from activation: the peer must admit callbacks first.
        _pump = PumpAsync(token);
    }

    private PluginInstallationPolicy Policy() => new(_session, _business?.DeveloperEnabled == true,
        Volatile.Read(ref _ready) && !_stop.IsCancellationRequested && _state?.TryRead(out _) == true, false);

    private async Task PumpAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        try
        {
            do
            {
                token.ThrowIfCancellationRequested();
                await _management!.SynchronizePolicyAsync(token).ConfigureAwait(false);
                if (!_management.IsBusy && Policy().CanLoadMarket)
                    foreach (var issue in await _management.RestoreOfflineAsync(token).ConfigureAwait(false)) report(issue.Code);
                await _publisher!.SynchronizeAsync(token).ConfigureAwait(false);
                while (_navigation.Reader.TryRead(out var navigation))
                {
                    if (!_application!.Runtime.Active.Contains(navigation.Owner)) continue;
                    try { await _publisher.SetOpenAsync(navigation.Owner, navigation.Page, true, token).ConfigureAwait(false); }
                    catch (AbiHostException error) when (error.Code == AbiErrors.HANDLE_INVALID) { }
                }
                await _publisher.ReportVisibilityAsync(token).ConfigureAwait(false);
            } while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public async ValueTask<byte[]> ExecuteAsync(uint contract, SidecarBinaryValue input, CancellationToken token)
    {
        if (!Policy().Ready || _publisher is null || input.CodecId != SidecarWireCodecs.GeneratedDto)
            throw new InvalidDataException("Plugin session is not ready.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        switch (contract)
        {
            case 1: await _publisher.InvokeActionAsync(input.ToArray(), lifetime.Token).ConfigureAwait(false); break;
            case 2: await _publisher.InvokeManagementAsync(input.ToArray(), lifetime.Token).ConfigureAwait(false); break;
            case 3: await _publisher.ForwardDrawingInputAsync(input.ToArray(), lifetime.Token).ConfigureAwait(false); break;
            default: throw new InvalidDataException("Unknown plugin callback.");
        }
        return [];
    }

    public ValueTask<byte[]> QueryAsync(uint contract, SidecarBinaryValue input, CancellationToken token)
    {
        if (!Policy().CanLoadMarket || contract != 1 || _marketService is null)
            throw new InvalidOperationException("plugin.market.service-unavailable");
        return _marketService.InvokeAsync(input, token);
    }

    public ValueTask DisposeAsync() { lock (_lifecycle) return new(_disposal ??= DisposeCoreAsync()); }
    private async Task DisposeCoreAsync()
    {
        Volatile.Write(ref _ready, false);
        List<Exception> failures = [];
        async Task Drain(Func<Task> work) { try { await work().ConfigureAwait(false); } catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException) { failures.Add(error); } }
        await Drain(() => _stop.CancelAsync()).ConfigureAwait(false);
        _navigation.Writer.TryComplete();
        // Activation failure is reported by the peer, but all already-acquired resources still retire.
        if (_activation is not null) { try { await _activation.ConfigureAwait(false); } catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException) { } }
        await Drain(() => _pump).ConfigureAwait(false);
        if (_marketService is not null) await Drain(() => _marketService.DisposeAsync().AsTask()).ConfigureAwait(false);
        if (_management is not null) await Drain(() => _management.DisposeAsync().AsTask()).ConfigureAwait(false);
        if (_publisher is not null) await Drain(() => _publisher.DisposeAsync().AsTask()).ConfigureAwait(false);
        if (_application is not null) await Drain(() => _application.DisposeAsync().AsTask()).ConfigureAwait(false);
        if (_business is not null) await Drain(() => _business.DisposeAsync().AsTask()).ConfigureAwait(false);
        else if (_state is not null) await Drain(() => _state.DisposeAsync().AsTask()).ConfigureAwait(false);
        if (_http is not null) await Drain(() => _http.DisposeAsync().AsTask()).ConfigureAwait(false);
        if (_rasters is not null) await Drain(() => _rasters.DisposeAsync().AsTask()).ConfigureAwait(false);
        await Drain(() => { _catalog?.Dispose(); return Task.CompletedTask; }).ConfigureAwait(false);
        await Drain(() => { _marketCatalog?.Dispose(); return Task.CompletedTask; }).ConfigureAwait(false);
        await Drain(() => { _deviceKey?.Dispose(); _marketTrust?.Dispose(); return Task.CompletedTask; }).ConfigureAwait(false);
        await Drain(() => { _keys?.Dispose(); return Task.CompletedTask; }).ConfigureAwait(false);
        _active?.Dispose(); _stop.Dispose();
        if (failures.Count != 0) throw new AggregateException("Plugin session retirement failed.", failures);
    }
    private static void Check(XsrResult result)
    { if (!result.IsSuccess) throw new InvalidOperationException("Host discovery failed: " + result.Error?.Code.Value); }
    private sealed class Observer : IXsrDispatchObserver
    {
        public void OnCompleted(XsrDispatchObservation observation) { }
    }
}
