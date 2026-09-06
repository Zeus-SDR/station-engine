// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Globalization;
using System.Text.Json;

#if ZEUS_PRODUCT_HOST
namespace Zeus.Product.Hosting.GodsEye;
#else
namespace Zeus.Server.GodsEye;
#endif

public static class GodsEyeParsers
{
    private const int MilitaryFlightsMaxItems = 2_000;
    public static IReadOnlyList<GodsEyeItemDto> ParseEarthquakes(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = new List<GodsEyeItemDto>();
        foreach (var feature in document.RootElement.GetProperty("features").EnumerateArray())
        {
            var properties = feature.GetProperty("properties");
            var coordinates = feature.GetProperty("geometry").GetProperty("coordinates");
            if (!TryCoordinate(coordinates[1], coordinates[0], out var lat, out var lon)) continue;
            var milliseconds = properties.TryGetProperty("time", out var time) && time.TryGetInt64(out var value) ? value : 0;
            result.Add(new GodsEyeItemDto(
                feature.TryGetProperty("id", out var id) ? id.GetString() ?? $"quake-{result.Count}" : $"quake-{result.Count}",
                properties.TryGetProperty("place", out var place) ? place.GetString() ?? "Earthquake" : "Earthquake",
                lat, lon, DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, milliseconds)),
                properties.TryGetProperty("mag", out var magnitude) && magnitude.TryGetDouble(out var mag) ? mag : null));
        }
        return result;
    }

    public static IReadOnlyList<GodsEyeItemDto> ParseLaunches(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = new List<GodsEyeItemDto>();
        foreach (var launch in document.RootElement.GetProperty("results").EnumerateArray())
        {
            if (!launch.TryGetProperty("pad", out var pad) || pad.ValueKind != JsonValueKind.Object
                || !TryNumber(pad, "latitude", out var lat) || !TryNumber(pad, "longitude", out var lon)
                || !TryCoordinate(lat, lon, out lat, out lon)) continue;
            var net = launch.TryGetProperty("net", out var netElement)
                && DateTimeOffset.TryParse(netElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                    ? parsed : DateTimeOffset.UnixEpoch;
            var status = launch.TryGetProperty("status", out var statusElement)
                && statusElement.ValueKind == JsonValueKind.Object && statusElement.TryGetProperty("name", out var statusName)
                    ? statusName.GetString() : null;
            result.Add(new GodsEyeItemDto(
                launch.GetProperty("id").GetString() ?? $"launch-{result.Count}",
                launch.GetProperty("name").GetString() ?? "Launch",
                lat, lon, net,
                Status: status,
                Site: pad.TryGetProperty("name", out var site) ? site.GetString() : null));
        }
        return result;
    }

    public static IReadOnlyList<GodsEyeItemDto> ParseAircraft(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = new List<GodsEyeItemDto>();
        if (!document.RootElement.TryGetProperty("states", out var states) || states.ValueKind != JsonValueKind.Array) return result;
        foreach (var row in states.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 11
                || !TryNullableDouble(row[5], out var lon) || !TryNullableDouble(row[6], out var lat)
                || lon is null || lat is null || !TryCoordinate(lat.Value, lon.Value, out var latitude, out var longitude)) continue;
            var icao = row[0].GetString()?.Trim() ?? $"aircraft-{result.Count}";
            var callsign = row[1].ValueKind == JsonValueKind.String ? row[1].GetString()?.Trim() : null;
            var timestamp = row[3].ValueKind == JsonValueKind.Number && row[3].TryGetInt64(out var seconds)
                ? SafeUnixSeconds(seconds) : DateTimeOffset.UnixEpoch;
            TryNullableDouble(row[7], out var altitude);
            TryNullableDouble(row[9], out var velocityMs);
            TryNullableDouble(row[10], out var heading);
            result.Add(new GodsEyeItemDto(icao, string.IsNullOrWhiteSpace(callsign) ? icao.ToUpperInvariant() : callsign,
                latitude, longitude, timestamp, HeadingDeg: heading is { } finiteHeading && double.IsFinite(finiteHeading) ? finiteHeading : null,
                SpeedKnots: velocityMs is { } finiteVelocity && double.IsFinite(finiteVelocity) ? finiteVelocity * 1.9438444924406 : null,
                AltitudeM: altitude is { } finiteAltitude && double.IsFinite(finiteAltitude) ? finiteAltitude : null,
                Callsign: callsign));
        }
        return result;
    }

    public static IReadOnlyList<GodsEyeItemDto> ParseFires(string csv)
    {
        var lines = csv.Replace("\r", "", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || string.Equals(lines[0].Trim(), "Invalid MAP_KEY.", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("NASA FIRMS rejected the map key.");
        var headers = SplitCsv(lines[0]);
        var columns = headers.Select((value, index) => (value, index)).ToDictionary(x => x.value.Trim(), x => x.index, StringComparer.OrdinalIgnoreCase);
        if (!columns.ContainsKey("latitude") || !columns.ContainsKey("longitude")
            || !columns.ContainsKey("acq_date") || !columns.ContainsKey("acq_time"))
            throw new InvalidDataException("NASA FIRMS returned a non-CSV response.");
        if (lines.Length < 2) return [];
        var result = new List<GodsEyeItemDto>();
        for (var i = 1; i < lines.Length; i++)
        {
            var row = SplitCsv(lines[i]);
            if (!CellDouble(row, columns, "latitude", out var lat) || !CellDouble(row, columns, "longitude", out var lon)
                || !TryCoordinate(lat, lon, out lat, out lon)) continue;
            var date = Cell(row, columns, "acq_date");
            var time = Cell(row, columns, "acq_time").PadLeft(4, '0');
            if (!DateTimeOffset.TryParseExact($"{date} {time}", "yyyy-MM-dd HHmm", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp)) continue;
            CellDouble(row, columns, "frp", out var frp);
            result.Add(new GodsEyeItemDto($"fire-{date}-{time}-{lat:F5}-{lon:F5}", "Active fire", lat, lon, timestamp,
                Frp: frp, Confidence: Cell(row, columns, "confidence")));
        }
        return result;
    }

    public static GodsEyeItemDto? ParseAisPosition(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("Message", out var message) || !message.TryGetProperty("PositionReport", out var report)
            || !TryNumber(report, "Latitude", out var lat) || !TryNumber(report, "Longitude", out var lon)
            || !TryCoordinate(lat, lon, out lat, out lon)) return null;
        var meta = root.TryGetProperty("MetaData", out var metadata) ? metadata : default;
        var mmsi = meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("MMSI", out var mmsiElement)
            ? FormatMmsi(mmsiElement) : FormattableString.Invariant($"vessel-{lat}-{lon}");
        var name = meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("ShipName", out var shipName) ? shipName.GetString()?.Trim() : null;
        var callsign = meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("CallSign", out var callSign) ? callSign.GetString()?.Trim() : null;
        var hasHeading = TryNumber(report, "TrueHeading", out var heading);
        var hasSpeed = TryNumber(report, "Sog", out var speed);
        var timestamp = meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("time_utc", out var time)
            && DateTimeOffset.TryParse(time.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : DateTimeOffset.UtcNow;
        return new GodsEyeItemDto(mmsi, string.IsNullOrWhiteSpace(name) ? $"MMSI {mmsi}" : name, lat, lon, timestamp,
            HeadingDeg: hasHeading && heading is >= 0 and <= 360 ? heading : null,
            SpeedKnots: hasSpeed && speed >= 0 ? speed : null,
            Callsign: callsign);
    }

    public static IReadOnlyList<GodsEyeItemDto> ParseMilitaryFlights(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return [];
        if (!root.TryGetProperty("ac", out var aircraft) || aircraft.ValueKind != JsonValueKind.Array) return [];
        var reportedTime = root.TryGetProperty("now", out var now) && now.TryGetDouble(out var value) ? value : 0;
        // readsb-compatible endpoints use milliseconds; older snapshots used seconds.
        var timestamp = SafeUnixSeconds(reportedTime > 100_000_000_000 ? reportedTime / 1_000 : reportedTime);
        var result = new List<GodsEyeItemDto>();
        foreach (var item in aircraft.EnumerateArray())
        {
            if (result.Count >= MilitaryFlightsMaxItems) break;
            if (!TryNumber(item, "lat", out var lat) || !TryNumber(item, "lon", out var lon)
                || !TryCoordinate(lat, lon, out lat, out lon)) continue;
            var hex = Text(item, "hex")?.TrimStart('~') ?? $"mil-{result.Count}";
            var callsign = Text(item, "flight")?.Trim();
            var name = string.IsNullOrWhiteSpace(callsign) ? hex.ToUpperInvariant() : callsign;
            var altitude = TryNumber(item, "alt_baro", out var altitudeFeet) ? altitudeFeet * 0.3048 : (double?)null;
            var speed = TryNumber(item, "gs", out var groundSpeed) ? groundSpeed : (double?)null;
            var heading = TryNumber(item, "track", out var track) ? track : (double?)null;
            var positionTime = TryNumber(item, "seen_pos", out var ageSeconds) && ageSeconds >= 0
                ? timestamp - TimeSpan.FromSeconds(Math.Min(ageSeconds, (timestamp - DateTimeOffset.UnixEpoch).TotalSeconds))
                : timestamp;
            result.Add(new GodsEyeItemDto(hex, name, lat, lon, positionTime,
                HeadingDeg: heading, SpeedKnots: speed, AltitudeM: altitude,
                Status: Text(item, "t"), Callsign: callsign));
        }
        return result;
    }

    public static IReadOnlyList<GodsEyeItemDto> ParseRadioStations(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
        var rows = document.RootElement.EnumerateArray();
        var result = new List<GodsEyeItemDto>();
        foreach (var item in rows)
        {
            if (!TryNumber(item, "geo_lat", out var lat) || !TryNumber(item, "geo_long", out var lon)
                || !TryCoordinate(lat, lon, out lat, out lon)) continue;
            var id = Text(item, "stationuuid") ?? $"radio-{result.Count}";
            result.Add(new GodsEyeItemDto(id, Text(item, "name") ?? "Radio station", lat, lon,
                DateTimeOffset.UnixEpoch, Status: Text(item, "tags"), Site: Text(item, "country"),
                Callsign: Text(item, "url_resolved") ?? Text(item, "url")));
        }
        return result;
    }

    public static IReadOnlyList<GodsEyeItemDto> ParseBikeshare(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return [];
        var result = new List<GodsEyeItemDto>();
        if (root.TryGetProperty("networks", out var networks) && networks.ValueKind == JsonValueKind.Array)
        {
            foreach (var network in networks.EnumerateArray())
            {
                if (!network.TryGetProperty("location", out var location)
                    || !TryNumber(location, "latitude", out var lat) || !TryNumber(location, "longitude", out var lon)
                    || !TryCoordinate(lat, lon, out lat, out lon)) continue;
                var id = Text(network, "id") ?? $"bikeshare-{result.Count}";
                result.Add(new GodsEyeItemDto(id, Text(network, "name") ?? "Bikeshare network", lat, lon,
                    DateTimeOffset.UnixEpoch, Status: "Bikeshare network", Site: Text(location, "city")));
            }
            return result;
        }
        if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("stations", out var stations)
            || stations.ValueKind != JsonValueKind.Array) return result;
        foreach (var station in stations.EnumerateArray())
        {
            if (!TryNumber(station, "lat", out var lat) || !TryNumber(station, "lon", out var lon)
                || !TryCoordinate(lat, lon, out lat, out lon)) continue;
            var bikes = TryNumber(station, "num_bikes_available", out var available) ? (int)available : 0;
            var docks = TryNumber(station, "num_docks_available", out var empty) ? (int)empty : 0;
            result.Add(new GodsEyeItemDto(Text(station, "station_id") ?? $"station-{result.Count}",
                Text(station, "name") ?? "Bikeshare station", lat, lon, DateTimeOffset.UnixEpoch,
                Status: $"{bikes} bikes / {docks} docks"));
        }
        return result;
    }

    public static IReadOnlyList<GodsEyeItemDto> ParseTraffic(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return [];
        if (!root.TryGetProperty("flowSegmentData", out var flow)) return [];
        var coordinates = flow.TryGetProperty("coordinates", out var geometry)
            && geometry.TryGetProperty("coordinate", out var points) && points.ValueKind == JsonValueKind.Array
                ? points : default;
        if (coordinates.ValueKind != JsonValueKind.Array) return [];
        var point = coordinates.EnumerateArray().FirstOrDefault();
        if (point.ValueKind != JsonValueKind.Object || !TryNumber(point, "latitude", out var lat)
            || !TryNumber(point, "longitude", out var lon) || !TryCoordinate(lat, lon, out lat, out lon)) return [];
        TryNumber(flow, "currentSpeed", out var speed);
        TryNumber(flow, "freeFlowSpeed", out var freeFlow);
        var ratio = freeFlow > 0 ? speed / freeFlow : 0;
        return [new GodsEyeItemDto("tomtom-flow", "Live traffic flow", lat, lon, DateTimeOffset.UnixEpoch,
            SpeedKnots: speed * 0.539956803, Status: $"{Math.Round(ratio * 100)}% of free-flow speed")];
    }

    public static IReadOnlyList<GodsEyeItemDto> ParseMappedInstallations(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return [];
        if (!document.RootElement.TryGetProperty("elements", out var elements) || elements.ValueKind != JsonValueKind.Array) return [];
        var result = new List<GodsEyeItemDto>();
        foreach (var element in elements.EnumerateArray())
        {
            var coordinate = element.TryGetProperty("center", out var center) ? center : element;
            if (!TryNumber(coordinate, "lat", out var lat) || !TryNumber(coordinate, "lon", out var lon)
                || !TryCoordinate(lat, lon, out lat, out lon)) continue;
            var tags = element.TryGetProperty("tags", out var tagObject) ? tagObject : default;
            var id = $"osm-{Text(element, "type") ?? "feature"}-{(element.TryGetProperty("id", out var idValue) ? idValue.GetRawText() : result.Count.ToString(CultureInfo.InvariantCulture))}";
            result.Add(new GodsEyeItemDto(id,
                tags.ValueKind == JsonValueKind.Object ? Text(tags, "name") ?? "Mapped installation" : "Mapped installation",
                lat, lon, DateTimeOffset.UnixEpoch,
                Status: tags.ValueKind == JsonValueKind.Object ? Text(tags, "military") ?? Text(tags, "landuse") : null,
                Site: "OpenStreetMap mapped context"));
        }
        return result;
    }

    private static bool TryCoordinate(JsonElement lat, JsonElement lon, out double latitude, out double longitude)
    {
        latitude = longitude = 0;
        return TryElementDouble(lat, out latitude) && TryElementDouble(lon, out longitude) && TryCoordinate(latitude, longitude, out latitude, out longitude);
    }

    private static DateTimeOffset SafeUnixSeconds(double value)
    {
        if (!double.IsFinite(value) || value <= 0) return DateTimeOffset.UnixEpoch;
        var milliseconds = value * 1_000;
        var maximum = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();
        if (!double.IsFinite(milliseconds) || milliseconds >= maximum) return DateTimeOffset.MaxValue;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds);
    }

    private static bool TryCoordinate(double lat, double lon, out double latitude, out double longitude)
    {
        latitude = lat; longitude = lon;
        return double.IsFinite(lat) && double.IsFinite(lon) && lat is >= -90 and <= 90 && lon is >= -180 and <= 180;
    }

    private static bool TryNumber(JsonElement parent, string name, out double value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var element) && TryElementDouble(element, out value);
    }

    private static string? Text(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool TryElementDouble(JsonElement element, out double value)
    {
        if (element.ValueKind == JsonValueKind.Number) return element.TryGetDouble(out value);
        return double.TryParse(element.ValueKind == JsonValueKind.String ? element.GetString() : null, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryNullableDouble(JsonElement element, out double? value)
    {
        value = null;
        if (element.ValueKind == JsonValueKind.Null || !TryElementDouble(element, out var parsed)) return false;
        value = parsed; return true;
    }

    private static string FormatMmsi(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var integral))
            return integral.ToString(CultureInfo.InvariantCulture);
        return element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.GetRawText();
    }

    private static string[] SplitCsv(string line) => line.Split(',');
    private static string Cell(string[] row, IReadOnlyDictionary<string, int> columns, string name) => columns.TryGetValue(name, out var index) && index < row.Length ? row[index].Trim().Trim('"') : "";
    private static bool CellDouble(string[] row, IReadOnlyDictionary<string, int> columns, string name, out double value) => double.TryParse(Cell(row, columns, name), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
