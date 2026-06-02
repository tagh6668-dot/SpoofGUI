using System.Buffers.Binary;

namespace SpoofGUI.Engine;

internal static class IpTcp
{
    public const byte FlagFin = 0x01;
    public const byte FlagSyn = 0x02;
    public const byte FlagRst = 0x04;
    public const byte FlagPsh = 0x08;
    public const byte FlagAck = 0x10;

    public static int IpVersion(byte[] p) => p[0] >> 4;
    public static int IpHeaderLen(byte[] p) => (p[0] & 0x0F) * 4;
    public static byte Protocol(byte[] p) => p[9];
    public static int TotalLength(byte[] p) => BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(2, 2));
    public static uint SrcIp(byte[] p) => BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(12, 4));
    public static uint DstIp(byte[] p) => BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(16, 4));

    public static int TcpHeaderLen(byte[] p, int ipHeaderLen) => (p[ipHeaderLen + 12] >> 4) * 4;
    public static ushort SrcPort(byte[] p, int ipHeaderLen) => BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(ipHeaderLen, 2));
    public static ushort DstPort(byte[] p, int ipHeaderLen) => BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(ipHeaderLen + 2, 2));
    public static uint SeqNum(byte[] p, int ipHeaderLen) => BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(ipHeaderLen + 4, 4));
    public static uint AckNum(byte[] p, int ipHeaderLen) => BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(ipHeaderLen + 8, 4));
    public static byte Flags(byte[] p, int ipHeaderLen) => p[ipHeaderLen + 13];

    public static int PayloadLen(byte[] p)
    {
        var ipHeaderLen = IpHeaderLen(p);
        return TotalLength(p) - ipHeaderLen - TcpHeaderLen(p, ipHeaderLen);
    }

    public static bool HasFlag(byte flags, byte flag) => (flags & flag) != 0;

    public static bool IsTcp(byte[] p, uint received) => received >= 20 && IpVersion(p) == 4 && Protocol(p) == 6;

    public static byte[] BuildFakePayloadPacket(byte[] source, byte[] fakePayload, uint seqNum)
    {
        var ipHeaderLen = IpHeaderLen(source);
        var tcpHeaderLen = TcpHeaderLen(source, ipHeaderLen);
        var headerLen = ipHeaderLen + tcpHeaderLen;
        var total = headerLen + fakePayload.Length;

        var packet = new byte[total];
        Array.Copy(source, 0, packet, 0, headerLen);
        Array.Copy(fakePayload, 0, packet, headerLen, fakePayload.Length);

        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), (ushort)total);
        var ident = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4, 2));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), (ushort)((ident + 1) & 0xFFFF));

        packet[ipHeaderLen + 13] |= FlagPsh;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(ipHeaderLen + 4, 4), seqNum);

        return packet;
    }
}
