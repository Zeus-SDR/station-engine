// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zeus.Contracts;

/// <summary>
/// Operator-configured transverter profiles. Zeus keeps operator, DSP, CAT,
/// TCI, memory, and safety state in external RF Hz and translates to physical
/// IF only at the hardware protocol boundary.
/// </summary>
public sealed record TransverterSettingsDto(
    bool Enabled = false,
    long IfFrequencyHz = 28_000_000,
    long RfFrequencyHz = 144_000_000,
    IReadOnlyList<TransverterBandDto>? Bands = null,
    int? ActiveBandId = null);

/// <summary>
/// One Thetis-compatible transverter band. RF limits are inclusive and the
/// radio IF is <c>RF - LO + error</c>.
/// </summary>
public sealed record TransverterBandDto(
    int Id,
    bool Enabled = false,
    string ButtonText = "",
    long LoOffsetHz = 0,
    long LoErrorHz = 0,
    long BeginFrequencyHz = 0,
    long EndFrequencyHz = 0,
    double RxGainDb = 0,
    bool RxOnly = false,
    int Power = 100,
    bool DisablePa = true,
    TransverterRxAntenna RxAntenna = TransverterRxAntenna.Default);

[JsonConverter(typeof(TransverterRxAntennaJsonConverter))]
public enum TransverterRxAntenna
{
    Default = 0,
    Ant1 = 1,
    Ant2 = 2,
    Ant3 = 3,
}

public sealed class TransverterRxAntennaJsonConverter
    : JsonConverter<TransverterRxAntenna>
{
    public override TransverterRxAntenna Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number
            && reader.TryGetInt32(out int number)
            && Enum.IsDefined((TransverterRxAntenna)number))
            return (TransverterRxAntenna)number;

        if (reader.TokenType == JsonTokenType.String
            && Enum.TryParse<TransverterRxAntenna>(
                reader.GetString(), ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed))
            return parsed;

        throw new JsonException("Invalid transverter RX antenna");
    }

    public override void Write(
        Utf8JsonWriter writer,
        TransverterRxAntenna value,
        JsonSerializerOptions options) =>
        writer.WriteNumberValue((int)value);
}

/// <summary>Replace the persisted transverter conversion settings.</summary>
public sealed record TransverterSettingsSetRequest(
    bool Enabled,
    long IfFrequencyHz,
    long RfFrequencyHz,
    string RadioKey = "default",
    string LayoutId = "default",
    IReadOnlyList<TransverterBandDto>? Bands = null,
    int? ActiveBandId = null);
