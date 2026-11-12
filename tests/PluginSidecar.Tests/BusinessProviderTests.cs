using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;

internal static class BusinessProviderTests
{
    private static readonly QualifiedId Kind=new("nexa:catalog");
    private static readonly BusinessProviderMethod Read=new("ReadProject",Abi100Schemas.ResourceRef,Abi100Schemas.CatalogProject);
    private static InvocationOptions Options()=>new(Guid.NewGuid(),TimeSpan.FromSeconds(3),default){IdempotencyKey=Guid.NewGuid().ToString("N")};
    private static void Check(bool value,[System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))]string? text=null)
    {if(!value)throw new InvalidOperationException("Business provider: "+text);}
    private static T Success<T>(AbiReply<T> reply)=>reply.Success?reply.Value:throw new InvalidOperationException(reply.Error!.Code);
    internal static async Task Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"nexa-provider-"+Guid.NewGuid().ToString("N"));
        PluginHostRuntime? host=null;
        HostCapabilityRoute[] routes=[
            HostRoutes.Typed<ProviderDescriptor,RegistrationRef>("Catalog","RegisterProvider",(c,v)=>ValueTask.FromResult(host!.Services.RegisterHostProvider(c,Kind,v,[Read]))),
            HostRoutes.Typed<PageRequest,Page<ProviderDescriptor>>("Catalog","ListProviders",(_,_)=>ValueTask.FromResult(new Page<ProviderDescriptor>(host!.Services.HostProviders(Kind),null,new("test")))),
            HostRoutes.Typed<ResourceRef,CatalogProject>("Catalog","ReadProject",async(c,v)=>CatalogProject.Read(await host!.Services.QueryHostProvider(c,Kind,v.Provider,Read,ResourceRef.Write(v))))
        ];
        try
        {
            await using(var runtime=new PluginHostRuntime(root,_=>true,Abi100Schemas.Validate,routes))
            {
                host=runtime;var provider=new Entry("provider");var consumer=new Entry("consumer");
                Check((await runtime.LoadAsync([provider.Source(),consumer.Source()])).All(r=>r.State==LoadState.Active));
                using var api=consumer.Context!.AsAbi100();using var owner=provider.Context!.AsAbi100();
                Check(Success(await api.Catalog.ListProviders(new(null,16),Options())).Items.Length==2);
                var project=Success(await api.Catalog.ReadProject(new(new("provider:catalog"),"first"),Options()));
                Check(project.Name=="first"&&provider.Calls==1);
                provider.Fail=true;
                Check((await api.Catalog.ReadProject(new(new("provider:catalog"),"failed"),Options())).Error?.Code==AbiErrors.NOT_FOUND);
                Check((await api.Catalog.ReadProject(new(new("provider:unavailable"),"failed"),Options())).Error?.Code==AbiErrors.PROVIDER_UNAVAILABLE);
                provider.Fail=false;
                provider.Invalid=true;
                Check(!(await api.Catalog.ReadProject(new(new("provider:catalog"),"invalid"),Options())).Success);
                provider.Invalid=false;provider.Block=true;
                var pending=api.Catalog.ReadProject(new(new("provider:catalog"),"blocked"),Options()).AsTask();
                await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!(await owner.Resources.Release(new(provider.Registration!),Options())).Success);
                await runtime.StopAsync("provider");provider.Release.TrySetResult();
                Check(!(await pending).Success);
                Check(Success(await api.Catalog.ListProviders(new(null,16),Options())).Items.IsEmpty);
                Check(!(await api.Catalog.ReadProject(new(new("provider:catalog"),"retired"),Options())).Success);
            }
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
        Console.WriteLine("PASS: typed business-provider callbacks, staged visibility, query-only effects, result schema checks, live registration holds and provider retirement.");
    }
    private sealed class Entry(string id):IPluginBootstrapV1
    {
        internal ABIContext? Context;internal RegistrationRef? Registration;internal int Calls;internal bool Invalid,Block,Fail;private ulong _handler;
        internal TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static readonly ImmutableArray<string> Permissions=["nexa.catalog.read","nexa.catalog.write","nexa.tasks.read","nexa.tasks.write","nexa.resources.write"];
        internal PluginLoadSource Source()=>new(new(1,id,"1.0.0",1,new("test","test"),new([],[],"fixture"),[100],[],[],[],Permissions,[]),Permissions,_=>ValueTask.FromResult<IPluginBootstrapV1>(this),()=>ValueTask.CompletedTask);
        public async ValueTask<AbiReply<PreInitResult>> OnPreInitialize(PreInitContext context)
        {
            _handler=context.Callbacks.Register("catalog",async(input,options)=>
            {
                Calls++;var call=BusinessProviderCall.Read(input);
                Check(call.Provider.Value is "provider:catalog" or "provider:unavailable"&&call.Kind==Kind&&call.Method==Read.Name&&call.InputSchema==Read.Input&&call.OutputSchema==Read.Output);
                if(Fail)return AbiReply<AbiValue>.Fail(new(AbiErrors.NOT_FOUND,"missing","Active",id,"catalog",options.OperationId,options.CorrelationId,false,ImmutableDictionary<string,string>.Empty));
                using var api=Context!.AsAbi100();
                Check((await api.Tasks.CreateOwned(new(new("provider:forbidden"),new("forbidden",[]),new("parent"),true),Options())).Error?.Code==AbiErrors.STAGE_DENIED);
                if(Block){Entered.TrySetResult();await Release.Task.WaitAsync(options.Cancellation);}
                return AbiReply<AbiValue>.Ok(Invalid?Empty.Write(new()):CatalogProject.Write(new(ResourceRef.Read(call.Inputs),ResourceRef.Read(call.Inputs).ProjectId,"",new("nexa:resource/mod"),[])));
            }).Value;
            var draft=new Abi100Draft(Success(await context.Negotiator.GetABIContext(100,Options()))).Require("nexa.catalog/100").Require("nexa.tasks/100").Require("nexa.resources/100");
            Context=Success(await draft.Make(context.Negotiator,Guid.NewGuid(),Permissions,default));return AbiReply<PreInitResult>.Ok(new(Context,[],[],[],[]));
        }
        public async ValueTask<AbiReply<Unit>> OnInitialize(ABIContext context,StageContext stage)
        {
            if(id=="provider")
            {
                using var api=context.ForStage100(stage);
                var descriptor=new ProviderDescriptor(new("provider:catalog"),Kind,100,Abi100Schemas.BusinessProviderCall.Digest,new("Provider",[]),["ReadProject"],["application"],SelectionPolicy.ExplicitRequest,ConcurrencyPolicy.Parallel,3000,ProviderFailurePolicy.FailOperation,Abi100Schemas.Empty,_handler);
                Check(!(await api.Catalog.RegisterProvider(descriptor with{Id=new("nexa:modrinth")},Options())).Success);
                Registration=Success(await api.Catalog.RegisterProvider(descriptor,Options()));
                Success(await api.Catalog.RegisterProvider(descriptor with{Id=new("provider:unavailable"),FailurePolicy=ProviderFailurePolicy.Unavailable},Options()));
                Check(Success(await api.Catalog.ListProviders(new(null,16),Options())).Items.IsEmpty);
            }
            return AbiReply<Unit>.Ok(new());
        }
        public ValueTask<AbiReply<Unit>> OnPostInitialize(ABIContext context,StageContext stage)=>Ok();
        public ValueTask<AbiReply<CheckupResult>> OnCheckup(ABIContext context,StageContext stage)=>ValueTask.FromResult(AbiReply<CheckupResult>.Ok(new(CheckupStatus.Passed,[],[])));
        public ValueTask<AbiReply<Unit>> OnFinalize(ABIContext context,StageContext stage)=>Ok();
        public ValueTask<AbiReply<Unit>> OnActivated(ABIContext context,InvocationContext invocation)=>Ok();
        public ValueTask<AbiReply<Unit>> OnStop(StopContext context)=>Ok();
        public ValueTask<AbiReply<Unit>> OnAbort(AbortContext context)=>Ok();
        private static ValueTask<AbiReply<Unit>> Ok()=>ValueTask.FromResult(AbiReply<Unit>.Ok(new()));
    }
}
