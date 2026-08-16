// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Numerics;

namespace Zeus.Server.Tdoa;

internal sealed record ValidatedTdoaCapture(
    string Id,
    double LatitudeDeg,
    double LongitudeDeg,
    double AltitudeMeters,
    long ReferenceTimeTaiNanoseconds,
    double SampleRateHz,
    double GroupDelayNanoseconds,
    double ClockUncertaintyNanoseconds,
    double SampleRateCorrectionPpm,
    double ResamplingUncertaintyNanoseconds,
    Complex[] Samples);

internal static class TdoaCaptureValidator
{
    public static IReadOnlyList<ValidatedTdoaCapture> Validate(TdoaSolveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!double.IsFinite(request.CenterFrequencyHz) || request.CenterFrequencyHz <= 0)
            throw new TdoaValidationException("centerFrequencyHz must be a finite positive number.");
        if (!string.Equals(request.PropagationModel, "groundwave", StringComparison.Ordinal))
            throw new TdoaValidationException("propagationModel must be 'groundwave'; skywave timing is not supported.");
        if (request.Stations is not { Count: >= TdoaLimits.MinStations and <= TdoaLimits.MaxStations })
            throw new TdoaValidationException($"stations must contain {TdoaLimits.MinStations} to {TdoaLimits.MaxStations} captures.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ValidatedTdoaCapture>(request.Stations.Count);
        int totalSamples = 0;

        foreach (var station in request.Stations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string id = station.Id?.Trim() ?? string.Empty;
            if (id.Length is < 1 or > 64 || !ids.Add(id))
                throw new TdoaValidationException("Every station id must be unique and contain 1 to 64 characters.");
            if (!double.IsFinite(station.LatitudeDeg) || station.LatitudeDeg is < -90 or > 90
                || !double.IsFinite(station.LongitudeDeg) || station.LongitudeDeg is < -180 or > 180)
                throw new TdoaValidationException($"Station '{id}' has invalid WGS84 coordinates.");
            if (!double.IsFinite(station.AltitudeMeters) || station.AltitudeMeters is < -500 or > 20_000)
                throw new TdoaValidationException($"Station '{id}' altitudeMeters is outside [-500, 20000].");
            if (!station.ClockLocked)
                throw new TdoaValidationException($"Station '{id}' clock is unlocked. Host/network arrival timestamps are not accepted.");
            if (!long.TryParse(station.ReferenceTimeTaiNanoseconds, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out long taiNs) || taiNs <= 0)
                throw new TdoaValidationException($"Station '{id}' referenceTimeTaiNanoseconds must be a positive GNSS/TAI sample-epoch integer string.");
            if (!double.IsFinite(station.SampleRateHz) || station.SampleRateHz is < 8_000 or > 10_000_000)
                throw new TdoaValidationException($"Station '{id}' sampleRateHz is outside [8000, 10000000].");
            if (!double.IsFinite(station.GroupDelayNanoseconds) || Math.Abs(station.GroupDelayNanoseconds) > 1_000_000_000)
                throw new TdoaValidationException($"Station '{id}' groupDelayNanoseconds is invalid.");
            if (!double.IsFinite(station.ClockUncertaintyNanoseconds) || station.ClockUncertaintyNanoseconds is < 0 or > 1_000_000)
                throw new TdoaValidationException($"Station '{id}' clockUncertaintyNanoseconds must be in [0, 1000000].");

            byte[] bytes;
            try { bytes = Convert.FromBase64String(station.IqBase64 ?? string.Empty); }
            catch (FormatException) { throw new TdoaValidationException($"Station '{id}' iqBase64 is not valid base64."); }
            if (bytes.Length % 8 != 0)
                throw new TdoaValidationException($"Station '{id}' IQ payload must contain complex-float32 little-endian pairs.");
            int sampleCount = bytes.Length / 8;
            if (sampleCount is < TdoaLimits.MinComplexSamplesPerStation or > TdoaLimits.MaxComplexSamplesPerStation)
                throw new TdoaValidationException($"Station '{id}' must contain {TdoaLimits.MinComplexSamplesPerStation} to {TdoaLimits.MaxComplexSamplesPerStation} complex samples.");
            totalSamples = checked(totalSamples + sampleCount);
            if (totalSamples > TdoaLimits.MaxTotalComplexSamples)
                throw new TdoaValidationException($"Total IQ is limited to {TdoaLimits.MaxTotalComplexSamples} complex samples.");

            var samples = new Complex[sampleCount];
            for (int i = 0, offset = 0; i < samples.Length; i++, offset += 8)
            {
                if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                float re = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4)));
                float im = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4, 4)));
                if (!float.IsFinite(re) || !float.IsFinite(im))
                    throw new TdoaValidationException($"Station '{id}' IQ payload contains a non-finite sample.");
                samples[i] = new Complex(re, im);
            }

            result.Add(new ValidatedTdoaCapture(id, station.LatitudeDeg, station.LongitudeDeg,
                station.AltitudeMeters, taiNs, station.SampleRateHz, station.GroupDelayNanoseconds,
                station.ClockUncertaintyNanoseconds, 0, 0, samples));
        }

        return TdoaSampleRateNormalizer.ToCommonGrid(result, cancellationToken);
    }
}
