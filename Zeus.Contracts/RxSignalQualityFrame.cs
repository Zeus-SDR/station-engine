// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;
using System.Buffers.Binary;

namespace Zeus.Contracts;

/// <summary>
/// Receive signal-quality estimate derived from average-power PSD in
/// the active RX passband. Absolute power fields are calibrated dBm; SNR is a
/// power ratio and must never have board calibration applied to it.
/// </summary>
public readonly record struct RxSignalQualityFrame(
    byte RxId,
    float SnrDb,
    float SignalOnlyDbm,
    float IntegratedNoiseDbm,
    float Confidence)
{
    public const int ByteLength = 2 + 4 * 4;

    public static RxSignalQualityFrame Unavailable(byte rxId) => new(
        rxId, float.NaN, float.NaN, float.NaN, 0f);

    public bool IsValid =>
        float.IsFinite(SnrDb) &&
        float.IsFinite(SignalOnlyDbm) &&
        float.IsFinite(IntegratedNoiseDbm) &&
        float.IsFinite(Confidence) && Confidence > 0f;

    public void Serialize(IBufferWriter<byte> writer)
    {
        var span = writer.GetSpan(ByteLength);
        span[0] = (byte)MsgType.RxSignalQuality;
        span[1] = RxId;
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(2, 4), SnrDb);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(6, 4), SignalOnlyDbm);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(10, 4), IntegratedNoiseDbm);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(14, 4), Confidence);
        writer.Advance(ByteLength);
    }

    public static RxSignalQualityFrame Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < ByteLength)
            throw new InvalidDataException($"RxSignalQualityFrame requires {ByteLength} bytes, got {bytes.Length}");
        if (bytes[0] != (byte)MsgType.RxSignalQuality)
            throw new InvalidDataException($"expected RxSignalQuality (0x{(byte)MsgType.RxSignalQuality:X2}), got 0x{bytes[0]:X2}");
        return new RxSignalQualityFrame(
            RxId: bytes[1],
            SnrDb: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(2, 4)),
            SignalOnlyDbm: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(6, 4)),
            IntegratedNoiseDbm: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(10, 4)),
            Confidence: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(14, 4)));
    }
}
