using Nexa.Plugin.Bootstrap.V1;
namespace Nexa.Plugin.Hosting;

// The reference remains in captured ExecutionContexts after Dispose; they observe
// a revoked lease rather than silently returning to ordinary Active authority.
internal sealed class ProbeExecution(CapabilityAuthority authority,PluginIdentity identity,CancellationToken cancellation):IDisposable
{
    internal static readonly AsyncLocal<ProbeExecution?> Current=new();
    internal readonly CapabilityAuthority Authority=authority;
    internal readonly PluginIdentity Identity=identity;
    internal readonly CancellationToken Cancellation=cancellation;
    private readonly ProbeExecution? _parent=Current.Value;
    private int _closed;
    internal bool Closed=>Volatile.Read(ref _closed)!=0;
    internal void Enter()=>Current.Value=this;
    public void Dispose(){Interlocked.Exchange(ref _closed,1);Current.Value=_parent;}
}
