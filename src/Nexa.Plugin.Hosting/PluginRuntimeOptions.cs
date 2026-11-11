using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100.Data;
namespace Nexa.Plugin.Hosting;

/// <summary>Trusted embedding options; these never come from a plugin manifest.</summary>
public sealed record PluginRuntimeOptions
{
    public TimeProvider Clock { get; init; } = TimeProvider.System;
    public bool AutomaticSchedules { get; init; } = true;
    public IWorkflowInteraction? Interaction { get; init; }
}

public sealed record WorkflowInteractionRequest(Guid RequestId, PluginIdentity Owner, EntityId Run,
    string Step, UIDialog Dialog, SchemaRef AnswerSchema, Guid CorrelationId);

/// <summary>Display an authorized dialog through the Host's state/intent boundary.
/// Repeated RequestId values refer to the same interrupted request.</summary>
public interface IWorkflowInteraction
{
    ValueTask<AbiReply<AbiValue>> AskAsync(WorkflowInteractionRequest request, CancellationToken cancellation);
}
