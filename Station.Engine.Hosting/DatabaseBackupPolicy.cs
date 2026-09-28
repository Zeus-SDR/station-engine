// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Compiled into both Station.Engine.Hosting (which BUILDS the .zeusdb
// container and restores station-owned entries) and ZeusProduct (which
// restores product-owned entries before its stores open). Sharing one source
// keeps the two hosts from drifting on which settings travel, which never
// leave the machine, and where the product's file-backed settings live.

using System.Text.Json;
using System.Text.Json.Nodes;
using LiteDB;
using JsonSerializer = System.Text.Json.JsonSerializer;

#if ZEUS_PRODUCT_HOST
namespace Zeus.Product.Hosting.Data;
#else
namespace Zeus.Server;
#endif

/// <summary>
/// What the operator-facing settings backup (.zeusdb) may carry.
///
/// Secrets policy: passwords, API keys, tokens and the credential vault never
/// leave the machine through a backup. Machine-bound state (LAN opt-in,
/// firewall grants, sidecar runtime bookkeeping, per-install identities) never
/// travels either. Both are stripped when the container is BUILT and again
/// when a backup is STAGED (older backups predate the policy).
///
/// On RESTORE the local machine's own secret is grafted back ONLY when the
/// restored setting still points at the same destination the local secret was
/// entered for (<see cref="SecretRule.Identity"/>: e.g. the DX cluster
/// host/port/callsign or the SMTP host/username). A restored setting that now
/// targets a different server keeps an empty secret, so a secret can never be
/// sent to a server it was not entered for. Rules with no identity guard a
/// fixed third-party service (GodsEye keys) or a per-install value.
/// </summary>
internal static class DatabaseBackupPolicy
{
    /// <summary>One secret-bearing object: <see cref="ParentPath"/> locates
    /// it (empty = the document root), <see cref="Secrets"/> are stripped, and
    /// the local values are grafted back only when every
    /// <see cref="Identity"/> sibling matches between local and restored.</summary>
    internal sealed record SecretRule(string[] ParentPath, string[] Secrets, string[] Identity);

    /// <summary>Collections that stay on this machine: secrets
    /// (credentials, remote_password, user_sessions, diagnostic report broker
    /// tokens) and machine-local state (mobile_lan_access is an explicit
    /// per-machine LAN opt-in and must never be switched on by an
    /// import).</summary>
    internal static readonly IReadOnlyList<string> LocalOnlyCollections =
    [
        "credentials",
        "remote_password",
        "user_sessions",
        "diagnostic_report_tokens",
        "mobile_lan_access",
        "hamclock_sidecar_state",
        "windows_firewall_grant",
    ];

    /// <summary>Secret top-level fields (BSON names) inside otherwise-portable
    /// settings collections.</summary>
    internal static readonly IReadOnlyDictionary<string, SecretRule[]> BsonSecretRules =
        new Dictionary<string, SecretRule[]>(StringComparer.Ordinal)
        {
            // LoginCommands is blanked whole: it routinely carries a
            // "set/password" style login line.
            ["dxcluster_sources"] =
                [new([], ["Password", "LoginCommands"], ["Host", "Port", "Callsign"])],
            ["dxcluster_config"] =
                [new([], ["Password", "LoginCommands"], ["Host", "Port", "Callsign"])],
            ["godseye_feature_settings"] =
                [new([], ["GoogleMapsApiKey", "CesiumIonToken", "TomTomApiKey"], [])],
            ["godseye_layer_settings"] = [new([], ["ApiKey"], [])],
            ["kiwi_settings"] = [new([], ["Password"], ["Url"])],
        };

    /// <summary>Secret JSON properties inside JSON documents, keyed by backup
    /// entry key (or <see cref="JsonInBsonFields"/> document key). Property
    /// names are matched case-insensitively.</summary>
    internal static readonly IReadOnlyDictionary<string, SecretRule[]> JsonSecretRules =
        new Dictionary<string, SecretRule[]>(StringComparer.Ordinal)
        {
            ["voyeur-settings"] =
            [
                new(["alerts.config", "email"], ["password"], ["host", "username"]),
                new(["alerts.config", "ntfy"], ["token", "topic"], ["serverUrl"]),
            ],
            // product-bundle.json and its engine mirror. Swept sub-configs:
            // Kpa500/Lp100a/Lp500/MercuryLux (serial port names), Vk3amp
            // (host/ports), Pat (base URL), Genius (host/port; the access code
            // is memory-only), FreeDV reporter/selection (callsign/grid) carry
            // no secrets. The RF2K VNC password is the one secret; the Genius
            // pairing serial is a per-install identity.
            [ProductBundleKey] =
            [
                new(["rf2k"], ["vncPassword"], ["host"]),
                new([], ["geniusPairingSerial"], []),
            ],
        };

    /// <summary>BSON string fields that hold a whole JSON document governed
    /// by <see cref="JsonSecretRules"/>: collection → (field, rules key).</summary>
    internal static readonly IReadOnlyDictionary<string, (string Field, string Key)> JsonInBsonFields =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["product_bundle_settings"] = ("Json", ProductBundleKey),
        };

    internal const string ProductBundleKey = "product-bundle";
    internal const string ProductBundleFileName = "product-bundle.json";
    internal const string ProductDataDirectoryEnvironmentVariable = "ZEUS_PRODUCT_DATA_DIR";

    internal sealed record ProductSettingsFile(string Key, string Path);

    /// <summary>Test seam: the plugin stores themselves always use the real
    /// platform local-app-data root, which tests must never touch.</summary>
    internal static string? LocalApplicationDataOverrideForTests { get; set; }

    /// <summary>Secret-bearing file entries get owner-only permissions on
    /// restore (Unix), matching how their stores write them.</summary>
    internal static bool IsSecretBearingFile(string key) => JsonSecretRules.ContainsKey(key);

    /// <summary>
    /// ZeusProduct's small file-backed plugin settings documents. Paths are
    /// resolved exactly like the stores resolve them — VoyeurPaths.AppDataRoot
    /// (DoNotVerify, ~/.zeus fallback) and RecorderSettingsStore /
    /// VoiceKeyerSettingsStore.DefaultPluginDataDirectory (plain
    /// LocalApplicationData) — and a test pins them equal. Voice-keyer audio,
    /// recorder WAVs, SSTV images and Voyeur's archive are operator content,
    /// not settings, and are intentionally not included.
    /// </summary>
    internal static IReadOnlyList<ProductSettingsFile> ProductSettingsFiles()
    {
        var voyeurLocal = LocalApplicationDataOverrideForTests ?? Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(voyeurLocal))
            voyeurLocal = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zeus");
        var pluginLocal = LocalApplicationDataOverrideForTests
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var plugins = System.IO.Path.Combine(pluginLocal, "Zeus", "Product", "plugins");
        return
        [
            new("voyeur-settings", System.IO.Path.Combine(voyeurLocal, "Zeus", "voyeur-settings.json")),
            new("recorder-settings", System.IO.Path.Combine(
                plugins, "org.openhpsdr.recorder", "settings.json")),
            new("voicekeyer-settings", System.IO.Path.Combine(
                plugins, "org.openhpsdr.voicekeyer", "settings.json")),
            new("hf-aprs-settings", System.IO.Path.Combine(
                plugins, "org.openhpsdr.hf-aprs", "settings.json")),
            new("sstv-settings", System.IO.Path.Combine(
                plugins, "org.openhpsdr.sstv", "sstv-settings.json")),
        ];
    }

    /// <summary>Removes every local-only collection and strips every secret in
    /// <paramref name="database"/> (a private snapshot/staging copy, never a
    /// live store).</summary>
    internal static void StripLiteDatabase(LiteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        foreach (var name in LocalOnlyCollections)
            database.DropCollection(name);

        var existing = database.GetCollectionNames().ToHashSet(StringComparer.Ordinal);
        foreach (var (collectionName, rules) in BsonSecretRules)
        {
            if (!existing.Contains(collectionName))
                continue;
            var collection = database.GetCollection<BsonDocument>(collectionName);
            foreach (var document in collection.FindAll().ToList())
            {
                var changed = false;
                foreach (var field in rules.SelectMany(rule => rule.Secrets))
                {
                    if (document.TryGetValue(field, out var value)
                        && value.IsString
                        && value.AsString.Length > 0)
                    {
                        document[field] = string.Empty;
                        changed = true;
                    }
                }
                if (changed)
                    collection.Update(document);
            }
        }

        foreach (var (collectionName, (field, key)) in JsonInBsonFields)
        {
            if (!existing.Contains(collectionName))
                continue;
            var collection = database.GetCollection<BsonDocument>(collectionName);
            foreach (var document in collection.FindAll().ToList())
            {
                if (!document.TryGetValue(field, out var value) || !value.IsString)
                    continue;
                var stripped = StripJson(key, System.Text.Encoding.UTF8.GetBytes(value.AsString));
                if (stripped is null)
                {
                    // Unparsable: its secrets cannot be stripped, so it does
                    // not travel at all.
                    collection.Delete(document["_id"]);
                    continue;
                }
                var text = System.Text.Encoding.UTF8.GetString(stripped);
                if (!string.Equals(text, value.AsString, StringComparison.Ordinal))
                {
                    document[field] = text;
                    collection.Update(document);
                }
            }
        }
    }

    /// <summary>
    /// Grafts this machine's own local-only collections and (identity-matched)
    /// secret values from <paramref name="localPath"/> — the authoritative file
    /// about to be replaced — into <paramref name="restoredPath"/>. Must run
    /// while no store holds <paramref name="localPath"/> (restore runs before
    /// the stores open). Best-effort: an unreadable local file leaves the
    /// restored secrets empty — safe, and never blocks recovery from a
    /// corrupt file.
    /// </summary>
    internal static void GraftLocalLiteDatabase(
        string localPath,
        string restoredPath,
        Action<string>? warning = null)
    {
        if (!File.Exists(localPath))
            return;

        Dictionary<string, List<BsonDocument>> localOnly;
        Dictionary<string, Dictionary<BsonValue, BsonDocument>> localDocuments;
        try
        {
            using var local = new LiteDatabase(localPath);
            var names = local.GetCollectionNames().ToHashSet(StringComparer.Ordinal);
            localOnly = LocalOnlyCollections
                .Where(names.Contains)
                .ToDictionary(
                    name => name,
                    name => local.GetCollection<BsonDocument>(name)
                        .FindAll()
                        .Select(static document => new BsonDocument(document))
                        .ToList(),
                    StringComparer.Ordinal);
            localDocuments = BsonSecretRules.Keys
                .Concat(JsonInBsonFields.Keys)
                .Where(names.Contains)
                .ToDictionary(
                    name => name,
                    name => local.GetCollection<BsonDocument>(name)
                        .FindAll()
                        .Where(static document => document.ContainsKey("_id"))
                        .ToDictionary(
                            static document => document["_id"],
                            static document => new BsonDocument(document)),
                    StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            warning?.Invoke(
                $"database.restore could not read local secrets from '{Path.GetFileName(localPath)}'; " +
                $"restored secret fields stay empty: {ex.Message}");
            return;
        }

        if (localOnly.Count == 0 && localDocuments.Count == 0)
            return;

        using var restored = new LiteDatabase(restoredPath);
        foreach (var (name, documents) in localOnly)
        {
            restored.DropCollection(name);
            if (documents.Count > 0)
                restored.GetCollection<BsonDocument>(name).InsertBulk(documents);
        }

        var restoredNames = restored.GetCollectionNames().ToHashSet(StringComparer.Ordinal);
        foreach (var (name, locals) in localDocuments)
        {
            if (!restoredNames.Contains(name))
                continue;
            var collection = restored.GetCollection<BsonDocument>(name);
            foreach (var document in collection.FindAll().ToList())
            {
                if (!document.TryGetValue("_id", out var id)
                    || !locals.TryGetValue(id, out var localDocument))
                    continue;
                var changed = false;
                if (BsonSecretRules.TryGetValue(name, out var rules))
                {
                    foreach (var rule in rules)
                    {
                        if (!rule.Identity.All(field => SameBsonIdentity(
                                document.TryGetValue(field, out var a) ? a : null,
                                localDocument.TryGetValue(field, out var b) ? b : null)))
                            continue;
                        foreach (var field in rule.Secrets)
                        {
                            if (localDocument.TryGetValue(field, out var localValue)
                                && localValue.IsString
                                && localValue.AsString.Length > 0)
                            {
                                document[field] = localValue;
                                changed = true;
                            }
                        }
                    }
                }
                if (JsonInBsonFields.TryGetValue(name, out var jsonField)
                    && document.TryGetValue(jsonField.Field, out var restoredJson)
                    && restoredJson.IsString
                    && localDocument.TryGetValue(jsonField.Field, out var localJson)
                    && localJson.IsString)
                {
                    var grafted = GraftJson(
                        jsonField.Key,
                        System.Text.Encoding.UTF8.GetBytes(restoredJson.AsString),
                        System.Text.Encoding.UTF8.GetBytes(localJson.AsString),
                        compact: true);
                    var text = System.Text.Encoding.UTF8.GetString(grafted);
                    if (!string.Equals(text, restoredJson.AsString, StringComparison.Ordinal))
                    {
                        document[jsonField.Field] = text;
                        changed = true;
                    }
                }
                if (changed)
                    collection.Update(document);
            }
        }
        restored.Checkpoint();
    }

    /// <summary>Returns <paramref name="bytes"/> with this entry's secret JSON
    /// properties removed, or null when the entry has secrets but is not valid
    /// JSON (it is then omitted rather than exported with secrets intact).</summary>
    internal static byte[]? StripJson(string key, byte[] bytes)
    {
        if (!JsonSecretRules.TryGetValue(key, out var rules))
            return bytes;
        JsonNode? root;
        try { root = JsonNode.Parse(bytes); }
        catch (JsonException) { return null; }
        if (root is not JsonObject)
            return null;
        var changed = false;
        foreach (var rule in rules)
        {
            if (Navigate(root, rule.ParentPath) is not JsonObject parent)
                continue;
            foreach (var secret in rule.Secrets)
            {
                if (FindProperty(parent, secret) is { } name)
                {
                    parent.Remove(name);
                    changed = true;
                }
            }
        }
        return changed ? JsonSerializer.SerializeToUtf8Bytes(root, IndentedJson) : bytes;
    }

    /// <summary>Grafts the local file's identity-matched secret JSON
    /// properties into the restored bytes. Best-effort, like
    /// <see cref="GraftLocalLiteDatabase"/>.</summary>
    internal static byte[] GraftLocalJson(string key, byte[] restoredBytes, string localPath)
    {
        if (!JsonSecretRules.ContainsKey(key) || !File.Exists(localPath))
            return restoredBytes;
        byte[] localBytes;
        try { localBytes = File.ReadAllBytes(localPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return restoredBytes;
        }
        return GraftJson(key, restoredBytes, localBytes, compact: false);
    }

    private static byte[] GraftJson(string key, byte[] restoredBytes, byte[] localBytes, bool compact)
    {
        if (!JsonSecretRules.TryGetValue(key, out var rules))
            return restoredBytes;
        JsonNode? local;
        JsonNode? restored;
        try
        {
            local = JsonNode.Parse(localBytes);
            restored = JsonNode.Parse(restoredBytes);
        }
        catch (JsonException)
        {
            return restoredBytes;
        }
        if (local is null || restored is not JsonObject)
            return restoredBytes;

        var changed = false;
        foreach (var rule in rules)
        {
            if (Navigate(local, rule.ParentPath) is not JsonObject localParent
                || Navigate(restored, rule.ParentPath) is not JsonObject restoredParent)
                continue;
            if (!rule.Identity.All(name => SameJsonIdentity(
                    Property(restoredParent, name), Property(localParent, name))))
                continue;
            foreach (var secret in rule.Secrets)
            {
                if (FindProperty(localParent, secret) is not { } localName
                    || localParent[localName] is not JsonValue localValue
                    || !localValue.TryGetValue<string>(out var value)
                    || value.Length == 0)
                    continue;
                if (FindProperty(restoredParent, secret) is { } existing)
                    restoredParent.Remove(existing);
                restoredParent[localName] = value;
                changed = true;
            }
        }
        if (!changed)
            return restoredBytes;
        return JsonSerializer.SerializeToUtf8Bytes(
            restored, compact ? CompactJson : IndentedJson);
    }

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions CompactJson = new();

    private static bool SameBsonIdentity(BsonValue? a, BsonValue? b)
    {
        var aMissing = a is null || a.IsNull;
        var bMissing = b is null || b.IsNull;
        if (aMissing || bMissing)
            return aMissing && bMissing;
        if (a!.IsString && b!.IsString)
            return SameText(a.AsString, b.AsString);
        return a.Equals(b);
    }

    private static bool SameJsonIdentity(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        if (a is JsonValue av && av.TryGetValue<string>(out var aText)
            && b is JsonValue bv && bv.TryGetValue<string>(out var bText))
            return SameText(aText, bText);
        return string.Equals(a.ToJsonString(), b.ToJsonString(), StringComparison.Ordinal);
    }

    private static bool SameText(string a, string b) =>
        string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static JsonNode? Property(JsonObject obj, string name) =>
        FindProperty(obj, name) is { } actual ? obj[actual] : null;

    private static JsonNode? Navigate(JsonNode node, string[] path)
    {
        var current = node;
        foreach (var segment in path)
        {
            if (current is not JsonObject obj || FindProperty(obj, segment) is not { } name)
                return null;
            current = obj[name];
        }
        return current;
    }

    private static string? FindProperty(JsonObject obj, string name)
    {
        foreach (var (candidate, _) in obj)
        {
            if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        return null;
    }
}
