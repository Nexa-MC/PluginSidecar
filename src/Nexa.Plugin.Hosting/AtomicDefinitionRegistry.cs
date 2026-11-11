using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100.Data;
namespace Nexa.Plugin.Hosting;

public sealed record PublishedDefinition(PluginIdentity Owner, Definition Definition, Revision Revision, RegistrationRef Registration);

/// <summary>Owned staged definitions; publication and authority activation share one Host gate.</summary>
public sealed partial class AtomicDefinitionRegistry(CapabilityAuthority authority, Func<Definition, bool>? validate = null)
{
    private readonly Dictionary<Guid, Dictionary<QualifiedId, PublishedDefinition>> _staged = [];
    private ImmutableDictionary<QualifiedId, PublishedDefinition> _published = ImmutableDictionary<QualifiedId, PublishedDefinition>.Empty;
    private long _storedBytes;
    private Revision _revision = new(Guid.NewGuid().ToString("N"));
    private readonly Dictionary<Guid, Revision> _stagedRevisions = [];
    public Snapshot<DefinitionBatch> Snapshot()
    {
        lock (authority.PublicationGate) return new(new(_published.Values.OrderBy(e => e.Definition.Id.Value).Select(e => Resolve(ContentCaller(Guid.Empty),e.Definition,null)).OfType<Definition>().ToImmutableArray()), _revision, 1, DateTimeOffset.UtcNow);
    }
    public ImmutableArray<PublishedDefinition> Entries { get { lock (authority.PublicationGate) return _published.Values.ToImmutableArray(); } }
    public ImmutableArray<RegistrationRef> Stage(HostInvocation invocation, ImmutableArray<Definition> definitions)
    {
        if (invocation.Phase != "Initialize") throw new InvalidOperationException(AbiErrors.STAGE_DENIED);
        if (definitions.IsDefault || definitions.Length > 4096) throw new FormatException(AbiErrors.SCHEMA_INVALID);
        foreach (var definition in definitions)
        {
            DataCodec.Validate(Definition.Write(definition));
            if (definition.Id.IsHostNamespace || !definition.Id.Value.StartsWith(invocation.Identity.PluginId + ":", StringComparison.Ordinal)) throw new AbiHostException(AbiErrors.CAPABILITY_DENIED);
            if (validate is null || !validate(definition)) throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
        }
        lock (authority.PublicationGate)
        {
            invocation.Options.Cancellation.ThrowIfCancellationRequested();
            long addedBytes = definitions.Sum(d => (long)EncodedSize(d));
            if (_storedBytes + addedBytes > 32 * 1024 * 1024) throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
            var current = _staged.GetValueOrDefault(invocation.GroupId) ?? [];
            if (_published.Count + _staged.Values.Sum(g => g.Count) + definitions.Length > 4096) throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
            var next = new Dictionary<QualifiedId, PublishedDefinition>(current);
            foreach (var definition in definitions)
            {
                if (_published.ContainsKey(definition.Id) || next.ContainsKey(definition.Id)) throw new AbiHostException(AbiErrors.REGISTRATION_DUPLICATE);
                next.Add(definition.Id, new(invocation.Identity, definition, new(Guid.NewGuid().ToString("N")), new(authority.AllocateRegistrationHandle(), invocation.Identity.PluginId, invocation.Identity.LoadInstanceId, definition.Id, RegistrationState.Staged)));
            }
            _staged[invocation.GroupId] = next;
            _storedBytes += addedBytes;
            _stagedRevisions[invocation.GroupId] = new(Guid.NewGuid().ToString("N"));
            return definitions.Select(d => next[d.Id].Registration).ToImmutableArray();
        }
    }
    public ImmutableArray<QualifiedId> MissingReferences(Guid groupId)
    {
        lock (authority.PublicationGate)
        {
            var staged = _staged.GetValueOrDefault(groupId) ?? [];
            return staged.Values.SelectMany(e => e.Definition.References).Where(r => r.Required && !staged.ContainsKey(r.Id) && !_published.ContainsKey(r.Id)).Select(r => r.Id).Distinct().ToImmutableArray();
        }
    }
    internal void Publish(Guid groupId)
    {
        lock (authority.PublicationGate)
        {
            var affected=AffectedRegistries(groupId);ValidateContent(groupId);
            if (!MissingReferences(groupId).IsEmpty) throw new InvalidOperationException(AbiErrors.DEPENDENCY_MISSING);
            var staged = _staged.GetValueOrDefault(groupId) ?? [];
            if (staged.Keys.Any(_published.ContainsKey)) throw new InvalidOperationException(AbiErrors.CONFLICT);
            _published = _published.AddRange(staged.Select(p => new KeyValuePair<QualifiedId, PublishedDefinition>(p.Key, p.Value with { Registration = p.Value.Registration with { State = RegistrationState.Active } })));
            _staged.Remove(groupId); _stagedRevisions.Remove(groupId);PublishContent(groupId,affected);NotifyContent(affected);
        }
    }
    public void Abort(Guid groupId) { lock (authority.PublicationGate) { if (_staged.Remove(groupId, out var entries)) _storedBytes -= entries.Values.Sum(e => (long)EncodedSize(e.Definition)); _stagedRevisions.Remove(groupId);foreach(var item in _content.Where(e=>e.Value.Group==groupId).ToArray()){_storedBytes-=item.Value.Bytes;_content.Remove(item.Key);} } }
    public void Retire(Guid instanceId)
    {
        lock (authority.PublicationGate)
        {
            var registries=AffectedRegistries(Guid.Empty,instanceId);
            var retiredRegistries=_content.Values.Where(e=>e.Kind=="registry"&&e.Owner.LoadInstanceId==instanceId).Select(e=>RegistryDescriptor.Read(e.Value)).ToArray();
            foreach(var item in _content.Where(e=>e.Value.Owner.LoadInstanceId==instanceId).ToArray()){_storedBytes-=item.Value.Bytes;_content.Remove(item.Key);}
            var removed = _published.Where(p => p.Value.Owner.LoadInstanceId == instanceId).ToArray();
            _storedBytes -= removed.Sum(e => (long)EncodedSize(e.Value.Definition));
            _published = _published.RemoveRange(removed.Select(p => p.Key));InvalidateContent(registries);NotifyContent(registries);
            foreach(var descriptor in retiredRegistries)
            {
                try{_consumers[descriptor.Consumer].Publish(descriptor,new(new([]),_revision,1,DateTimeOffset.UtcNow));_consumerFaults.Remove(descriptor.Id);}
                catch(Exception error)when(error is not OutOfMemoryException and not AccessViolationException){_consumerFaults.Add(descriptor.Id);}
            }
        }
    }
    private static int EncodedSize(Definition definition) => AbiValueBinary.Encode(Definition.Write(definition)).Length;
}
