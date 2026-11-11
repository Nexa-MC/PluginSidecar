using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
namespace Nexa.Plugin.Hosting;

/// <summary>Trusted acquisition verifies/decrypts bytes before CreateEntry is called.</summary>
public sealed record PluginLoadSource(PluginManifest Manifest, ImmutableArray<string> GrantedPermissions,
    Func<CancellationToken, ValueTask<IPluginBootstrapV1>> CreateVerifiedEntry, Func<ValueTask> Release);
public sealed record PluginLoadReport(PluginIdentity Identity, LoadState State, LoadStage LastStage, AbiError? Error, ImmutableArray<AbiError> CleanupErrors)
{ public ImmutableArray<Finding> Diagnostics { get; init; } = []; }

/// <summary>Serialized dependency groups with atomic publication and fail-before-stop hot conflicts.</summary>
public sealed partial class PluginLoadCoordinator(CapabilityAuthority authority, AtomicDefinitionRegistry registry,
    Func<PluginIdentity, ValueTask>? retireResources = null, PluginRuntimeServices? services = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid,Attempt> _instances=[];
    private readonly Dictionary<string, Attempt> _active = new(StringComparer.Ordinal);
    private bool _disposed;
    public ImmutableArray<PluginIdentity> Active { get { lock (authority.PublicationGate) return _active.Values.Select(a => a.Identity).ToImmutableArray(); } }

    public async ValueTask<ImmutableArray<PluginLoadReport>> LoadAsync(IReadOnlyList<PluginLoadSource> sources, CancellationToken cancellation = default)
    {
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (sources.Count > 256 || sources.Select(s => s.Manifest.PluginId).Distinct(StringComparer.Ordinal).Count() != sources.Count) throw new ArgumentException("Duplicate or excessive plugin load batch.");
            var attempts = sources.Select(s => new Attempt(s)).ToArray();
            foreach (var attempt in attempts)
            {
                try { ValidateManifest(attempt.Source.Manifest); }
                catch (Exception error) when (Recoverable(error)) { attempt.State = LoadState.Failed; attempt.Failure = Error(attempt, AbiErrors.PACKAGE_INVALID); }
            }
            var byId = attempts.ToDictionary(a => a.Identity.PluginId, StringComparer.Ordinal);
            var groups = BuildGroups(attempts, byId);
            var preparationFailures = new Dictionary<Guid, AbiError>();
            // Verify and negotiate the whole candidate batch before using its conflict declarations.
            foreach (var members in groups)
            {
                Guid group = Guid.NewGuid();
                foreach (var member in members) member.GroupId = group;
                AbiError? failure = members.Select(m => m.Failure).FirstOrDefault(f => f is not null);
                if (failure is not null) { preparationFailures[group] = failure; continue; }
                try { foreach (var member in members) member.Admission = authority.Admit(member.Identity, group, member.Source.GrantedPermissions.Where(member.Source.Manifest.RequestedPermissions.Contains).ToImmutableArray()); }
                catch (Exception error) when (Recoverable(error)) { preparationFailures[group] = Error(members[0], AbiErrors.CAPABILITY_DENIED); continue; }
                foreach (var member in members)
                {
                    try
                    {
                        cancellation.ThrowIfCancellationRequested();
                        member.Entry = await member.Source.CreateVerifiedEntry(cancellation).ConfigureAwait(false);
                        member.Stage = LoadStage.PreInitialize;
                        lock(authority.PublicationGate)_instances.Add(member.Identity.LoadInstanceId,member);
                        var bootstrap = new BootstrapServices(member,authority);
                        AbiReply<PreInitResult> pre;
                        try { pre = await Bound(member, token => member.Entry.OnPreInitialize(new(member.Identity, 1, member.Admission!.Negotiator, bootstrap, bootstrap, bootstrap, token, StageBudgets.For(LoadStage.PreInitialize))), cancellation).ConfigureAwait(false); }
                        finally { member.Admission!.FreezePreInitialize(); }
                        if (!pre.Success) { failure = pre.Error; break; }
                        if (!member.Admission!.IsReady || !ReferenceEquals(pre.Value.FinalContext, member.Admission.Context) || !SameDeclarations(member.Source.Manifest, pre.Value))
                        { failure = Error(member, AbiErrors.ABI_CONTRACT_MISMATCH); break; }
                        member.Final = pre.Value.FinalContext; member.Completed.Add(LoadStage.PreInitialize);
                        var missing = member.Source.Manifest.RequiredDependencies.FirstOrDefault(dependency => !ProviderExists(dependency, byId));
                        if (missing is not null) { failure = Error(member, AbiErrors.DEPENDENCY_MISSING); break; }
                    }
                    catch (Exception e) when (Recoverable(e)) { failure = Error(member, e is OperationCanceledException ? AbiErrors.CANCELLED : AbiErrors.PLUGIN_EXCEPTION); break; }
                }
                if (failure is not null) preparationFailures[group] = failure;
            }
            foreach (var members in groups)
            {
                Guid group = members[0].GroupId;
                AbiError? failure = preparationFailures.GetValueOrDefault(group);
                if (failure is null)
                {
                    foreach (var phase in new[] { LoadStage.Initialize, LoadStage.PostInitialize, LoadStage.Checkup, LoadStage.Finalize })
                    {
                        foreach (var member in members)
                        {
                            member.Stage = phase;
                            var stage = new StageContext(member.Identity, phase, Guid.NewGuid(), group, cancellation, StageBudgets.For(phase));
                            bool entered = false;
                            try
                            {
                                member.Admission!.Enter(stage); entered = true;
                                var reply = await Bound(member, token => InvokeStage(member, stage with { Cancellation = token }), cancellation).ConfigureAwait(false);
                                if (!reply.Success) { failure = reply.Error; break; }
                                member.Completed.Add(phase);
                            }
                            catch (Exception e) when (Recoverable(e)) { failure = Error(member, e is OperationCanceledException ? AbiErrors.DEADLINE_EXCEEDED : AbiErrors.PLUGIN_EXCEPTION); break; }
                            finally { if (entered) member.Admission!.End(stage); }
                        }
                        if (phase == LoadStage.Checkup)
                        {
                            // Host conflict checks cannot be overridden by a successful plugin Checkup.
                            var hot = _active.Values.Where(old => members.Any(member => Conflicts(member, old))).ToArray();
                            bool batchConflict = members.Any(member => attempts.Any(other => !ReferenceEquals(member, other) && !preparationFailures.ContainsKey(other.GroupId) && Conflicts(member, other)));
                            if (hot.Length != 0 || batchConflict)
                            {
                                failure = Error(members[0], AbiErrors.PLUGIN_CONFLICT);
                                await StopClosure(hot.Select(a => a.Identity.PluginId).ToHashSet(StringComparer.Ordinal)).ConfigureAwait(false);
                            }
                            if (failure is null && !registry.MissingReferences(group).IsEmpty) failure = Error(members[0], AbiErrors.DEPENDENCY_MISSING);
                        }
                        if (failure is not null) break;
                    }
                }
                if (failure is null)
                {
                    try
                    {
                        authority.CommitGroup(members.Select(a => a.Admission!).ToArray(), () =>
                        {
                            if (members.Any(m => _active.ContainsKey(m.Identity.PluginId))) throw new InvalidOperationException(AbiErrors.CONFLICT);
                            services?.ValidateCommit(group);
                            registry.Publish(group); services?.Publish(group);
                            foreach (var member in members) { _active.Add(member.Identity.PluginId, member); member.State = LoadState.Active; }
                        });
                        foreach (var member in members)
                        {
                            if (member.State != LoadState.Active) continue;
                            var activated = await CleanupCall(member, () => member.Entry!.OnActivated(member.Final!, new(member.Identity, Guid.NewGuid(), Guid.NewGuid(), null, default, TimeSpan.FromSeconds(5)))).ConfigureAwait(false);
                            if (activated is null) services?.Activated(member.Identity);
                            if (activated is not null)
                            {
                                member.Cleanup.Add(activated); member.Failure = activated;
                                await StopClosure(new(StringComparer.Ordinal) { member.Identity.PluginId }).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (Exception e) when (Recoverable(e)) { failure = Error(members[0], AbiErrors.COMMIT_REJECTED); }
                }
                if (failure is not null)
                {
                    registry.Abort(group); services?.Abort(group);
                    foreach (var member in members.Reverse())
                    {
                        member.Invocations.Cancel(); member.Failure = failure; member.State = LoadState.Failed; member.Admission?.Retire(true);
                        if (member.Entry is not null && member.Stage != LoadStage.Checksum)
                        {
                            var cleanup = await CleanupCall(member, () => member.Entry.OnAbort(new(member.Identity, member.Stage, failure, member.Completed.ToImmutableArray(), new AbiValue.Null(), default, StageBudgets.Cleanup, member.Final))).ConfigureAwait(false);
                            if (cleanup is not null) member.Cleanup.Add(cleanup);
                        }
                        await RunRegisteredCleanup(member).ConfigureAwait(false);
                        await RetireResources(member).ConfigureAwait(false);
                        await Release(member).ConfigureAwait(false); member.Admission?.CompleteStop();
                    }
                }
            }
            return attempts.Select(a => new PluginLoadReport(a.Identity, a.State, a.Stage, a.Failure, a.Cleanup.ToImmutableArray()) { Diagnostics = Findings(a) }).ToImmutableArray();
        }
        finally { _gate.Release(); }
    }

    public async ValueTask StopAsync(string pluginId, CancellationToken cancellation = default)
    { await _gate.WaitAsync(cancellation).ConfigureAwait(false); try { await StopClosure(new(StringComparer.Ordinal) { pluginId }).ConfigureAwait(false); } finally { _gate.Release(); } }

    private async ValueTask StopClosure(HashSet<string> ids)
    {
        bool changed;
        do { changed = false; foreach (var entry in _active.Values) if (entry.Source.Manifest.RequiredDependencies.Any(d => ids.Contains(d.PluginId))) changed |= ids.Add(entry.Identity.PluginId); foreach(var consumer in (services?.RequiredConsumers(ids)??[]).Concat(registry.RequiredConsumers(ids)))changed |= ids.Add(consumer); } while (changed);
        var affected = _active.Values.Where(a => ids.Contains(a.Identity.PluginId)).ToArray();
        var requiredConsumers = affected.ToDictionary(a => a.Identity.PluginId, a => (services?.RequiredConsumers(new HashSet<string> {a.Identity.PluginId}) ?? []).Concat(registry.RequiredConsumers(new HashSet<string> {a.Identity.PluginId})).Distinct().ToImmutableArray());
        // Seal every affected instance before any callback; cycles cannot keep a provider reachable.
        lock (authority.PublicationGate)
        {
            foreach (var entry in affected) { entry.Invocations.Cancel(); entry.Admission!.Retire(false); registry.Retire(entry.Identity.LoadInstanceId); services?.Retire(entry.Identity.LoadInstanceId); _active.Remove(entry.Identity.PluginId); }
        }
        var ordered = new List<Attempt>(); var visiting = new HashSet<string>(StringComparer.Ordinal);
        void ConsumersFirst(Attempt entry)
        {
            if (!visiting.Add(entry.Identity.PluginId)) return;
            foreach (var consumer in affected.Where(a => a.Source.Manifest.RequiredDependencies.Any(d => d.PluginId == entry.Identity.PluginId) || requiredConsumers[entry.Identity.PluginId].Contains(a.Identity.PluginId))) ConsumersFirst(consumer);
            ordered.Add(entry);
        }
        foreach (var entry in affected) ConsumersFirst(entry);
        foreach (var entry in ordered)
        {
            var cleanup = await CleanupCall(entry, () => entry.Entry!.OnStop(new(entry.Identity, entry.Final!, "plugin.stopped", default, StageBudgets.Cleanup))).ConfigureAwait(false);
            if (cleanup is not null) entry.Cleanup.Add(cleanup);
            await RunRegisteredCleanup(entry).ConfigureAwait(false);
            await RetireResources(entry).ConfigureAwait(false);
            await Release(entry).ConfigureAwait(false); entry.Admission!.CompleteStop(); entry.State = LoadState.Unloaded;
        }
    }
    private bool ProviderExists(PluginDependency dependency, Dictionary<string, Attempt> batch) =>
        batch.TryGetValue(dependency.PluginId, out var pending) ? PluginVersion.Matches(pending.Identity.PluginVersion, dependency.Version) && pending.State != LoadState.Failed :
        _active.TryGetValue(dependency.PluginId, out var active) && PluginVersion.Matches(active.Identity.PluginVersion, dependency.Version);
    private static bool Conflicts(Attempt left, Attempt right) => left.Identity.PluginId == right.Identity.PluginId ||
        left.Source.Manifest.Conflicts.Any(c => c.PluginId == right.Identity.PluginId && PluginVersion.Matches(right.Identity.PluginVersion, c.Version)) ||
        right.Source.Manifest.Conflicts.Any(c => c.PluginId == left.Identity.PluginId && PluginVersion.Matches(left.Identity.PluginVersion, c.Version));
    private static ImmutableArray<PluginDependency> Dependencies(PluginManifest manifest) => manifest.RequiredDependencies.IsDefault ? [] : manifest.RequiredDependencies.Where(d => d is not null && !string.IsNullOrWhiteSpace(d.PluginId) && d.Version is not null).ToImmutableArray();
    private static List<Attempt[]> BuildGroups(Attempt[] attempts, Dictionary<string, Attempt> byId)
    {
        var groups = new List<Attempt[]>(); var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var first in attempts)
        {
            if (visited.Contains(first.Identity.PluginId)) continue;
            var group = new List<Attempt>(); var queue = new Queue<Attempt>(); queue.Enqueue(first);
            while (queue.TryDequeue(out var member))
            {
                if (!visited.Add(member.Identity.PluginId)) continue; group.Add(member);
                foreach (var dependency in Dependencies(member.Source.Manifest)) if (byId.TryGetValue(dependency.PluginId, out var provider)) queue.Enqueue(provider);
                foreach (var consumer in attempts.Where(a => Dependencies(a.Source.Manifest).Any(d => d.PluginId == member.Identity.PluginId))) queue.Enqueue(consumer);
            }
            var ordered = new List<Attempt>(); var ordering = new HashSet<string>(StringComparer.Ordinal);
            void ProviderFirst(Attempt member)
            {
                if (!ordering.Add(member.Identity.PluginId)) return;
                foreach (var dependency in Dependencies(member.Source.Manifest).OrderBy(d => d.PluginId, StringComparer.Ordinal))
                    if (byId.TryGetValue(dependency.PluginId, out var provider) && group.Contains(provider)) ProviderFirst(provider);
                ordered.Add(member);
            }
            foreach (var member in group.OrderBy(a => a.Identity.PluginId, StringComparer.Ordinal)) ProviderFirst(member);
            groups.Add(ordered.ToArray());
        }
        return groups;
    }
    private static async ValueTask<AbiReply<Unit>> InvokeStage(Attempt member, StageContext stage)
    {
        switch (stage.Stage)
        {
            case LoadStage.Initialize: return await member.Entry!.OnInitialize(member.Final!, stage).ConfigureAwait(false);
            case LoadStage.PostInitialize: return await member.Entry!.OnPostInitialize(member.Final!, stage).ConfigureAwait(false);
            case LoadStage.Finalize: return await member.Entry!.OnFinalize(member.Final!, stage).ConfigureAwait(false);
            case LoadStage.Checkup:
                var check = await member.Entry!.OnCheckup(member.Final!, stage).ConfigureAwait(false);
                if (!check.Success) return AbiReply<Unit>.Fail(check.Error!);
                if(!AddFindings(member,check.Value.Findings))return AbiReply<Unit>.Fail(Error(member,AbiErrors.MESSAGE_TOO_LARGE));
                return check.Value.Status == CheckupStatus.Failed || check.Value.Findings.Any(f => f.Fatal) ? AbiReply<Unit>.Fail(Error(member, AbiErrors.CHECKUP_FAILED)) : AbiReply<Unit>.Ok(new());
            default: throw new InvalidOperationException();
        }
    }
    private static async ValueTask<AbiReply<T>> Bound<T>(Attempt member, Func<CancellationToken, ValueTask<AbiReply<T>>> callback, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(StageBudgets.For(member.Stage));
        var task = Task.Run(async()=>await callback(deadline.Token).ConfigureAwait(false),deadline.Token);
        try { return await task.WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch { _ = Observe(task); throw; }
    }
    private static async Task Observe(Task task) { try { await task.ConfigureAwait(false); } catch (Exception e) when (Recoverable(e)) { } }
    private static async ValueTask<AbiError?> CleanupCall(Attempt member, Func<ValueTask<AbiReply<Unit>>> callback)
    {
        try { var task = Task.Run(async()=>await callback().ConfigureAwait(false)); _ = Observe(task); var result = await task.WaitAsync(StageBudgets.Cleanup).ConfigureAwait(false); return result.Error; }
        catch (Exception e) when (Recoverable(e)) { return Error(member, AbiErrors.PLUGIN_EXCEPTION); }
    }
    private static async ValueTask RunRegisteredCleanup(Attempt member)
    {
        CallbackHandle[] cleanupHandles;
        lock(member.Callbacks) cleanupHandles=member.CleanupCallbacks.AsEnumerable().Reverse().ToArray();
        using var deadline = new CancellationTokenSource(StageBudgets.Cleanup);
        long started=System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var handle in cleanupHandles)
        {
            var remaining=StageBudgets.Cleanup-System.Diagnostics.Stopwatch.GetElapsedTime(started);
            if(remaining<=TimeSpan.Zero){member.Cleanup.Add(Error(member,AbiErrors.DEADLINE_EXCEEDED));break;}
            lock(member.Callbacks){if(!member.CleanupRegistrations.Remove(handle.Value))continue;member.CleanupCallbacks.Remove(handle);}
            var result=await InvokeCleanup(member,handle.Value,member.State==LoadState.Active?"Stop":"Abort",new(Guid.NewGuid(),remaining,deadline.Token)).ConfigureAwait(false);
            if(!result.Success)member.Cleanup.Add(result.Error!);
        }
        lock(member.Callbacks) { member.CleanupCallbacks.Clear();member.CleanupRegistrations.Clear(); member.Callbacks.Clear(); }
    }
    private async ValueTask Release(Attempt member)
    { lock(authority.PublicationGate)_instances.Remove(member.Identity.LoadInstanceId);try { var task = Task.Run(async()=>await member.Source.Release().ConfigureAwait(false)); _ = Observe(task); await task.WaitAsync(StageBudgets.Cleanup).ConfigureAwait(false); } catch (Exception e) when (Recoverable(e)) { member.Cleanup.Add(Error(member, AbiErrors.PLUGIN_EXCEPTION)); } }
    private async ValueTask RetireResources(Attempt member)
    {
        if (retireResources is null) return;
        try { var task = retireResources(member.Identity).AsTask(); _ = Observe(task); await task.WaitAsync(StageBudgets.Cleanup).ConfigureAwait(false); }
        catch (Exception e) when (Recoverable(e)) { member.Cleanup.Add(Error(member, AbiErrors.PLUGIN_EXCEPTION)); }
    }
    private static bool SameDeclarations(PluginManifest manifest, PreInitResult result) => manifest.RequiredDependencies.SequenceEqual(result.RequiredDependencies) && manifest.OptionalDependencies.SequenceEqual(result.OptionalDependencies) && manifest.Conflicts.SequenceEqual(result.Conflicts);
    private static void ValidateManifest(PluginManifest manifest)
    {
        if (manifest.RequiredDependencies.IsDefault || manifest.OptionalDependencies.IsDefault || manifest.Conflicts.IsDefault || manifest.RequestedPermissions.IsDefault || manifest.DeclaredAbiCandidates.IsDefault ||
            manifest.RequiredDependencies.Any(d => d is null || string.IsNullOrWhiteSpace(d.PluginId) || d.Version is null) || manifest.OptionalDependencies.Any(d => d is null || string.IsNullOrWhiteSpace(d.PluginId) || d.Version is null))
            throw new FormatException(AbiErrors.PACKAGE_INVALID);
        _ = PluginVersion.Parse(manifest.PluginVersion);
        _ = new Nexa.Plugin.Sdk.Abi100.Data.QualifiedId(manifest.PluginId + ":identity");
        if (manifest.PluginId == "nexa" || manifest.BootstrapVersion != 1 || manifest.ManifestVersion != 1 || !manifest.DeclaredAbiCandidates.Contains(100)) throw new FormatException(AbiErrors.PACKAGE_INVALID);
        foreach (var range in manifest.RequiredDependencies.Select(d => d.Version).Concat(manifest.OptionalDependencies.Select(d => d.Version)).Concat(manifest.Conflicts.Select(c => c.Version)))
        { if (range.Minimum is not null) _ = PluginVersion.Parse(range.Minimum); if (range.Maximum is not null) _ = PluginVersion.Parse(range.Maximum); }
    }
    private static bool Recoverable(Exception e) => e is not OutOfMemoryException and not AccessViolationException;
    private static AbiError Error(Attempt member, string code) => new(code, code, member.Stage.ToString(), "plugin.load", member.Identity.PluginId, member.Identity.LoadInstanceId, Guid.NewGuid(), false, ImmutableDictionary<string, string>.Empty);
    public async ValueTask DisposeAsync()
    { await _gate.WaitAsync().ConfigureAwait(false); try { if (_disposed) return; _disposed = true; await StopClosure(_active.Keys.ToHashSet(StringComparer.Ordinal)).ConfigureAwait(false); } finally { _gate.Release(); } }
    private sealed class Attempt(PluginLoadSource source)
    {
        internal PluginLoadSource Source = source;
        internal PluginIdentity Identity = new(source.Manifest.PluginId, source.Manifest.PluginVersion, Guid.NewGuid());
        internal Guid GroupId;
        internal IPluginBootstrapV1? Entry;
        internal CancellationTokenSource Invocations = new();
        internal CapabilityAuthority.Admission? Admission;
        internal ABIContext? Final;
        internal LoadStage Stage = LoadStage.Checksum;
        internal LoadState State = LoadState.Verified;
        internal AbiError? Failure;
        internal List<LoadStage> Completed = [];
        internal List<AbiError> Cleanup = [];
        internal List<Finding> Diagnostics = [];
        internal Dictionary<ulong, Func<AbiValue, InvocationOptions, ValueTask<AbiReply<AbiValue>>>> Callbacks = [];
        internal List<CallbackHandle> CleanupCallbacks = [];
        internal Dictionary<ulong,Nexa.Plugin.Sdk.Abi100.Data.RegistrationRef> CleanupRegistrations=[];
        internal ulong NextCallback;
    }
    private sealed class BootstrapServices(Attempt member,CapabilityAuthority authority) : IDiagnosticsV1, IResourcesV1, ICallbacksV1
    {
        public ValueTask<AbiReply<Unit>> ReportDiagnostic(Finding finding, InvocationOptions options)
        {
            return ValueTask.FromResult(AddFindings(member,[finding])?AbiReply<Unit>.Ok(new()):AbiReply<Unit>.Fail(Error(member,AbiErrors.MESSAGE_TOO_LARGE)));
        }
        public ValueTask<AbiReply<RegistrationHandle>> RegisterCleanup(CallbackHandle callback,InvocationOptions options)
        {
            try{options.Cancellation.ThrowIfCancellationRequested();var reference=RegisterCleanupCore(member,authority,callback.Value);return ValueTask.FromResult(AbiReply<RegistrationHandle>.Ok(new(reference.Handle)));}
            catch(Exception error)when(Recoverable(error)){return ValueTask.FromResult(AbiReply<RegistrationHandle>.Fail(Error(member,error is AbiHostException known?known.Code:error is OperationCanceledException?AbiErrors.CANCELLED:AbiErrors.PLUGIN_EXCEPTION)));}
        }
        public CallbackHandle Register(string contractId, Func<AbiValue, InvocationOptions, ValueTask<AbiReply<AbiValue>>> callback)
        {
            ArgumentNullException.ThrowIfNull(callback);
            lock (member.Callbacks) { if(member.Invocations.IsCancellationRequested)throw new InvalidOperationException(AbiErrors.PLUGIN_STOPPED);if (member.Callbacks.Count >= 256) throw new InvalidOperationException(AbiErrors.MESSAGE_TOO_LARGE);var handle = new CallbackHandle(++member.NextCallback); member.Callbacks.Add(handle.Value, callback); return handle; }
        }
        public void Release(CallbackHandle callback) { lock (member.Callbacks) { member.Callbacks.Remove(callback.Value); member.CleanupCallbacks.Remove(callback);member.CleanupRegistrations.Remove(callback.Value); } }
    }
}
