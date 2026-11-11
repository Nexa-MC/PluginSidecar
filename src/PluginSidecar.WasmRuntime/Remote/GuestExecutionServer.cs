using System.Security.Cryptography;
using System.Threading.Channels;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100.Data;
namespace PluginSidecar.WasmRuntime;

/// <summary>One authenticated Sidecar session's import-free execution owner.</summary>
public sealed class GuestExecutionServer:IPluginBinaryExchange,IAsyncDisposable
{
    private readonly object _gate=new();private readonly Dictionary<Guid,Guest> _guests=[];private bool _closed;private int _creating;
    public async ValueTask<byte[]> ExchangeAsync(ReadOnlyMemory<byte> request,CancellationToken cancellation)
    {
        using var stream=new MemoryStream(request.ToArray(),false);using var reader=new BinaryReader(stream);
        byte operation=reader.ReadByte();Guid id=GuestExecutionWire.Guid(reader);if(id==Guid.Empty)throw new InvalidDataException();
        if(operation==GuestExecutionWire.Create)
        {
            byte[] wasm=GuestExecutionWire.Bytes(reader,reader.ReadInt32());GuestExecutionWire.End(reader);
            lock(_gate){ObjectDisposedException.ThrowIf(_closed,this);if(_guests.ContainsKey(id)||_guests.Count+_creating>=16)throw new InvalidOperationException("Guest quota or duplicate identity.");_creating++;}
            WasmPluginMachine? machine=null;
            try
            {
                machine=await Task.Run(()=>new WasmPluginMachine(wasm),cancellation).ConfigureAwait(false);
                lock(_gate){cancellation.ThrowIfCancellationRequested();ObjectDisposedException.ThrowIf(_closed,this);_guests.Add(id,new(machine));machine=null;}
                return [1];
            }
            finally{CryptographicOperations.ZeroMemory(wasm);lock(_gate)_creating--;if(machine is not null)await machine.DisposeAsync().ConfigureAwait(false);}
        }
        Guest? guest;
        if(operation==GuestExecutionWire.Dispose)
        {
            GuestExecutionWire.End(reader);lock(_gate)_guests.Remove(id,out guest);
            if(guest is not null)await guest.DisposeAsync().ConfigureAwait(false);return [1];
        }
        lock(_gate){ObjectDisposedException.ThrowIf(_closed,this);guest=_guests.GetValueOrDefault(id)??throw new InvalidOperationException("Guest unavailable.");}
        Guid run=GuestExecutionWire.Guid(reader);if(run==Guid.Empty)throw new InvalidDataException();
        int number=reader.ReadInt32();byte[] value=GuestExecutionWire.Bytes(reader,reader.ReadInt32());GuestExecutionWire.End(reader);
        if(value.Length>DataCodec.MaximumBytes)throw new InvalidDataException();
        return operation switch
        {
            GuestExecutionWire.Start=>await guest.Start(run,number,AbiValueBinary.Decode(value),cancellation).ConfigureAwait(false),
            GuestExecutionWire.Resume=>await guest.Resume(run,number,GuestExecutionWire.ReadReply(value),cancellation).ConfigureAwait(false),
            _=>throw new InvalidDataException("Unknown execution operation.")
        };
    }
    private sealed class Guest(WasmPluginMachine machine):IAsyncDisposable
    {
        private readonly object _gate=new();private Guid _run;private int _sequence;private bool _active,_exchange,_closed;
        private CancellationTokenSource? _lifetime;private Task? _worker;
        private Channel<AbiValue>? _out;private TaskCompletionSource<AbiReply<AbiValue>>? _reply;
        internal async ValueTask<byte[]> Start(Guid run,int budget,AbiValue input,CancellationToken cancellation)
        {
            if(budget is <1 or >300000)throw new InvalidDataException();
            lock(_gate)
            {
                ObjectDisposedException.ThrowIf(_closed,this);
                if(_active||_exchange)throw new InvalidOperationException("A guest callback is already running.");
                _run=run;_sequence=0;_active=true;_exchange=true;
                _lifetime?.Dispose();_lifetime=new CancellationTokenSource(TimeSpan.FromMilliseconds(budget));
                _out=Channel.CreateBounded<AbiValue>(1);var token=_lifetime.Token;var output=_out;
                _worker=Task.Run(async()=>
                {
                    AbiValue result;
                    try
                    {
                        var value=await machine.RunDetailedAsync(input,async(call,stop)=>
                        {
                            TaskCompletionSource<AbiReply<AbiValue>> pending;
                            lock(_gate){pending=new(TaskCreationOptions.RunContinuationsAsynchronously);_reply=pending;_sequence++;}
                            await output.Writer.WriteAsync(GuestExecutionWire.Call(call),stop).ConfigureAwait(false);
                            return await pending.Task.WaitAsync(stop).ConfigureAwait(false);
                        },TimeSpan.FromMilliseconds(budget),token).ConfigureAwait(false);
                        result=DataCodec.Map(("kind",DataCodec.WriteString("complete")),("value",value));
                    }
                    catch(Exception e)when(e is not OutOfMemoryException and not AccessViolationException)
                    {result=DataCodec.Map(("kind",DataCodec.WriteString("failed")),("code",DataCodec.WriteString(e is OperationCanceledException?AbiErrors.CANCELLED:AbiErrors.PLUGIN_EXCEPTION)));}
                    output.Writer.TryWrite(result);output.Writer.TryComplete();
                });
            }
            return await Next(cancellation).ConfigureAwait(false);
        }
        internal async ValueTask<byte[]> Resume(Guid run,int sequence,AbiReply<AbiValue> reply,CancellationToken cancellation)
        {
            lock(_gate)
            {
                ObjectDisposedException.ThrowIf(_closed,this);
                if(!_active||_exchange||run!=_run||sequence!=_sequence||_reply is null)throw new InvalidOperationException("Stale guest continuation.");
                _exchange=true;var pending=_reply;_reply=null;pending.SetResult(reply);
            }
            return await Next(cancellation).ConfigureAwait(false);
        }
        private async ValueTask<byte[]> Next(CancellationToken cancellation)
        {
            try
            {
                var result=await _out!.Reader.ReadAsync(cancellation).ConfigureAwait(false);
                var kind=DataCodec.ReadString(DataCodec.Field(DataCodec.ReadMap(result),"kind"));
                lock(_gate){if(kind!="invoke")_active=false;}
                return AbiValueBinary.Encode(result);
            }
            catch{lock(_gate){_lifetime?.Cancel();_closed=true;}throw;}
            finally{lock(_gate)_exchange=false;}
        }
        public async ValueTask DisposeAsync()
        {
            Task? worker;lock(_gate){_closed=true;_lifetime?.Cancel();worker=_worker;}
            if(worker is not null)await worker.ConfigureAwait(false);
            await machine.DisposeAsync().ConfigureAwait(false);_lifetime?.Dispose();
        }
    }
    public async ValueTask DisposeAsync()
    {
        Guest[] guests;lock(_gate){if(_closed)return;_closed=true;guests=_guests.Values.ToArray();_guests.Clear();}
        foreach(var guest in guests)await guest.DisposeAsync().ConfigureAwait(false);
    }
}
