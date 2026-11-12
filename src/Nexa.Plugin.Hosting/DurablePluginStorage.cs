using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100.Data;
namespace Nexa.Plugin.Hosting;

/// <summary>Private namespace storage with atomic durable writes, revision CAS and owned transaction tokens.</summary>
public sealed partial class DurablePluginStorage(string root, Func<SchemaRef, AbiValue, bool> validateSchema) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, NamespaceLease> _namespaces = [];
    private readonly Dictionary<Guid, TransactionLease> _transactions = [];
    private readonly Dictionary<string, NamespaceState> _states = new(StringComparer.Ordinal);
    private bool _disposed;
    private FileStream? _directoryLease;
    private const int MaximumNamespaceBytes = 1024 * 1024;
    private const int MaximumKeys = 4096;

    public IEnumerable<HostCapabilityRoute> Routes =>
    [
        HostRoutes.Typed<NamespaceRequest, StorageNamespace>("Storage", "OpenNamespace", Open),
        HostRoutes.Typed<StorageKey, StorageValue>("Storage", "Read", Read),
        HostRoutes.Typed<StorageWrite, StorageValue>("Storage", "Write", Write),
        HostRoutes.Typed<StorageWrite, StorageValue>("Storage", "CompareExchange", Write),
        HostRoutes.Typed<StorageQuery, Page<StorageEntry>>("Storage", "Query", Query),
        HostRoutes.Typed<StorageNamespace, StorageTransaction>("Storage", "BeginTransaction", Begin),
        HostRoutes.Typed<StorageCommit, StorageNamespace>("Storage", "Commit", Commit),
        HostRoutes.Typed<StorageMigration, OperationRef>("Storage", "Migrate", Migrate)
    ];

    public async ValueTask<StorageNamespace> Open(HostInvocation caller, NamespaceRequest request)
    {
        RequireOwner(caller.Identity, request.Namespace);
        if (request.SchemaVersion == 0) throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
        await _gate.WaitAsync(caller.Options.Cancellation).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_directoryLease is null)
            {
                bool exists = Directory.Exists(root); Directory.CreateDirectory(root);
                if (!exists && !OperatingSystem.IsWindows()) File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                _directoryLease = new FileStream(Path.Combine(root, ".owner-lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            if (_namespaces.Count >= 1024) throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
            string key = caller.Identity.PluginId + "\n" + request.Namespace.Value;
            if (!_states.TryGetValue(key, out var state))
            {
                if (_states.Count >= 64) throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
                state = await Load(key, request.SchemaVersion, caller.Options.Cancellation).ConfigureAwait(false);
                _states.Add(key, state);
            }
            if (state.SchemaVersion != request.SchemaVersion) throw new AbiHostException(AbiErrors.SCHEMA_INVALID,
                ImmutableDictionary<string, string>.Empty.Add("actualSchemaVersion", state.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            var token = Guid.NewGuid(); var reference = new StorageNamespace(token, caller.Identity.LoadInstanceId, request.Namespace, state.Revision);
            _namespaces.Add(token, new(caller.Identity, reference, state)); return reference;
        }
        finally { _gate.Release(); }
    }
    public async ValueTask<StorageValue> Read(HostInvocation caller, StorageKey request)
    {
        await _gate.WaitAsync(caller.Options.Cancellation).ConfigureAwait(false);
        try { var lease = Resolve(caller, request.Namespace); ValidateKey(request.Key); return lease.State.Values.GetValueOrDefault(request.Key) ?? throw new AbiHostException(AbiErrors.NOT_FOUND); }
        finally { _gate.Release(); }
    }
    public async ValueTask<StorageValue> Write(HostInvocation caller, StorageWrite request)
    {
        await _gate.WaitAsync(caller.Options.Cancellation).ConfigureAwait(false);
        try
        {
            var lease = Resolve(caller, request.Namespace);
            var values = lease.State.Values.ToBuilder(); Apply(values, request);
            var revision = NewRevision(); var next = values.ToImmutable();
            await Save(lease.State, next, revision, caller.Options.Cancellation).ConfigureAwait(false);
            lease.State.Values = next; lease.State.Revision = revision; return next[request.Key];
        }
        finally { _gate.Release(); }
    }
    public async ValueTask<Page<StorageEntry>> Query(HostInvocation caller, StorageQuery request)
    {
        await _gate.WaitAsync(caller.Options.Cancellation).ConfigureAwait(false);
        try
        {
            var state = Resolve(caller, request.Namespace).State;
            if (request.Page.Limit is < 1 or > 256 || request.Prefix.Length > 512) throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
            string? after = null;
            if (request.Page.Cursor is { } cursor)
            {
                try
                {
                    var data = DataCodec.ReadMap(AbiValueBinary.Decode(Convert.FromBase64String(cursor)));
                    if (DataCodec.ReadString(DataCodec.Field(data, "revision")) != state.Revision.Value || DataCodec.ReadString(DataCodec.Field(data, "prefix")) != request.Prefix)
                        throw new AbiHostException(AbiErrors.STALE_REVISION);
                    after = DataCodec.ReadString(DataCodec.Field(data, "after"));
                }
                catch (FormatException) { throw new AbiHostException(AbiErrors.SCHEMA_INVALID); }
            }
            var matches = state.Values.Where(p => p.Key.StartsWith(request.Prefix, StringComparison.Ordinal) && (after is null || string.CompareOrdinal(p.Key, after) > 0)).OrderBy(p => p.Key, StringComparer.Ordinal).Take(checked((int)request.Page.Limit + 1)).ToArray();
            var page = matches.Take((int)request.Page.Limit).ToArray();
            string? next = matches.Length > request.Page.Limit ? Convert.ToBase64String(AbiValueBinary.Encode(DataCodec.Map(("revision", DataCodec.WriteString(state.Revision.Value)), ("prefix", DataCodec.WriteString(request.Prefix)), ("after", DataCodec.WriteString(page[^1].Key))))) : null;
            return new(page.Select(p => new StorageEntry(p.Key, p.Value)).ToImmutableArray(), next, state.Revision);
        }
        finally { _gate.Release(); }
    }
    public async ValueTask<StorageTransaction> Begin(HostInvocation caller, StorageNamespace request)
    {
        await _gate.WaitAsync(caller.Options.Cancellation).ConfigureAwait(false);
        try
        {
            var lease = Resolve(caller, request);
            if (_transactions.Count >= 1024) throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
            var reference = new StorageTransaction(Guid.NewGuid(), request, lease.State.Revision);
            _transactions.Add(reference.Token, new(caller.Identity.LoadInstanceId, reference, lease)); return reference;
        }
        finally { _gate.Release(); }
    }
    public async ValueTask<StorageNamespace> Commit(HostInvocation caller, StorageCommit request)
    {
        await _gate.WaitAsync(caller.Options.Cancellation).ConfigureAwait(false);
        try
        {
            if (!_transactions.TryGetValue(request.Transaction.Token, out var transaction) || transaction.Owner != caller.Identity.LoadInstanceId || transaction.Reference != request.Transaction)
                throw new AbiHostException(AbiErrors.HANDLE_EXPIRED);
            var lease = Resolve(caller, request.Transaction.Namespace);
            if (lease.State.Revision != request.Transaction.Revision) throw new AbiHostException(AbiErrors.STALE_REVISION);
            if (request.Writes.IsDefault || request.Writes.Length is < 1 or > 256 || request.Writes.Select(w => w.Key).Distinct(StringComparer.Ordinal).Count() != request.Writes.Length)
                throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
            var values = lease.State.Values.ToBuilder();
            foreach (var write in request.Writes)
            {
                if (Resolve(caller, write.Namespace) != lease) throw new AbiHostException(AbiErrors.HANDLE_OWNER_MISMATCH);
                Apply(values, write);
            }
            var revision = NewRevision(); var next = values.ToImmutable();
            await Save(lease.State, next, revision, caller.Options.Cancellation).ConfigureAwait(false);
            lease.State.Values = next; lease.State.Revision = revision; _transactions.Remove(transaction.Reference.Token);
            return lease.Reference with { Revision = revision };
        }
        finally { _gate.Release(); }
    }
    public async ValueTask RetireAsync(Guid loadInstanceId)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var token in _namespaces.Where(p => p.Value.Owner.LoadInstanceId == loadInstanceId).Select(p => p.Key).ToArray()) _namespaces.Remove(token);
            foreach (var token in _transactions.Where(p => p.Value.Owner == loadInstanceId).Select(p => p.Key).ToArray()) _transactions.Remove(token);
            foreach (var key in _states.Where(p => !_namespaces.Values.Any(n => ReferenceEquals(n.State, p.Value))).Select(p => p.Key).ToArray()) _states.Remove(key);
        }
        finally { _gate.Release(); }
    }
    private NamespaceLease Resolve(HostInvocation caller, StorageNamespace reference)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_namespaces.TryGetValue(reference.Token, out var lease) || lease.Owner != caller.Identity || reference.LoadInstanceId != caller.Identity.LoadInstanceId || reference.Namespace != lease.Reference.Namespace)
            throw new AbiHostException(AbiErrors.HANDLE_OWNER_MISMATCH);
        return lease;
    }
    private void Apply(ImmutableDictionary<string, StorageValue>.Builder values, StorageWrite request)
    {
        ValidateKey(request.Key); DataCodec.Validate(request.Value);
        if (!validateSchema(request.Schema, request.Value)) throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
        if (values.TryGetValue(request.Key, out var prior))
        {
            if (request.ExpectedRevision != prior.Revision) throw new AbiHostException(AbiErrors.STALE_REVISION);
        }
        else if (request.ExpectedRevision is not null) throw new AbiHostException(AbiErrors.STALE_REVISION);
        if (!values.ContainsKey(request.Key) && values.Count >= MaximumKeys) throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
        values[request.Key] = new(request.Value, request.Schema, NewRevision());
    }
    private async ValueTask<NamespaceState> Load(string key, ulong schemaVersion, CancellationToken token)
    {
        string path = Path.Combine(root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".nps");
        if (!File.Exists(path)) return new(path, schemaVersion, NewRevision(), ImmutableDictionary<string, StorageValue>.Empty);
        var file = new FileInfo(path);
        if (file.Length > MaximumNamespaceBytes || (file.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Invalid plugin storage file.");
        var map = DataCodec.ReadMap(AbiValueBinary.Decode(await File.ReadAllBytesAsync(path, token).ConfigureAwait(false)));
        var values = DataCodec.ReadMap(DataCodec.Field(map, "values")).ToImmutableDictionary(p => p.Key, p => StorageValue.Read(p.Value), StringComparer.Ordinal);
        return new(path, DataCodec.ReadUInt64(DataCodec.Field(map, "schemaVersion")), Revision.Read(DataCodec.Field(map, "revision")), values);
    }
    private static async ValueTask Save(NamespaceState state, ImmutableDictionary<string, StorageValue> values, Revision revision, CancellationToken token)
    {
        byte[] bytes = AbiValueBinary.Encode(DataCodec.Map(("schemaVersion", DataCodec.WriteUInt64(state.SchemaVersion)), ("revision", Revision.Write(revision)), ("values", new AbiValue.Map(values.ToImmutableDictionary(p => p.Key, p => StorageValue.Write(p.Value), StringComparer.Ordinal)))));
        if (bytes.Length > MaximumNamespaceBytes) throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
        Directory.CreateDirectory(Path.GetDirectoryName(state.Path)!);
        string temporary = state.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            { await stream.WriteAsync(bytes, token).ConfigureAwait(false); await stream.FlushAsync(token).ConfigureAwait(false); stream.Flush(true); }
            token.ThrowIfCancellationRequested(); File.Move(temporary, state.Path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void ValidateKey(string key) { if (string.IsNullOrEmpty(key) || key.Length > 512 || key.Any(char.IsControl)) throw new AbiHostException(AbiErrors.SCHEMA_INVALID); }
    private static void RequireOwner(PluginIdentity caller, QualifiedId id)
    { if (id.IsHostNamespace || !id.Value.StartsWith(caller.PluginId + ":", StringComparison.Ordinal)) throw new AbiHostException(AbiErrors.CAPABILITY_DENIED); }
    private static Revision NewRevision() => new(Guid.NewGuid().ToString("N"));
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { if (_disposed) return; _disposed = true; _directoryLease?.Dispose(); _namespaces.Clear(); _transactions.Clear(); _states.Clear(); }
        finally { _gate.Release(); }
    }
    private sealed record NamespaceLease(PluginIdentity Owner, StorageNamespace Reference, NamespaceState State);
    private sealed record TransactionLease(Guid Owner, StorageTransaction Reference, NamespaceLease Namespace);
    private sealed class NamespaceState(string path, ulong schemaVersion, Revision revision, ImmutableDictionary<string, StorageValue> values)
    {
        internal string Path = path;
        internal ulong SchemaVersion = schemaVersion;
        internal Revision Revision = revision;
        internal ImmutableDictionary<string, StorageValue> Values = values;
    }
}
