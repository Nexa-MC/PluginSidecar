using System.Security.Cryptography;
namespace PluginSidecar.WasmRuntime;

/// <summary>Explicit fragmentation within the existing 64 KiB Sidecar value limit.</summary>
public sealed class ChunkedPluginExchange(IPluginBinaryExchange wire):IPluginBinaryExchange
{
    public const int ChunkBytes=48*1024,MaximumBytes=26*1024*1024;
    internal static byte[] Packet(byte op,Guid id,Action<BinaryWriter>? write=null)=>GuestExecutionWire.Request(op,id,write);
    public async ValueTask<byte[]> ExchangeAsync(ReadOnlyMemory<byte> request,CancellationToken cancellation)
    {
        if(request.Length>MaximumBytes)throw new InvalidDataException("Transfer quota exceeded.");
        Guid id=Guid.NewGuid();
        try
        {
            await wire.ExchangeAsync(Packet(1,id,w=>{w.Write(request.Length);w.Write(SHA256.HashData(request.Span));}),cancellation).ConfigureAwait(false);
            for(int offset=0;offset<request.Length;offset+=ChunkBytes)
            {
                int start=offset,count=Math.Min(ChunkBytes,request.Length-offset);
                await wire.ExchangeAsync(Packet(2,id,w=>{w.Write(start);w.Write(count);w.Write(request.Span.Slice(start,count));}),cancellation).ConfigureAwait(false);
            }
            var header=await wire.ExchangeAsync(Packet(3,id),cancellation).ConfigureAwait(false);
            using var reader=new BinaryReader(new MemoryStream(header,false));int size=reader.ReadInt32();var digest=GuestExecutionWire.Bytes(reader,32);GuestExecutionWire.End(reader);
            if(size<0||size>4*1024*1024)throw new InvalidDataException("Reply quota exceeded.");
            byte[] result=new byte[size];
            for(int offset=0;offset<size;offset+=ChunkBytes)
            {
                int start=offset,count=Math.Min(ChunkBytes,size-offset);
                var chunk=await wire.ExchangeAsync(Packet(4,id,w=>{w.Write(start);w.Write(count);}),cancellation).ConfigureAwait(false);
                if(chunk.Length!=count)throw new InvalidDataException("Truncated reply.");chunk.CopyTo(result,offset);
            }
            if(!CryptographicOperations.FixedTimeEquals(SHA256.HashData(result),digest))throw new InvalidDataException("Reply integrity failed.");
            return result;
        }
        finally
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try{await wire.ExchangeAsync(Packet(5,id),timeout.Token).ConfigureAwait(false);}catch(Exception e)when(e is not OutOfMemoryException and not AccessViolationException){}
        }
    }
}

/// <summary>One authenticated session's bounded transfer buffers. No filesystem staging.</summary>
public sealed class ChunkedPluginEndpoint(IPluginBinaryExchange inner):IPluginBinaryExchange,IDisposable
{
    private sealed class Transfer(byte[] bytes,byte[] digest)
    {
        internal byte[] Bytes=bytes,Digest=digest;internal int Offset;internal bool Executing,Reply;
        internal readonly CancellationTokenSource Stop=new(TimeSpan.FromMinutes(5));
    }
    private readonly object _gate=new();private readonly Dictionary<Guid,Transfer> _transfers=[];private long _bytes;private bool _closed;
    public async ValueTask<byte[]> ExchangeAsync(ReadOnlyMemory<byte> request,CancellationToken cancellation)
    {
        if(request.Length>65535)throw new InvalidDataException();
        using var reader=new BinaryReader(new MemoryStream(request.ToArray(),false));byte op=reader.ReadByte();Guid id=GuestExecutionWire.Guid(reader);
        if(id==Guid.Empty)throw new InvalidDataException();
        Transfer transfer;
        lock(_gate)
        {
            ObjectDisposedException.ThrowIf(_closed,this);
            foreach(var old in _transfers.Where(p=>p.Value.Stop.IsCancellationRequested&&!p.Value.Executing).Select(p=>p.Key).ToArray())Remove(old);
            if(op==1)
            {
                int length=reader.ReadInt32();var digest=GuestExecutionWire.Bytes(reader,32);GuestExecutionWire.End(reader);
                if(length<0||length>ChunkedPluginExchange.MaximumBytes||_transfers.Count>=64||_bytes+length>64*1024*1024||_transfers.ContainsKey(id))throw new InvalidDataException("Transfer admission failed.");
                _transfers.Add(id,new(new byte[length],digest));_bytes+=length;return [1];
            }
            if(op==5){GuestExecutionWire.End(reader);Remove(id);return [1];}
            transfer=_transfers.GetValueOrDefault(id)??throw new InvalidDataException("Transfer expired.");
            if(transfer.Executing)throw new InvalidOperationException("Transfer is executing.");
            if(op==2)
            {
                int offset=reader.ReadInt32(),length=reader.ReadInt32();
                if(transfer.Reply||offset!=transfer.Offset||length is <1 or >ChunkedPluginExchange.ChunkBytes||length>transfer.Bytes.Length-offset)throw new InvalidDataException("Invalid chunk order.");
                GuestExecutionWire.Bytes(reader,length).CopyTo(transfer.Bytes,offset);GuestExecutionWire.End(reader);transfer.Offset+=length;return [1];
            }
            if(op==4)
            {
                int offset=reader.ReadInt32(),length=reader.ReadInt32();GuestExecutionWire.End(reader);
                if(!transfer.Reply||offset<0||length is <1 or >ChunkedPluginExchange.ChunkBytes||offset>transfer.Bytes.Length-length)throw new InvalidDataException();
                return transfer.Bytes.AsSpan(offset,length).ToArray();
            }
            GuestExecutionWire.End(reader);
            if(op!=3||transfer.Reply||transfer.Offset!=transfer.Bytes.Length||!CryptographicOperations.FixedTimeEquals(SHA256.HashData(transfer.Bytes),transfer.Digest))throw new InvalidDataException("Invalid completed transfer.");
            transfer.Executing=true;
        }
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellation,transfer.Stop.Token);
        try
        {
            var result=await inner.ExchangeAsync(transfer.Bytes,linked.Token).ConfigureAwait(false);
            lock(_gate)
            {
                linked.Token.ThrowIfCancellationRequested();
                if(_closed||!_transfers.ContainsKey(id)||result.Length>4*1024*1024||_bytes-transfer.Bytes.Length+result.Length>64*1024*1024)throw new InvalidDataException("Reply admission failed.");
                _bytes+=result.Length-transfer.Bytes.Length;CryptographicOperations.ZeroMemory(transfer.Bytes);transfer.Bytes=result;transfer.Reply=true;transfer.Executing=false;
                using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);writer.Write(result.Length);writer.Write(SHA256.HashData(result));return stream.ToArray();
            }
        }
        catch{lock(_gate){transfer.Executing=false;Remove(id);}throw;}
    }
    private void Remove(Guid id)
    {
        if(!_transfers.TryGetValue(id,out var transfer))return;
        transfer.Stop.Cancel();if(transfer.Executing)return;
        _transfers.Remove(id);_bytes-=transfer.Bytes.Length;CryptographicOperations.ZeroMemory(transfer.Bytes);transfer.Stop.Dispose();
    }
    public void Dispose(){lock(_gate){_closed=true;foreach(var id in _transfers.Keys.ToArray())Remove(id);}}
}
