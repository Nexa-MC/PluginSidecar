using System.Collections.Immutable;
using System.Security.Cryptography;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100.Data;
namespace Nexa.Plugin.Hosting;

public sealed partial class AtomicDefinitionRegistry
{
    private sealed record Content(PluginIdentity Owner,Guid Group,RegistrationRef Registration,string Kind,AbiValue Value,int Bytes);
    private readonly Dictionary<QualifiedId,Content> _content=[];
    private readonly Dictionary<QualifiedId,IRegistryConsumer> _consumers=[];
    private readonly HashSet<QualifiedId> _consumerFaults=[];
    private PluginRuntimeOptions _options=new();
    internal Action<QualifiedId,Snapshot<DefinitionBatch>>? ContentChanged;
    internal void ConfigureContent(PluginRuntimeOptions options)
    {
        _options=options;
        foreach(var consumer in options.RegistryConsumers)
            if(consumer is null||!_consumers.TryAdd(consumer.Id,consumer))throw new ArgumentException("Registry consumers must have unique IDs.",nameof(options));
    }
    private static bool VisibleContent(HostInvocation c,Content entry)=>entry.Registration.State==RegistrationState.Active||entry.Group==c.GroupId;
    private IEnumerable<Content> ContentOf(HostInvocation c,string kind)=>_content.Values.Where(e=>e.Kind==kind&&VisibleContent(c,e));
    private Content FindContent(HostInvocation c,QualifiedId id,string kind)=>_content.TryGetValue(id,out var entry)&&entry.Kind==kind&&VisibleContent(c,entry)?entry:throw new AbiHostException(AbiErrors.NOT_FOUND);
    private static void OwnContent(HostInvocation c,QualifiedId id)
    {if(!id.Value.StartsWith(c.Identity.PluginId+":",StringComparison.Ordinal))throw new AbiHostException(AbiErrors.CAPABILITY_DENIED);}
    private RegistrationRef StageContent<T>(HostInvocation c,QualifiedId id,string kind,T value) where T:IAbiData<T>
    {
        OwnContent(c,id);var data=T.Write(value);int bytes=AbiValueBinary.Encode(data).Length;
        if(_content.ContainsKey(id))throw new AbiHostException(AbiErrors.REGISTRATION_DUPLICATE);
        if(_content.Count>=1024||_storedBytes+bytes>32*1024*1024)throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
        var registration=new RegistrationRef(++_nextHandle,c.Identity.PluginId,c.Identity.LoadInstanceId,id,RegistrationState.Staged);
        _content.Add(id,new(c.Identity,c.GroupId,registration,kind,data,bytes));_storedBytes+=bytes;
        _stagedRevisions[c.GroupId]=new(Guid.NewGuid().ToString("N"));return registration;
    }
    private IEnumerable<HostCapabilityRoute> ContentRoutes => [
        HostRoutes.Typed<RegistryDescriptor,RegistrationRef>("Definitions","DefineRegistry",(c,v)=>
        {
            lock(authority.PublicationGate)
            {
                if(!_consumers.ContainsKey(v.Consumer))throw new AbiHostException(AbiErrors.CAPABILITY_UNAVAILABLE);
                if(v.Deletion!=DeletionPolicy.RejectWhileReferenced)throw new AbiHostException(AbiErrors.CAPABILITY_UNAVAILABLE);
                if(v.MergeContract.Value!="nexa:merge/closed-v1")throw new AbiHostException(AbiErrors.CAPABILITY_UNAVAILABLE);
                if(string.IsNullOrWhiteSpace(v.EntryIdentity)||v.SupportedOperations.Any(op=>op is not ("Get" or "Query" or "Watch" or "Register" or "RegisterBatch" or "UpdateOwned" or "RemoveOwned")))throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
                return ValueTask.FromResult(StageContent(c,v.Id,"registry",v));
            }
        }),
        HostRoutes.Typed<QualifiedRequest,RegistryDescriptor>("Definitions","DescribeRegistry",(c,v)=>
        {lock(authority.PublicationGate)return ValueTask.FromResult(RegistryDescriptor.Read(FindContent(c,v.Id,"registry").Value));}),
        HostRoutes.Typed<PageRequest,Page<RegistryDescriptor>>("Definitions","ListRegistries",(c,v)=>ValueTask.FromResult(ListRegistries(c,v))),
        HostRoutes.Typed<TagDefinition,RegistrationRef>("Definitions","RegisterTag",(c,v)=>
        {lock(authority.PublicationGate)return ValueTask.FromResult(StageContent(c,v.Id,"tag",v));}),
        HostRoutes.Typed<QualifiedRequest,TagResolution>("Definitions","ResolveTag",(c,v)=>
        {lock(authority.PublicationGate)return ValueTask.FromResult(new TagResolution(ResolveTag(c,v.Id,[]).OrderBy(id=>id.Value,StringComparer.Ordinal).ToImmutableArray(),VisibleRevision(c)));}),
        HostRoutes.Typed<OverlayDefinition,RegistrationRef>("Definitions","RegisterOverlay",(c,v)=>
        {lock(authority.PublicationGate){OwnContent(c,v.Id);return ValueTask.FromResult(StageContent(c,v.Id,"overlay",v));}}),
        HostRoutes.Typed<OverlayDefinition,MutationPlan>("Definitions","PreviewOverlay",(c,v)=>
        {
            lock(authority.PublicationGate)
            {
                OwnContent(c,v.Id);var target=Visible(c).SingleOrDefault(d=>d.Definition.Id==v.Target)??throw new AbiHostException(AbiErrors.NOT_FOUND);
                CheckOverlay(c,v,target,c.Identity);
                var resolved=Resolve(c,target.Definition,v);
                string hash=Convert.ToHexString(SHA256.HashData(AbiValueBinary.Encode(OverlayDefinition.Write(v))));
                return ValueTask.FromResult(new MutationPlan(new(hash),VisibleRevision(c),hash,new("content.overlay.preview",[]),
                    v.Mode is MergePolicy.Replace or MergePolicy.Mask?ConfirmationRequirement.UserConfirmation:ConfirmationRequirement.None,
                    [new("active",DataCodec.WriteBoolean(resolved is not null)),new("result",resolved is null?new AbiValue.Null():Definition.Write(resolved))]));
            }
        }),
        HostRoutes.Typed<QualifiedRequest,ResolutionReport>("Definitions","ExplainResolution",(c,v)=>
        {
            lock(authority.PublicationGate)
            {
                var target=Visible(c).SingleOrDefault(d=>d.Definition.Id==v.Id)??throw new AbiHostException(AbiErrors.NOT_FOUND);
                var overlays=OrderedOverlays(c,target.Definition,null);bool fault=_consumerFaults.Contains(target.Definition.Registry);
                return ValueTask.FromResult(new ResolutionReport(v.Id,!fault&&Resolve(c,target.Definition,null) is not null,overlays.Select(o=>o.Id).ToImmutableArray(),
                    fault?[new(AbiErrors.PROVIDER_UNAVAILABLE,new("content.consumer.failed",[]),target.Definition.Registry.Value,true)]:[],VisibleRevision(c)));
            }
        })
    ];
    private Page<RegistryDescriptor> ListRegistries(HostInvocation caller,PageRequest request)
    {
        lock(authority.PublicationGate)
        {
            if(request.Limit is <1 or >256)throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
            var revision=VisibleRevision(caller);string? after=null;
            if(request.Cursor is not null)
            {
                var map=DataCodec.ReadMap(AbiValueBinary.Decode(Convert.FromBase64String(request.Cursor)));
                if(DataCodec.ReadGuid(DataCodec.Field(map,"owner"))!=caller.Identity.LoadInstanceId||Revision.Read(DataCodec.Field(map,"revision"))!=revision)throw new AbiHostException(AbiErrors.STALE_REVISION);
                after=DataCodec.ReadString(DataCodec.Field(map,"after"));
            }
            var entries=ContentOf(caller,"registry").Select(e=>RegistryDescriptor.Read(e.Value)).Where(e=>after is null||string.CompareOrdinal(e.Id.Value,after)>0).OrderBy(e=>e.Id.Value,StringComparer.Ordinal).Take((int)request.Limit+1).ToArray();
            var page=entries.Take((int)request.Limit).ToImmutableArray();
            string? cursor=entries.Length>request.Limit?Convert.ToBase64String(AbiValueBinary.Encode(DataCodec.Map(("owner",DataCodec.WriteGuid(caller.Identity.LoadInstanceId)),("revision",Revision.Write(revision)),("after",DataCodec.WriteString(page[^1].Id.Value))))):null;
            return new(page,cursor,revision);
        }
    }
    private HashSet<QualifiedId> ResolveTag(HostInvocation caller,QualifiedId id,HashSet<QualifiedId> path,Dictionary<QualifiedId,HashSet<QualifiedId>>? memo=null)
    {
        caller.Options.Cancellation.ThrowIfCancellationRequested();memo??=[];
        if(memo.TryGetValue(id,out var known))return new(known);
        if(path.Count>=32||!path.Add(id))throw new AbiHostException(AbiErrors.CONFLICT);
        try
        {
            var tag=TagDefinition.Read(FindContent(caller,id,"tag").Value);var members=tag.Add.ToHashSet();
            foreach(var include in tag.Include)
            {
                var other=TagDefinition.Read(FindContent(caller,include,"tag").Value);
                if(other.Registry!=tag.Registry)throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
                members.UnionWith(ResolveTag(caller,include,path,memo));if(members.Count>4096)throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
            }
            members.ExceptWith(tag.Remove);
            var definitions=Visible(caller).Where(d=>d.Definition.Registry==tag.Registry).Select(d=>d.Definition.Id).ToHashSet();
            if(members.Any(id=>!definitions.Contains(id)))throw new AbiHostException(AbiErrors.DEPENDENCY_MISSING);
            memo[id]=new(members);return members;
        }
        finally{path.Remove(id);}
    }
    private RegistryDescriptor? Registry(HostInvocation caller,QualifiedId id)=>_content.TryGetValue(id,out var content)&&content.Kind=="registry"&&VisibleContent(caller,content)?RegistryDescriptor.Read(content.Value):null;
    private void CheckOverlay(HostInvocation caller,OverlayDefinition overlay,PublishedDefinition target,PluginIdentity owner)
    {
        if(overlay.Schema!=target.Definition.Schema)throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
        var registry=Registry(caller,target.Definition.Registry);
        if(registry is null)throw new AbiHostException(AbiErrors.CAPABILITY_UNAVAILABLE);
        if(overlay.Mode!=registry.MergePolicy)throw new AbiHostException(AbiErrors.CAPABILITY_DENIED);
        if(owner!=target.Owner&&(registry.Ownership!=OwnershipPolicy.AuditedOverlay||_options.ApproveForeignOverlay?.Invoke(owner,overlay)!=true))throw new AbiHostException(AbiErrors.CAPABILITY_DENIED);
    }
    private ImmutableArray<OverlayDefinition> OrderedOverlays(HostInvocation caller,Definition target,OverlayDefinition? preview)
    {
        var entries=ContentOf(caller,"overlay").Select(e=>(Entry:e,Overlay:OverlayDefinition.Read(e.Value))).Where(p=>p.Overlay.Target==target.Id).ToArray();
        var owner=Visible(caller).Single(d=>d.Definition.Id==target.Id);
        foreach(var entry in entries)CheckOverlay(caller,entry.Overlay,owner,entry.Entry.Owner);
        var overlays=entries.Select(e=>e.Overlay).ToList();
        if(preview is not null){overlays.RemoveAll(o=>o.Id==preview.Id);overlays.Add(preview);}
        if(overlays.Count==0)return [];
        var registry=Registry(caller,target.Registry)!;
        if(registry.OrderingPolicy==OrderingPolicy.UserSelection)
        {
            if(_options.SelectOverlays is null)throw new AbiHostException(AbiErrors.CONFLICT);
            var selected=_options.SelectOverlays(target.Id,overlays.Select(o=>o.Id).ToImmutableArray());
            if(selected.IsDefault||selected.Distinct().Count()!=selected.Length||selected.Any(id=>!overlays.Any(o=>o.Id==id)))throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
            var ordered=selected.Select(id=>overlays.Single(o=>o.Id==id)).ToImmutableArray();
            for(int i=0;i<ordered.Length;i++)
                if(ordered[i].Before.Any(id=>selected.Contains(id)&&selected.IndexOf(id)<=i)||ordered[i].After.Any(id=>selected.Contains(id)&&selected.IndexOf(id)>=i))throw new AbiHostException(AbiErrors.CONFLICT);
            return ordered;
        }
        var edges=overlays.ToDictionary(o=>o.Id,_=>new HashSet<QualifiedId>());
        foreach(var overlay in overlays)
        {
            foreach(var before in overlay.Before){if(!edges.ContainsKey(before))throw new AbiHostException(AbiErrors.DEPENDENCY_MISSING);edges[before].Add(overlay.Id);}
            foreach(var after in overlay.After){if(!edges.ContainsKey(after))throw new AbiHostException(AbiErrors.DEPENDENCY_MISSING);edges[overlay.Id].Add(after);}
        }
        var result=ImmutableArray.CreateBuilder<OverlayDefinition>();
        while(edges.Count>0)
        {
            var ready=edges.Where(e=>e.Value.Count==0).Select(e=>e.Key).OrderBy(id=>id.Value,StringComparer.Ordinal).ToArray();
            if(ready.Length==0||ready.Length>1&&registry.OrderingPolicy==OrderingPolicy.RejectAmbiguity)throw new AbiHostException(AbiErrors.CONFLICT);
            var id=ready[0];result.Add(overlays.Single(o=>o.Id==id));edges.Remove(id);foreach(var remaining in edges.Values)remaining.Remove(id);
        }
        return result.ToImmutable();
    }
    private Definition? Resolve(HostInvocation caller,Definition original,OverlayDefinition? preview)
    {
        Definition? result=original;
        foreach(var overlay in OrderedOverlays(caller,original,preview))
        {
            if(overlay.Mode==MergePolicy.Mask){result=null;continue;}
            if(result is null&&overlay.Mode!=MergePolicy.Replace)throw new AbiHostException(AbiErrors.CONFLICT);
            var body=overlay.Mode switch {MergePolicy.Replace=>overlay.Patch,MergePolicy.Add=>Merge(result!.Body,overlay.Patch,true,0),MergePolicy.Merge=>Merge(result!.Body,overlay.Patch,false,0),_=>throw new AbiHostException(AbiErrors.SCHEMA_INVALID)};
            result=original with {Body=body};if(validate?.Invoke(result)!=true)throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
        }
        return result;
    }
    private static AbiValue Merge(AbiValue value,AbiValue patch,bool addOnly,int depth)
    {
        if(depth>32)throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
        if(addOnly&&value is AbiValue.Array a&&patch is AbiValue.Array b)return new AbiValue.Array(a.Value.AddRange(b.Value));
        if(value is not AbiValue.Map current||patch is not AbiValue.Map changes)throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
        var result=current.Value.ToBuilder();
        foreach(var pair in changes.Value)
        {
            if(result.TryGetValue(pair.Key,out var previous))
            {
                if(addOnly)throw new AbiHostException(AbiErrors.CONFLICT);
                result[pair.Key]=previous is AbiValue.Map&&pair.Value is AbiValue.Map?Merge(previous,pair.Value,false,depth+1):pair.Value;
            }
            else result.Add(pair.Key,pair.Value);
        }
        var merged=new AbiValue.Map(result.ToImmutable());DataCodec.Validate(merged);return merged;
    }
    private HostInvocation ContentCaller(Guid group)=>new(new("nexa","0.0.0",Guid.Empty),group,group==Guid.Empty?"Active":"Checkup",new(Guid.NewGuid(),TimeSpan.FromSeconds(5),default));
    internal Snapshot<DefinitionBatch> RegistrySnapshot(QualifiedId registry)
    {
        lock(authority.PublicationGate)
        {
            var caller=ContentCaller(Guid.Empty);var values=Visible(caller).Where(d=>d.Definition.Registry==registry).Select(d=>Resolve(caller,d.Definition,null)).OfType<Definition>().OrderBy(d=>d.Id.Value,StringComparer.Ordinal).ToImmutableArray();
            return new(new(values),_revision,1,DateTimeOffset.UtcNow);
        }
    }
    internal ImmutableArray<string> RequiredConsumers(ISet<string> providers)
    {
        lock(authority.PublicationGate)
        {
            bool Matches(QualifiedId id)=>providers.Contains(id.Value.Split(':')[0]);
            var result=_published.Values.Where(e=>Matches(e.Definition.Registry)||e.Definition.References.Any(r=>r.Required&&Matches(r.Id))).Select(e=>e.Owner.PluginId).ToHashSet();
            foreach(var entry in _content.Values.Where(e=>e.Registration.State==RegistrationState.Active))
            {
                if(entry.Kind=="tag")
                {var tag=TagDefinition.Read(entry.Value);if(Matches(tag.Registry)||tag.Add.Concat(tag.Include).Any(Matches))result.Add(entry.Owner.PluginId);}
                if(entry.Kind=="overlay")
                {var overlay=OverlayDefinition.Read(entry.Value);if(Matches(overlay.Target)||overlay.Before.Concat(overlay.After).Any(Matches))result.Add(entry.Owner.PluginId);}
            }
            return result.ToImmutableArray();
        }
    }
    internal void CheckRegistryOperation(HostInvocation caller,QualifiedId id,string operation)
    {if(Registry(caller,id) is { } registry&&!registry.SupportedOperations.Contains(operation))throw new AbiHostException(AbiErrors.CAPABILITY_DENIED);}
    private ImmutableArray<QualifiedId> AffectedRegistries(Guid group,Guid? instance=null)
    {
        var caller=ContentCaller(group);var definitions=instance is null?(_staged.GetValueOrDefault(group)?.Values.AsEnumerable()??[]):_published.Values.Where(e=>e.Owner.LoadInstanceId==instance);
        var ids=definitions.Select(e=>e.Definition.Registry).ToHashSet();
        foreach(var content in _content.Values.Where(e=>instance is null?e.Group==group:e.Owner.LoadInstanceId==instance))
        {
            if(content.Kind=="registry")ids.Add(content.Registration.DefinitionId);
            if(content.Kind=="tag")ids.Add(TagDefinition.Read(content.Value).Registry);
            if(content.Kind=="overlay")
            {var target=OverlayDefinition.Read(content.Value).Target;var definition=Visible(caller).FirstOrDefault(e=>e.Definition.Id==target);if(definition is not null)ids.Add(definition.Definition.Registry);}
        }
        return ids.ToImmutableArray();
    }
    private void ValidateContent(Guid group)
    {
        var caller=ContentCaller(group);var affected=group==Guid.Empty?null:AffectedRegistries(group).ToHashSet();
        foreach(var tag in ContentOf(caller,"tag").Where(e=>group==Guid.Empty||e.Group==group))ResolveTag(caller,tag.Registration.DefinitionId,[]);
        foreach(var overlay in ContentOf(caller,"overlay").Where(e=>group==Guid.Empty||e.Group==group))
        {var value=OverlayDefinition.Read(overlay.Value);var target=Visible(caller).SingleOrDefault(d=>d.Definition.Id==value.Target)??throw new AbiHostException(AbiErrors.DEPENDENCY_MISSING);CheckOverlay(caller,value,target,overlay.Owner);}
        foreach(var entry in ContentOf(caller,"registry").Where(e=>affected is null||affected.Contains(e.Registration.DefinitionId)))
        {
            var registry=RegistryDescriptor.Read(entry.Value);var definitions=Visible(caller).Where(d=>d.Definition.Registry==registry.Id).Select(d=>d.Definition).ToArray();
            if(!registry.ReferenceTypes.IsEmpty)
            {
                var visible=Visible(caller).ToDictionary(e=>e.Definition.Id,e=>e.Definition.Registry);
                if(definitions.SelectMany(d=>d.References).Any(r=>visible.TryGetValue(r.Id,out var kind)&&!registry.ReferenceTypes.Contains(kind)))throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
            }
            if(definitions.Any(d=>d.Schema!=registry.Schema))throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
            var batch=new DefinitionBatch(definitions.Select(d=>Resolve(caller,d,null)).OfType<Definition>().ToImmutableArray());
            if(AbiValueBinary.Encode(DefinitionBatch.Write(batch)).Length>1024*1024)throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
            if(!_consumers[registry.Consumer].Validate(registry,batch))throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
        }
    }
    private void PublishContent(Guid group,ImmutableArray<QualifiedId> affected)
    {
        foreach(var entry in _content.Where(e=>e.Value.Group==group).ToArray())_content[entry.Key]=entry.Value with {Registration=entry.Value.Registration with {State=RegistrationState.Active}};
        InvalidateContent(affected);
    }
    private void InvalidateContent(IEnumerable<QualifiedId> registries)
    {
        var changed=registries.ToHashSet();
        foreach(var entry in _published.Where(e=>changed.Contains(e.Value.Definition.Registry)).ToArray())_published=_published.SetItem(entry.Key,entry.Value with {Revision=new(Guid.NewGuid().ToString("N"))});
        _revision=new(Guid.NewGuid().ToString("N"));
    }
    private void NotifyContent(IEnumerable<QualifiedId> registryIds)
    {
        foreach(var registryId in registryIds.Distinct())
        {
            try
            {
                var snapshot=RegistrySnapshot(registryId);ContentChanged?.Invoke(registryId,snapshot);
                if(_content.TryGetValue(registryId,out var entry)&&entry.Kind=="registry"&&entry.Registration.State==RegistrationState.Active)
                {
                    var registry=RegistryDescriptor.Read(entry.Value);_consumers[registry.Consumer].Publish(registry,snapshot);_consumerFaults.Remove(registryId);
                }
            }
            catch(Exception error)when(error is not OutOfMemoryException and not AccessViolationException){_consumerFaults.Add(registryId);}
        }
    }
}
