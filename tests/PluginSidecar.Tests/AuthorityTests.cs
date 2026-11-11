using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Hosting;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;

internal static class AuthorityTests
{
    internal static async Task Run()
    {
        int executions = 0;
        var descriptor = Abi100Catalog.Operations.Single(o => o.Group == "State" && o.Name == "UpdateOwned");
        var authority = new CapabilityAuthority([new(descriptor, (invocation, argument) =>
        {
            executions++;
            Check(invocation.Identity.PluginId == "test.plugin" && invocation.Phase == "Active");
            return ValueTask.FromResult(AbiReply<AbiValue>.Ok(argument));
        }) { MaximumReplyBytes=65536 }]);
        var identity = new PluginIdentity("test.plugin", "1.0.0", Guid.NewGuid()); var group = Guid.NewGuid();
        var admission = authority.Admit(identity, group, [descriptor.Permission]);
        var options = new InvocationOptions(Guid.NewGuid(), TimeSpan.FromSeconds(2), default) { CorrelationId = Guid.NewGuid(), IdempotencyKey = "edit-1" };
        Check(!admission.Context.GetAbility(descriptor.ContractId).Success);
        Check(!(await admission.Negotiator.GetABIContext(200, options)).Success);
        Check((await admission.Negotiator.GetABIContext(0, options)).Error?.Code == AbiErrors.ABI_VERSION_INVALID);
        Check((await admission.Negotiator.GetABIContext(101, options)).Error?.Code == AbiErrors.ABI_VERSION_INVALID);
        var draft = await admission.Negotiator.GetABIContext(100, options);
        var missing = await new Abi100Draft(draft.Value).Require("missing.capability").Make(admission.Negotiator, Guid.NewGuid(), [], default);
        Check(!missing.Success && missing.Error!.Code == AbiErrors.CAPABILITY_UNAVAILABLE);
        var assemble = new Abi100Draft(draft.Value).Require("nexa.state").Optional("missing.capability");
        var makeId = Guid.NewGuid();
        var ready = await assemble.Make(admission.Negotiator, makeId, [descriptor.Permission], default);
        Check(ready.Success && ReferenceEquals(ready.Value, (await assemble.Make(admission.Negotiator, makeId, [descriptor.Permission], default)).Value));
        var changed = await assemble.Make(admission.Negotiator, makeId, [], default);
        Check(!changed.Success && changed.Error!.Code == AbiErrors.REQUEST_ID_REUSED);
        var ability = ready.Value.GetAbility(descriptor.ContractId).Value;
        var beforeActive = await ability.Invoke(descriptor.ContractId, descriptor.MethodId, new AbiValue.Null(), options);
        Check(!beforeActive.Success && beforeActive.Error!.Code == AbiErrors.STAGE_DENIED && executions == 0);
        foreach (var phase in new[] { LoadStage.Initialize, LoadStage.PostInitialize, LoadStage.Checkup, LoadStage.Finalize })
        { var stage = new StageContext(identity, phase, Guid.NewGuid(), group, default, TimeSpan.FromSeconds(2)); admission.Enter(stage); admission.End(stage); }
        admission.Activate();
        var argument = DataCodec.Map(("value", new AbiValue.Int64(1)));
        Check((await ability.Invoke(descriptor.ContractId, descriptor.MethodId, argument, options)).Success);
        Check((await ability.Invoke(descriptor.ContractId, descriptor.MethodId, argument, options with { OperationId = Guid.NewGuid() })).Success && executions == 1);
        var conflict = await ability.Invoke(descriptor.ContractId, descriptor.MethodId, new AbiValue.Null(), options);
        Check(!conflict.Success && conflict.Error!.Code == AbiErrors.REQUEST_ID_REUSED && executions == 1);
        var stale = await ability.Invoke(descriptor.ContractId, descriptor.MethodId, argument, options with { StageInvocationId = Guid.NewGuid() });
        Check(!stale.Success && stale.Error!.Code == AbiErrors.STAGE_FROZEN);
        var oversized=await ability.Invoke(descriptor.ContractId,descriptor.MethodId,new AbiValue.Array([new AbiValue.Utf8String(new string('x',35000)),new AbiValue.Utf8String(new string('x',35000))]),options with {IdempotencyKey="oversized"});
        Check(oversized.Error?.Code==AbiErrors.MESSAGE_TOO_LARGE);
        Check((await ability.Invoke(descriptor.ContractId,descriptor.MethodId,argument,options with {IdempotencyKey="after-large"})).Success);
        admission.Retire(false);
        var stopped = await ability.Invoke(descriptor.ContractId, descriptor.MethodId, argument, options);
        Check(!stopped.Success && stopped.Error!.Code == AbiErrors.PLUGIN_STOPPED);
        admission.CompleteStop();
        Check(!ready.Value.GetAbility(descriptor.ContractId).Success);
        var release = new TaskCompletionSource<AbiReply<AbiValue>>(TaskCreationOptions.RunContinuationsAsynchronously);
        int slowCalls = 0;
        var slowAuthority = new CapabilityAuthority([new(descriptor, (_, _) => { Interlocked.Increment(ref slowCalls); return new(release.Task); })]);
        var slowIdentity = identity with { LoadInstanceId = Guid.NewGuid() }; var slowGroup = Guid.NewGuid();
        var slow = slowAuthority.Admit(slowIdentity, slowGroup, [descriptor.Permission]);
        var slowDraft = await slow.Negotiator.GetABIContext(100, options);
        var slowReady = await new Abi100Draft(slowDraft.Value).Require("nexa.state").Make(slow.Negotiator, Guid.NewGuid(), [descriptor.Permission], default);
        foreach (var phase in new[] { LoadStage.Initialize, LoadStage.PostInitialize, LoadStage.Checkup, LoadStage.Finalize })
        { var stage = new StageContext(slowIdentity, phase, Guid.NewGuid(), slowGroup, default, TimeSpan.FromSeconds(2)); slow.Enter(stage); slow.End(stage); }
        slow.Activate(); var slowAbility = slowReady.Value.GetAbility(descriptor.ContractId).Value;
        var unknown = await slowAbility.Invoke(descriptor.ContractId, descriptor.MethodId, argument, options with { Budget = TimeSpan.FromMilliseconds(30) }).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Check(!unknown.Success && unknown.Error!.Code == AbiErrors.OUTCOME_UNKNOWN);
        var same = slowAbility.Invoke(descriptor.ContractId, descriptor.MethodId, argument, options).AsTask();
        release.SetResult(AbiReply<AbiValue>.Ok(argument));
        Check((await same).Success && slowCalls == 1);
        slow.Retire(false); slow.CompleteStop();
        Console.WriteLine("PASS: real Host authority exact ABI/Make, required vs optional admission, stages, idempotency input binding and retired instance rejection.");
    }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Authority assertion failed."); }
}
