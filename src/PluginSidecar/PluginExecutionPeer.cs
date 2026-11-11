using System.Collections.Concurrent;
using Nexa.Sidecar.Protocol;
using Nexa.Sidecar.Transport;
using PluginSidecar.WasmRuntime;
namespace PluginSidecar;

internal sealed class PluginExecutionPeer(SidecarFrameTransport wire)
{
    private readonly ConcurrentDictionary<Guid,CancellationTokenSource> _calls=new();
    private readonly List<Task> _workers=[];private bool _active, _retired;
    internal async Task RunAsync(CancellationToken cancellation)
    {
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        await using var execution=new GuestExecutionServer();using var endpoint=new ChunkedPluginEndpoint(execution);
        try
        {
            using(var startup=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {startup.CancelAfter(TimeSpan.FromSeconds(15));await Register(startup.Token).ConfigureAwait(false);}
            while(!lifetime.IsCancellationRequested)
            {
                var frame=await wire.ReceiveAsync(lifetime.Token).ConfigureAwait(false);
                switch(frame.MessageType)
                {
                    case SidecarMessageType.Activate:
                        if(_active||_retired)throw new InvalidDataException("Duplicate activation.");_active=true;break;
                    case SidecarMessageType.Deactivate:
                        _active=false;_retired=true;CancelCalls();
                        await Task.WhenAll(_workers).ConfigureAwait(false);
                        await execution.DisposeAsync().ConfigureAwait(false);break;
                    case SidecarMessageType.HealthPing:
                        await Send(SidecarMessageType.HealthPong,frame.Payload,frame.CorrelationId,lifetime.Token).ConfigureAwait(false);break;
                    case SidecarMessageType.Cancel:
                        var cancelled=SidecarControlMessages.DecodeCancel(frame.Payload.Span);
                        if(_calls.TryGetValue(cancelled.CorrelationId,out var pending))Cancel(pending);break;
                    case SidecarMessageType.CommandRequest:
                        if(!_active||!frame.CorrelationId.IsAssigned||_calls.Count>=32)throw new InvalidDataException("Execution request not admitted.");
                        var stop=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);stop.CancelAfter(TimeSpan.FromMinutes(5));
                        if(!_calls.TryAdd(frame.CorrelationId.Value,stop)){stop.Dispose();throw new InvalidDataException("Duplicate correlation.");}
                        _workers.RemoveAll(t=>t.IsCompletedSuccessfully);
                        foreach(var fault in _workers.Where(t=>t.IsFaulted))await fault.ConfigureAwait(false);
                        _workers.Add(Execute(frame,endpoint,stop,lifetime.Token));break;
                    case SidecarMessageType.Unregister:
                        _active=false;CancelCalls();
                        await Send(SidecarMessageType.Unregistered,SidecarControlMessages.EncodeUnregister("Retired"),frame.CorrelationId,lifetime.Token).ConfigureAwait(false);break;
                    case SidecarMessageType.Shutdown:return;
                    default:throw new InvalidDataException("Unexpected Host frame.");
                }
            }
        }
        finally
        {
            _active=false;lifetime.Cancel();CancelCalls();
            try{await Task.WhenAll(_workers).ConfigureAwait(false);}catch(Exception e)when(e is not OutOfMemoryException and not AccessViolationException){}
        }
    }
    private async Task Register(CancellationToken token)
    {
        var hello=await wire.ReceiveAsync(token).ConfigureAwait(false);
        if(hello.MessageType!=SidecarMessageType.Hello)throw new InvalidDataException();
        var offered=SidecarHandshake.DecodeHelloDetails(hello.Payload.Span);
        if(offered.ProtocolVersion!=SidecarProtocol.Version||!offered.Features.HasFlag(SidecarFeatures.BinaryPayloads))throw new InvalidDataException();
        var features=offered.Features&(SidecarFeatures.BinaryPayloads|SidecarFeatures.Health|SidecarFeatures.Unregistration);
        await Send(SidecarMessageType.Welcome,SidecarHandshake.EncodeWelcome(SidecarProtocol.Version,Guid.NewGuid(),null,features),hello.CorrelationId,token).ConfigureAwait(false);
        await Send(SidecarMessageType.RegisterBegin,SidecarRegistration.EncodeBegin(1),SidecarCorrelationId.Create(),token).ConfigureAwait(false);
        var item=new SidecarRegistrationItem(SidecarRegistrationKind.Command,RemoteWasmPluginMachine.ExecutionContract,0,SidecarWireCodecs.GeneratedDto){ResultCodecId=SidecarWireCodecs.GeneratedDto};
        await Send(SidecarMessageType.RegisterItem,SidecarRegistration.EncodeItem(item),SidecarCorrelationId.Create(),token).ConfigureAwait(false);
        await Send(SidecarMessageType.RegisterEnd,SidecarRegistration.EncodeEnd(),SidecarCorrelationId.Create(),token).ConfigureAwait(false);
        await Send(SidecarMessageType.StateSnapshotBegin,SidecarStateSnapshot.EncodeBegin(0),SidecarCorrelationId.Create(),token).ConfigureAwait(false);
        await Send(SidecarMessageType.StateSnapshotEnd,SidecarStateSnapshot.EncodeEnd(),SidecarCorrelationId.Create(),token).ConfigureAwait(false);
        if((await wire.ReceiveAsync(token).ConfigureAwait(false)).MessageType!=SidecarMessageType.Ready)throw new InvalidDataException("Host rejected registration.");
    }
    private async Task Execute(SidecarFrame frame,ChunkedPluginEndpoint endpoint,CancellationTokenSource stop,CancellationToken lifetime)
    {
        byte[] bytes=[];string? error=null;
        try
        {
            var input=SidecarDataMessages.DecodeBinaryRequestDetails(frame.Payload.Span);
            if(input.ContractId!=1||!input.IsBinary||input.Value.CodecId!=SidecarWireCodecs.GeneratedDto)throw new InvalidDataException();
            bytes=await endpoint.ExchangeAsync(input.Value.ToArray(),stop.Token).ConfigureAwait(false);
        }
        catch(OperationCanceledException){error="plugin.execution.cancelled";}
        catch(Exception e)when(e is not OutOfMemoryException and not AccessViolationException){error="plugin.execution.failed";}
        finally{_calls.TryRemove(frame.CorrelationId.Value,out _);stop.Dispose();}
        await Send(SidecarMessageType.CommandResult,SidecarDataMessages.EncodeBinaryResult(error is null,new(SidecarWireCodecs.GeneratedDto,bytes),error),frame.CorrelationId,lifetime).ConfigureAwait(false);
    }
    private ValueTask Send(SidecarMessageType type,ReadOnlyMemory<byte> bytes,SidecarCorrelationId correlation,CancellationToken token)=>wire.SendAsync(new(SidecarProtocol.Version,type,SidecarFrameTraits.None,correlation,bytes),token);
    private static void Cancel(CancellationTokenSource source){try{source.Cancel();}catch(ObjectDisposedException){}}
    private void CancelCalls(){foreach(var stop in _calls.Values)Cancel(stop);}
}
