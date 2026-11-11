using System.Collections.Immutable;
using Nexa.Plugin.Bootstrap.V1;
using Nexa.Plugin.Sdk.Abi100.Data;
namespace PluginSidecar.WasmRuntime;

internal static class GuestExecutionWire
{
    internal const byte Create=1,Start=2,Resume=3,Dispose=4;
    internal static byte[] Request(byte operation,Guid guest,Action<BinaryWriter>? write=null)
    {using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);writer.Write(operation);writer.Write(guest.ToByteArray());write?.Invoke(writer);return stream.ToArray();}
    internal static Guid Guid(BinaryReader reader)=>new(Bytes(reader,16));
    internal static byte[] Bytes(BinaryReader reader,int length)
    {if(length<0||length>26*1024*1024)throw new InvalidDataException();var bytes=reader.ReadBytes(length);if(bytes.Length!=length)throw new EndOfStreamException();return bytes;}
    internal static void End(BinaryReader reader){if(reader.BaseStream.Position!=reader.BaseStream.Length)throw new InvalidDataException();}
    internal static AbiValue Call(WasmHostCall call)=>DataCodec.Map(("kind",DataCodec.WriteString("invoke")),("contract",DataCodec.WriteString(call.Contract)),
        ("method",DataCodec.WriteUInt64(call.Method)),("arguments",call.Arguments),("operationId",DataCodec.WriteGuid(call.OperationId)),
        ("key",Optional(call.IdempotencyKey)),("revision",Optional(call.ExpectedRevision)),
        ("experiment",call.ExperimentId is {} id?DataCodec.WriteGuid(id):new AbiValue.Null()),
        ("budget",call.Budget is {} budget?DataCodec.WriteInt64((long)budget.TotalMilliseconds):new AbiValue.Null()));
    internal static WasmHostCall ReadCall(ImmutableDictionary<string,AbiValue> fields)=>new(
        DataCodec.ReadString(DataCodec.Field(fields,"contract")),checked((uint)DataCodec.ReadUInt64(DataCodec.Field(fields,"method"))),DataCodec.Field(fields,"arguments"),
        DataCodec.ReadGuid(DataCodec.Field(fields,"operationId")),Text(DataCodec.Field(fields,"key")),Text(DataCodec.Field(fields,"revision")),
        DataCodec.Field(fields,"experiment") is AbiValue.Null?null:DataCodec.ReadGuid(fields["experiment"]),
        DataCodec.Field(fields,"budget") is AbiValue.Null?null:TimeSpan.FromMilliseconds(DataCodec.ReadInt64(fields["budget"])));
    internal static byte[] Reply(AbiReply<AbiValue> reply)=>AbiValueBinary.Encode(DataCodec.Map(("success",DataCodec.WriteBoolean(reply.Success)),
        ("value",reply.Success?reply.Value:new AbiValue.Null()),("error",reply.Success?new AbiValue.Null():WasmPluginMachine.EncodeError(reply.Error!))));
    internal static AbiReply<AbiValue> ReadReply(ReadOnlySpan<byte> bytes)
    {
        var map=DataCodec.ReadMap(AbiValueBinary.Decode(bytes));
        if(DataCodec.ReadBoolean(DataCodec.Field(map,"success")))return AbiReply<AbiValue>.Ok(DataCodec.Field(map,"value"));
        var error=DataCodec.ReadMap(DataCodec.Field(map,"error"));string S(string key)=>DataCodec.ReadString(DataCodec.Field(error,key));
        ImmutableDictionary<string,string> Strings(string key)=>DataCodec.ReadMap(DataCodec.Field(error,key)).ToImmutableDictionary(p=>p.Key,p=>DataCodec.ReadString(p.Value));
        return AbiReply<AbiValue>.Fail(new(S("code"),S("message"),S("stage"),S("operation"),S("pluginId"),DataCodec.ReadGuid(error["loadInstanceId"]),DataCodec.ReadGuid(error["correlationId"]),DataCodec.ReadBoolean(error["retryable"]),Strings("details"))
            {MessageKey=S("messageKey"),OperationId=DataCodec.ReadGuid(error["operationId"]),Parameters=Strings("parameters")});
    }
    private static AbiValue Optional(string? value)=>value is null?new AbiValue.Null():DataCodec.WriteString(value);
    private static string? Text(AbiValue value)=>value is AbiValue.Null?null:DataCodec.ReadString(value);
}
