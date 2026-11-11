using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100;
using Nexa.Plugin.Sdk.Abi100.Data;
namespace Nexa.Plugin.Hosting;

public sealed partial class PluginRuntimeServices
{
    private IEnumerable<HostCapabilityRoute> AvailabilityRoutes => [
        HostRoutes.Typed<QualifiedRequest,SubscriptionRef>("Contracts","WatchAvailability",(c,v)=>
        {
            lock(_gate){var reference=Subscribe(c,"contract-availability",v.Id.Value);RefreshAvailability(_subscriptions[reference.Token]);return ValueTask.FromResult(reference);}
        }),
        HostRoutes.Typed<ServiceLookup,SubscriptionRef>("Plugins","WatchAvailability",(c,v)=>
        {
            if(v.MinimumVersion<1||v.MaximumVersion<v.MinimumVersion)throw new AbiHostException(AbiErrors.SCHEMA_INVALID);
            lock(_gate){var reference=Subscribe(c,"service-availability",v.Service.Value);var sub=_subscriptions[reference.Token];sub.ServiceLookup=v;RefreshAvailability(sub);return ValueTask.FromResult(reference);}
        })
    ];
    private void RefreshAvailability()
    {foreach(var sub in _subscriptions.Values.Where(s=>s.Kind is "contract-availability" or "service-availability"))RefreshAvailability(sub);}
    private void RefreshAvailability(Subscription sub)
    {
        if(!sub.Active||sub.Closed)return;
        ulong version=0;bool available=false;
        string kind=sub.Kind=="contract-availability"?"contract":"service";
        if(_entries.TryGetValue((kind,new(sub.Key)),out var entry)&&entry.Registration.State==RegistrationState.Active)
        {
            if(kind=="contract"){version=PublicContract.Read(entry.Value).Version;available=true;}
            else if(sub.ServiceLookup is { } lookup)
            {version=ServiceDefinition.Read(entry.Value).Contract.Version;available=entry.Owner.PluginId==lookup.Provider&&version>=lookup.MinimumVersion&&version<=lookup.MaximumVersion;}
        }
        var snapshot=new CapabilityDescription(sub.Key,version,available?CapabilityAvailability.Supported:CapabilityAvailability.Unavailable,[],[]);
        if(sub.LastAvailability is { } last&&snapshot.Availability==last.Availability&&snapshot.Version==last.Version)return;
        if(Enqueue(sub,CapabilityDescription.Write(snapshot)))sub.LastAvailability=snapshot;
    }
}
