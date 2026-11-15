using System.Text;
using Nexa.Plugin.MarketDelivery;

namespace PluginSidecar;

internal enum MarketTransferVerb : byte { Begin = 1, Append = 2, Finish = 3, Status = 4, Abort = 5 }
internal enum MarketTransferPhase : byte { Receiving = 1, Installing = 2, Installed = 3, Declined = 4, Failed = 5, Aborted = 6 }
internal sealed record MarketTransferRequest(MarketTransferVerb Verb, Guid Operation, string Id = "", string Version = "", string Subject = "", int Offset = 0, byte[]? Chunk = null);
internal sealed record MarketTransferReply(MarketTransferPhase Phase, int Offset, byte[] DevicePublicKey, string Error = "");

/// <summary>Versioned private native service payload; not a guest SDK ABI.</summary>
internal static class PluginMarketWire
{
    internal const string Service = "sidecar.service.plugins.market";
    internal const string Caller = "Nexa.Cloud.Market.Sidecar";
    internal const int ChunkLimit = 48 * 1024, PacketLimit = 25 * 1024 * 1024 + 16 + 8192 + 1024;
    private const uint Magic = 0x314D504E; // NPM1, little-endian
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static MarketTransferRequest Decode(byte[] bytes)
    {
        if (bytes.Length > 60 * 1024) throw new InvalidDataException("Market request exceeds its budget.");
        using var stream = new MemoryStream(bytes, false); using var reader = new BinaryReader(stream, Utf8);
        if (reader.ReadUInt32() != Magic) throw new InvalidDataException("Unknown Market service version.");
        var verb = (MarketTransferVerb)reader.ReadByte(); var operation = new Guid(Read(reader, 16));
        if (!Enum.IsDefined(verb) || operation == Guid.Empty) throw new InvalidDataException("Invalid Market operation.");
        MarketTransferRequest request = verb switch
        {
            MarketTransferVerb.Begin => new(verb, operation, Text(reader, false), Text(reader, false), Text(reader, false)),
            MarketTransferVerb.Append => new(verb, operation, Offset: reader.ReadInt32(), Chunk: Blob(reader, ChunkLimit, false)),
            _ => new(verb, operation)
        };
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing Market request bytes.");
        return request;
    }
    internal static byte[] Encode(MarketTransferRequest request)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Utf8, true);
        writer.Write(Magic); writer.Write((byte)request.Verb); writer.Write(request.Operation.ToByteArray());
        if (request.Verb == MarketTransferVerb.Begin)
        { Text(writer, request.Id); Text(writer, request.Version); Text(writer, request.Subject); }
        else if (request.Verb == MarketTransferVerb.Append)
        { writer.Write(request.Offset); Blob(writer, request.Chunk ?? []); }
        writer.Flush(); return stream.ToArray();
    }
    internal static byte[] Encode(MarketTransferReply reply)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Utf8, true);
        writer.Write(Magic); writer.Write((byte)reply.Phase); writer.Write(reply.Offset);
        Blob(writer, reply.DevicePublicKey); Text(writer, reply.Error); writer.Flush(); return stream.ToArray();
    }
    internal static MarketTransferReply DecodeReply(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false); using var reader = new BinaryReader(stream, Utf8);
        if (bytes.Length > 8192 || reader.ReadUInt32() != Magic) throw new InvalidDataException("Invalid Market reply.");
        var phase = (MarketTransferPhase)reader.ReadByte(); int offset = reader.ReadInt32();
        var reply = new MarketTransferReply(phase, offset, Blob(reader, 4096, true), Text(reader, true));
        if (!Enum.IsDefined(phase) || offset < 0 || offset > PacketLimit || stream.Position != stream.Length)
            throw new InvalidDataException("Invalid Market reply.");
        return reply;
    }
    internal static EncryptedDelivery DecodeDelivery(byte[] bytes)
    {
        if (bytes.Length > PacketLimit) throw new InvalidDataException("Invalid Market delivery.");
        using var stream = new MemoryStream(bytes, false); using var reader = new BinaryReader(stream);
        if (reader.ReadInt32() != 1) throw new InvalidDataException("Invalid Market delivery version.");
        int grant = reader.ReadInt32(), signature = reader.ReadInt32(), cipher = reader.ReadInt32();
        if (grant is < 1 or > 8192 || signature is < 1 or > 1024 || cipher is < 1 or > 25 * 1024 * 1024
            || 16L + grant + signature + cipher != bytes.Length) throw new InvalidDataException("Invalid Market delivery lengths.");
        return new(Read(reader, grant), Read(reader, signature), Read(reader, cipher));
    }
    private static void Text(BinaryWriter writer, string text) => Blob(writer, Utf8.GetBytes(text));
    private static string Text(BinaryReader reader, bool empty) => Utf8.GetString(Blob(reader, 1024, empty));
    private static void Blob(BinaryWriter writer, byte[] bytes) { writer.Write(bytes.Length); writer.Write(bytes); }
    private static byte[] Blob(BinaryReader reader, int maximum, bool empty)
    { int size = reader.ReadInt32(); if (size < (empty ? 0 : 1) || size > maximum) throw new InvalidDataException("Invalid Market field."); return Read(reader, size); }
    private static byte[] Read(BinaryReader reader, int size)
    { byte[] bytes = reader.ReadBytes(size); if (bytes.Length != size) throw new EndOfStreamException(); return bytes; }
}
