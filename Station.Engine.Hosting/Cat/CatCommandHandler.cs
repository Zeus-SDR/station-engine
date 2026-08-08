// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using Zeus.Contracts;

namespace Zeus.Server.Cat;

/// <summary>
/// Stateless-per-connection Kenwood TS-2000 command dispatcher. Holds the
/// per-session Auto-Information level and routes each parsed command to the
/// verified Zeus seams. Deliberately decoupled from socket I/O (it takes a
/// <c>send</c> callback and a <c>latestRxDbm</c> accessor) so the full Tier-1
/// command surface — including the safety-critical "no auto-key" and per-source
/// MOX-ownership behaviour — is unit-testable without a TCP connection.
///
/// All keying goes through <see cref="TxService.TrySetMox"/> with
/// <see cref="MoxSource.Cat"/>; CAT never arms PureSignal and only keys on an
/// explicit <c>TX;</c>.
/// </summary>
internal sealed class CatCommandHandler
{
    private readonly RadioService _radio;
    private readonly TxService _tx;
    private readonly CatOptions _options;
    private readonly Func<double> _latestRxDbm;
    private readonly Action<string> _send;

    private int _autoInfo;

    public CatCommandHandler(
        RadioService radio,
        TxService tx,
        CatOptions options,
        Func<double> latestRxDbm,
        Action<string> send)
    {
        _radio = radio;
        _tx = tx;
        _options = options;
        _latestRxDbm = latestRxDbm;
        _send = send;
    }

    /// <summary>True once the client issued AI1/AI2 — gates unsolicited pushes.</summary>
    public bool AutoInfoEnabled => Volatile.Read(ref _autoInfo) > 0;

    /// <summary>Server-side toggle for per-port "Auto Report" (piHPSDR AutoRprt
    /// parity): pre-enables AI1 so devices that expect unsolicited frequency
    /// updates but never send <c>AI1;</c> (some amplifiers, loggers, legacy CAT
    /// clients) get them. Distinct from a client's <c>AI1;</c> in that it does
    /// NOT seed an initial IF frame — a device that never sent AI cannot be
    /// assumed to be listening at connect time; the next state event drives the
    /// first push naturally.</summary>
    public void EnableAutoInfo() => Volatile.Write(ref _autoInfo, 1);

    public void Dispatch(string token) =>
        DispatchAsync(token, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public async ValueTask DispatchAsync(string token, CancellationToken cancellationToken)
    {
        string cmd = CatProtocol.CommandId(token);
        string args = CatProtocol.Args(token);
        switch (cmd)
        {
            case "ID": _send("ID019;"); break;                            // TS-2000 id (Hamlib requires first)
            case "PS": _send("PS1;"); break;                              // power on
            case "AI": HandleAi(args); break;
            case "FA": HandleFreq(args, vfoB: false); break;
            case "FB": HandleFreq(args, vfoB: true); break;
            case "MD": HandleMode(args); break;
            case "IF": HandleIf(); break;
            case "TX":
                await _tx.TrySetMoxFromCatAsync(true, cancellationToken);
                break;                                                    // explicit key only
            case "RX":
                await _tx.TrySetMoxFromCatAsync(false, cancellationToken);
                break;
            case "FR": HandleFr(args); break;
            case "FT": HandleFt(args); break;
            case "SM": HandleSmeter(); break;
            case "PC": HandlePc(args); break;
            default: _send(CatProtocol.Error); break;                     // "?;" — Kenwood unknown/unsupported
        }
    }

    private void HandleAi(string args)
    {
        if (args.Length == 0)
        {
            _send(CatProtocol.Response("AI", Volatile.Read(ref _autoInfo).ToString()));
            return;
        }
        if (int.TryParse(args.AsSpan(0, 1), out int level))
        {
            Volatile.Write(ref _autoInfo, level);
            // On enabling, optionally seed current state so the client need not
            // poll. RX/status only — never a TX frame, never a key.
            if (level > 0 && _options.SendInitialStateOnConnect)
                HandleIf();
        }
    }

    private void HandleFreq(string args, bool vfoB)
    {
        string cmd = vfoB ? "FB" : "FA";
        if (args.Length == 0)
        {
            var state = _radio.Snapshot();
            // Match Thetis: with RX2 exposed, VFO B remains RX2. In the
            // single-receiver layout it is RX1's independent split-TX dial.
            long f = vfoB ? RadioFrequencyResolver.CatVfoBHz(state) : state.VfoHz;
            _send(CatProtocol.Response(cmd, CatProtocol.FormatFreq(f)));
            return;
        }
        if (CatProtocol.TryParseFreq(args, out long hz)
            && _radio.IsExternalFrequencyAvailable(hz))
        {
            // External source — bypass the frozen-NCO recenter heuristic so the
            // hardware tracks the commanded frequency absolutely (issue #461,
            // same as TCI). Kenwood set commands have no reply.
            if (vfoB && _radio.Snapshot().Rx2Enabled) _radio.SetVfoB(hz);
            else if (vfoB) _radio.SetSplitFrequency(0, hz);
            else _radio.SetVfo(hz, fromExternal: true);
        }
    }

    private void HandleMode(string args)
    {
        if (args.Length == 0)
        {
            _send(CatProtocol.Response("MD", CatProtocol.ModeDigit(_radio.Snapshot().Mode)));
            return;
        }
        var mode = CatProtocol.ParseMode(args[..1]);
        if (mode is not null) _radio.SetMode(mode.Value);
    }

    private void HandleIf()
    {
        var state = _radio.Snapshot();
        _send(CatProtocol.Response("IF",
            CatProtocol.BuildIfBody(state.VfoHz, state.Mode, _tx.IsMoxOn,
                RadioFrequencyResolver.IsSplitEnabledForTx(state))));
    }

    private void HandleFr(string args)
    {
        // The primary receive dial is always Kenwood VFO A.
        if (args.Length == 0) _send(CatProtocol.Response("FR", "0"));
    }

    private void HandleFt(string args)
    {
        if (args.Length == 0)
        {
            _send(CatProtocol.Response("FT",
                RadioFrequencyResolver.IsSplitEnabledForTx(_radio.Snapshot()) ? "1" : "0"));
            return;
        }
        if (args[0] is '0' or '1')
        {
            var state = _radio.Snapshot();
            _radio.SetSplit(state.TxReceiverIndex, args[0] == '1');
        }
    }

    private void HandleSmeter()
    {
        // SM reply: "SM" + P1(meter 0=main) + 4-digit value (0000-0030).
        _send(CatProtocol.Response("SM", "0" + CatProtocol.SMeterField(_latestRxDbm())));
    }

    private void HandlePc(string args)
    {
        if (args.Length == 0)
        {
            int cur = Math.Clamp(_radio.Snapshot().DrivePct, 0, 100);
            _send(CatProtocol.Response("PC", cur.ToString().PadLeft(3, '0')));
            return;
        }
        if (int.TryParse(args, out int set))
        {
            set = Math.Clamp(set, 0, 100);
            if (_options.LimitPowerLevels) set = Math.Min(set, 50);
            _radio.SetDrive(set);
        }
    }
}
