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
/// Also answers the Thetis/PowerSDR "ZZ" extended dialect (4-letter ids such as
/// <c>ZZFA</c>), so clients that speak it to Thetis's TCP CAT server — N1MM+
/// (port type TCP, radio "Flex5000") and DXLab Commander (rig type "Thetis") —
/// connect directly (issue #2939). Each ZZ command shares the exact state
/// semantics of its TS-2000 sibling; only the framing and strict argument
/// lengths differ (Thetis replies "?;" to a wrong-length argument).
///
/// All keying goes through <see cref="TxService.TrySetMox"/> with
/// <see cref="MoxSource.Cat"/>; CAT never arms PureSignal and only keys on an
/// explicit <c>TX;</c> / <c>ZZTX1;</c>.
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
            case "AI": HandleAi(args, "AI", zzStrict: false); break;
            case "FA": HandleFreq(args, vfoB: false, "FA", zzStrict: false); break;
            case "FB": HandleFreq(args, vfoB: true, "FB", zzStrict: false); break;
            case "MD": HandleMode(args); break;
            case "IF": HandleIf(); break;
            case "TX":
                await _tx.TrySetMoxFromCatAsync(true, cancellationToken);
                break;                                                    // explicit key only
            case "RX":
                await _tx.TrySetMoxFromCatAsync(false, cancellationToken);
                break;
            case "FR": HandleFr(args); break;
            case "FT": HandleFt(args, "FT", zzStrict: false); break;
            case "SM": HandleSmeter(); break;
            case "PC": HandlePc(args, "PC", zzStrict: false); break;
            case "ZZ":
                // Thetis/PowerSDR extended dialect (issue #2939): every "ZZxx"
                // token arrives here via the 2-letter id; the 4-letter id
                // selects the ZZ handler.
                await DispatchZzAsync(token, cancellationToken);
                break;
            default: _send(CatProtocol.Error); break;                     // "?;" — Kenwood unknown/unsupported
        }
    }

    /// <summary>
    /// Thetis/PowerSDR "ZZ" extended commands. Thetis framing rules
    /// (CATParser.cs CheckFormat + CATStructs.xml lengths): a get carries the
    /// command's get-parameter length (usually 0), a set its set-parameter
    /// length, and anything else is "?;" with no state change. Get replies echo
    /// the get-parameter before the answer ("ZZSM0;" → "ZZSM0nnn;"). Set
    /// commands never reply. Unknown ZZ ids fall through to "?;".
    /// </summary>
    private async ValueTask DispatchZzAsync(string token, CancellationToken cancellationToken)
    {
        string cmd = CatProtocol.ZzCommandId(token);
        string args = CatProtocol.ZzArgs(token);
        switch (cmd)
        {
            case "ZZID":                                                  // rig id — Thetis sends no reply
                if (args.Length != 0) _send(CatProtocol.Error);
                break;
            case "ZZPS": HandleZzPs(args); break;
            case "ZZAI": HandleAi(args, "ZZAI", zzStrict: true); break;
            case "ZZFA": HandleFreq(args, vfoB: false, "ZZFA", zzStrict: true); break;
            case "ZZFB": HandleFreq(args, vfoB: true, "ZZFB", zzStrict: true); break;
            case "ZZMD": HandleZzMode(args, "ZZMD", rx2: false); break;
            case "ZZME": HandleZzMode(args, "ZZME", rx2: true); break;
            case "ZZIF": HandleZzIf(args); break;
            case "ZZTX":
                await HandleZzTxAsync(args, cancellationToken);
                break;
            case "ZZSP": HandleFt(args, "ZZSP", zzStrict: true); break;
            case "ZZSW":
                // Thetis ZZSW sets console.SwapVFOA_BTX, which checks
                // chkVFOBTX ("Swap VFO A/B TX Buttons" = transmit on VFO B;
                // chkVFOBTX_CheckedChanged sets Display.TXOnVFOB and forces
                // chkVFOSplit when RX2 is off). That is Kenwood FT1 semantics,
                // so it shares HandleFt. The VFO-contents swap is a different
                // command (ZZVS), which Zeus does not implement and answers
                // "?;".
                HandleFt(args, "ZZSW", zzStrict: true);
                break;
            case "ZZSM": HandleZzSm(args); break;
            case "ZZPC": HandlePc(args, "ZZPC", zzStrict: true); break;
            default: _send(CatProtocol.Error); break;                     // "?;" — unsupported ZZ id or too-short token
        }
    }

    private void HandleAi(string args, string cmd, bool zzStrict)
    {
        if (args.Length == 0)
        {
            _send(CatProtocol.Response(cmd, Volatile.Read(ref _autoInfo).ToString()));
            return;
        }
        // ZZAI is strict (Thetis CATStructs.xml: exactly 1 set digit).
        if (zzStrict && args.Length != 1)
        {
            _send(CatProtocol.Error);
            return;
        }
        if (int.TryParse(args.AsSpan(0, 1), out int level))
        {
            Volatile.Write(ref _autoInfo, level);
            // On enabling, optionally seed current state so the client need not
            // poll. RX/status only — never a TX frame, never a key. The seed
            // stays Kenwood IF format even for ZZAI: Thetis's own broadcast is
            // Kenwood too (console.cs BroadcastFreqChange sends "FA...;").
            if (level > 0 && _options.SendInitialStateOnConnect)
                HandleIf();
        }
        else if (zzStrict) _send(CatProtocol.Error);
    }

    private void HandleFreq(string args, bool vfoB, string cmd, bool zzStrict)
    {
        if (args.Length == 0)
        {
            var state = _radio.Snapshot();
            // Match Thetis: with RX2 exposed, VFO B remains RX2. In the
            // single-receiver layout it is RX1's independent split-TX dial.
            long f = vfoB ? RadioFrequencyResolver.CatVfoBHz(state) : state.VfoHz;
            _send(CatProtocol.Response(cmd, CatProtocol.FormatFreq(f)));
            return;
        }
        // ZZFA/ZZFB are strict (Thetis CATStructs.xml: exactly 11 set digits).
        if (zzStrict && args.Length != 11)
        {
            _send(CatProtocol.Error);
            return;
        }
        if (!CatProtocol.TryParseFreq(args, out long hz))
        {
            if (zzStrict) _send(CatProtocol.Error);
            return;
        }
        if (_radio.IsExternalFrequencyAvailable(hz))
        {
            // External source — bypass the frozen-NCO recenter heuristic so the
            // hardware tracks the commanded frequency absolutely (issue #461,
            // same as TCI). Kenwood/Thetis set commands have no reply.
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

    /// <summary>
    /// ZZMD (RX1 mode) / ZZME (RX2 mode) with the 2-digit Thetis mode codes
    /// (<c>CATCommands.Mode2String</c>/<c>String2Mode</c>). ZZME reads the
    /// independent RX2 receiver mode while RX2 is exposed and answers RX1's
    /// code otherwise (no live RX2 mode exists then). A ZZME set retunes the
    /// RX2 mode only while RX2 is enabled — SetMode(…, TxVfo.B) falls back to
    /// RX1 when RX2 is off, which would surprise a ZZME sender, so it is gated.
    /// Thetis range-checks 00..11 and answers "?;" outside it; 08 (SPEC) and 11
    /// (DRM) are in range but have no Zeus mode, so they change nothing.
    /// </summary>
    private void HandleZzMode(string args, string cmd, bool rx2)
    {
        if (args.Length == 0)
        {
            var state = _radio.Snapshot();
            var mode = rx2 && state.Rx2Enabled ? state.Rx2().Mode : state.Mode;
            _send(CatProtocol.Response(cmd, CatProtocol.ZzModeDigit(mode)));
            return;
        }
        if (args.Length != 2
            || !int.TryParse(args, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int code)
            || code < 0 || code > 11)
        {
            _send(CatProtocol.Error);
            return;
        }
        var parsed = CatProtocol.ParseZzMode(args);
        if (parsed is null) return; // 08/11: no Zeus equivalent → no state change
        if (!rx2) _radio.SetMode(parsed.Value);
        else if (_radio.Snapshot().Rx2Enabled) _radio.SetMode(parsed.Value, TxVfo.B);
    }

    private void HandleIf()
    {
        var state = _radio.Snapshot();
        _send(CatProtocol.Response("IF",
            CatProtocol.BuildIfBody(state.VfoHz, state.Mode, _tx.IsMoxOn,
                RadioFrequencyResolver.IsSplitEnabledForTx(state))));
    }

    /// <summary>ZZIF is get-only in Thetis (CATStructs.xml nsetparms=-1); any
    /// argument is "?;". Same state sources as <see cref="HandleIf"/>, with the
    /// 36-byte Thetis body (2-digit mode field).</summary>
    private void HandleZzIf(string args)
    {
        if (args.Length != 0)
        {
            _send(CatProtocol.Error);
            return;
        }
        var state = _radio.Snapshot();
        _send(CatProtocol.Response("ZZIF",
            CatProtocol.BuildZzIfBody(state.VfoHz, state.Mode, _tx.IsMoxOn,
                RadioFrequencyResolver.IsSplitEnabledForTx(state))));
    }

    private void HandleFr(string args)
    {
        // The primary receive dial is always Kenwood VFO A.
        if (args.Length == 0) _send(CatProtocol.Response("FR", "0"));
    }

    private void HandleFt(string args, string cmd, bool zzStrict)
    {
        if (args.Length == 0)
        {
            _send(CatProtocol.Response(cmd,
                RadioFrequencyResolver.IsSplitEnabledForTx(_radio.Snapshot()) ? "1" : "0"));
            return;
        }
        // ZZSP/ZZSW are strict (Thetis CATStructs.xml: exactly 1 set digit,
        // "0" or "1").
        if (zzStrict && args.Length != 1)
        {
            _send(CatProtocol.Error);
            return;
        }
        if (args[0] is '0' or '1')
        {
            var state = _radio.Snapshot();
            _radio.SetSplit(state.TxReceiverIndex, args[0] == '1');
        }
        else if (zzStrict) _send(CatProtocol.Error);
    }

    private void HandleSmeter()
    {
        // SM reply: "SM" + P1(meter 0=main) + 4-digit value (0000-0030).
        _send(CatProtocol.Response("SM", "0" + CatProtocol.SMeterField(_latestRxDbm())));
    }

    /// <summary>
    /// ZZSM is get-only with a 1-digit meter selector that the reply echoes
    /// ("ZZSM0;" → "ZZSM0nnn;"). Thetis 0 = main / 1 = RX2; Zeus exposes a
    /// single meter source here (the same <c>_latestRxDbm</c> as SM), so both
    /// selectors answer from it. Anything else is "?;" (Thetis behaviour).
    /// </summary>
    private void HandleZzSm(string args)
    {
        if (args is "0" or "1")
            _send(CatProtocol.Response("ZZSM", args + CatProtocol.ZzSMeterField(_latestRxDbm())));
        else
            _send(CatProtocol.Error);
    }

    /// <summary>
    /// ZZTX mirrors TX/RX exactly: "ZZTX1;" keys via the CAT MOX source,
    /// "ZZTX0;" unkeys, a bare get reports state. Any other argument is "?;"
    /// and never keys. Explicit key only, never auto-key.
    /// </summary>
    private async ValueTask HandleZzTxAsync(string args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            _send(CatProtocol.Response("ZZTX", _tx.IsMoxOn ? "1" : "0"));
            return;
        }
        switch (args)
        {
            case "1":
                await _tx.TrySetMoxFromCatAsync(true, cancellationToken);
                break;
            case "0":
                await _tx.TrySetMoxFromCatAsync(false, cancellationToken);
                break;
            default:
                _send(CatProtocol.Error);
                break;
        }
    }

    /// <summary>
    /// ZZPS: Zeus never powers anything off via CAT, so a get always reports
    /// "1" and a set is accepted and ignored (no reply, Thetis set semantics).
    /// Wrong-length arguments are "?;".
    /// </summary>
    private void HandleZzPs(string args)
    {
        if (args.Length == 0)
        {
            _send(CatProtocol.Response("ZZPS", "1"));
            return;
        }
        if (args.Length != 1) _send(CatProtocol.Error);
    }

    private void HandlePc(string args, string cmd, bool zzStrict)
    {
        if (args.Length == 0)
        {
            int cur = Math.Clamp(_radio.Snapshot().DrivePct, 0, 100);
            _send(CatProtocol.Response(cmd, cur.ToString().PadLeft(3, '0')));
            return;
        }
        // ZZPC is strict (Thetis CATStructs.xml: exactly 3 set digits).
        if (zzStrict && args.Length != 3)
        {
            _send(CatProtocol.Error);
            return;
        }
        if (int.TryParse(args, out int set))
        {
            set = Math.Clamp(set, 0, 100);
            if (_options.LimitPowerLevels) set = Math.Min(set, 50);
            _radio.SetDrive(set);
        }
        else if (zzStrict) _send(CatProtocol.Error);
    }
}
