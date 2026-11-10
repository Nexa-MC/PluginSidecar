using Nexa.Plugin.Bootstrap.V1;
using static Nexa.Plugin.Sdk.Abi100.Values;
namespace Nexa.Plugin.Sdk.Abi100;

public sealed class Events100(EventAbility ability)
{
    public const string ContractId = "nexa.events/100";
    public ValueTask<AbiReply<AbiValue>> DeclareEvent(EventDeclaration value, InvocationOptions options) => ability.Invoke(ContractId, 1, M(("name", S(value.Name)), ("payloadSchemaId", S(value.PayloadSchemaId)), ("payloadSchemaDigest", S(value.PayloadSchemaDigest)), ("delivery", S("BestEffortAtMostOnce")), ("maxPayloadBytes", I(value.MaxPayloadBytes)), ("queueLimit", I(value.QueueLimit))), options);
    public ValueTask<AbiReply<AbiValue>> Subscribe(EventSubscription value, InvocationOptions options) => ability.Invoke(ContractId, 2, M(("sourcePluginId", S(value.SourcePluginId)), ("eventName", S(value.EventName)), ("schemaId", S(value.SchemaId)), ("handler", U(value.Handler.Value)), ("required", B(value.Required)), ("concurrency", I(value.Concurrency)), ("queueLimit", I(value.QueueLimit))), options);
    public ValueTask<AbiReply<AbiValue>> ExportService(ServiceDeclaration value, CallbackHandle callback, InvocationOptions options) => ability.Invoke(ContractId, 3, M(("serviceId", S(value.ServiceId)), ("contractId", S(value.ContractId)), ("contractVersion", U(value.ContractVersion)), ("schemaDigest", S(value.SchemaDigest)), ("methods", new AbiValue.Array(value.Methods)), ("healthProbe", value.HealthProbe), ("handler", U(callback.Value))), options);
    public ValueTask<AbiReply<AbiValue>> Publish(EventHandle handle, AbiValue payload, InvocationOptions options) => ability.Invoke(ContractId, 4, M(("handle", U(handle.Value)), ("payload", payload)), options);
    public ValueTask<AbiReply<AbiValue>> Unsubscribe(RegistrationHandle handle, InvocationOptions options) => ability.Invoke(ContractId, 5, M(("handle", U(handle.Value))), options);
}
