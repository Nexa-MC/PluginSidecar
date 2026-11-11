using System.Collections.Immutable;
using System.Threading.Channels;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;

internal static class ActionConfirmationTests
{
    private static InvocationOptions Options()=>new(Guid.NewGuid(),TimeSpan.FromSeconds(5),default)
        {IdempotencyKey=Guid.NewGuid().ToString("N"),CorrelationId=Guid.NewGuid()};
    private static void Check(bool value,[System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))]string? text=null)
    {if(!value)throw new InvalidOperationException("Action confirmation: "+text);}
    private static T Success<T>(AbiReply<T> reply)=>reply.Success?reply.Value:throw new InvalidOperationException(reply.Error!.Code);
    private static ActionRequest Request(string key)=>new(new("confirm:effect"),new(new("nexa:global"),null,"test"),TextResult.Write(new(key)),null,key);
    private static async Task<OperationRecord> Terminal(Abi100Context api,EntityId id)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while(true)
        {
            var result=Success(await api.Tasks.Read(new(id),Options()));
            if(result.State is OperationState.Completed or OperationState.Cancelled or OperationState.Failed or OperationState.OutcomeUnknown)return result;
            await Task.Delay(5,timeout.Token);
        }
    }
    internal static async Task Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"nexa-confirm-"+Guid.NewGuid().ToString("N"));
        var prompt=new Prompt();
        try
        {
            await using(var runtime=new PluginHostRuntime(root,_=>true,Abi100Schemas.Validate,options:new(){ActionConfirmation=prompt}))
            {
                var provider=new Entry("confirm");var caller=new Entry("caller");var limited=new Entry("limited"){DenyActionEffects=true};
                Check((await runtime.LoadAsync([provider.Source(),caller.Source(),limited.Source()])).All(r=>r.State==LoadState.Active));
                using var api=caller.Context!.AsAbi100();using var owner=provider.Context!.AsAbi100();
                var first=Success(await api.Actions.Invoke(Request("once"),Options()));
                var question=await prompt.Next();
                Check(question.Request.Caller==caller.Identity&&question.Request.Provider==provider.Identity&&
                    AbiValueBinary.Encode(ActionRequest.Write(question.Request.Request)).SequenceEqual(AbiValueBinary.Encode(ActionRequest.Write(Request("once")))));
                var duplicate=Success(await api.Actions.Invoke(Request("once"),Options()));
                Check(provider.Calls==0);question.Answer.SetResult(true);
                Check((await Terminal(api,first.Id)).State==OperationState.Completed);
                Check((await Terminal(api,duplicate.Id)).State==OperationState.Completed&&provider.Calls==1&&prompt.Count==1);
                var conflicting=Success(await api.Actions.Invoke(Request("once") with{Inputs=TextResult.Write(new("changed"))},Options()));
                Check((await Terminal(api,conflicting.Id)).Failure?.Code==AbiErrors.REQUEST_ID_REUSED&&prompt.Count==1);
                using(var limitedApi=limited.Context!.AsAbi100())
                {
                    var forbidden=Success(await limitedApi.Workflows.Start(new(new("confirm:workflow"),TextResult.Write(new("forbidden")),"v1"),Options()));
                    Check((await Terminal(limitedApi,forbidden.Id)).Failure?.Code==AbiErrors.PERMISSION_DENIED&&prompt.Count==1&&provider.Calls==1);
                }

                var declined=Success(await api.Actions.Invoke(Request("declined"),Options()));
                (await prompt.Next()).Answer.SetResult(false);
                Check((await Terminal(api,declined.Id)).Failure?.Code==AbiErrors.PERMISSION_DENIED&&provider.Calls==1);
                var replayDecline=Success(await api.Actions.Invoke(Request("declined"),Options()));
                Check((await Terminal(api,replayDecline.Id)).Failure?.Code==AbiErrors.PERMISSION_DENIED&&prompt.Count==2);

                var cancelled=Success(await api.Actions.Invoke(Request("cancel"),Options()));
                var late=await prompt.Next();Success(await api.Tasks.Cancel(new(cancelled.Id),Options()));
                Check((await Terminal(api,cancelled.Id)).State==OperationState.Cancelled);
                late.Answer.SetResult(true);
                var replayCancel=Success(await api.Actions.Invoke(Request("cancel"),Options()));
                Check((await Terminal(api,replayCancel.Id)).State==OperationState.Cancelled&&provider.Calls==1&&prompt.Count==3);

                // A rejected persistent workflow has no pending business-effect marker.
                var flow=Success(await owner.Workflows.Start(new(new("confirm:workflow"),TextResult.Write(new("flow")),"v1"),Options()));
                (await prompt.Next()).Answer.SetResult(false);
                var failed=await Terminal(owner,flow.Id);
                Check(failed.State==OperationState.Failed&&failed.Failure?.Code==AbiErrors.PERMISSION_DENIED);

                // A side effect with an invalid result cannot be reported as safely retryable.
                var invalid=Success(await api.Actions.Invoke(Request("invalid"),Options()));
                (await prompt.Next()).Answer.SetResult(true);
                Check((await Terminal(api,invalid.Id)).State==OperationState.OutcomeUnknown&&provider.Calls==2);
                var replayInvalid=Success(await api.Actions.Invoke(Request("invalid"),Options()));
                Check((await Terminal(api,replayInvalid.Id)).Failure?.Code==AbiErrors.OUTCOME_UNKNOWN&&provider.Calls==2);

                using(var releaseHandler=new ManualResetEventSlim())
                {
                    provider.BlockingRelease=releaseHandler;
                    try
                    {
                        var blockedAction=Success(await api.Actions.Invoke(Request("blocked"),Options()));
                        (await prompt.Next()).Answer.SetResult(true);
                        await provider.HandlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        Success(await api.Tasks.Cancel(new(blockedAction.Id),Options()));
                        Check((await Terminal(api,blockedAction.Id)).State==OperationState.OutcomeUnknown);
                        var replayBlocked=Success(await api.Actions.Invoke(Request("blocked"),Options()));
                        Check((await Terminal(api,replayBlocked.Id)).Failure?.Code==AbiErrors.OUTCOME_UNKNOWN&&provider.Calls==3);
                    }
                    finally{releaseHandler.Set();}
                    await provider.HandlerExited.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }

                var lost=Success(await api.Actions.Invoke(Request("provider-stop"),Options()));
                var retired=await prompt.Next();await runtime.StopAsync("confirm");
                Check((await Terminal(api,lost.Id)).State==OperationState.Cancelled);
                retired.Answer.SetResult(true);Check(provider.Calls==3);
            }
            await using(var noUi=new PluginHostRuntime(root,_=>true,Abi100Schemas.Validate))
            {
                var entry=new Entry("confirm");Check((await noUi.LoadAsync([entry.Source()])).Single().State==LoadState.Active);
                using var api=entry.Context!.AsAbi100();
                Check((await api.Actions.Invoke(Request("missing-ui"),Options())).Error?.Code==AbiErrors.CAPABILITY_UNAVAILABLE&&entry.Calls==0);
                var saved=Success(await api.Workflows.ListRuns(new(null,256),Options())).Items.Single();
                Check(saved.State==OperationState.Failed&&saved.Failure?.Code==AbiErrors.PERMISSION_DENIED);
            }
            // Even an adapter blocked synchronously cannot outlive the confirmation budget.
            using var release=new ManualResetEventSlim();var blocked=new BlockingPrompt(release);
            await using(var timed=new PluginHostRuntime(root,_=>true,Abi100Schemas.Validate,options:new(){ActionConfirmation=blocked}))
            {
                var entry=new Entry("confirm");Check((await timed.LoadAsync([entry.Source()])).Single().State==LoadState.Active);
                using var api=entry.Context!.AsAbi100();
                var operation=Success(await api.Actions.Invoke(Request("timeout"),Options() with{Budget=TimeSpan.FromMilliseconds(150)}));
                await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check((await Terminal(api,operation.Id)).Failure?.Code==AbiErrors.DEADLINE_EXCEEDED&&entry.Calls==0);
                release.Set();await blocked.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var replay=Success(await api.Actions.Invoke(Request("timeout"),Options()));
                Check((await Terminal(api,replay.Id)).Failure?.Code==AbiErrors.DEADLINE_EXCEEDED&&entry.Calls==0);
            }
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
        Console.WriteLine("PASS: trusted action confirmation, exact request identity, duplicate coalescing, decline/cancel, late approval fencing, provider retirement, synchronous timeout and clean workflow recovery.");
    }
    private sealed record Question(ActionConfirmationRequest Request,TaskCompletionSource<bool> Answer);
    private sealed class Prompt:IActionConfirmation
    {
        private readonly Channel<Question> _questions=Channel.CreateUnbounded<Question>();internal int Count;
        public ValueTask<bool> ConfirmAsync(ActionConfirmationRequest request,CancellationToken cancellation)
        {
            Interlocked.Increment(ref Count);var answer=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Check(_questions.Writer.TryWrite(new(request,answer)));
            return new(answer.Task); // Deliberately ignore cancellation to exercise late answers.
        }
        internal async Task<Question> Next()=>await _questions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }
    private sealed class BlockingPrompt(ManualResetEventSlim release):IActionConfirmation
    {
        internal TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously),Exited=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<bool> ConfirmAsync(ActionConfirmationRequest request,CancellationToken cancellation)
        {Entered.SetResult();release.Wait(TimeSpan.FromSeconds(10));Exited.SetResult();return ValueTask.FromResult(true);}
    }
    private sealed class Entry(string id):IPluginBootstrapV1
    {
        internal ABIContext? Context;internal PluginIdentity? Identity;internal int Calls;private ulong _handler;
        internal bool DenyActionEffects;
        internal ManualResetEventSlim? BlockingRelease;
        internal TaskCompletionSource HandlerEntered=new(TaskCreationOptions.RunContinuationsAsynchronously),HandlerExited=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static readonly string[] Groups=["Actions","Workflows","Tasks"];
        private static readonly ImmutableArray<string> Permissions=Abi100Catalog.Operations.Where(o=>Groups.Contains(o.Group)).Select(o=>o.Permission).Where(p=>!string.IsNullOrEmpty(p)).Distinct().ToImmutableArray();
        private ImmutableArray<string> Grants=>DenyActionEffects?Permissions.Remove("nexa.actions.write"):Permissions;
        internal PluginLoadSource Source()=>new(new(1,id,"1.0.0",1,new("test","test"),new([],[],"fixture"),[100],[],[],[],Grants,[]),Grants,_=>ValueTask.FromResult<IPluginBootstrapV1>(this),()=>ValueTask.CompletedTask);
        public async ValueTask<AbiReply<PreInitResult>> OnPreInitialize(PreInitContext context)
        {
            Identity=context.Identity;
            _handler=context.Callbacks.Register("effect",(input,options)=>
            {
                Interlocked.Increment(ref Calls);var value=ActionRequest.Read(input).Inputs;
                if(TextResult.Read(value).Text=="blocked")
                {HandlerEntered.SetResult();BlockingRelease!.Wait(TimeSpan.FromSeconds(10));HandlerExited.SetResult();}
                return ValueTask.FromResult(AbiReply<AbiValue>.Ok(TextResult.Read(value).Text=="invalid"?Empty.Write(new()):value));
            }).Value;
            var draft=new Abi100Draft(Success(await context.Negotiator.GetABIContext(100,Options())));
            foreach(var contract in Abi100Catalog.Operations.Where(o=>Groups.Contains(o.Group)).Select(o=>o.ContractId).Distinct())draft=draft.Require(contract);
            Context=Success(await draft.Make(context.Negotiator,Guid.NewGuid(),Grants,default));return AbiReply<PreInitResult>.Ok(new(Context,[],[],[],[]));
        }
        public async ValueTask<AbiReply<Unit>> OnInitialize(ABIContext context,StageContext stage)
        {
            if(id!="confirm")return AbiReply<Unit>.Ok(new());
            using var api=context.ForStage100(stage);
            Success(await api.Actions.Register(new(new("confirm:effect"),Abi100Schemas.TextResult,Abi100Schemas.TextResult,[],[],IdempotencyMode.RequiredKey,true,false,ConfirmationRequirement.UserConfirmation,null,new("effect",[]),_handler),Options()));
            var node=new WorkflowNode("effect",WorkflowNodeKind.Action,new("confirm:effect"),new AbiValue.Null(),[],1,1,0,ParallelCompletion.All,[],Abi100Schemas.TextResult,Abi100Schemas.TextResult);
            Success(await api.Workflows.Register(new(new("confirm:workflow"),Abi100Schemas.TextResult,Abi100Schemas.TextResult,[node],"v1",true,ConcurrencyPolicy.Parallel,8,32),Options()));
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
