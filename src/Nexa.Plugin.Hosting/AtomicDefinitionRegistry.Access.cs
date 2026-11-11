using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100.Data;
namespace Nexa.Plugin.Hosting;

public sealed partial class AtomicDefinitionRegistry
{
    public IEnumerable<HostCapabilityRoute> Routes => DefinitionRoutes.Concat(ContentRoutes);
    private IEnumerable<HostCapabilityRoute> DefinitionRoutes =>
    [
        HostRoutes.Typed<Definition, RegistrationRef>("Definitions", "Register", (caller, value) => {lock(authority.PublicationGate){CheckRegistryOperation(caller,value.Registry,"Register");return ValueTask.FromResult(Stage(caller, [value])[0]);}}),
        HostRoutes.Typed<DefinitionBatch, Page<RegistrationRef>>("Definitions", "RegisterBatch", (caller, value) =>
        {
            lock (authority.PublicationGate)
            {
                foreach(var definition in value.Definitions)CheckRegistryOperation(caller,definition.Registry,"RegisterBatch");
                var handles = Stage(caller, value.Definitions);
                return ValueTask.FromResult(new Page<RegistrationRef>(handles, null, VisibleRevision(caller)));
            }
        }),
        HostRoutes.Typed<DefinitionQuery, Snapshot<Definition>>("Definitions", "Get", (caller, value) => ValueTask.FromResult(Get(caller, value))),
        HostRoutes.Typed<DefinitionSearch, Page<Definition>>("Definitions", "Query", (caller, value) => ValueTask.FromResult(Query(caller, value))),
        HostRoutes.Typed<DefinitionUpdate, RegistrationRef>("Definitions", "UpdateOwned", (caller, value) => ValueTask.FromResult(Update(caller, value))),
        HostRoutes.Typed<DefinitionRemove, Empty>("Definitions", "RemoveOwned", (caller, value) => { Remove(caller, value); return ValueTask.FromResult(new Empty()); })
    ];

    public Snapshot<Definition> Get(HostInvocation caller, DefinitionQuery request)
    {
        lock (authority.PublicationGate)
        {
            caller.Options.Cancellation.ThrowIfCancellationRequested();CheckRegistryOperation(caller,request.Registry,"Get");
            var entry = Visible(caller).FirstOrDefault(e => e.Definition.Id == request.Id && e.Definition.Registry == request.Registry)
                ?? throw new AbiHostException(AbiErrors.NOT_FOUND);
            var resolved=Resolve(caller,entry.Definition,null)??throw new AbiHostException(AbiErrors.NOT_FOUND);
            return new(resolved, entry.Revision, entry.Definition.Schema.Version, DateTimeOffset.UtcNow);
        }
    }

    public Page<Definition> Query(HostInvocation caller, DefinitionSearch request)
    {
        if (request.Page.Limit is < 1 or > 256 || request.Tags.IsDefault) throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
        lock (authority.PublicationGate)
        {
            caller.Options.Cancellation.ThrowIfCancellationRequested();CheckRegistryOperation(caller,request.Registry,"Query");
            HashSet<QualifiedId>? members=null;
            foreach(var tag in request.Tags)
            {
                if(TagDefinition.Read(FindContent(caller,tag,"tag").Value).Registry!=request.Registry)throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
                var resolved=ResolveTag(caller,tag,[]);if(members is null)members=resolved;else members.IntersectWith(resolved);
            }
            string filter=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(AbiValueBinary.Encode(new AbiValue.Array(request.Tags.OrderBy(t=>t.Value,StringComparer.Ordinal).Select(id=>QualifiedId.Write(id)).ToImmutableArray()))));
            var revision = VisibleRevision(caller);
            string? after = null;
            if (request.Page.Cursor is { } cursor)
            {
                if (cursor.Length > 4096) throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
                var fields = DataCodec.ReadMap(AbiValueBinary.Decode(Convert.FromBase64String(cursor)));
                if (Revision.Read(DataCodec.Field(fields, "revision")) != revision || QualifiedId.Read(DataCodec.Field(fields, "registry")) != request.Registry || DataCodec.ReadString(DataCodec.Field(fields,"filter"))!=filter || DataCodec.ReadGuid(DataCodec.Field(fields,"owner"))!=caller.Identity.LoadInstanceId)
                    throw new AbiHostException(AbiErrors.STALE_REVISION);
                after = DataCodec.ReadString(DataCodec.Field(fields, "after"));
            }
            var entries = Visible(caller).Where(e => e.Definition.Registry == request.Registry && (members is null||members.Contains(e.Definition.Id)) && (after is null || string.CompareOrdinal(e.Definition.Id.Value, after) > 0))
                .OrderBy(e => e.Definition.Id.Value, StringComparer.Ordinal).Select(e=>Resolve(caller,e.Definition,null)).OfType<Definition>().Take(checked((int)request.Page.Limit + 1)).ToArray();
            var page = entries.Take((int)request.Page.Limit).ToImmutableArray();
            string? next = entries.Length > request.Page.Limit ? Convert.ToBase64String(AbiValueBinary.Encode(DataCodec.Map(
                ("revision", Revision.Write(revision)), ("registry", QualifiedId.Write(request.Registry)), ("filter",DataCodec.WriteString(filter)), ("owner",DataCodec.WriteGuid(caller.Identity.LoadInstanceId)), ("after", DataCodec.WriteString(page[^1].Id.Value))))) : null;
            return new(page, next, revision);
        }
    }

    public RegistrationRef Update(HostInvocation caller, DefinitionUpdate request)
    {
        DataCodec.Validate(Definition.Write(request.Definition));
        if (validate is null || !validate(request.Definition)) throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
        lock (authority.PublicationGate)
        {
            CheckRegistryOperation(caller,request.Definition.Registry,"UpdateOwned");
            var previous = Owned(caller, request.Definition.Registry, request.Definition.Id, request.ExpectedRevision);
            if (request.Definition.References.Any(r => r.Required && !_published.ContainsKey(r.Id))) throw new AbiHostException(AbiErrors.DEPENDENCY_MISSING);
            long delta = EncodedSize(request.Definition) - EncodedSize(previous.Definition);
            if (_storedBytes + delta > 32 * 1024 * 1024) throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
            var next = previous with { Definition = request.Definition, Revision = new(Guid.NewGuid().ToString("N")) };
            _published = _published.SetItem(request.Definition.Id, next);
            try{ValidateContent(Guid.Empty);}catch{_published=_published.SetItem(request.Definition.Id,previous);throw;}
            _storedBytes += delta; _revision = new(Guid.NewGuid().ToString("N"));NotifyContent([request.Definition.Registry]);
            return next.Registration;
        }
    }

    public void Remove(HostInvocation caller, DefinitionRemove request)
    {
        lock (authority.PublicationGate)
        {
            CheckRegistryOperation(caller,request.Registry,"RemoveOwned");
            var previous = Owned(caller, request.Registry, request.Id, request.ExpectedRevision);
            if(_content.Values.Any(e=>e.Kind=="overlay"&&OverlayDefinition.Read(e.Value).Target==request.Id||e.Kind=="tag"&&TagDefinition.Read(e.Value).Add.Contains(request.Id)&&!TagDefinition.Read(e.Value).Remove.Contains(request.Id)))throw new AbiHostException(AbiErrors.CONFLICT);
            if (_published.Values.Concat(_staged.Values.SelectMany(g => g.Values)).Any(e => e.Definition.Id != request.Id && e.Definition.References.Any(r => r.Required && r.Id == request.Id)))
                throw new AbiHostException(AbiErrors.CONFLICT);
            _published = _published.Remove(request.Id);
            try{ValidateContent(Guid.Empty);}catch{_published=_published.SetItem(request.Id,previous);throw;}
            _storedBytes -= EncodedSize(previous.Definition); _revision = new(Guid.NewGuid().ToString("N"));NotifyContent([request.Registry]);
        }
    }

    private PublishedDefinition Owned(HostInvocation caller, QualifiedId registry, QualifiedId id, Revision expected)
    {
        if (caller.Phase != "Active") throw new AbiHostException(AbiErrors.STAGE_DENIED);
        caller.Options.Cancellation.ThrowIfCancellationRequested();
        var entry = _published.GetValueOrDefault(id) ?? throw new AbiHostException(AbiErrors.NOT_FOUND);
        if (entry.Owner != caller.Identity || entry.Definition.Registry != registry) throw new AbiHostException(AbiErrors.HANDLE_OWNER_MISMATCH);
        if (entry.Revision != expected) throw new AbiHostException(AbiErrors.STALE_REVISION);
        return entry;
    }
    private IEnumerable<PublishedDefinition> Visible(HostInvocation caller) => _published.Values.Concat(
        caller.Phase is "Initialize" or "PostInitialize" or "Checkup" or "Finalize"
            ? (_staged.GetValueOrDefault(caller.GroupId)?.Values.AsEnumerable() ?? []) : []);
    private Revision VisibleRevision(HostInvocation caller) => new(_revision.Value + "." +
        (caller.Phase is "Initialize" or "PostInitialize" or "Checkup" or "Finalize" ? (_stagedRevisions.GetValueOrDefault(caller.GroupId)?.Value ?? "empty") : "active"));
}
