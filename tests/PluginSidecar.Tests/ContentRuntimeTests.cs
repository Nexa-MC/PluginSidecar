using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;

internal static class ContentRuntimeTests
{
    private static readonly QualifiedId RegistryId=new("owner:registry");
    private static readonly SchemaRef Schema=new(new("nexa:content-test"),1,"fixture");
    private static InvocationOptions Options()=>new(Guid.NewGuid(),TimeSpan.FromSeconds(5),default){IdempotencyKey=Guid.NewGuid().ToString("N")};
    private static T Ok<T>(AbiReply<T> reply)=>reply.Success?reply.Value:throw new InvalidOperationException(reply.Error!.Code+" "+reply.Error.Operation);
    private static void Check(bool value,[System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))]string? expression=null)
    {if(!value)throw new InvalidOperationException("Content assertion: "+expression);}
    private static AbiValue Map(string key,long value)=>DataCodec.Map((key,DataCodec.WriteInt64(value)));
    private static Definition Def(string name,AbiValue body)=>new(new("owner:"+name),RegistryId,Schema,body,[]);
    private static OverlayDefinition Overlay(string id,string target,MergePolicy mode,AbiValue patch)=>new(new(id),new(target),mode,Schema,patch,[],[]);
    private static RegistryDescriptor Registry(MergePolicy mode,OrderingPolicy ordering=OrderingPolicy.ExplicitConstraints)=>new(RegistryId,Schema,"id",[],mode,ordering,ActivationPolicy.GroupCommit,
        OwnershipPolicy.AuditedOverlay,["Get","Query","Watch","Register","RegisterBatch","UpdateOwned","RemoveOwned"],[],new("nexa:content-test-consumer"),DeletionPolicy.RejectWhileReferenced,new("nexa:merge/closed-v1"));
    private static string Temp(){string path=Path.Combine(Path.GetTempPath(),"nexa-content-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);return path;}
    private static PluginHostRuntime Host(string root,PluginRuntimeOptions options)=>new(root,d=>d.Schema==Schema&&d.Body is AbiValue.Map or AbiValue.Array,Abi100Schemas.Validate,options:options);
    private static async Task Loaded(PluginHostRuntime host,Entry entry)
    {var report=(await host.LoadAsync([entry.Source()])).Single();Check(report.State==LoadState.Active);}
    internal static async Task Run()
    {
        await Content();await Modes();await Rejections();await Selection();
        Console.WriteLine("PASS: real registry consumption, recursive tags, resolved overlays, ordering/authorization, previews, watches, CAS, rollback, consumer fault reporting and content dependency stop closure.");
    }
    private static async Task Content()
    {
        string root=Temp();var consumer=new Consumer();bool approve=false;
        try
        {
            await using var host=Host(root,new(){AutomaticSchedules=false,RegistryConsumers=[consumer],ApproveForeignOverlay=(_,_)=>approve});
            var first=Def("a",DataCodec.Map(("base",DataCodec.WriteInt64(1)),("nested",Map("original",1))));var second=Def("b",Map("base",2));
            var entry=new Entry("owner")
            {
                Registry=Registry(MergePolicy.Merge),Definitions=[first,second],Watch=true,
                Tags=[new(new("owner:all"),RegistryId,[first.Id,second.Id],[],[]),new(new("owner:one"),RegistryId,[],[second.Id],[new("owner:all")])],
                Overlays=[Overlay("owner:first","owner:a",MergePolicy.Merge,DataCodec.Map(("nested",Map("added",2)))),Overlay("owner:second","owner:a",MergePolicy.Merge,Map("base",3)) with {After=[new("owner:first")]}]
            };
            await Loaded(host,entry);using var api=entry.Context!.AsAbi100();
            Check(consumer.Last!.Value.Definitions.Length==2&&consumer.Publications==1);
            Check(Ok(await api.Definitions.ListRegistries(new(null,1),Options())).Items.Single().Id==RegistryId);
            Check(Ok(await api.Definitions.DescribeRegistry(new(RegistryId),Options())).Consumer==consumer.Id);
            var read=Ok(await api.Definitions.Get(new(RegistryId,first.Id),Options()));Check(Number(read.Value.Body,"base")==3);
            await Loaded(host,new Entry("unrelated"));await host.StopAsync("unrelated");
            Check(consumer.Publications==1&&Ok(await api.Definitions.Get(new(RegistryId,first.Id),Options())).Revision==read.Revision);
            var nested=DataCodec.Field(DataCodec.ReadMap(read.Value.Body),"nested");Check(Number(nested,"original")==1&&Number(nested,"added")==2);
            var tag=Ok(await api.Definitions.ResolveTag(new(new("owner:one")),Options()));Check(tag.Members.SequenceEqual([first.Id]));
            Check(Ok(await api.Definitions.Query(new(RegistryId,[new("owner:one")],new(null,10)),Options())).Items.Single().Id==first.Id);
            var report=Ok(await api.Definitions.ExplainResolution(new(first.Id),Options()));Check(report.Active&&report.Contributions.SequenceEqual([new("owner:first"),new("owner:second")]));
            var preview=Ok(await api.Definitions.PreviewOverlay(Overlay("owner:preview","owner:a",MergePolicy.Merge,Map("base",9)) with {After=[new("owner:second")]},Options()));
            var previewData=Definition.Read(preview.Constraints.Single(v=>v.Name=="result").Value);Check(Number(previewData.Body,"base")==9);
            Check(Number(Ok(await api.Definitions.Get(new(RegistryId,first.Id),Options())).Value.Body,"base")==3);
            Check(Snapshot<DefinitionBatch>.Read(Ok(await api.Resources.ReadSubscription(new(entry.Subscription!,256),Options())).Items.Single()).Value.Definitions.Length==2);
            var page=Ok(await api.Definitions.Query(new(RegistryId,[],new(null,1)),Options()));
            Check((await api.Definitions.Query(new(RegistryId,[new("owner:one")],new(page.NextCursor,1)),Options())).Error?.Code==AbiErrors.STALE_REVISION);
            Ok(await api.Definitions.UpdateOwned(new(first with {Body=Map("new",4)},read.Revision),Options()));
            Check(Number(consumer.Last!.Value.Definitions.Single(d=>d.Id==first.Id).Body,"new")==4);
            Check(Ok(await api.Resources.ReadSubscription(new(entry.Subscription!,256),Options())).Items.Length==1);
            var current=Ok(await api.Definitions.Get(new(RegistryId,first.Id),Options()));
            Check((await api.Definitions.RemoveOwned(new(RegistryId,first.Id,current.Revision),Options())).Error?.Code==AbiErrors.CONFLICT);
            var foreign=new Entry("foreign"){Overlays=[Overlay("foreign:change","owner:a",MergePolicy.Merge,Map("base",7))]};
            int before=consumer.Publications;Check((await host.LoadAsync([foreign.Source()])).Single().State==LoadState.Failed);Check(consumer.Publications==before);
            approve=true;foreign=new Entry("foreign"){Overlays=[Overlay("foreign:change","owner:a",MergePolicy.Merge,Map("base",7)) with {After=[new("owner:second")]}]};
            await Loaded(host,foreign);Check(Number(Ok(await api.Definitions.Get(new(RegistryId,first.Id),Options())).Value.Body,"base")==7);
            Check((await api.Definitions.UpdateOwned(new(first,current.Revision),Options())).Error?.Code==AbiErrors.STALE_REVISION);
            consumer.Throw=true;current=Ok(await api.Definitions.Get(new(RegistryId,first.Id),Options()));Ok(await api.Definitions.UpdateOwned(new(first,current.Revision),Options()));
            Check(!Ok(await api.Definitions.ExplainResolution(new(first.Id),Options())).Active);
            consumer.Throw=false;current=Ok(await api.Definitions.Get(new(RegistryId,first.Id),Options()));Ok(await api.Definitions.UpdateOwned(new(first,current.Revision),Options()));
            Check(Ok(await api.Definitions.ExplainResolution(new(first.Id),Options())).Active);
            await host.StopAsync("owner");Check(host.Active.IsEmpty&&consumer.Last!.Value.Definitions.IsEmpty);
        }
        finally{Directory.Delete(root,true);}
    }
    private static async Task Modes()
    {
        foreach(var mode in Enum.GetValues<MergePolicy>())
        {
            string root=Temp();var consumer=new Consumer();
            try
            {
                await using var host=Host(root,new(){AutomaticSchedules=false,RegistryConsumers=[consumer]});
                var definition=Def("a",Map("base",1));var entry=new Entry("owner"){Registry=Registry(mode),Definitions=[definition],Overlays=[Overlay("owner:patch","owner:a",mode,Map("changed",2))]};
                await Loaded(host,entry);using var api=entry.Context!.AsAbi100();var reply=await api.Definitions.Get(new(RegistryId,definition.Id),Options());
                if(mode==MergePolicy.Mask){Check(reply.Error?.Code==AbiErrors.NOT_FOUND);Check(host.Definitions.Snapshot().Value.Definitions.IsEmpty);Check(!Ok(await api.Definitions.ExplainResolution(new(definition.Id),Options())).Active);}
                else{var data=DataCodec.ReadMap(Ok(reply).Value.Body);Check(data.ContainsKey("changed")&&data.ContainsKey("base")== (mode!=MergePolicy.Replace));}
            }
            finally{Directory.Delete(root,true);}
        }
    }
    private static async Task Rejections()
    {
        foreach(string kind in new[]{"tag-cycle","overlay-cycle","ambiguous","schema","consumer"})
        {
            string root=Temp();var consumer=new Consumer(){Reject=kind=="consumer"};
            try
            {
                await using var host=Host(root,new(){AutomaticSchedules=false,RegistryConsumers=[consumer]});
                var entry=new Entry("owner"){Registry=Registry(MergePolicy.Merge,kind=="ambiguous"?OrderingPolicy.RejectAmbiguity:OrderingPolicy.ExplicitConstraints),Definitions=[Def("a",Map("base",1))]};
                if(kind=="tag-cycle")entry.Tags=[new(new("owner:x"),RegistryId,[],[],[new("owner:y")]),new(new("owner:y"),RegistryId,[],[],[new("owner:x")])];
                if(kind is "overlay-cycle" or "ambiguous")entry.Overlays=[Overlay("owner:x","owner:a",MergePolicy.Merge,Map("a",1)) with {After=kind=="overlay-cycle"?[new("owner:y")]:[]},Overlay("owner:y","owner:a",MergePolicy.Merge,Map("b",2)) with {After=kind=="overlay-cycle"?[new("owner:x")]:[]}];
                if(kind=="schema")entry.Overlays=[Overlay("owner:invalid","owner:a",MergePolicy.Merge,new AbiValue.Null())];
                Check((await host.LoadAsync([entry.Source()])).Single().State==LoadState.Failed);
                Check(host.Definitions.Entries.IsEmpty&&consumer.Publications==0);
            }
            finally{Directory.Delete(root,true);}
        }
    }
    private static async Task Selection()
    {
        foreach(bool reversed in new[]{false,true})
        {
            string root=Temp();var consumer=new Consumer();
            try
            {
                await using var host=Host(root,new(){AutomaticSchedules=false,RegistryConsumers=[consumer],SelectOverlays=(_,ids)=>reversed?ids.Reverse().ToImmutableArray():ids});
                var entry=new Entry("owner")
                {
                    Registry=Registry(MergePolicy.Merge,OrderingPolicy.UserSelection),Definitions=[Def("a",Map("value",0))],
                    Overlays=[Overlay("owner:a-patch","owner:a",MergePolicy.Merge,Map("value",1)),Overlay("owner:b-patch","owner:a",MergePolicy.Merge,Map("value",2)) with {After=[new("owner:a-patch")]}]
                };
                var report=(await host.LoadAsync([entry.Source()])).Single();
                if(reversed)Check(report.State==LoadState.Failed&&consumer.Publications==0);
                else{Check(report.State==LoadState.Active);using var api=entry.Context!.AsAbi100();Check(Number(Ok(await api.Definitions.Get(new(RegistryId,new("owner:a")),Options())).Value.Body,"value")==2);}
            }
            finally{Directory.Delete(root,true);}
        }
    }
    private static long Number(AbiValue value,string name)=>DataCodec.ReadInt64(DataCodec.Field(DataCodec.ReadMap(value),name));
    private sealed class Consumer:IRegistryConsumer
    {
        public QualifiedId Id=>new("nexa:content-test-consumer");internal Snapshot<DefinitionBatch>? Last;internal int Publications;internal bool Reject,Throw;
        public bool Validate(RegistryDescriptor registry,DefinitionBatch definitions)=>!Reject;
        public void Publish(RegistryDescriptor registry,Snapshot<DefinitionBatch> snapshot){if(Throw)throw new IOException("fixture");Last=snapshot;Publications++;}
    }
    private sealed class Entry(string id):IPluginBootstrapV1
    {
        internal RegistryDescriptor? Registry;internal ImmutableArray<Definition> Definitions=[];internal ImmutableArray<TagDefinition> Tags=[];internal ImmutableArray<OverlayDefinition> Overlays=[];
        internal ABIContext? Context;internal SubscriptionRef? Subscription;internal bool Watch;
        private static readonly ImmutableArray<string> Permissions=["nexa.definitions.read","nexa.definitions.write","nexa.resources.read","nexa.resources.write"];
        internal PluginLoadSource Source()=>new(new(1,id,"1.0.0",1,new("test","test"),new([],[],"fixture"),[100],[],[],[],Permissions,[]),Permissions,_=>ValueTask.FromResult<IPluginBootstrapV1>(this),()=>ValueTask.CompletedTask);
        public async ValueTask<AbiReply<PreInitResult>> OnPreInitialize(PreInitContext c)
        {Context=Ok(await new Abi100Draft(Ok(await c.Negotiator.GetABIContext(100,Options()))).Require("nexa.definitions").Require("nexa.resources").Make(c.Negotiator,Guid.NewGuid(),Permissions,default));return AbiReply<PreInitResult>.Ok(new(Context,[],[],[],[]));}
        public async ValueTask<AbiReply<Unit>> OnInitialize(ABIContext c,StageContext stage)
        {
            using var api=c.ForStage100(stage);
            if(Registry is not null)Ok(await api.Definitions.DefineRegistry(Registry,Options()));
            if(!Definitions.IsEmpty)Ok(await api.Definitions.RegisterBatch(new(Definitions),Options()));
            foreach(var tag in Tags)Ok(await api.Definitions.RegisterTag(tag,Options()));
            foreach(var overlay in Overlays)Ok(await api.Definitions.RegisterOverlay(overlay,Options()));
            if(Watch)Subscription=Ok(await api.Definitions.Watch(new(RegistryId),Options()));
            return AbiReply<Unit>.Ok(new());
        }
        public ValueTask<AbiReply<Unit>> OnPostInitialize(ABIContext c,StageContext stage)=>Done();
        public ValueTask<AbiReply<CheckupResult>> OnCheckup(ABIContext c,StageContext stage)=>ValueTask.FromResult(AbiReply<CheckupResult>.Ok(new(CheckupStatus.Passed,[],[])));
        public ValueTask<AbiReply<Unit>> OnFinalize(ABIContext c,StageContext stage)=>Done();public ValueTask<AbiReply<Unit>> OnActivated(ABIContext c,InvocationContext invocation)=>Done();
        public ValueTask<AbiReply<Unit>> OnStop(StopContext c)=>Done();public ValueTask<AbiReply<Unit>> OnAbort(AbortContext c)=>Done();
        private static ValueTask<AbiReply<Unit>> Done()=>ValueTask.FromResult(AbiReply<Unit>.Ok(new()));
    }
}
