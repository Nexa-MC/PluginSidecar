using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;
namespace Nexa.Plugin.Hosting;

public sealed class AbiHostException(string code, ImmutableDictionary<string, string>? details = null) : Exception(code)
{
    public string Code { get; } = code;
    public ImmutableDictionary<string, string> Details { get; } = details ?? ImmutableDictionary<string, string>.Empty;
}

public static class HostRoutes
{
    public static HostCapabilityRoute Typed<TInput, TOutput>(string group, string method,
        Func<HostInvocation, TInput, ValueTask<TOutput>> handler) where TInput : IAbiData<TInput> where TOutput : IAbiData<TOutput>
    {
        var descriptor = Abi100Catalog.Operations.Single(d => d.Group == group && d.Name == method);
        return new(descriptor, async (invocation, input) =>
        {
            try { return AbiReply<AbiValue>.Ok(TOutput.Write(await handler(invocation, TInput.Read(input)).ConfigureAwait(false))); }
            catch (AbiHostException error) { return Failure(error.Code, error.Details); }
            catch (FormatException) { return Failure(AbiErrors.SCHEMA_INVALID, ImmutableDictionary<string, string>.Empty); }
            AbiReply<AbiValue> Failure(string code, ImmutableDictionary<string, string> details) => AbiReply<AbiValue>.Fail(new(code, code, invocation.Phase,
                descriptor.ContractId + "." + method, invocation.Identity.PluginId, invocation.Identity.LoadInstanceId, invocation.Options.CorrelationId, false, details) { OperationId = invocation.Options.OperationId });
        });
    }
}
