// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Snapshot of the on-board iambic keyer knobs the host emulator must
/// match. Built from the same fields <c>RadioService.PushCwToP2</c> sends
/// to the radio so the monitor tone's timing tracks the RF keyer.
/// </summary>
public readonly record struct HostIambicKeyerConfig(
    int SpeedWpm,
    CwKeyerMode Mode,
    int Weight,
    bool PaddleReverse);

/// <summary>
/// Host-side port of the Orion Protocol-2 FPGA iambic keyer
/// (<c>docs/references/firmware/orion-mkii_7000-8000dle/P2-gateware/Orion2_P2_v2.2.10/iambic.v</c>),
/// clocked at 48 kHz — the same rate the gateware instantiates
/// (<c>iambic #(48)</c> in <c>Orion.v</c>) and the same rate
/// <see cref="CwSidetoneSource"/> renders, so one <see cref="Step"/> is
/// one audio sample.
///
/// Used only as the host <em>monitor</em> sidetone source on Orion-MkII
/// DLE gateware older than 2.2.10, which reports raw paddle pins but not
/// the FPGA keyer's shaped <c>keyout</c> (issue #2977). The radio still
/// keys RF from its own FPGA keyer; this class never touches the wire.
///
/// Single-threaded: owned by the DSP render thread. <see cref="Step"/>
/// must not allocate.
/// </summary>
public sealed class HostIambicKeyer
{
    /// <summary>Verilog <c>clock_speed</c> in kHz. Must equal
    /// <see cref="CwSidetoneSource.OutputRateHz"/> / 1000.</summary>
    public const int ClockKhz = CwSidetoneSource.OutputRateHz / 1000;

    // iambic.v localparam — LETTERSPACE is kept for fidelity but is
    // unreachable: Zeus pins strict spacing OFF on the wire
    // (Protocol2Client.CwKeyerSpacing = false), so DOTHELD/DASHHELD
    // always take the `else key_state <= LOOP` arm.
    private const int Loop = 0;
    private const int Predot = 1;
    private const int Predash = 2;
    private const int SendDot = 3;
    private const int SendDash = 4;
    private const int DotDelay = 5;
    private const int DashDelay = 6;
    private const int DotHeld = 7;
    private const int DashHeld = 8;
    private const int LetterSpace = 9;

    private int _state;
    private int _delay;
    private bool _dotMemory;
    private bool _dashMemory;
    private bool _keyerOut;
    // dot_delay / dash_delay depend only on the clamped WPM and weight.
    // Cached so Step does not divide on every sample. -1 never matches a
    // clamped knob, so the first iambic clock always fills them.
    private int _delayWpm = -1;
    private int _delayWeight = -1;
    private int _dotDelay;
    private int _dashDelay;

    /// <summary>True while the machine is in LOOP with the output low.
    /// Paddle memories are not part of the test: iambic.v's LOOP state
    /// never reads them (the next PREDOT/PREDASH clears them), and a
    /// memory left set must not pin the sidetone renderer in the
    /// per-sample loop.</summary>
    public bool IsIdle => _state == Loop && !_keyerOut;

    /// <summary>True after the most recent clock left the machine in
    /// PREDOT. Render uses this to drop a latched dot press.</summary>
    internal bool InPredot => _state == Predot;

    /// <summary>True after the most recent clock left the machine in
    /// PREDASH. Render uses this to drop a latched dash press.</summary>
    internal bool InPredash => _state == Predash;

    internal bool DotMemory => _dotMemory;
    internal bool DashMemory => _dashMemory;

    /// <summary>Return to LOOP, clear memories, delay and output. Safe to
    /// call mid-element; the caller is responsible for ramping the
    /// audible envelope down so the operator does not hear a click.</summary>
    public void Reset()
    {
        _state = Loop;
        _delay = 0;
        _dotMemory = false;
        _dashMemory = false;
        _keyerOut = false;
    }

    /// <summary>
    /// Advance one 48 kHz keyer clock and return the registered
    /// <c>keyer_out</c> after the edge.
    /// </summary>
    /// <param name="dotKey">Raw DOT jack level, before paddle swap
    /// (status byte 0 bit 1).</param>
    /// <param name="dashKey">Raw DASH jack level, before paddle swap
    /// (status byte 0 bit 2).</param>
    /// <param name="config">Live WPM / mode / weight / reverse. Clamped
    /// to the same 1..60 WPM and 33..66 weight the P2 wire uses.</param>
    public bool Step(bool dotKey, bool dashKey, in HostIambicKeyerConfig config)
    {
        // Host emulator is iambic-only (issue #2977). Straight-key host
        // sidetone stays on ExternalPttService's raw-paddle Down/Up path.
        // iambic.v's iambic=0 (straight/bug) LOOP branch is therefore
        // omitted; CWX and IO8 are wired false for the same reason.
        // Reset so a mid-element state cannot survive the mode change.
        if (config.Mode is not (CwKeyerMode.IambicA or CwKeyerMode.IambicB))
        {
            Reset();
            return false;
        }

        int wpm = Math.Clamp(config.SpeedWpm, 1, 60);
        int weight = Math.Clamp(config.Weight, 33, 66);
        if (wpm != _delayWpm || weight != _delayWeight)
        {
            // Integer math identical to iambic.v:
            //   assign dot_delay  = (1200 * clock_speed)/cw_speed;
            //   assign dash_delay = (dot_delay * 3 * weight)/50;
            _delayWpm = wpm;
            _delayWeight = weight;
            _dotDelay = (1200 * ClockKhz) / wpm;
            _dashDelay = (_dotDelay * 3 * weight) / 50;
        }
        bool keyerModeB = config.Mode == CwKeyerMode.IambicB;
        // Combinational paddle swap, same as the Verilog wires.
        bool dot = config.PaddleReverse ? dashKey : dotKey;
        bool dash = config.PaddleReverse ? dotKey : dashKey;

        // Non-blocking-assignment model: every RHS is the pre-clock
        // register value; multiple writes to one register take the last
        // assignment in source order (Verilog always-block NBA).
        int nextState = _state;
        int nextDelay = _delay;
        bool nextDotMem = _dotMemory;
        bool nextDashMem = _dashMemory;
        bool nextOut = _keyerOut;

        switch (_state)
        {
            case Loop:
                if (dot)
                    nextState = Predot;
                else if (dash)
                    nextState = Predash;
                else
                    nextOut = false; // CWX || IO8, both wired false
                break;

            case Predot:
                nextDotMem = false;
                nextDashMem = false;
                nextState = SendDot;
                break;

            case Predash:
                nextDotMem = false;
                nextDashMem = false;
                nextState = SendDash;
                break;

            case SendDot:
                // keyer_out <= 1, then maybe <= 0 on the trip clock (last
                // write wins). delay >= dot_delay is against the
                // pre-increment counter, so delay walks 0 .. dot_delay-1
                // with the output high (dot_delay samples) and the trip
                // clock drives the output low.
                nextOut = true;
                if (_delay >= _dotDelay)
                {
                    nextDelay = 0;
                    nextOut = false;
                    nextState = DotDelay;
                }
                else
                {
                    nextDelay = _delay + 1;
                }
                // Mode A: both paddles released clears dash memory on
                // this clock. Mode B: dash pressed sets dash memory.
                // Same-clock as the state test, matching the Verilog.
                if (!keyerModeB)
                {
                    if (!dot && !dash)
                        nextDashMem = false;
                }
                else if (dash)
                {
                    nextDashMem = true;
                }
                break;

            case SendDash:
                nextOut = true;
                if (_delay >= _dashDelay)
                {
                    nextDelay = 0;
                    nextOut = false;
                    nextState = DashDelay;
                }
                else
                {
                    nextDelay = _delay + 1;
                }
                if (!keyerModeB)
                {
                    if (!dot && !dash)
                        nextDotMem = false;
                }
                else if (dot)
                {
                    nextDotMem = true;
                }
                break;

            case DotDelay:
                if (_delay >= _dotDelay)
                {
                    nextDelay = 0;
                    nextState = _dashMemory ? Predash : DotHeld;
                }
                else
                {
                    nextDelay = _delay + 1;
                }
                if (dash)
                    nextDashMem = true;
                break;

            case DashDelay:
                if (_delay >= _dotDelay)
                {
                    nextDelay = 0;
                    nextState = _dotMemory ? Predot : DashHeld;
                }
                else
                {
                    nextDelay = _delay + 1;
                }
                if (dot)
                    nextDotMem = true;
                break;

            case DotHeld:
                if (dot)
                    nextState = Predot;
                else if (dash)
                    nextState = Predash;
                // letter_space is always false (CwKeyerSpacing pin).
                // LETTERSPACE is therefore unreachable; the arm is kept
                // so a future wire-pin flip cannot silently drop the
                // state. See case LetterSpace below.
                else
                    nextState = Loop;
                break;

            case DashHeld:
                if (dash)
                    nextState = Predash;
                else if (dot)
                    nextState = Predot;
                else
                    nextState = Loop;
                break;

            case LetterSpace:
                // Unreachable while letter_space is false. Port kept
                // byte-for-byte with iambic.v (2 × dot_delay, paddle
                // event memory during the pause).
                if (_delay >= 2 * _dotDelay)
                {
                    nextDelay = 0;
                    if (_dotMemory)
                        nextState = Predot;
                    else if (_dashMemory)
                        nextState = Predash;
                    else
                        nextState = Loop;
                }
                else
                {
                    nextDelay = _delay + 1;
                }
                if (dot)
                    nextDotMem = true;
                if (dash)
                    nextDashMem = true;
                break;

            default:
                nextState = Loop;
                break;
        }

        _state = nextState;
        _delay = nextDelay;
        _dotMemory = nextDotMem;
        _dashMemory = nextDashMem;
        _keyerOut = nextOut;
        return _keyerOut;
    }
}
