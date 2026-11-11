using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100.Data;
namespace PluginSidecar.WasmRuntime;

/// <summary>Host-side continuation driver. Guest effects always use the caller's
/// original admitted capability delegate, never a remotely supplied identity.</summary>
public sealed class RemoteWasmPluginMachine:IWasmPluginMachine
{
    public const string ExecutionContract="plugin.execution.v1";
    public const string PackageName="Nexa.Plugin.Sidecar";
    private readonly IPluginBinaryExchange _exchange;private readonly Guid _guest;
    private readonly SemaphoreSlim _gate=new(1,1);private bool _faulted,_disposed;
    private RemoteWasmPluginMachine(IPluginBinaryExchange exchange,Guid guest){_exchange=exchange;_guest=guest;}
    public static async ValueTask<IWasmPluginMachine> CreateAsync(IPluginBinaryExchange exchange,ReadOnlyMemory<byte> wasm,CancellationToken cancellation=default)
    {
        if(wasm.Length is <8 or >25*1024*1024)throw new InvalidDataException(AbiErrors.PACKAGE_INVALID);
        var guest=Guid.NewGuid();var machine=new RemoteWasmPluginMachine(exchange,guest);
        try
        {
            var reply=await exchange.ExchangeAsync(GuestExecutionWire.Request(GuestExecutionWire.Create,guest,w=>{w.Write(wasm.Length);w.Write(wasm.Span);}),cancellation).ConfigureAwait(false);
            if(reply.Length!=1||reply[0]!=1)throw new InvalidDataException(AbiErrors.PACKAGE_INVALID);
            return machine;
        }
        catch{await machine.DisposeAsync().ConfigureAwait(false);throw;}
    }
    public async ValueTask<AbiValue> RunDetailedAsync(AbiValue invocation,Func<WasmHostCall,CancellationToken,ValueTask<AbiReply<AbiValue>>> invoke,TimeSpan budget,CancellationToken cancellation=default)
    {
        if(budget<=TimeSpan.Zero||budget>TimeSpan.FromMinutes(5))throw new ArgumentOutOfRangeException(nameof(budget));
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(budget);
        var token=timeout.Token;await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed,this);if(_faulted)throw new InvalidOperationException("Reload the interrupted guest.");
            var run=Guid.NewGuid();byte[] input=AbiValueBinary.Encode(invocation);
            byte[] request=GuestExecutionWire.Request(GuestExecutionWire.Start,_guest,w=>{w.Write(run.ToByteArray());w.Write((int)Math.Ceiling(budget.TotalMilliseconds));w.Write(input.Length);w.Write(input);});
            for(int sequence=1;sequence<=1025;sequence++)
            {
                var bytes=await _exchange.ExchangeAsync(request,token).ConfigureAwait(false);token.ThrowIfCancellationRequested();
                var fields=DataCodec.ReadMap(AbiValueBinary.Decode(bytes));
                string kind=DataCodec.ReadString(DataCodec.Field(fields,"kind"));
                if(kind=="complete")return DataCodec.Field(fields,"value");
                if(kind=="failed")throw new InvalidDataException(DataCodec.ReadString(DataCodec.Field(fields,"code")));
                if(kind!="invoke"||sequence>1024)throw new InvalidDataException("Invalid remote continuation.");
                var call=GuestExecutionWire.ReadCall(fields);
                var pending=invoke(call,token).AsTask();
                AbiReply<AbiValue> result;
                try{result=await pending.WaitAsync(token).ConfigureAwait(false);}catch{_=Observe(pending);throw;}
                var reply=GuestExecutionWire.Reply(result);
                request=GuestExecutionWire.Request(GuestExecutionWire.Resume,_guest,w=>{w.Write(run.ToByteArray());w.Write(sequence);w.Write(reply.Length);w.Write(reply);});
            }
            throw new InvalidDataException("Guest continuation quota exceeded.");
        }
        catch{_faulted=true;throw;}
        finally{_gate.Release();}
    }
    private static async Task Observe(Task task){try{await task.ConfigureAwait(false);}catch(Exception e)when(e is not OutOfMemoryException and not AccessViolationException){}}
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if(_disposed)return;_disposed=true;
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try{await _exchange.ExchangeAsync(GuestExecutionWire.Request(GuestExecutionWire.Dispose,_guest),timeout.Token).ConfigureAwait(false);}
            catch(Exception e)when(e is not OutOfMemoryException and not AccessViolationException){}
        }
        finally{_gate.Release();}
    }
}
