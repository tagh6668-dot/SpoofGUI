using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SpoofGUI.Engine;

internal static class TlsClientHello
{
    private const string TemplateHex =
        "1603010200010001fc030341d5b549d9cd1adfa7296c8418d157dc7b624c842824ff493b9375bb48d34f2b20bf018bcc90a7c89a230094815ad0c15b736e38c01209d72d282cb5e2105328150024130213031301c02cc030c02bc02fcca9cca8c024c028c023c027009f009e006b006700ff0100018f0000000b00090000066d63692e6972000b000403000102000a00160014001d0017001e0019001801000101010201030104002300000010000e000c02683208687474702f312e310016000000170000000d002a0028040305030603080708080809080a080b080408050806040105010601030303010302040205020602002b00050403040303002d00020101003300260024001d0020435bacc4d05f9d41fef44ab3ad55616c36e0613473e2338770efdaa98693d217001500d5000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";

    private static readonly byte[] Template = Convert.FromHexString(TemplateHex);
    private const int TemplateSniLen = 6;

    private static byte[] Slice(byte[] arr, int start, int end)
    {
        var len = end - start;
        var res = new byte[len];
        Array.Copy(arr, start, res, 0, len);
        return res;
    }

    private static readonly byte[] Static1 = Slice(Template, 0, 11);
    private static readonly byte[] Static2 = [0x20];
    private static readonly byte[] Static3 = Slice(Template, 76, 120);
    private static readonly byte[] Static4 = Slice(Template, 127 + TemplateSniLen, 262 + TemplateSniLen);
    private static readonly byte[] Static5 = [0x00, 0x15];

    public static byte[] Build(byte[] fakeSni)
    {
        var rnd = RandomNumberGenerator.GetBytes(32);
        var sessionId = RandomNumberGenerator.GetBytes(32);
        var keyShare = RandomNumberGenerator.GetBytes(32);

        var sniExt = new byte[5 + fakeSni.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(sniExt.AsSpan(0, 2), (ushort)(fakeSni.Length + 5));
        BinaryPrimitives.WriteUInt16BigEndian(sniExt.AsSpan(2, 2), (ushort)(fakeSni.Length + 3));
        sniExt[4] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(sniExt.AsSpan(5, 2), (ushort)fakeSni.Length);
        Array.Copy(fakeSni, 0, sniExt, 7, fakeSni.Length);

        var padLen = 219 - fakeSni.Length;
        var padExt = new byte[2 + padLen];
        BinaryPrimitives.WriteUInt16BigEndian(padExt.AsSpan(0, 2), (ushort)padLen);

        return [
            .. Static1, .. rnd, .. Static2, .. sessionId, .. Static3,
            .. sniExt, .. Static4, .. keyShare, .. Static5, .. padExt,
        ];
    }
}
