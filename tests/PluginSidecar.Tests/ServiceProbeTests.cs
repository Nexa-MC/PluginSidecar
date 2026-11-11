using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;

internal static class ServiceProbeTests
{
    private static readonly PublicContract Contract=new(new("probe:contract"),1,Abi100Schemas.TextResult,
        [new(1,"echo",Abi100Schemas.TextResult,Abi100Schemas.TextResult,true,IdempotencyMode.ReadOnly,false)]);
    private static InvocationOptions Options()=>new(Guid.NewGuid(),TimeSpan.FromSeconds(5),default){IdempotencyKey=Guid.NewGuid().ToString("N")};
    private static void Check(bool value,[System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))]string? text=null)
    {if(!value)throw new InvalidOperationException("Service probes: "+text);}
    private static T Success<T>(AbiReply<T> reply)=>reply.Success?reply.Value:throw new InvalidOperationException(reply.Error!.Code);
    internal static async Task Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"nexa-probes-"+Guid.NewGuid().ToString("N"));
        try
        {
            await using(var runtime=new PluginHostRuntime(root,_=>true,Abi100Schemas.Validate))
            {
                var provider=new Entry("probe");var consumer=new Entry("consumer"){Required=true};
                var reports=await runtime.LoadAsync([consumer.Source(),provider.Source()]);
                Check(reports.All(r=>r.State==LoadState.Active)&&provider.Probes==2);
                Check(reports.Single(r=>r.Identity.PluginId=="probe").Diagnostics.Any(f=>f.Code=="CHECKUP_NOTE"));
                provider.ResumeDetached.SetResult();
                Check(await provider.DetachedResult.Task.WaitAsync(TimeSpan.FromSeconds(5))==AbiErrors.STAGE_FROZEN);
                using var api=consumer.Context!.AsAbi100();using var p=provider.Context!.AsAbi100();
                Check((await api.Plugins.ProbeService(new(consumer.Binding!,new("probe:predicate"),TextResult.Write(new("ok"))),Options())).Error?.Code==AbiErrors.STAGE_DENIED);
                Check(Success(await p.Contracts.Describe(new(Contract.Id,1),Options())).Id==Contract.Id);
                var report=Success(await api.Plugins.ReadOwnLoadReport(new(),Options()));
                Check(report.Plugin=="consumer"&&report.State=="Active"&&report.LoadInstance==consumer.Context!.Info.Identity.LoadInstanceId&&report.DependencyChain.SequenceEqual(["probe"]));
                Check(report.Findings.All(f=>f.Target=="consumer")&&report.Findings.Any(f=>f.Code=="BOOTSTRAP_NOTE")&&!report.Findings.Any(f=>f.Code=="CHECKUP_NOTE"));
                Check((await p.Resources.Release(new(provider.PredicateRegistration!),Options())).Error?.Code==AbiErrors.CONFLICT);
                await runtime.StopAsync("probe");Check(runtime.Active.IsEmpty);
            }
            await using(var runtime=new PluginHostRuntime(root,_=>true,Abi100Schemas.Validate))
            {
                // The same read-only boundary also applies when the provider is already Active.
                var provider=new Entry("probe");Check((await runtime.LoadAsync([provider.Source()])).Single().State==LoadState.Active);
                var consumer=new Entry("consumer");Check((await runtime.LoadAsync([consumer.Source()])).Single().State==LoadState.Active);
                Check(provider.Probes==2);provider.ResumeDetached.SetResult();
                Check(await provider.DetachedResult.Task.WaitAsync(TimeSpan.FromSeconds(5))==AbiErrors.STAGE_FROZEN);
            }
            await using(var runtime=new PluginHostRuntime(root,_=>true,Abi100Schemas.Validate))
            {
                var provider=new Entry("probe");var consumer=new Entry("consumer"){Required=true,FailCheckup=true};
                var reports=await runtime.LoadAsync([provider.Source(),consumer.Source()]);
                Check(reports.All(r=>r.State==LoadState.Failed)&&runtime.Active.IsEmpty);
                Check(reports.Single(r=>r.Identity.PluginId=="consumer").Diagnostics.Any(f=>f.Code=="PROBE_DECLINED"&&f.Fatal));
                provider.ResumeDetached.TrySetResult();
            }
            await using(var runtime=new PluginHostRuntime(root,_=>true,Abi100Schemas.Validate))
            {
                var provider=new Entry("probe"){MissingProbe=true};
                Check((await runtime.LoadAsync([provider.Source()])).Single().State==LoadState.Failed);
            }
            await using(var runtime=new PluginHostRuntime(root,_=>true,Abi100Schemas.Validate))
            {
                var provider=new Entry("probe");var consumer=new Entry("consumer"){Required=true,TimeoutProbe=true};
                Check((await runtime.LoadAsync([provider.Source(),consumer.Source()])).All(r=>r.State==LoadState.Active));
                provider.ReleaseProbe.SetResult();
                Check(await provider.LateProbe.Task.WaitAsync(TimeSpan.FromSeconds(5))==AbiErrors.STAGE_FROZEN);
            }
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
        Console.WriteLine("PASS: staged/active service probes, owned binding and schema checks, pure predicates, query-only callbacks, revoked detached queries, reference protection and group rollback.");
    }
    private sealed class Entry(string id):IPluginBootstrapV1
    {
        internal ABIContext? Context;internal ServiceBinding? Binding;internal RegistrationRef? PredicateRegistration;
        internal bool Required,FailCheckup,MissingProbe,TimeoutProbe;internal int Probes;private ulong _handler;
        internal TaskCompletionSource ReleaseProbe=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<string?> LateProbe=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ResumeDetached=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<string?> DetachedResult=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static readonly string[] Groups=["Contracts","Plugins","Predicates","Tasks","Resources"];
        private static readonly ImmutableArray<string> Permissions=Abi100Catalog.Operations.Where(o=>Groups.Contains(o.Group)).Select(o=>o.Permission).Where(p=>!string.IsNullOrEmpty(p)).Distinct().ToImmutableArray();
        private PluginManifest Manifest()=>new(1,id,"1.0.0",1,new("test","test"),new([],[],"fixture"),[100],
            Required?[new("probe",new(null,true,null,true))]:[],[],[],Permissions,[]);
        internal PluginLoadSource Source()=>new(Manifest(),Permissions,_=>ValueTask.FromResult<IPluginBootstrapV1>(this),()=>ValueTask.CompletedTask);
        public async ValueTask<AbiReply<PreInitResult>> OnPreInitialize(PreInitContext context)
        {
            Success(await context.Diagnostics.ReportDiagnostic(new("BOOTSTRAP_NOTE","load.note",false),Options()));
            _handler=context.Callbacks.Register("probe",async(input,options)=>
            {
                Interlocked.Increment(ref Probes);var request=PredicateRequest.Read(input);
                using var api=Context!.AsAbi100();
                if(TextResult.Read(request.Inputs).Text=="timeout")
                {
                    await ReleaseProbe.Task; // Deliberately ignores the caller's cancellation.
                    LateProbe.TrySetResult((await api.Contracts.Describe(new(Contract.Id,1),Options())).Error?.Code);
                    return AbiReply<AbiValue>.Ok(PredicateResult.Write(new(PredicateOutcome.True,request.Consistency,[])));
                }
                Check(Success(await api.Contracts.Describe(new(Contract.Id,1),Options())).Id==Contract.Id);
                Check((await api.Tasks.CreateOwned(new(new("probe:forbidden"),new("forbidden",[]),new("parent"),true),Options())).Error?.Code==AbiErrors.STAGE_DENIED);
                if(Probes==1)_=Task.Run(async()=>
                {
                    await ResumeDetached.Task;
                    using var later=Context!.AsAbi100();
                    DetachedResult.TrySetResult((await later.Contracts.Describe(new(Contract.Id,1),Options())).Error?.Code);
                });
                bool pass=TextResult.Read(request.Inputs).Text!="fail";
                return AbiReply<AbiValue>.Ok(PredicateResult.Write(new(pass?PredicateOutcome.True:PredicateOutcome.False,request.Consistency,
                    pass?[]:[new("PROBE_DECLINED",new("probe.declined",[]),"input",true)])));
            }).Value;
            var draft=new Abi100Draft(Success(await context.Negotiator.GetABIContext(100,Options())));
            foreach(var contract in Abi100Catalog.Operations.Where(o=>Groups.Contains(o.Group)).Select(o=>o.ContractId).Distinct())draft=draft.Require(contract);
            Context=Success(await draft.Make(context.Negotiator,Guid.NewGuid(),Permissions,default));
            return AbiReply<PreInitResult>.Ok(new(Context,Manifest().RequiredDependencies,[],[],[]));
        }
        public async ValueTask<AbiReply<Unit>> OnInitialize(ABIContext context,StageContext stage)
        {
            using var api=context.ForStage100(stage);
            var report=Success(await api.Plugins.ReadOwnLoadReport(new(),Options()));
            Check(report.Plugin==id&&report.State=="Initialize"&&report.Findings.Single().Code=="BOOTSTRAP_NOTE");
            if(id!="probe")return AbiReply<Unit>.Ok(new());
            Success(await api.Contracts.DefineService(Contract,Options()));
            if(!MissingProbe)PredicateRegistration=Success(await api.Predicates.Register(new(new("probe:predicate"),Abi100Schemas.TextResult,_handler,new AbiValue.Null()),Options()));
            Success(await api.Predicates.Register(new(new("probe:pure"),Abi100Schemas.TextResult,null,new AbiValue.Bool(true)),Options()));
            Success(await api.Plugins.ExportService(new(new("probe:service"),Contract,_handler,new("probe:predicate")),Options()));
            Success(await api.Plugins.ExportService(new(new("probe:pure-service"),Contract,_handler,new("probe:pure")),Options()));
            return AbiReply<Unit>.Ok(new());
        }
        private ServiceBinding? _pure;
        public async ValueTask<AbiReply<Unit>> OnPostInitialize(ABIContext context,StageContext stage)
        {
            if(id=="consumer")
            {
                using var api=context.ForStage100(stage);
                Binding=Success(await api.Plugins.BindService(new("probe",new("probe:service"),1,1,Required),Options()));
                _pure=Success(await api.Plugins.BindService(new("probe",new("probe:pure-service"),1,1,false),Options()));
            }
            return AbiReply<Unit>.Ok(new());
        }
        public async ValueTask<AbiReply<CheckupResult>> OnCheckup(ABIContext context,StageContext stage)
        {
            if(id=="consumer")
            {
                using var api=context.ForStage100(stage);var input=TextResult.Write(new("ok"));
                if(TimeoutProbe)
                {
                    var timed=await api.Plugins.ProbeService(new(Binding!,new("probe:predicate"),TextResult.Write(new("timeout"))),Options() with{Budget=TimeSpan.FromMilliseconds(150)});
                    Check(timed.Error?.Code is AbiErrors.DEADLINE_EXCEEDED or AbiErrors.CANCELLED);
                    return AbiReply<CheckupResult>.Ok(new(CheckupStatus.Passed,[],[]));
                }
                Check((await api.Plugins.ProbeService(new(Binding! with{ProviderLoadInstance=Guid.NewGuid()},new("probe:predicate"),input),Options())).Error?.Code==AbiErrors.HANDLE_OWNER_MISMATCH);
                Check((await api.Plugins.ProbeService(new(Binding!,new("probe:pure"),input),Options())).Error?.Code==AbiErrors.CAPABILITY_DENIED);
                Check((await api.Plugins.ProbeService(new(Binding!,new("probe:predicate"),Empty.Write(new())),Options())).Error?.Code==AbiErrors.SCHEMA_INVALID);
                Check(Success(await api.Plugins.ProbeService(new(_pure!,new("probe:pure"),input),Options())).Valid);
                Check(Success(await api.Plugins.ProbeService(new(Binding!,new("probe:predicate"),input),Options())).Valid);
                var failed=Success(await api.Plugins.ProbeService(new(Binding!,new("probe:predicate"),TextResult.Write(new("fail"))),Options()));
                Check(!failed.Valid&&failed.Issues.Single().Code=="PROBE_DECLINED");
                if(FailCheckup)return AbiReply<CheckupResult>.Ok(new(CheckupStatus.Failed,[new("PROBE_DECLINED","probe.declined",true)],[]));
            }
            return AbiReply<CheckupResult>.Ok(new(CheckupStatus.Passed,id=="probe"?[new("CHECKUP_NOTE","probe.checked",false)]:[],[]));
        }
        public ValueTask<AbiReply<Unit>> OnFinalize(ABIContext context,StageContext stage)=>Ok();
        public ValueTask<AbiReply<Unit>> OnActivated(ABIContext context,InvocationContext invocation)=>Ok();
        public ValueTask<AbiReply<Unit>> OnStop(StopContext context)=>Ok();
        public ValueTask<AbiReply<Unit>> OnAbort(AbortContext context)=>Ok();
        private static ValueTask<AbiReply<Unit>> Ok()=>ValueTask.FromResult(AbiReply<Unit>.Ok(new()));
    }
}
