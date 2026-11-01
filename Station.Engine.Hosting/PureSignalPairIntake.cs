// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using System.Buffers.Binary;
using System.Text.Json;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// The WDSP calls the pair intake makes on the engine it feeds. Called under
/// the intake's feed gate, so an implementation must not call back into the
/// intake.
/// </summary>
internal interface IPureSignalPairTarget
{
    void SetPsMox(bool moxOn);
    void FeedPsFeedbackBlock(ReadOnlySpan<float> txI, ReadOnlySpan<float> txQ,
        ReadOnlySpan<float> rxI, ReadOnlySpan<float> rxQ);

    /// <summary>calcc's cumulative fit attempts and completed fits (info[7], info[5]).</summary>
    (int Attempts, int Completed) PsFitCounts();

    /// <summary>calcc's feedback level at its last fit (info[4], 0..256); zero when unknown.</summary>
    double PsFeedbackLevel();

    /// <summary>Discards calcc's learned correction, so its next fit is taken in full.</summary>
    void ResetPsCalibration();

    /// <summary>Keeps the running correction through calibration resets.</summary>
    void HoldPsCorrection(bool hold);

    void SavePsCorrection(string path);
    void RestorePsCorrection(string path);
}

/// <summary>
/// Engine-owned state the intake reads, and the feedback attenuation it may
/// move. Read under the intake's feed gate: no member may wait for the intake.
/// </summary>
internal interface IPureSignalPairIntakeHost
{
    /// <summary>The operator's auto-attenuate switch; the level preset and the wrong-level discard follow it.</summary>
    bool AutoAttenuate { get; }

    /// <summary>The feedback attenuation in force, in dB.</summary>
    int FeedbackAttenuationDb { get; }

    /// <summary>Moves the feedback attenuation at once, from the feed path (the level preset).</summary>
    void ApplyFeedbackAttenuationDb(int db);

    /// <summary>Records a preset attenuation as settled; called off the feed path.</summary>
    void RecordPresetAttenuationDb(int db);

    /// <summary>The operator HW peak, for the held-correction key.</summary>
    double HwPeak { get; }

    /// <summary>Lower edge (kHz) of the band in use, or 0 when none: nothing is held off band.</summary>
    int BandKhz { get; }

    /// <summary>Session directory for held corrections.</summary>
    string HeldCorrectionDirectory { get; }
}

/// <summary>One set of aligned pairs: reference and feedback I/Q planes of equal length.</summary>
internal readonly ref struct PureSignalPairSpans
{
    internal PureSignalPairSpans(ReadOnlySpan<float> txI, ReadOnlySpan<float> txQ,
        ReadOnlySpan<float> rxI, ReadOnlySpan<float> rxQ)
    {
        TxI = txI;
        TxQ = txQ;
        RxI = rxI;
        RxQ = rxQ;
    }

    internal ReadOnlySpan<float> TxI { get; }
    internal ReadOnlySpan<float> TxQ { get; }
    internal ReadOnlySpan<float> RxI { get; }
    internal ReadOnlySpan<float> RxQ { get; }
    internal int Length => TxI.Length;

    internal bool IsWellFormed => TxQ.Length == TxI.Length && RxI.Length == TxI.Length && RxQ.Length == TxI.Length;
}

/// <summary>The header of one pair block (kind 3).</summary>
/// <param name="Sequence">Block number in the arm, from 0.</param>
/// <param name="KeyEpoch">The key epoch the pairs were aligned in.</param>
/// <param name="GainSequence">The feedback gain announcement the source applied.</param>
/// <param name="HwPeak">The HW peak the source conditioned with; 0 when it had none.</param>
/// <param name="SampleRateHz">Pair rate; must be the engine's external TX rate.</param>
/// <param name="Complex">Pairs per plane.</param>
internal readonly record struct PureSignalPairBlockHeader(
    long Sequence, long KeyEpoch, long GainSequence, double HwPeak, int SampleRateHz, int Complex);

/// <summary>
/// Binary frames of the external pair protocol, version 2. Every frame starts
/// with the 48-byte little-endian header: magic, version, kind, sequence,
/// three kind-specific 64-bit fields, sample rate and complex count. See
/// docs/designs/ps-external-pair-intake.md.
/// </summary>
internal static class PureSignalPairProtocol
{
    internal const uint Magic = 0x5A465031; // ZFP1
    internal const ushort Version = 2;
    /// <summary>Raw TX reference, engine to source.</summary>
    internal const ushort ReferenceKind = 1;
    /// <summary>Aligned pairs, source to engine.</summary>
    internal const ushort PairBlockKind = 3;
    internal const int HeaderBytes = 48;
    /// <summary>Primary txI, txQ, rxI, rxQ, then fallback txI, txQ, rxI, rxQ.</summary>
    internal const int PairPlanes = 8;
    internal const int MaximumPairComplex = 4_096;
    internal const int MaximumReferenceComplex = 16_384;

    internal static int PairBlockBytes(int complex) => HeaderBytes + PairPlanes * complex * sizeof(float);

    internal static int ReferenceBytes(int complex) => HeaderBytes + 2 * complex * sizeof(float);

    internal static void WritePairBlock(Span<byte> destination, in PureSignalPairBlockHeader header,
        PureSignalPairSpans primary, PureSignalPairSpans fallback)
    {
        int complex = header.Complex;
        if (complex <= 0 || complex > MaximumPairComplex || !primary.IsWellFormed || !fallback.IsWellFormed ||
            primary.Length != complex || fallback.Length != complex)
            throw new ArgumentException("Pair planes must all hold the header's complex count.", nameof(header));
        if (destination.Length != PairBlockBytes(complex))
            throw new ArgumentException("Destination does not match the pair block size.", nameof(destination));
        WriteHeader(destination, PairBlockKind, header.Sequence, header.KeyEpoch, header.GainSequence,
            BitConverter.DoubleToInt64Bits(header.HwPeak), header.SampleRateHz, complex);
        int offset = HeaderBytes;
        offset = WritePlane(destination, offset, primary.TxI);
        offset = WritePlane(destination, offset, primary.TxQ);
        offset = WritePlane(destination, offset, primary.RxI);
        offset = WritePlane(destination, offset, primary.RxQ);
        offset = WritePlane(destination, offset, fallback.TxI);
        offset = WritePlane(destination, offset, fallback.TxQ);
        offset = WritePlane(destination, offset, fallback.RxI);
        WritePlane(destination, offset, fallback.RxQ);
    }

    internal static bool TryReadPairBlockHeader(ReadOnlySpan<byte> source, out PureSignalPairBlockHeader header)
    {
        header = default;
        if (!TryReadHeader(source, PairBlockKind, out long sequence, out long keyEpoch, out long gainSequence,
                out long hwPeakBits, out int sampleRate, out int complex))
            return false;
        double hwPeak = BitConverter.Int64BitsToDouble(hwPeakBits);
        if (sequence < 0 || keyEpoch < 0 || gainSequence < 0 || sampleRate <= 0 ||
            complex <= 0 || complex > MaximumPairComplex || !double.IsFinite(hwPeak) || hwPeak < 0 ||
            source.Length != PairBlockBytes(complex))
            return false;
        header = new(sequence, keyEpoch, gainSequence, hwPeak, sampleRate, complex);
        return true;
    }

    /// <summary>
    /// Copies the eight planes of a block whose header has been read into
    /// <paramref name="planes"/>, bit for bit. False on any non-finite value.
    /// </summary>
    internal static bool TryReadPairPlanes(ReadOnlySpan<byte> block, int complex, float[][] planes)
    {
        int offset = HeaderBytes;
        for (int plane = 0; plane < PairPlanes; plane++)
        {
            var destination = planes[plane];
            for (int index = 0; index < complex; index++, offset += sizeof(float))
            {
                float value = BinaryPrimitives.ReadSingleLittleEndian(block[offset..]);
                if (!float.IsFinite(value)) return false;
                destination[index] = value;
            }
        }
        return true;
    }

    /// <summary>
    /// A raw reference frame. <paramref name="keyEpoch"/> is the epoch the TX
    /// observer read when it stamped the frame; <paramref name="twoToneRevision"/>
    /// the lock-free two-tone revision it saw.
    /// </summary>
    internal static void WriteReference(Span<byte> destination, long sequence, long keyEpoch, long twoToneRevision,
        int sampleRateHz, ReadOnlySpan<float> iq)
    {
        if (iq.IsEmpty || (iq.Length & 1) != 0 || iq.Length / 2 > MaximumReferenceComplex)
            throw new ArgumentException("Reference must be whole complex samples within the frame limit.", nameof(iq));
        if (destination.Length != ReferenceBytes(iq.Length / 2))
            throw new ArgumentException("Destination does not match the reference frame size.", nameof(destination));
        WriteHeader(destination, ReferenceKind, sequence, keyEpoch, twoToneRevision, 0, sampleRateHz, iq.Length / 2);
        WritePlane(destination, HeaderBytes, iq);
    }

    internal static bool TryReadReferenceHeader(ReadOnlySpan<byte> source, out long sequence, out long keyEpoch,
        out long twoToneRevision, out int sampleRateHz, out int complex)
    {
        if (!TryReadHeader(source, ReferenceKind, out sequence, out keyEpoch, out twoToneRevision,
                out long reserved, out sampleRateHz, out complex) ||
            sequence < 0 || keyEpoch < 0 || twoToneRevision < 0 || reserved != 0 || sampleRateHz <= 0 ||
            complex <= 0 || complex > MaximumReferenceComplex || source.Length != ReferenceBytes(complex))
        {
            sequence = keyEpoch = twoToneRevision = 0;
            sampleRateHz = complex = 0;
            return false;
        }
        return true;
    }

    private static int WritePlane(Span<byte> destination, int offset, ReadOnlySpan<float> values)
    {
        foreach (float value in values)
        {
            BinaryPrimitives.WriteSingleLittleEndian(destination[offset..], value);
            offset += sizeof(float);
        }
        return offset;
    }

    private static void WriteHeader(Span<byte> destination, ushort kind, long sequence,
        long fieldA, long fieldB, long fieldC, int sampleRate, int complex)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], kind);
        BinaryPrimitives.WriteInt64LittleEndian(destination[8..], sequence);
        BinaryPrimitives.WriteInt64LittleEndian(destination[16..], fieldA);
        BinaryPrimitives.WriteInt64LittleEndian(destination[24..], fieldB);
        BinaryPrimitives.WriteInt64LittleEndian(destination[32..], fieldC);
        BinaryPrimitives.WriteInt32LittleEndian(destination[40..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(destination[44..], complex);
    }

    private static bool TryReadHeader(ReadOnlySpan<byte> source, ushort kind, out long sequence,
        out long fieldA, out long fieldB, out long fieldC, out int sampleRate, out int complex)
    {
        sequence = fieldA = fieldB = fieldC = 0;
        sampleRate = complex = 0;
        if (source.Length < HeaderBytes || BinaryPrimitives.ReadUInt32LittleEndian(source) != Magic ||
            BinaryPrimitives.ReadUInt16LittleEndian(source[4..]) != Version ||
            BinaryPrimitives.ReadUInt16LittleEndian(source[6..]) != kind)
            return false;
        sequence = BinaryPrimitives.ReadInt64LittleEndian(source[8..]);
        fieldA = BinaryPrimitives.ReadInt64LittleEndian(source[16..]);
        fieldB = BinaryPrimitives.ReadInt64LittleEndian(source[24..]);
        fieldC = BinaryPrimitives.ReadInt64LittleEndian(source[32..]);
        sampleRate = BinaryPrimitives.ReadInt32LittleEndian(source[40..]);
        complex = BinaryPrimitives.ReadInt32LittleEndian(source[44..]);
        return true;
    }
}

/// <summary>
/// Generic PureSignal intake for feedback pairs that an external source has
/// already aligned. One instance serves one arm. It feeds calcc the primary
/// pair set until calcc rejects <see cref="FallbackRejections"/> fits in a
/// row, then the fallback set until an over on it is clean (see
/// <see cref="CheckFitsLocked"/>); it baselines and checks
/// calcc's fit counts, discards a fit learned at the wrong feedback level,
/// presets the feedback attenuation from the first strong pairs, and holds,
/// saves and resumes a validated correction across key edges. It announces
/// key edges, gain changes and calibration status to the source, and drops
/// blocks from an earlier key epoch so nothing of an over is fed after its
/// key-up. The source owns everything before the pair (see
/// docs/designs/ps-external-pair-intake.md).
/// Lock order: the feed gate may take the control gate, never the reverse;
/// WDSP is called only under the feed gate.
/// </summary>
internal sealed class PureSignalPairIntake
{
    /// <summary>Rejected primary fits in a row before the fallback set is fed.</summary>
    internal const int FallbackRejections = 3;

    /// <summary>Returns from the fallback set to the primary set allowed in one arm.</summary>
    internal const int MaximumPrimaryReturns = 3;

    /// <summary>
    /// How long calcc can hold a collected sample: its collection clears at every
    /// key-down (LSETUP) and after five seconds of collecting (LCOLLECT). A fit
    /// rejected within this long of a two-tone block may be the two-tone's.
    /// </summary>
    internal const long CalccCollectionMs = 5_000;
    internal const long FitCheckMs = 500;
    internal const double MaximumHwPeak = 2.0;
    internal const int MaximumTagLength = 64;

    private readonly object _gate = new();
    private readonly object _controlGate = new();
    private readonly IPureSignalPairTarget _target;
    private readonly IPureSignalPairIntakeHost _host;
    private readonly RxMode _mode;
    private readonly int _sampleRateHz;
    private readonly int _maximumAttenuationDb;
    private readonly Action<byte[]> _sendControl;
    private readonly ILogger _log;
    private readonly Func<long> _clockMs;
    private readonly Action<Action> _offFeedPath;
    private readonly ExternalFeedbackLevelPreset _levelPreset = new();
    private readonly float[][] _planes = new float[PureSignalPairProtocol.PairPlanes][];
    private bool _started;
    private bool _stopped;
    private bool _transmitting;
    private bool _resumePending;
    private long _keyEpoch;
    private long _gainSequence;
    private long _nextSequence;
    private int _announcedAttenuationDb;
    private double? _announcedHwPeak;
    private long? _announcedReceiverHz;
    private string? _geometry;
    private long _fitCheckMs = long.MinValue;
    private (int Attempts, int Completed) _fitCounts;
    private bool _fitCountsRead;
    private int _rejectedInARow;
    private bool _fallback;
    private bool _probation;
    private int _primaryReturns;
    private bool _overRejected;
    private bool _overCompleted;
    private long _lastTwoToneFedMs = long.MinValue;
    // Written by a key edge under the feed gate and by a two-tone epoch under
    // the control gate alone; read under the feed gate.
    private bool _twoToneRunning;
    private int? _completedAtStart;
    private double? _lastFeedbackLevel;

    /// <param name="sendControl">
    /// Queues one UTF-8 JSON control message to the source, in call order.
    /// It must not block or call back into the intake.
    /// </param>
    /// <param name="offFeedPath">Runs work off the feed path; the thread pool by default.</param>
    internal PureSignalPairIntake(IPureSignalPairTarget target, IPureSignalPairIntakeHost host, RxMode mode,
        int sampleRateHz, int maximumAttenuationDb, Action<byte[]> sendControl, ILogger log,
        Func<long> clockMs, Action<Action>? offFeedPath = null)
    {
        _target = target;
        _host = host;
        _mode = mode;
        _sampleRateHz = sampleRateHz;
        _maximumAttenuationDb = maximumAttenuationDb;
        _sendControl = sendControl;
        _log = log;
        _clockMs = clockMs;
        _offFeedPath = offFeedPath ?? (work => ThreadPool.QueueUserWorkItem(_ => work()));
        _announcedAttenuationDb = Math.Clamp(host.FeedbackAttenuationDb, 0, maximumAttenuationDb);
        for (int plane = 0; plane < _planes.Length; plane++) _planes[plane] = [];
    }

    /// <summary>
    /// True, the default, feeds the primary set until the fallback engages;
    /// false feeds the fallback set throughout.
    /// </summary>
    internal bool PreferPrimary { get; set; } = true;

    /// <summary>
    /// False, the default, feeds a block built with an earlier feedback gain
    /// and counts it (<see cref="StaleGainBlocks"/>); true drops it.
    /// </summary>
    internal bool DropStaleGainBlocks { get; set; }

    /// <summary>The key epoch in force. Published only after its edge has been queued to the source.</summary>
    internal long KeyEpoch => Volatile.Read(ref _keyEpoch);

    /// <summary>The last feedback gain announced to the source.</summary>
    internal long GainSequence => Volatile.Read(ref _gainSequence);

    internal bool Transmitting { get { lock (_gate) return _transmitting; } }
    internal bool UsingFallback { get { lock (_gate) return _fallback; } }
    internal bool OnProbation { get { lock (_gate) return _probation; } }
    internal string? Geometry { get { lock (_gate) return _geometry; } }
    internal long BlocksFed { get { lock (_gate) return _blocksFed; } }
    internal long StaleKeyBlocks { get { lock (_gate) return _staleKeyBlocks; } }
    internal long StaleGainBlocks { get { lock (_gate) return _staleGainBlocks; } }
    private long _blocksFed;
    private long _staleKeyBlocks;
    private long _staleGainBlocks;

    /// <summary>The source's HW-peak suggestion, raised on the receive thread; a handler must not block.</summary>
    internal event Action<double>? HwPeakSuggested;

    /// <summary>
    /// The source is ready: blocks are fed from here on. <paramref name="geometry"/>
    /// is the source's opaque tag for the correction geometry. Returns a
    /// failure reason, or null.
    /// </summary>
    internal string? Start(string? geometry)
    {
        if (!IsValidTag(geometry)) return "pair-geometry-tag";
        lock (_gate)
        {
            if (_stopped) return null;
            _geometry = geometry;
            _started = true;
        }
        return null;
    }

    /// <summary>A late geometry change from the source on a ready arm.</summary>
    internal string? SetGeometry(string? geometry)
    {
        if (!IsValidTag(geometry)) return "pair-geometry-tag";
        lock (_gate) if (!_stopped) _geometry = geometry;
        return null;
    }

    /// <summary>
    /// Ends the arm: a feed in flight completes first and none follows, and
    /// PS MOX is cleared if it was set. The caller then disables PureSignal.
    /// </summary>
    internal void Stop()
    {
        lock (_gate)
        {
            if (_stopped) return;
            Volatile.Write(ref _stopped, true);
            if (!_transmitting) return;
            _transmitting = false;
            _target.SetPsMox(false);
        }
    }

    /// <summary>
    /// PureSignal is enabled for this arm: the correction is held through
    /// calibration resets, and the arm's first key-down resumes the last
    /// validated correction for the same geometry.
    /// </summary>
    internal void StartHold()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _resumePending = true;
        }
        _target.HoldPsCorrection(true);
    }

    /// <summary>
    /// Follows the authoritative transmit state into calcc's MOX. The edge is
    /// queued to the source before its epoch is published, so a reference
    /// frame stamped with the new epoch is always queued behind it. A block in
    /// flight is fed before the edge; any block of the old epoch after it is
    /// dropped. A key-down resumes a held correction, a key-up saves a
    /// validated one, both off the feed gate.
    /// </summary>
    internal void SetTransmitting(bool transmitting, double? twoToneLowHz = null, double? twoToneHighHz = null,
        long? twoToneRevision = null)
    {
        lock (_gate)
        {
            if (_stopped || _transmitting == transmitting) return;
            lock (_controlGate)
            {
                long epoch = _keyEpoch + 1;
                Send(new { type = "key", transmitting, keyEpoch = epoch, twoToneLowHz, twoToneHighHz, twoToneRevision });
                Volatile.Write(ref _keyEpoch, epoch);
            }
            _transmitting = transmitting;
            Volatile.Write(ref _twoToneRunning, transmitting && IsTwoTone(twoToneLowHz, twoToneHighHz));
            if (transmitting) BeginOverLocked();
            _target.SetPsMox(transmitting);
        }
        if (transmitting) ResumeHeldCorrection();
        else SaveValidatedCorrection();
    }

    /// <summary>
    /// The arm, the first message of the arm's control stream. It carries the
    /// feedback gain, its sequence and the HW peak the intake holds at the
    /// moment it is queued, so the source starts from exactly what later
    /// announcements change. Call it once, before the intake is shared.
    /// </summary>
    /// <param name="twoToneRevision">The engine's two-tone revision at the arm.</param>
    /// <param name="receiverHz">The receiver frequency at the arm, if known.</param>
    internal void SendArm(long twoToneRevision, long? receiverHz)
    {
        lock (_controlGate)
        {
            if (Volatile.Read(ref _stopped)) return;
            double hwPeak = _host.HwPeak;
            _announcedHwPeak = hwPeak;
            _announcedReceiverHz = receiverHz;
            Send(new
            {
                type = "arm",
                mode = _mode.ToString(),
                sampleRate = _sampleRateHz,
                protocol = (int)PureSignalPairProtocol.Version,
                attenuationDb = _announcedAttenuationDb,
                gain = GainFor(_announcedAttenuationDb),
                gainSequence = Volatile.Read(ref _gainSequence),
                hwPeak,
                twoToneRevision,
                vfoHz = receiverHz,
            });
        }
    }

    /// <summary>The receiver frequency, announced once per change.</summary>
    internal void AnnounceReceiverFrequency(long hz)
    {
        lock (_controlGate)
        {
            if (Volatile.Read(ref _stopped) || _announcedReceiverHz == hz) return;
            _announcedReceiverHz = hz;
            Send(new { type = "tune", vfoHz = hz });
        }
    }

    /// <summary>The engine's two-tone pair for <paramref name="revision"/>, read off the TX path.</summary>
    internal void AnnounceTwoTone(long revision, double? lowHz, double? highHz)
    {
        lock (_controlGate)
        {
            if (Volatile.Read(ref _stopped)) return;
            Send(new { type = "two-tone-epoch", revision, twoToneLowHz = lowHz, twoToneHighHz = highHz });
            Volatile.Write(ref _twoToneRunning, IsTwoTone(lowHz, highHz));
        }
    }

    // Tones 1 Hz apart or closer have no period a source could align on.
    private static bool IsTwoTone(double? lowHz, double? highHz) =>
        lowHz is double low && highHz is double high && Math.Abs(high - low) > 1;

    /// <summary>
    /// A key-down ends the previous over. If it ran on the fallback set, had a
    /// completed fit and no counted rejection, the primary set is fed again on
    /// probation, at most <see cref="MaximumPrimaryReturns"/> times an arm.
    /// Caller holds _gate.
    /// </summary>
    private void BeginOverLocked()
    {
        if (_fallback && PreferPrimary && _overCompleted && !_overRejected &&
            _primaryReturns < MaximumPrimaryReturns)
        {
            _fallback = false;
            _probation = true;
            _rejectedInARow = 0;
            _primaryReturns++;
            _log.LogInformation("ps_pair_intake.primary_return returns={Returns}", _primaryReturns);
        }
        _overRejected = false;
        _overCompleted = false;
    }

    /// <summary>The operator HW peak, announced once per change.</summary>
    internal void AnnounceHwPeak(double hwPeak)
    {
        lock (_controlGate)
        {
            if (Volatile.Read(ref _stopped) || _announcedHwPeak == hwPeak) return;
            _announcedHwPeak = hwPeak;
            Send(new { type = "hw-peak", value = hwPeak });
        }
    }

    /// <summary>
    /// Every change of the feedback attenuation, whoever made it (the servo,
    /// a band restore, the level preset). Announced once per actual change
    /// with the next gain sequence. Never waits for a feed.
    /// </summary>
    internal void OnFeedbackAttenuationChanged(int db)
    {
        int clamped = Math.Clamp(db, 0, _maximumAttenuationDb);
        lock (_controlGate)
        {
            if (Volatile.Read(ref _stopped) || clamped == _announcedAttenuationDb) return;
            _announcedAttenuationDb = clamped;
            long sequence = _gainSequence + 1;
            Volatile.Write(ref _gainSequence, sequence);
            Send(new { type = "feedback-gain", gainSequence = sequence, attenuationDb = clamped, gain = GainFor(clamped) });
        }
    }

    /// <summary>The digital feedback gain for an attenuation in dB.</summary>
    internal static float GainFor(int attenuationDb) => (float)Math.Pow(10, -attenuationDb / 20.0);

    /// <summary>
    /// One pair block from the source. Returns a failure reason that ends the
    /// arm, or null when the block was fed or dropped as stale.
    /// </summary>
    internal string? HandleBlock(ReadOnlySpan<byte> frame)
    {
        if (!PureSignalPairProtocol.TryReadPairBlockHeader(frame, out var header) ||
            header.SampleRateHz != _sampleRateHz)
            return "pair-geometry";
        lock (_gate)
        {
            if (_stopped) return null;
            if (!_started) return "pair-before-ready";
            if (header.Sequence != _nextSequence) return "pair-sequence";
            _nextSequence++;
            // The source can only echo values it has been sent.
            if (header.KeyEpoch > _keyEpoch) return "pair-key-epoch";
            if (header.GainSequence > Volatile.Read(ref _gainSequence)) return "pair-gain-sequence";
            if (_planes[0].Length != header.Complex)
                for (int plane = 0; plane < _planes.Length; plane++) _planes[plane] = new float[header.Complex];
            if (!PureSignalPairProtocol.TryReadPairPlanes(frame, header.Complex, _planes)) return "pair-nonfinite";
            // Aligned before the source saw the latest key edge: never fed.
            if (header.KeyEpoch != _keyEpoch || !_transmitting)
            {
                _staleKeyBlocks++;
                return null;
            }
            if (header.GainSequence != Volatile.Read(ref _gainSequence))
            {
                _staleGainBlocks++;
                if (DropStaleGainBlocks) return null;
            }
            FeedLocked(header.HwPeak, header.Complex);
            _blocksFed++;
        }
        return null;
    }

    /// <summary>
    /// The source-to-engine control messages this intake owns: <c>fault</c>
    /// (returns the source's reason) and <c>hw-peak-suggestion</c>.
    /// <paramref name="handled"/> is false for any other type, which the
    /// caller handles. Returns a failure reason, or null.
    /// </summary>
    internal string? HandleControl(JsonElement root, out bool handled)
    {
        handled = false;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String)
            return null;
        switch (type.GetString())
        {
            case "fault":
                handled = true;
                return root.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String &&
                    reason.GetString() is { } text && IsValidReason(text)
                    ? text : "source-fault";
            case "hw-peak-suggestion":
                handled = true;
                if (!root.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Number ||
                    !value.TryGetDouble(out double peak) || !double.IsFinite(peak) || peak <= 0 || peak > MaximumHwPeak)
                    return "control-protocol";
                HwPeakSuggested?.Invoke(peak);
                return null;
            default:
                return null;
        }
    }

    // Caller holds _gate. The same order as the in-engine feed it replaces:
    // feed, fit check, level preset; the clock is read once, before the feed.
    private void FeedLocked(double hwPeak, int complex)
    {
        long now = _clockMs();
        var primary = Pairs(0, complex);
        var fallback = Pairs(4, complex);
        if (hwPeak > 0)
        {
            if (Volatile.Read(ref _twoToneRunning)) _lastTwoToneFedMs = now;
            var fed = PreferPrimary && !_fallback ? primary : fallback;
            _target.FeedPsFeedbackBlock(fed.TxI, fed.TxQ, fed.RxI, fed.RxQ);
            CheckFitsLocked(now);
            PresetFeedbackLevelLocked(fallback, hwPeak);
        }
        else
        {
            // Without a HW peak the level cannot be judged: fed, never checked.
            _target.FeedPsFeedbackBlock(fallback.TxI, fallback.TxQ, fallback.RxI, fallback.RxQ);
        }
    }

    private PureSignalPairSpans Pairs(int firstPlane, int complex) => new(
        _planes[firstPlane].AsSpan(0, complex), _planes[firstPlane + 1].AsSpan(0, complex),
        _planes[firstPlane + 2].AsSpan(0, complex), _planes[firstPlane + 3].AsSpan(0, complex));

    /// <summary>
    /// Counts calcc's rejected fits while the primary set is fed; after
    /// <see cref="FallbackRejections"/> in a row the fallback set is fed. A
    /// completed fit resets the count. A fit rejected within
    /// <see cref="CalccCollectionMs"/> of a two-tone block is not counted: a
    /// test signal's rejections must not move a voice feed. After an over on
    /// the fallback set with a completed fit and no counted rejection, the next
    /// key-down returns to the primary set on probation (<see cref="BeginOverLocked"/>);
    /// one counted rejection on probation goes straight back to the fallback
    /// set, and a completed primary fit ends probation. Caller holds _gate.
    /// </summary>
    private void CheckFitsLocked(long now)
    {
        if (_fitCheckMs != long.MinValue && now - _fitCheckMs < FitCheckMs) return;
        _fitCheckMs = now;
        var counts = _target.PsFitCounts();
        var previous = _fitCounts;
        _fitCounts = counts;
        // The first read is the baseline, even when it is (0, 0): the first
        // fit of the arm is then the first rejection counted.
        if (!_fitCountsRead)
        {
            _fitCountsRead = true;
            _completedAtStart = counts.Completed;
            SendStatusLocked();
            return;
        }
        int attempts = counts.Attempts - previous.Attempts, completed = counts.Completed - previous.Completed;
        if (completed > 0) DiscardFitAtWrongLevelLocked();
        bool testSignal = attempts > completed && _lastTwoToneFedMs != long.MinValue &&
            now - _lastTwoToneFedMs <= CalccCollectionMs;
        if (testSignal)
            _log.LogInformation("ps_pair_intake.two_tone_rejections_ignored attempts={Attempts} completed={Completed}",
                attempts, completed);
        if (completed > 0) _overCompleted = true;
        if (completed == 0 && attempts > 0 && !testSignal) _overRejected = true;
        if (!_fallback && PreferPrimary && attempts > 0)
        {
            if (completed > 0)
            {
                _rejectedInARow = 0;
                _probation = false;
            }
            else if (!testSignal)
            {
                _rejectedInARow += attempts;
                if (_rejectedInARow >= FallbackRejections || _probation)
                {
                    _fallback = true;
                    _log.LogWarning(
                        "ps_pair_intake.fallback rejectedInARow={Rejected} attempts={Attempts} completed={Completed} probation={Probation}",
                        _rejectedInARow, counts.Attempts, counts.Completed, _probation);
                    _probation = false;
                }
            }
        }
        SendStatusLocked();
    }

    /// <summary>
    /// Discards a fit calcc learned with the feedback outside the
    /// auto-attenuate window when the attenuation can still bring it inside:
    /// calcc takes the first fit after a reset in full and moves only part of
    /// the way per later fit, so a fit at the wrong level lingers. Off with
    /// auto-attenuate, where nothing would move the level. Caller holds _gate.
    /// </summary>
    private void DiscardFitAtWrongLevelLocked()
    {
        if (!_host.AutoAttenuate) return;
        double level = _target.PsFeedbackLevel();
        _lastFeedbackLevel = level;
        if (level <= 0) return;
        int attenuation = _host.FeedbackAttenuationDb;
        bool hot = level > PsAutoAttenuateService.FeedbackHighThreshold && attenuation < _maximumAttenuationDb;
        bool quiet = level < PsAutoAttenuateService.FeedbackLowThreshold && attenuation > 0;
        if (!hot && !quiet) return;
        _log.LogInformation("ps_pair_intake.fit_discarded fb={Level:F0} attn={Attenuation}", level, attenuation);
        _target.ResetPsCalibration();
    }

    /// <summary>
    /// Sets the attenuation before calcc's first fit of the arm: the level is
    /// predicted from the first strong fallback pairs and, when it is outside
    /// the servo's window, the attenuation that puts it at the servo's target
    /// is applied and calcc's half-collected buckets are discarded. Stands
    /// down once a fit has completed (the servo owns the level), and is off
    /// with auto-attenuate. Caller holds _gate.
    /// </summary>
    private void PresetFeedbackLevelLocked(PureSignalPairSpans pairs, double hwPeak)
    {
        if (_levelPreset.Done) return;
        if (_completedAtStart is not int start) return;
        if (_levelPreset.Add(pairs.TxI, pairs.TxQ, pairs.RxI, pairs.RxQ, hwPeak) is not double level) return;
        if (!_host.AutoAttenuate) return;
        // Read at the decision: a fit that has landed since the arm began
        // hands the level to the servo.
        if (_target.PsFitCounts().Completed != start) return;
        int current = _host.FeedbackAttenuationDb;
        if (ExternalFeedbackLevelPreset.AttenuationFor(level, current, _maximumAttenuationDb) is not int db) return;
        _log.LogInformation("ps_pair_intake.level_preset predicted={Level:F0} from={From}dB to={To}dB",
            level, current, db);
        // The gain changes and is announced before the reset drops what calcc
        // collected at the old level; the settled record follows off the feed path.
        _host.ApplyFeedbackAttenuationDb(db);
        OnFeedbackAttenuationChanged(db);
        _target.ResetPsCalibration();
        _offFeedPath(() => _host.RecordPresetAttenuationDb(db));
    }

    // Caller holds _gate. Carries only values the feed path has already read.
    private void SendStatusLocked()
    {
        lock (_controlGate)
        {
            Send(new
            {
                type = "status",
                fed = PreferPrimary && !_fallback ? "primary" : "fallback",
                attempts = _fitCounts.Attempts,
                completed = _fitCounts.Completed,
                feedbackLevel = _lastFeedbackLevel,
                attenuationDb = _announcedAttenuationDb,
                gainSequence = Volatile.Read(ref _gainSequence),
            });
        }
    }

    // Caller holds _gate.
    private PureSignalHeldCorrectionKey HeldKeyLocked() => new(
        _host.BandKhz, _mode, _geometry, _host.FeedbackAttenuationDb, (int)Math.Round(_host.HwPeak * 1000));

    /// <summary>
    /// On the arm's first key-down, resumes the last validated correction for
    /// the same geometry, if any. Not at arm time: WDSP installs a restored
    /// correction only once the TX channel runs.
    /// </summary>
    private void ResumeHeldCorrection()
    {
        PureSignalHeldCorrectionKey key;
        lock (_gate)
        {
            if (!_resumePending || _stopped) return;
            _resumePending = false;
            key = HeldKeyLocked();
        }
        if (key.BandKhz <= 0) return;
        string path = Path.Combine(_host.HeldCorrectionDirectory, key.FileName);
        if (!File.Exists(path)) return;
        _target.RestorePsCorrection(path);
        _log.LogInformation(
            "ps_pair_intake.correction_resumed band={Band}kHz mode={Mode} geometry={Geometry} attn={Attn}dB",
            key.BandKhz, key.Mode, key.Geometry, key.AttenuationDb);
    }

    /// <summary>
    /// After an over in which calcc completed a fit at a feedback level inside
    /// the auto-attenuate window, saves the running correction under the arm's
    /// geometry, so a later arm on the same geometry resumes it.
    /// </summary>
    private void SaveValidatedCorrection()
    {
        PureSignalHeldCorrectionKey key;
        int? start;
        lock (_gate)
        {
            if (_stopped) return;
            key = HeldKeyLocked();
            start = _completedAtStart;
        }
        if (key.BandKhz <= 0 || start is not int completedAtStart) return;
        if (_target.PsFitCounts().Completed <= completedAtStart) return;
        double level = _target.PsFeedbackLevel();
        if (level < PsAutoAttenuateService.FeedbackLowThreshold ||
            level > PsAutoAttenuateService.FeedbackHighThreshold)
            return;
        string directory = _host.HeldCorrectionDirectory;
        string path = Path.Combine(directory, key.FileName);
        _offFeedPath(() =>
        {
            try
            {
                Directory.CreateDirectory(directory);
                _target.SavePsCorrection(path);
                _log.LogInformation("ps_pair_intake.correction_saved band={Band}kHz fb={Level:F0} attn={Attn}dB",
                    key.BandKhz, level, key.AttenuationDb);
            }
            catch (Exception exception) { _log.LogWarning(exception, "ps_pair_intake.correction_save failed"); }
        });
    }

    // Caller holds _controlGate, so messages leave in the order they were made.
    private void Send<T>(T message) => _sendControl(JsonSerializer.SerializeToUtf8Bytes(message));

    /// <summary>A source tag that may become part of a file name: short, and only letters, digits, '_', '+', '-'.</summary>
    internal static bool IsValidTag(string? tag)
    {
        if (tag is null) return true;
        if (tag.Length is 0 or > MaximumTagLength) return false;
        foreach (char c in tag)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '+' or '-')) return false;
        return true;
    }

    private static bool IsValidReason(string reason)
    {
        if (reason.Length is 0 or > MaximumTagLength) return false;
        foreach (char c in reason)
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')) return false;
        return true;
    }
}

/// <summary>
/// The geometry a held correction was validated on. A saved correction is
/// resumed only onto an arm with the same band, mode, source geometry,
/// feedback attenuation and HW peak; anything else starts cold.
/// </summary>
internal readonly record struct PureSignalHeldCorrectionKey(
    int BandKhz, RxMode Mode, string? Geometry, int AttenuationDb, int HwPeakMilli)
{
    internal string FileName => $"{BandKhz}-{Mode}-{Geometry ?? "none"}-{AttenuationDb}-{HwPeakMilli}.txt";
}
