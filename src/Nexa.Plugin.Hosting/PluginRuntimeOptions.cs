using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100.Data;
namespace Nexa.Plugin.Hosting;

/// <summary>Trusted embedding options; these never come from a plugin manifest.</summary>
public sealed record PluginRuntimeOptions
{
    public TimeProvider Clock { get; init; } = TimeProvider.System;
    public bool AutomaticSchedules { get; init; } = true;
    public IWorkflowInteraction? Interaction { get; init; }
    public IActionConfirmation? ActionConfirmation { get; init; }
    public ImmutableArray<IRegistryConsumer> RegistryConsumers { get; init; } = [];
    public Func<PluginIdentity,OverlayDefinition,bool>? ApproveForeignOverlay { get; init; }
    public Func<QualifiedId,ImmutableArray<QualifiedId>,ImmutableArray<QualifiedId>>? SelectOverlays { get; init; }
}

public sealed record ActionConfirmationRequest(Guid RequestId, PluginIdentity Caller,
    PluginIdentity Provider, ActionDescriptor Action, ActionRequest Request, Guid CorrelationId);

/// <summary>Trusted Host confirmation UI. Approval applies only to this immutable request.
/// Implementations must honour cancellation and never execute the action themselves.</summary>
public interface IActionConfirmation
{
    ValueTask<bool> ConfirmAsync(ActionConfirmationRequest request, CancellationToken cancellation);
}

public sealed record WorkflowInteractionRequest(Guid RequestId, PluginIdentity Owner, EntityId Run,
    string Step, UIDialog Dialog, SchemaRef AnswerSchema, Guid CorrelationId);

/// <summary>Display an authorized dialog through the Host's state/intent boundary.
/// Repeated RequestId values refer to the same interrupted request.</summary>
public interface IWorkflowInteraction
{
    ValueTask<AbiReply<AbiValue>> AskAsync(WorkflowInteractionRequest request, CancellationToken cancellation);
}

/// <summary>Trusted application-state port. Validate is side-effect-free. Publish
/// accepts immutable state and must not call guest code or perform business effects.</summary>
public interface IRegistryConsumer
{
    QualifiedId Id { get; }
    bool Validate(RegistryDescriptor registry, DefinitionBatch definitions);
    void Publish(RegistryDescriptor registry, Snapshot<DefinitionBatch> snapshot);
}
