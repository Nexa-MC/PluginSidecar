using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using PluginSidecar.Application;
using PluginSidecar.Business;
using Host = Nexa.Sidecar.Host.Data;
using Effects = Nexa.Sidecar.Host.Effects.Data;

await BusinessTests.Run();
internal static class BusinessTests
{
    internal static void Check(bool value, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? expression = null)
    { if (!value) throw new InvalidOperationException("Business boundary: " + expression); }
    internal static T Ok<T>(AbiReply<T> result) => result.Success ? result.Value : throw new InvalidOperationException(result.Error!.Code);
    internal static InvocationOptions Options() => new(Guid.NewGuid(), TimeSpan.FromSeconds(5), default) { IdempotencyKey = Guid.NewGuid().ToString("N") };
    internal static async Task Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "plugin-business-" + Guid.NewGuid().ToString("N"));
        bool unavailable = false;
        var process = new Host.MinecraftProcessSnapshot(Guid.NewGuid(), "instance", 42, Host.MinecraftProcessState.Running, null, DateTimeOffset.UtcNow, null)
        { InstanceDirectory = Path.Combine(root, "games/versions/instance"), GameDirectory = Path.Combine(root, "games"), GameWindowConfirmed = false, LauncherVisibility = Host.MinecraftLauncherVisibility.Keep, WindowTitle = "" };
        var snapshot = new Host.SidecarPublicState(-1, null, 4, [new(0, "player", "", Host.LaunchProfileKind.Microsoft, "uuid", "", "", null, "")],
            8, [process], -1, null, -1, null, -1, null, -1);
        var state = new SidecarBusinessState(token =>
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(unavailable ? XsrResult.Failure<Host.SidecarPublicState>(new(XsrErrorKind.Unavailable, XsrSemanticId.Parse("xsr.unavailable"), "Lost")) : XsrResult.Success(snapshot));
        });
        var observer = new Observer();
        var empty = new XsrCommandRouterBuilder().Build(observer);
        var confirm = new Allow();
        try
        {
            await using var business = new PluginBusinessRoutes(state, empty, empty);
            XsrQueryRouterBuilder queries = new(); XsrCommandRouterBuilder commands = new();
            queries.Register<Effects.HostEmptyRequest, Effects.HostSettingsDefinitions>(Effects.SidecarEffectsRoutes.SettingsDefinitions,
                (_, _) => ValueTask.FromResult(XsrResult.Success(new Effects.HostSettingsDefinitions([], "instance"))));
            long checkedRevision = -1;
            queries.Register<Effects.HostAccountRequest, Effects.HostAccountEligibility>(Effects.SidecarEffectsRoutes.AccountEligibility,
                (request, _) => { checkedRevision = request.ExpectedRosterRevision; Check(request.Index == 0); return ValueTask.FromResult(XsrResult.Success(new Effects.HostAccountEligibility(true, true))); });
            queries.Register<Effects.HostEmptyRequest, Effects.HostPowerState>(Effects.SidecarEffectsRoutes.Power,
                (_, _) => ValueTask.FromResult(XsrResult.Success(new Effects.HostPowerState(true, 60, false))));
            var stopped = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
            commands.Register<Effects.MinecraftCancelProcessCommand>(Effects.MinecraftRouteIds.ProcessCancel,
                (request, _) => { stopped.TrySetResult(request.SessionId); return ValueTask.FromResult(XsrResult.Success()); });
            business.BindEffects(commands.Build(observer), queries.Build(observer));
            business.BindConfirmation(confirm);
            using var shell = new ShellFixture(observer);
            Check((await shell.Client.DiscoverAsync()).IsSuccess);
            business.BindShell(shell.Client);
            using var picker = new FilePickerFixture(observer) { Selection = root };
            Check((await picker.Client.DiscoverAsync()).IsSuccess);
            business.BindFilePicker(picker.Client);
            using var clipboard = new ClipboardFixture(observer) { Text = "shared clipboard", Image = ClipboardFixture.Png() };
            Check((await clipboard.Client.DiscoverAsync()).IsSuccess);
            business.BindClipboard(clipboard.Client, _ => { });
            business.BindPlatform(new((_, _, _, _) => Task.FromResult<string?>(root),
                _ => Task.FromResult<(string?, byte[]?)>((null, null)), (_, _, _) => Task.CompletedTask));
            await using var app = new PluginApplication(root, (_, _) => throw new InvalidOperationException("Test sources use their own bootstrap factory"),
                business.Routes, confirm, (_, _) => { }, (_, _) => { }, _ => { });
            business.BindApplication(app); business.Bind(app.Runtime.Services);
            var first = new Entry("first"); var second = new Entry("second");
            Check((await app.Runtime.LoadAsync([first.Source(), second.Source()])).All(report => report.State == LoadState.Active));
            using var api = first.Context!.AsAbi100(); using var other = second.Context!.AsAbi100();
            confirm.Allowed = false;
            Check(!(await api.Platform.ReadClipboard(new(), Options())).Success && clipboard.Reads == 0);
            Check(!(await api.Platform.WriteClipboard(new("denied", null), Options())).Success && clipboard.Writes == 0);
            confirm.Allowed = true;
            var content = Ok(await api.Platform.ReadClipboard(new(), Options()));
            Check(content.Text == "shared clipboard" && content.Image is not null && clipboard.Reads == 1);
            Check(!(await other.Platform.WriteClipboard(content, Options())).Success && clipboard.Writes == 0);
            Check((await api.Platform.WriteClipboard(content, Options())).Success && clipboard.Writes == 1);
            Check((await api.Platform.WriteClipboard(new(null, null), Options())).Success && clipboard.Text is null && clipboard.Image is null);
            var account = Ok(await api.Accounts.List(new(null, 32), Options())).Items.Single();
            var eligibility = Ok(await api.Accounts.ReadLaunchEligibility(new(account.Id), Options()));
            Check(eligibility.Allowed && checkedRevision == 4 && eligibility.Reasons.Length == 1);
            Check(!(await other.Accounts.ReadPublic(new(account.Id), Options())).Success);
            var power = Ok(await api.Platform.QueryPower(new(), Options()));
            Check(power.OnBattery && power.ChargePercent == 60);
            // macOS temporary roots can contain /var -> /private/var. Grants resolve
            // links intentionally; verify the revealed directory's identity, not its alias.
            File.WriteAllText(Path.Combine(root, "reveal-marker.txt"), "selected test directory");
            var picked = Ok(await api.Platform.PickFile(new(new("Directory", []), FilePickKind.Directory, []), Options()));
            Check(picker.Picks == 1 && picker.Title == "first: Directory");
            picker.Selection = null;
            var dismissed = await api.Platform.PickFile(new(new("Dismissed", []), FilePickKind.File, []), Options());
            Check(!dismissed.Success && dismissed.Error!.Code == AbiErrors.CANCELLED);
            picker.Selection = root;
            Check(!(await other.Platform.RevealFile(picked, Options())).Success && shell.Reveals == 0);
            Check((await api.Platform.RevealFile(picked, Options())).Success && shell.Reveals == 1);
            Check(shell.DirectoryPath is { } revealed && Path.GetFileName(revealed) == Path.GetFileName(root)
                && File.ReadAllText(Path.Combine(revealed, "reveal-marker.txt")) == "selected test directory");
            var displays = Ok(await api.Platform.QueryDisplay(new(), Options()));
            Check(displays.Items.Length == 1 && displays.Items[0].Scale == 1.25);
            Check(!(await api.Platform.OpenUri(new("http://example.com"), Options())).Success && shell.Opens == 0);
            var opening = api.Platform.OpenUri(new("https://example.com"), Options()).AsTask();
            await shell.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Check(!opening.IsCompleted);
            shell.Release.TrySetResult(); Check((await opening).Success && shell.Opens == 1);
            shell.Reject = true;
            Check(!(await api.Platform.OpenUri(new("https://example.com/fail"), Options())).Success && shell.Opens == 2);
            shell.Reject = false;
            var session = Ok(await api.Minecraft.Processes.ListSessions(new(null, 32), Options())).Items.Single();
            var watch = Ok(await api.Minecraft.Processes.WatchSession(new(session.Id, null, Abi100Schemas.GameSession), Options()));
            _ = Ok(await api.Resources.ReadSubscription(new(watch, 32), Options()));
            Check((await api.Minecraft.Processes.Stop(new(session.Id), Options())).Success);
            Check(await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5)) == process.SessionId);
            snapshot = snapshot with { ProcessesRevision = 9, Processes = [process with { State = Host.MinecraftProcessState.Exited, ExitCode = 0 }] };
            await state.RefreshAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                var changes = Ok(await api.Resources.ReadSubscription(new(watch, 32), Options()));
                if (changes.Items.Any(value => value is not AbiValue.Null && !GameSession.Read(value).Running)) break;
                await Task.Delay(5, deadline.Token);
            }
            unavailable = true;
            try { await state.RefreshAsync(); throw new InvalidOperationException("Lost state accepted"); } catch (AbiHostException) { }
            Check(!state.TryRead(out _) && !business.DeveloperEnabled);
            Check(!(await api.Accounts.ReadPublic(new(account.Id), Options())).Success);
            unavailable = false; snapshot = snapshot with { AccountsRevision = 5 };
            await state.RefreshAsync();
            Check(!(await api.Accounts.ReadPublic(new(account.Id), Options())).Success);
            Check(Ok(await api.Accounts.List(new(null, 32), Options())).Items.Single().Id != account.Id);
            picker.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            picker.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            picker.Drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var waitingForPicker = api.Platform.PickFile(new(new("Pending", []), FilePickKind.File, []),
                new(Guid.NewGuid(), TimeSpan.FromSeconds(30), default) { IdempotencyKey = "picker-stop" }).AsTask();
            await picker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Check(!waitingForPicker.IsCompleted);
            clipboard.Hold = 3072;
            var waitingForClipboard = api.Platform.ReadClipboard(new(),
                new(Guid.NewGuid(), TimeSpan.FromSeconds(30), default) { IdempotencyKey = "clipboard-stop" }).AsTask();
            await clipboard.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await app.Runtime.StopAsync("first").AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Check(!(await waitingForPicker.WaitAsync(TimeSpan.FromSeconds(5))).Success);
            Check(!(await waitingForClipboard.WaitAsync(TimeSpan.FromSeconds(5))).Success && clipboard.LastReleased > 0);
            clipboard.Resume.TrySetResult();
            picker.Release.TrySetResult(); await picker.Drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!(await api.Platform.QueryPower(new(), Options())).Success);
            Check((await other.Platform.QueryPower(new(), Options())).Success);
        }
        finally { await state.DisposeAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name?.StartsWith("Nexa.Services", StringComparison.Ordinal) == true));
        await StopCancelsPendingHostQuery();
        await ContextFixture.AdapterChecks(observer);
        await FilePickerFixture.AdapterChecks(observer);
        await ClipboardFixture.AdapterChecks(observer);
        await HttpHandlerTests.Run(observer);
        await ThemeOptionsTests.Run(observer);
        await ThemeActivationTests.Run(observer);
        Console.WriteLine("PASS: independent Sidecar business projections, credential-free eligibility, owner isolation, process cancellation, acknowledged shell effects, typed theme configuration and local document validation, file-grant isolation, revision fencing, lost-state recovery and retirement.");
    }
    private static async Task StopCancelsPendingHostQuery()
    {
        string root = Path.Combine(Path.GetTempPath(), "plugin-business-stop-" + Guid.NewGuid().ToString("N"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<Host.SettingsEffectiveSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new SidecarBusinessState(_ => ValueTask.FromResult(XsrResult.Success(
            new Host.SidecarPublicState(-1, null, -1, null, -1, null, -1, null, -1, null, -1, null, 1))));
        try
        {
            var observer = new Observer();
            var empty = new XsrCommandRouterBuilder().Build(observer);
            XsrQueryRouterBuilder queries = new();
            queries.Register<Host.SettingsEffectiveQuery, Host.SettingsEffectiveSnapshot>(Host.SettingsPolicyContract.EffectiveQuery, async (_, token) =>
            {
                entered.TrySetResult();
                try { return XsrResult.Success(await pending.Task.WaitAsync(token)); }
                catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            });
            await using var business = new PluginBusinessRoutes(state, empty, empty, foundationQueries: queries.Build(observer));
            await using var app = new PluginApplication(root, (_, _) => throw new InvalidOperationException(), business.Routes,
                new Allow(), (_, _) => { }, (_, _) => { }, _ => { });
            await state.RefreshAsync();
            business.BindApplication(app); business.Bind(app.Runtime.Services);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await business.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await business.DisposeAsync();
            Check(!business.DeveloperEnabled);
        }
        finally { await state.DisposeAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine("PASS: Sidecar retirement cancels pending Host settings queries and is idempotent.");
    }
    private sealed class Observer : IXsrDispatchObserver { public void OnCompleted(XsrDispatchObservation observation) { } }
    private sealed class Allow : IActionConfirmation
    { internal bool Allowed = true; public ValueTask<bool> ConfirmAsync(ActionConfirmationRequest request, CancellationToken cancellation) => ValueTask.FromResult(Allowed); }
    private sealed class Entry(string id) : IPluginBootstrapV1
    {
        internal ABIContext? Context;
        private static readonly ImmutableArray<string> Permissions = ["nexa.accounts.read", "nexa.minecraft.processes.read", "nexa.minecraft.processes.write", "nexa.platform.read", "nexa.platform.write", "nexa.tasks.read", "nexa.tasks.write", "nexa.resources.read", "nexa.resources.write"];
        internal PluginLoadSource Source() => new(new(1, id, "1.0.0", 1, new("test", "test"), new([], [], "fixture"), [100], [], [], [], Permissions, []),
            Permissions, _ => ValueTask.FromResult<IPluginBootstrapV1>(this), () => ValueTask.CompletedTask);
        public async ValueTask<AbiReply<PreInitResult>> OnPreInitialize(PreInitContext context)
        {
            var draft = new Abi100Draft(Ok(await context.Negotiator.GetABIContext(100, Options())))
                .Require("nexa.accounts/100").Require("nexa.minecraft.processes/100").Require("nexa.platform/100").Require("nexa.tasks/100").Require("nexa.resources/100");
            Context = Ok(await draft.Make(context.Negotiator, Guid.NewGuid(), Permissions, default));
            return AbiReply<PreInitResult>.Ok(new(Context, [], [], [], []));
        }
        public ValueTask<AbiReply<Unit>> OnInitialize(ABIContext context, StageContext stage) => Done();
        public ValueTask<AbiReply<Unit>> OnPostInitialize(ABIContext context, StageContext stage) => Done();
        public ValueTask<AbiReply<CheckupResult>> OnCheckup(ABIContext context, StageContext stage) => ValueTask.FromResult(AbiReply<CheckupResult>.Ok(new(CheckupStatus.Passed, [], [])));
        public ValueTask<AbiReply<Unit>> OnFinalize(ABIContext context, StageContext stage) => Done();
        public ValueTask<AbiReply<Unit>> OnActivated(ABIContext context, InvocationContext invocation) => Done();
        public ValueTask<AbiReply<Unit>> OnStop(StopContext context) => Done();
        public ValueTask<AbiReply<Unit>> OnAbort(AbortContext context) => Done();
        private static ValueTask<AbiReply<Unit>> Done() => ValueTask.FromResult(AbiReply<Unit>.Ok(new()));
    }
}
