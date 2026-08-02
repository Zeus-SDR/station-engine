// SPDX-License-Identifier: GPL-3.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server.SpeTaurus;

internal sealed class SpeTaurusConfigStore : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly ILiteCollection<SpeTaurusConfigEntry> _entries;
    private readonly object _sync = new();

    internal SpeTaurusConfigStore(string? dbPathOverride = null)
    {
        var dbPath = dbPathOverride ?? PrefsDbPath.EngineGet();
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);
        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _entries = _dbLease.Database.GetCollection<SpeTaurusConfigEntry>("spe_taurus_config");
    }

    internal SpeTaurusConfig? Get()
    {
        lock (_sync)
        {
            var entry = _entries.FindAll().FirstOrDefault();
            if (entry is null) return null;
            return new(
                Enabled: entry.Enabled,
                Transport: entry.Transport,
                PortName: entry.PortName,
                BaudRate: entry.BaudRate,
                BridgeHost: entry.BridgeHost,
                BridgePort: entry.BridgePort,
                AutoReconnect: entry.AutoReconnect,
                ActivePollingMs: entry.ActivePollingMs,
                IdlePollingMs: entry.IdlePollingMs,
                ResponseTimeoutMs: entry.ResponseTimeoutMs,
                ConnectTimeoutMs: entry.ConnectTimeoutMs,
                D2xxSerial: entry.D2xxSerial,
                ExpertServerUrl: entry.ExpertServerUrl,
                TuneArmTimeoutMs: entry.TuneArmTimeoutMs);
        }
    }

    internal void Set(SpeTaurusConfig config)
    {
        lock (_sync)
        {
            var entry = _entries.FindAll().FirstOrDefault() ?? new SpeTaurusConfigEntry();
            entry.Enabled = config.Enabled;
            entry.Transport = config.Transport;
            entry.PortName = config.PortName;
            entry.BaudRate = config.BaudRate;
            entry.BridgeHost = config.BridgeHost;
            entry.BridgePort = config.BridgePort;
            entry.AutoReconnect = config.AutoReconnect;
            entry.ActivePollingMs = config.ActivePollingMs;
            entry.IdlePollingMs = config.IdlePollingMs;
            entry.ResponseTimeoutMs = config.ResponseTimeoutMs;
            entry.ConnectTimeoutMs = config.ConnectTimeoutMs;
            entry.D2xxSerial = config.D2xxSerial;
            entry.ExpertServerUrl = config.ExpertServerUrl;
            entry.TuneArmTimeoutMs = config.TuneArmTimeoutMs;
            entry.UpdatedUtc = DateTime.UtcNow;
            if (entry.Id == 0) _entries.Insert(entry);
            else _entries.Update(entry);
        }
    }

    public void Dispose() => _dbLease.Dispose();
}

internal sealed class SpeTaurusConfigEntry
{
    public int Id { get; set; }
    public bool Enabled { get; set; }
    public string Transport { get; set; } = "local";
    public string PortName { get; set; } = "";
    public int BaudRate { get; set; } = 115200;
    public string BridgeHost { get; set; } = "";
    public int BridgePort { get; set; } = 9001;
    public bool AutoReconnect { get; set; } = true;
    public int ActivePollingMs { get; set; } = 100;
    public int IdlePollingMs { get; set; } = 1000;
    public int ResponseTimeoutMs { get; set; } = 1200;
    public int ConnectTimeoutMs { get; set; } = 3000;
    public string D2xxSerial { get; set; } = "";
    public string ExpertServerUrl { get; set; } = "";
    public int TuneArmTimeoutMs { get; set; } = 2000;
    public DateTime UpdatedUtc { get; set; }
}
