// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.
using System.Text.RegularExpressions;
using LiteDB;
#if ZEUS_PRODUCT_HOST
namespace Zeus.Product.Hosting.Aprs;
#else
namespace Zeus.Server.Aprs;
#endif

public sealed record AprsSettings(bool Enabled = false, string Callsign = "", int MaxAgeMinutes = 30);
public sealed class AprsSettingsStore(LiteDatabase database)
{
    private readonly ILiteCollection<BsonDocument> _collection = database.GetCollection("hud_aprs_settings");
    private readonly object _gate = new();
    public AprsSettings Get()
    {
        lock (_gate)
        {
            var doc = _collection.FindById(1);
            return doc == null ? new() : new(doc["enabled"].AsBoolean, doc["callsign"].AsString, doc["age"].AsInt32);
        }
    }
    public AprsSettings Set(AprsSettings settings)
    {
        var call = (settings.Callsign ?? "").Trim().ToUpperInvariant();
        if ((settings.Enabled || call.Length > 0) && !ValidCallsign(call)) throw new ArgumentException("Enter your callsign, optionally with an SSID (maximum nine characters).");
        if (settings.MaxAgeMinutes is < 5 or > 120) throw new ArgumentException("Position retention must be 5–120 minutes.");
        var next = settings with { Callsign = call };
        lock (_gate) _collection.Upsert(new BsonDocument { ["_id"] = 1, ["enabled"] = next.Enabled, ["callsign"] = call, ["age"] = next.MaxAgeMinutes });
        return next;
    }
    public static bool ValidCallsign(string value) => value.Length <= 9 && Regex.IsMatch(value, "^[A-Z0-9]{3,6}(-[A-Z0-9]{1,2})?$", RegexOptions.CultureInvariant);
}

