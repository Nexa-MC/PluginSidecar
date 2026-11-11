using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100.Data;
namespace Nexa.Plugin.Hosting;

public sealed partial class PluginLoadCoordinator
{
    internal LoadReport ReadOwnLoadReport(HostInvocation caller)
    {
        lock(authority.PublicationGate)
        {
            if(!_instances.TryGetValue(caller.Identity.LoadInstanceId,out var member)||member.Identity!=caller.Identity)
                throw new AbiHostException(AbiErrors.HANDLE_OWNER_MISMATCH);
            var dependencies=ImmutableArray.CreateBuilder<string>();var seen=new HashSet<string>(StringComparer.Ordinal){caller.Identity.PluginId};
            var pending=new Queue<Attempt>();pending.Enqueue(member);
            while(pending.TryDequeue(out var current))
                foreach(var dependency in current.Source.Manifest.RequiredDependencies.OrderBy(d=>d.PluginId,StringComparer.Ordinal))
                {
                    if(!seen.Add(dependency.PluginId))continue;
                    if(seen.Count>1024)throw new AbiHostException(AbiErrors.MESSAGE_TOO_LARGE);
                    dependencies.Add(dependency.PluginId);
                    var provider=_instances.Values.FirstOrDefault(a=>a.GroupId==member.GroupId&&a.Identity.PluginId==dependency.PluginId)??_active.GetValueOrDefault(dependency.PluginId);
                    if(provider is not null)pending.Enqueue(provider);
                }
            return new(caller.Identity.PluginId,caller.Identity.LoadInstanceId,member.State==LoadState.Active?"Active":member.Stage.ToString(),
                Findings(member).Select(f=>new ValidationIssue(f.Code,new(f.Message,[]),caller.Identity.PluginId,f.Fatal)).ToImmutableArray(),dependencies.ToImmutable());
        }
    }
    private static ImmutableArray<Finding> Findings(Attempt member)
    {lock(member.Diagnostics)return member.Diagnostics.ToImmutableArray();}
    private static bool AddFindings(Attempt member,IEnumerable<Finding> findings)
    {
        var incoming=findings.Take(1025).ToArray();
        if(incoming.Any(f=>f is null||f.Code is null||f.Message is null||f.Code.Length>256||f.Message.Length>4096))return false;
        lock(member.Diagnostics)
        {
            if(incoming.Length+member.Diagnostics.Count>1024||incoming.Sum(f=>(long)f.Code.Length+f.Message.Length)+member.Diagnostics.Sum(f=>(long)f.Code.Length+f.Message.Length)>262144)return false;
            member.Diagnostics.AddRange(incoming);return true;
        }
    }
}
