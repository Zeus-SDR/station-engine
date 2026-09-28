// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.

namespace Zeus.Server;

/// <summary>
/// Narrow frequency-acquisition bank for fixed 256-sample CW audio blocks.
///
/// Unlocked, the bank searches a configurable span around the requested tone
/// and follows the bin whose keying stands out from the running noise floor.
/// A steady carrier has almost no on/off swing, so a quieter keyed tone wins
/// even when a carrier of the same strength beats against it. Locked, the bank
/// stays on the requested tone. Reach and power are compared with the noise
/// floor and with each other, so a clean quiet tone is acquired the same way
/// as a loud one. A new center is only accepted after several consecutive
/// winners.
///
/// Decode power is the 256-sample Hann bin. A carrier about 200 Hz away is
/// still inside that main lobe, and a same-length Blackman-Harris window
/// widens the lobe, so it does not clear the key-on ratio. When the bank
/// shows an off-bin tone that strong, the decode power is the same Hann bin
/// with that carrier removed and scaled back to the Hann power of a tone
/// alone. The block stays 256 samples. Power tracking may sit on that
/// carrier while it is the loudest steady bin. The keying measurement stays
/// on the configured centre until the tracked bin's swing clears the noise
/// floor, the same bar the search uses, so the carrier is still removed from
/// the station being copied. Once that station is acquired, silence does not
/// walk the search onto noise. A steady carrier and a different keyed tone
/// still do, and a lock or retarget to a different tone drops the acquisition.
/// Locking the tone being copied keeps that history and the carrier
/// cancellation. The copied tone is the centre again as soon as the acquired
/// bin stops keying, so a retarget is measured from the station being copied
/// and not from a carrier or from noise.
/// </summary>
internal sealed class GoertzelDetector
{
    // 256 samples at 48 kHz is 5.33 ms: short enough to resolve 50 WPM key
    // edges while retaining ample processing gain for a narrow CW tone.
    public const int BlockSize = 256;
    public const int SearchStepHz = 25;
    // Search around a tone the operator clicked. A locked bank also spans
    // this, so a carrier beside the locked tone is visible.
    public const double UnlockedSearchHalfWidthHz = 250;
    // Search around the radio pitch when nothing was clicked: the ±125 Hz,
    // eleven-bin bank the decoder has always used. A 256-sample Hann bin
    // 125 Hz off still hears a tone at the pitch, so a walk onto noise
    // inside this span does not lose the station. Past it, it does.
    public const double PitchSearchHalfWidthHz = 125;
    public const int MaxSearchHalfWidthSteps = 10;
    private const int BinCount = (MaxSearchHalfWidthSteps * 2) + 1;
    private const int LockConfirmationBlocks = 8;
    // Long enough for several key-up/key-down transitions at ordinary CW speeds.
    private const double ModulationWindowSeconds = 1.5;
    private const double LowPercentile = 0.10;
    private const double MedianPercentile = 0.50;
    private const double HighPercentile = 0.90;
    // A challenger has to clear the locked bin by this much. One step away on
    // a clean tone is about one percent, and the beat beside an equal-level
    // carrier stays within a couple of percent of the tone, so the lock does
    // not walk off the keying.
    private const double ReachSwitchAdvantage = 1.03;
    // The rail gap is smoothed over a couple of seconds. A 1.5 s window's
    // winner walks between the tone and the beat as the keying changes; the
    // average does not.
    private const double ReachSmoothingSeconds = 1.8;
    // Power fallback for a steady tone. A tone 100 Hz off the locked bin is
    // about 1.45×; a noise bin is only a few percent louder.
    private const double PowerSwitchAdvantage = 1.15;
    // Low end of the live bank. The tone sits in a few bins; this percentile
    // stays on the quiet ones and is the running noise floor.
    private const double NoisePercentile = 0.10;
    // How far a bin must stand above that floor before it counts as a tone.
    // The ratio is what matters, so a 0.01-peak tone ranks like a loud one.
    // One tone's own main lobe still lifts nearby bins, so the factor stays
    // modest; a noise bin does not clear it.
    private const double ReachOverNoise = 1.5;
    private const double PowerOverNoise = 1.5;
    // Past the pitch span a bin that is not the station hears none of it.
    // There a search walks on power only for a signal clearly above the
    // noise: a noise bin that wins a few blocks is a few times the floor.
    private const double WidePowerOverNoise = 4.0;
    // Past the pitch span a bin is only copied, or chosen on keying reach,
    // once its key-down clears the noise floor by the same margin a clean
    // key-up swing needs. Noise swings 8× on its own; it does not sit 8×
    // above the quiet end of the bank.
    private const double WideKeyDownOverNoise = 8.0;
    private static readonly int PitchHalfSteps = HalfStepsFor(PitchSearchHalfWidthHz);
    // Below this the bin is an empty accumulator, not a signal.
    private const double NumericalReachGuard = 1e-12;
    private const double NumericalPowerGuard = 1e-18;
    private const double PowerSmoothing = 0.22;
    // Hann power at 200 Hz off a 256-sample bin is about a fifth of the
    // on-bin power, so a stronger carrier is several times a steady
    // acquired tone. A neighbour of that tone is not. Held across a few
    // blocks so one noisy block cannot latch it.
    private const double CarrierPowerAdvantage = 3.0;
    // A keyed tone shares its Hann lobe with a carrier a hundred hertz
    // away, so that carrier is not several times the tone bin. Keyed
    // neighbours are already excluded. Half the acquired peak is enough
    // to be the station holding the key-up floor up.
    private const double CarrierBesideKeyedTone = 0.5;
    private const int CarrierConfirmBlocks = 8;
    private const double CarrierHoldRatio = 0.5;
    private const double TonePeakDecay = 0.995;
    // Longer than a dah, shorter than a station that has gone quiet.
    // 375 blocks is two seconds at 256 samples and 48 kHz.
    private const double KeyingMemorySeconds = 2.0;
    private const double KeyedSwing = 8.0;
    // Carrier leakage flutters a shoulder by a fraction of a percent of
    // its amplitude. Keying still moves a rail by more than this, including
    // a tone whose key-up floor is that leakage.
    private const double KeyedReachFraction = 0.05;

    private readonly int _sampleRateHz;
    private readonly int _historyBlocks;
    private readonly int _modulationBlocks;
    private readonly int _keyingMemoryBlocks;
    private readonly double[] _window = new double[BlockSize];
    private readonly double[] _coefficients = new double[BinCount];
    private readonly double[] _frequenciesHz = new double[BinCount];
    private readonly double[] _instantPowers = new double[BinCount];
    private readonly double[] _smoothed = new double[BinCount];
    private readonly double[] _reaches = new double[BinCount];
    private readonly double[] _binScratch = new double[BinCount];
    private readonly double _reachSmoothing;
    private readonly double[] _history;
    private readonly RollingQuantiles[] _quantiles;
    private int _historyCursor;
    private int _historyCount;
    private int _activeCount = 1;
    private int _activeHalfSteps;
    private int _lockedIndex;
    private int _homeIndex;
    // Blocks spent on _lockedIndex since the search last moved, capped at
    // the keying memory. The turn-on that pulled the search here is older
    // than this and is not keying of this bin.
    private int _blocksOnLockedBin;
    // Off-centre bin the search chose because it was keying. Silence does
    // not walk the search from here onto noise. A real signal may. -1 until
    // that choice, and after the search leaves or the operator moves to a
    // different tone.
    private int _acquiredIndex = -1;
    // The candidate SelectCandidate just returned won on keying reach, not
    // on smoothed power. Read by the lock move in the same call.
    private bool _candidateFromKeying;
    private int _detectBlock;
    private int _reachFloorBlock = -1;
    private int _reachFloorLook = -1;
    private double _reachFloor;
    private int _pendingIndex = -1;
    private int _pendingBlocks;
    private bool _havePower;
    private bool _haveReach;
    private bool _searchLocked;
    private double _tonePeak;
    private int _tonePeakBin = -1;
    private int _carrierCandidate = -1;
    private int _carrierConfirm;
    private int _latchedCarrier = -1;
    // Long-window median power of each bin, refreshed every block.
    // A beat dips single blocks; the median does not, and that is the
    // level a steady carrier is judged on.
    private readonly double[] _windowMedian = new double[BinCount];
    private readonly bool[] _carrierSteady = new bool[BinCount];
    private int _kernelTone = -1;
    private int _kernelCarrier = -1;
    private double _kernelReal;
    private double _kernelImag;
    private double _kernelScale = 1;
    private double _configuredCenterFrequencyHz;
    private double _requestedHalfWidthHz = UnlockedSearchHalfWidthHz;

    public GoertzelDetector(int sampleRateHz, double centerFrequencyHz)
    {
        if (sampleRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        _sampleRateHz = sampleRateHz;
        _modulationBlocks = HistoryBlocksFor(sampleRateHz);
        _keyingMemoryBlocks = BlocksForSeconds(sampleRateHz, KeyingMemorySeconds);
        _historyBlocks = Math.Max(_modulationBlocks, _keyingMemoryBlocks);
        double blockSeconds = (double)BlockSize / sampleRateHz;
        _reachSmoothing = 1.0 - Math.Exp(-blockSeconds / ReachSmoothingSeconds);
        _history = new double[BinCount * _historyBlocks];
        _quantiles = new RollingQuantiles[BinCount];
        for (int i = 0; i < BinCount; i++) _quantiles[i] = new RollingQuantiles(_historyBlocks);
        for (int i = 0; i < BlockSize; i++)
            _window[i] = 0.5 - (0.5 * Math.Cos(2.0 * Math.PI * i / (BlockSize - 1)));
        Retune(centerFrequencyHz);
    }

    /// <summary>
    /// Tone being copied: the decode bin, not a carrier the search followed.
    /// </summary>
    public double TrackedFrequencyHz => _frequenciesHz[SelectDecodeBin()];

    /// <summary>Bin the search is sitting on. A steady carrier can pull this off the copied tone.</summary>
    internal double SearchFrequencyHz => _frequenciesHz[_lockedIndex];

    internal bool HasCarrier => _latchedCarrier >= 0;
    public bool SearchLocked => _searchLocked;

    /// <summary>Blocks of per-bin power kept for acquisition. Zero after a move to a different tone.</summary>
    internal int AcquisitionBlocks => _historyCount;

    /// <summary>True while an off-bin carrier is being removed from the decoded tone.</summary>
    internal bool CarrierCancellationArmed => _latchedCarrier >= 0;

    /// <summary>Frequency of the carrier being removed. Negative when none is.</summary>
    internal double LatchedCarrierHz =>
        _latchedCarrier >= 0 && _latchedCarrier < _activeCount
            ? _frequenciesHz[_latchedCarrier]
            : -1;

    /// <summary>Half-width of the live search, in Hz. Zero while locked.</summary>
    public double SearchHalfWidthHz => _searchLocked ? 0 : _activeHalfSteps * SearchStepHz;

    public void Retune(
        double centerFrequencyHz,
        bool locked = false,
        double searchHalfWidthHz = UnlockedSearchHalfWidthHz)
    {
        if (centerFrequencyHz <= 0 || centerFrequencyHz >= _sampleRateHz / 2.0)
            throw new ArgumentOutOfRangeException(nameof(centerFrequencyHz));

        // Read the copied tone before the lock flag or the grid changes.
        // One search step is a different bin. Locking the bin being copied,
        // or repeating it, keeps the history and the latched carrier. A lock
        // or retarget onto any other tone drops both. History is stored per
        // bin, so it can slide only when the new centre lands on this grid.
        double decodedHz = _frequenciesHz[DecodeBin()];
        double oldCenter = _configuredCenterFrequencyHz;
        int oldHalf = _activeHalfSteps;
        int oldCount = _activeCount;
        bool sameTone = Math.Abs(centerFrequencyHz - decodedHz) <= (SearchStepHz * 0.5);
        double centerMove = centerFrequencyHz - oldCenter;
        int binShift = (int)Math.Round(centerMove / SearchStepHz, MidpointRounding.AwayFromZero);
        bool onGrid = Math.Abs(centerMove - (binShift * (double)SearchStepHz)) <= 0.5;

        _configuredCenterFrequencyHz = centerFrequencyHz;
        _searchLocked = locked;
        _requestedHalfWidthHz = searchHalfWidthHz;
        // Locked tracking stays on the requested tone, but the bank still
        // covers the open search so a carrier beside that tone is visible.
        int halfSteps = locked
            ? HalfStepsFor(UnlockedSearchHalfWidthHz)
            : HalfStepsFor(searchHalfWidthHz);
        _activeHalfSteps = halfSteps;
        _activeCount = (halfSteps * 2) + 1;
        double highestFrequency = (_sampleRateHz / 2.0) - 1.0;
        for (int i = 0; i < _activeCount; i++)
        {
            int step = i - halfSteps;
            double frequency = Math.Clamp(centerFrequencyHz + (step * SearchStepHz), 1.0, highestFrequency);
            _frequenciesHz[i] = frequency;
            _coefficients[i] = 2.0 * Math.Cos(2.0 * Math.PI * frequency / _sampleRateHz);
        }

        if (!sameTone || !onGrid)
        {
            ClearTracking();
            return;
        }

        KeepDecodedTone(halfSteps - oldHalf - binShift, oldCount);
        if (!locked) return;

        // The lock is the tone being copied, even when the search was sitting
        // on a carrier beside it. Pending confirmation belongs to that search.
        if (_lockedIndex != halfSteps)
            _blocksOnLockedBin = 0;
        _lockedIndex = halfSteps;
        _pendingIndex = -1;
        _pendingBlocks = 0;
    }

    /// <summary>Drop the acquired offset and return to the configured pitch.</summary>
    public void Reset() => ClearTracking();

    private void ClearTracking()
    {
        Array.Clear(_instantPowers);
        Array.Clear(_smoothed);
        Array.Clear(_reaches);
        Array.Clear(_history);
        foreach (var quantiles in _quantiles) quantiles.Reset();
        _historyCursor = 0;
        _historyCount = 0;
        _havePower = false;
        _haveReach = false;
        _lockedIndex = _activeHalfSteps;
        _homeIndex = _activeHalfSteps;
        _blocksOnLockedBin = 0;
        _acquiredIndex = -1;
        _candidateFromKeying = false;
        _detectBlock = 0;
        _reachFloorBlock = -1;
        _pendingIndex = -1;
        _pendingBlocks = 0;
        _tonePeak = 0;
        _tonePeakBin = -1;
        _carrierCandidate = -1;
        _carrierConfirm = 0;
        _latchedCarrier = -1;
        _kernelTone = -1;
    }

    // The new centre is the tone being copied. Slide each bin so its history
    // and the latched carrier stay on the same frequency.
    private void KeepDecodedTone(int indexDelta, int oldCount)
    {
        int newCount = _activeCount;
        if (indexDelta != 0 || oldCount != newCount)
        {
            ShiftBins(_instantPowers, indexDelta, oldCount, newCount);
            ShiftBins(_smoothed, indexDelta, oldCount, newCount);
            ShiftBins(_reaches, indexDelta, oldCount, newCount);
            ShiftHistory(indexDelta, oldCount, newCount);
            _lockedIndex = RemapBin(_lockedIndex, indexDelta, newCount);
            _acquiredIndex = RemapBin(_acquiredIndex, indexDelta, newCount);
            _pendingIndex = RemapBin(_pendingIndex, indexDelta, newCount);
            _latchedCarrier = RemapBin(_latchedCarrier, indexDelta, newCount);
            _carrierCandidate = RemapBin(_carrierCandidate, indexDelta, newCount);
            _tonePeakBin = RemapBin(_tonePeakBin, indexDelta, newCount);
            int kernelTone = RemapBin(_kernelTone, indexDelta, newCount);
            int kernelCarrier = RemapBin(_kernelCarrier, indexDelta, newCount);
            if (kernelTone < 0 || kernelCarrier < 0)
            {
                _kernelTone = -1;
                _kernelCarrier = -1;
            }
            else
            {
                _kernelTone = kernelTone;
                _kernelCarrier = kernelCarrier;
            }

            if (_latchedCarrier < 0)
            {
                _carrierCandidate = -1;
                _carrierConfirm = 0;
            }

            if (_pendingIndex < 0)
                _pendingBlocks = 0;
            if (_lockedIndex < 0)
            {
                _lockedIndex = _activeHalfSteps;
                _blocksOnLockedBin = 0;
                _pendingIndex = -1;
                _pendingBlocks = 0;
            }
        }

        _homeIndex = _activeHalfSteps;
        // The peak has to stay on the copied tone. A fresh bin would drop
        // the carrier hold on the next block.
        if (_havePower)
            _tonePeakBin = _activeHalfSteps;
    }

    private void ShiftBins(double[] values, int indexDelta, int oldCount, int newCount)
    {
        var copy = new double[values.Length];
        int limit = Math.Min(oldCount, values.Length);
        for (int index = 0; index < limit; index++)
        {
            int next = index + indexDelta;
            if ((uint)next >= (uint)newCount || next >= values.Length) continue;
            copy[next] = values[index];
        }

        Array.Copy(copy, values, values.Length);
    }

    private void ShiftHistory(int indexDelta, int oldCount, int newCount)
    {
        var copy = new double[_history.Length];
        int row = _historyBlocks;
        int limit = Math.Min(oldCount, BinCount);
        for (int index = 0; index < limit; index++)
        {
            int next = index + indexDelta;
            if ((uint)next >= (uint)newCount || next >= BinCount) continue;
            Array.Copy(_history, index * row, copy, next * row, row);
        }

        Array.Copy(copy, _history, _history.Length);
        // Retargeting is infrequent. Rebuild the ordering from the shifted
        // rings so newly exposed bins retain the same zero-filled history.
        for (int candidate = 0; candidate < BinCount; candidate++)
        {
            _quantiles[candidate].Reset();
            for (int age = _historyCount; age > 0; age--)
            {
                int index = (_historyCursor - age + _historyBlocks) % _historyBlocks;
                _quantiles[candidate].Push(_history[candidate * _historyBlocks + index]);
            }
        }
    }

    private static int RemapBin(int index, int indexDelta, int count)
    {
        if (index < 0) return -1;
        int next = index + indexDelta;
        if ((uint)next >= (uint)count) return -1;
        return next;
    }

    public double DetectPower(ReadOnlySpan<float> samples)
    {
        if (samples.Length != BlockSize)
            throw new ArgumentException($"A block must contain exactly {BlockSize} samples.", nameof(samples));

        _detectBlock++;
        for (int candidate = 0; candidate < _activeCount; candidate++)
        {
            double power = DetectPowerAt(samples, _coefficients[candidate]);
            _instantPowers[candidate] = power;
            Remember(candidate, power);
        }

        if (_historyCount < _historyBlocks) _historyCount++;
        _historyCursor++;
        if (_historyCursor == _historyBlocks) _historyCursor = 0;
        _havePower = true;

        if (!_searchLocked)
            TrackStableCandidate(SelectCandidate());

        // The carrier reference follows the bin being decoded. A new bin
        // that is actually keying starts from its own power, so the one it
        // walked away from cannot keep the old peak. A steady carrier the
        // search sat on is not that bin. Once the station stops keying, the
        // centre's power is the carrier's skirt. Adopting that skirt pins
        // the peak, and a carrier a couple of bins away never clears the
        // nomination. Let the peak decay until the carrier does.
        int toneBin = SelectDecodeBin();
        double tone = _smoothed[toneBin];
        bool station = ClearsNoiseFloor(toneBin);
        if (_tonePeakBin != toneBin)
        {
            _tonePeakBin = toneBin;
            if (station)
                _tonePeak = tone;
        }

        if (station)
            _tonePeak = Math.Max(tone, _tonePeak * TonePeakDecay);
        else
            _tonePeak *= TonePeakDecay;

        return DecodePower(samples, toneBin);
    }

    // Locked, or already on the centre: that bin is the station. A search
    // step onto a steady carrier is not. The step that walked the search
    // there is still inside the long keying memory, so the carrier looks
    // keyed, but it has not gone key-up since the search arrived. The
    // centre stays the tone being copied until the tracked bin's own swing
    // clears the noise floor. That is the same bar the search uses, and it
    // does not ask for an 8× swing against a floor the carrier is holding
    // up. Silence does not keep this bin as the copied tone: a retarget is
    // measured from the station that is keying, or from the centre when
    // nothing is.
    private int SelectDecodeBin()
    {
        if (_searchLocked || _lockedIndex == _homeIndex)
            return _lockedIndex;

        if (!KeyedSinceArrival())
            return _homeIndex;

        // Two noisy blocks on a power walk clear 8×. Holding that bin
        // freezes the search off the station. Only a bin the reach search
        // actually chose is one silence should stay near.
        if (_candidateFromKeying)
            _acquiredIndex = _lockedIndex;
        return _lockedIndex;
    }

    // Same choice as SelectDecodeBin, without taking the acquisition hold.
    private int DecodeBin()
    {
        if (_searchLocked || _lockedIndex == _homeIndex)
            return _lockedIndex;
        if (!KeyedSinceArrival())
            return _homeIndex;
        return _lockedIndex;
    }

    // Swing after the search landed on this bin. The rise that made the bin
    // the candidate is not itself keying. A clean on/off still has to clear
    // 8× so a carrier the power walk sat on is not the tone: its leakage
    // never swings that hard. A tone whose key-up floor is that carrier
    // never clears 8× either. That one is accepted only while keying reach
    // is what selected the bin, and only when the swing clears the noise
    // floor the search already uses.
    private bool KeyedSinceArrival()
    {
        int look = _blocksOnLockedBin;
        if (look > _historyCount) look = _historyCount;
        if (look > _keyingMemoryBlocks) look = _keyingMemoryBlocks;
        if (look < 2) return false;
        // Past the pitch span, a bin is not the station until it has sat
        // here for a confirmation and actually keyed above the noise.
        if (WideSearch
            && (look < LockConfirmationBlocks || !KeyDownClearsNoise(_lockedIndex, look)))
            return false;
        if (IsKeyed(_lockedIndex, look)) return true;
        if (!_candidateFromKeying)
            return false;

        // The step that walked the search here is not keying. A carrier
        // shoulder clears the noise floor on that step alone. The swing
        // has to still be there after the step, which is a keyed tone
        // whose own floor is held up by the carrier.
        int settled = look - LockConfirmationBlocks;
        if (settled < 2) return false;
        double reach = KeyingReach(_lockedIndex, settled);
        if (reach <= NumericalReachGuard) return false;
        double amplitude = Math.Sqrt(Math.Max(_smoothed[_lockedIndex], NumericalPowerGuard));
        if (reach < amplitude * KeyedReachFraction) return false;
        return reach > AcquisitionReachFloor(settled);
    }

    // Unlocked and wider than the pitch span: an operator-clicked search.
    private bool WideSearch => !_searchLocked && _activeHalfSteps > PitchHalfSteps;

    // The bin's key-down level over the look, against the quiet end of the
    // bank's smoothed power. The high percentile is the key-down rail.
    private bool KeyDownClearsNoise(int candidate, int look)
    {
        if (look > _historyCount) look = _historyCount;
        if (look > _historyBlocks) look = _historyBlocks;
        if (look < 2) return false;
        double keyDown = _quantiles[candidate].Percentile(HighPercentile, look);
        double noise = Math.Max(NoiseFloor(_smoothed, _activeCount), NumericalPowerGuard);
        return keyDown > noise * WideKeyDownOverNoise;
    }

    // The search ranks a tone by its rail gap against the quiet end of the
    // bank. Carrier cancellation uses that same bar, so an off-centre
    // station does not have to swing 8× before the carrier is removed.
    private bool ClearsNoiseFloor(int candidate)
    {
        if (candidate < 0 || candidate >= _activeCount) return false;
        if (_haveReach)
        {
            double floor = Math.Max(
                NoiseFloor(_reaches, _activeCount) * ReachOverNoise,
                NumericalReachGuard);
            return _reaches[candidate] > floor;
        }

        int look = _historyCount < _keyingMemoryBlocks ? _historyCount : _keyingMemoryBlocks;
        if (look < 2) return false;
        return KeyingReach(candidate, look) > AcquisitionReachFloor(look);
    }

    private double AcquisitionReachFloor(int look)
    {
        if (_reachFloorBlock == _detectBlock && _reachFloorLook == look)
            return _reachFloor;

        int count = _activeCount;
        for (int candidate = 0; candidate < count; candidate++)
            _binScratch[candidate] = KeyingReach(candidate, look);
        HeapSort(_binScratch, count);
        double noise = Percentile(_binScratch, count, NoisePercentile);
        _reachFloor = Math.Max(noise * ReachOverNoise, NumericalReachGuard);
        _reachFloorBlock = _detectBlock;
        _reachFloorLook = look;
        return _reachFloor;
    }

    // Hann power of the decode bin, with a latched off-bin carrier taken
    // out. The scale makes a tone alone match the Hann power of that bin.
    private double DecodePower(ReadOnlySpan<float> samples, int toneBin)
    {
        int carrier = UpdateLatchedCarrier(toneBin);
        if (carrier < 0 || carrier == toneBin)
            return _instantPowers[toneBin];
        return CarrierNulledPower(samples, toneBin, carrier);
    }

    // A carrier is a steady peak beside the tone currently being decoded.
    // That tone is the tracked bin when the tracked bin is keying, and the
    // configured centre when tracking followed a steady carrier. The bin
    // being decoded is never the carrier. Steadiness is the long window's
    // low and high percentiles, not the min and max of single blocks: a
    // beat with the keyed tone dips individual blocks and would look like
    // keying. The hold is that same test again every block. A latched bin
    // that is no longer the steady peak is dropped.
    private int UpdateLatchedCarrier(int toneBin)
    {
        if (_activeCount < 2)
        {
            _latchedCarrier = -1;
            _carrierCandidate = -1;
            _carrierConfirm = 0;
            return -1;
        }

        ScoreCarrierWindow();
        if (_latchedCarrier >= 0
            && _latchedCarrier < _activeCount
            && _latchedCarrier != toneBin
            && IsSteadyCarrierPeak(_latchedCarrier)
            && _windowMedian[_latchedCarrier] > Math.Max(_tonePeak, NumericalPowerGuard) * CarrierHoldRatio)
        {
            return _latchedCarrier;
        }

        _latchedCarrier = -1;
        // The beside-tone threshold is what actually removes a carrier from
        // an off-centre station. It follows the noise-floor swing, not an
        // 8× ratio against this bin's own key-up floor: that floor is the
        // carrier's leakage, and the tone never clears 8×.
        double advantage = ClearsNoiseFloor(toneBin) ? CarrierBesideKeyedTone : CarrierPowerAdvantage;
        int loudest = -1;
        double loudestPower = Math.Max(_tonePeak, NumericalPowerGuard) * advantage;
        for (int candidate = 0; candidate < _activeCount; candidate++)
        {
            if (candidate == toneBin || !IsSteadyCarrierPeak(candidate))
                continue;
            double power = _windowMedian[candidate];
            if (power <= loudestPower) continue;
            loudestPower = power;
            loudest = candidate;
        }

        if (loudest < 0)
        {
            _carrierCandidate = -1;
            _carrierConfirm = 0;
            return -1;
        }

        if (loudest == _carrierCandidate)
            _carrierConfirm++;
        else
        {
            _carrierCandidate = loudest;
            _carrierConfirm = 1;
        }

        if (_carrierConfirm < CarrierConfirmBlocks) return -1;
        _latchedCarrier = loudest;
        return loudest;
    }

    // Percentile power over the keying memory. The low end is the robust
    // minimum. Keying puts that minimum on the key-up floor, so the high
    // percentile clears KeyedSwing. A carrier beating with the keyed tone
    // does not: the dip is a few blocks, and the median stays on the carrier.
    private void ScoreCarrierWindow()
    {
        int look = _historyCount < _keyingMemoryBlocks ? _historyCount : _keyingMemoryBlocks;
        for (int candidate = 0; candidate < _activeCount; candidate++)
        {
            if (look < 2)
            {
                _windowMedian[candidate] = 0;
                _carrierSteady[candidate] = false;
                continue;
            }

            double low = _quantiles[candidate].Percentile(LowPercentile, look);
            double median = _quantiles[candidate].Percentile(MedianPercentile, look);
            double high = _quantiles[candidate].Percentile(HighPercentile, look);
            _windowMedian[candidate] = median;
            _carrierSteady[candidate] = high <= Math.Max(low, NumericalPowerGuard) * KeyedSwing;
        }
    }

    // The lobe beside a carrier is almost as loud on a single block. The
    // long-window median is not. Only that peak is the carrier; a shoulder
    // that the beat has lifted for one block is left in place.
    private bool IsSteadyCarrierPeak(int candidate)
    {
        if ((uint)candidate >= (uint)_activeCount || !_carrierSteady[candidate])
            return false;
        double level = _windowMedian[candidate];
        if (candidate > 0 && _windowMedian[candidate - 1] > level) return false;
        if (candidate + 1 < _activeCount && _windowMedian[candidate + 1] > level) return false;
        return true;
    }

    // On/off swing of this bin over the keying memory. Two seconds covers a
    // long dah, so the tracked bin is not called quiet in the middle of one.
    // Carrier nomination does not use this min/max test.
    private bool IsKeyed(int candidate, int maxLook = int.MaxValue)
    {
        if (maxLook > _keyingMemoryBlocks) maxLook = _keyingMemoryBlocks;
        int look = _historyCount < maxLook ? _historyCount : maxLook;
        if (look < 2) return false;
        int origin = candidate * _historyBlocks;
        int index = _historyCursor;
        double min = double.PositiveInfinity;
        double max = 0;
        for (int age = 0; age < look; age++)
        {
            index--;
            if (index < 0) index += _historyBlocks;
            double power = _history[origin + index];
            if (power < min) min = power;
            if (power > max) max = power;
        }

        return max > Math.Max(min, NumericalPowerGuard) * KeyedSwing;
    }

    private double CarrierNulledPower(ReadOnlySpan<float> samples, int toneIndex, int carrierIndex)
    {
        EnsureCarrierKernel(toneIndex, carrierIndex);
        Correlate(samples, _frequenciesHz[toneIndex], out double toneReal, out double toneImag);
        Correlate(samples, _frequenciesHz[carrierIndex], out double carrierReal, out double carrierImag);
        double real = toneReal - ((_kernelReal * carrierReal) - (_kernelImag * carrierImag));
        double imag = toneImag - ((_kernelReal * carrierImag) + (_kernelImag * carrierReal));
        double power = ((real * real) + (imag * imag)) / (BlockSize * BlockSize) * _kernelScale;
        if (!double.IsFinite(power) || power < 0) return _instantPowers[toneIndex];
        return Math.Max(power, 1e-20);
    }

    private void EnsureCarrierKernel(int toneIndex, int carrierIndex)
    {
        if (_kernelTone == toneIndex && _kernelCarrier == carrierIndex) return;
        double toneHz = _frequenciesHz[toneIndex];
        double carrierHz = _frequenciesHz[carrierIndex];
        CorrelateSinusoid(toneHz, carrierHz, out double leakReal, out double leakImag);
        CorrelateSinusoid(carrierHz, carrierHz, out double selfReal, out double selfImag);
        double denominator = (selfReal * selfReal) + (selfImag * selfImag);
        if (denominator < 1e-30)
        {
            _kernelReal = 0;
            _kernelImag = 0;
            _kernelScale = 1;
        }
        else
        {
            _kernelReal = ((leakReal * selfReal) + (leakImag * selfImag)) / denominator;
            _kernelImag = ((leakImag * selfReal) - (leakReal * selfImag)) / denominator;
            CorrelateSinusoid(toneHz, toneHz, out double toneReal, out double toneImag);
            CorrelateSinusoid(carrierHz, toneHz, out double crossReal, out double crossImag);
            double remainReal = toneReal - ((_kernelReal * crossReal) - (_kernelImag * crossImag));
            double remainImag = toneImag - ((_kernelReal * crossImag) + (_kernelImag * crossReal));
            double tonePower = (toneReal * toneReal) + (toneImag * toneImag);
            double remainPower = (remainReal * remainReal) + (remainImag * remainImag);
            _kernelScale = remainPower > 1e-30 ? tonePower / remainPower : 1;
        }

        _kernelTone = toneIndex;
        _kernelCarrier = carrierIndex;
    }

    private void Correlate(ReadOnlySpan<float> samples, double frequencyHz, out double real, out double imag)
    {
        double omega = 2.0 * Math.PI * frequencyHz / _sampleRateHz;
        double cosine = Math.Cos(omega);
        double sine = Math.Sin(omega);
        double coefficient = cosine + cosine;
        double s1 = 0;
        double s2 = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            double s0 = (samples[i] * _window[i]) + (coefficient * s1) - s2;
            s2 = s1;
            s1 = s0;
        }

        real = s1 - (s2 * cosine);
        imag = s2 * sine;
    }

    private void CorrelateSinusoid(double measureHz, double sourceHz, out double real, out double imag)
    {
        double measure = 2.0 * Math.PI * measureHz / _sampleRateHz;
        double source = 2.0 * Math.PI * sourceHz / _sampleRateHz;
        double cosine = Math.Cos(measure);
        double sine = Math.Sin(measure);
        double coefficient = cosine + cosine;
        double s1 = 0;
        double s2 = 0;
        for (int i = 0; i < BlockSize; i++)
        {
            double sample = Math.Sin(source * i);
            double s0 = (sample * _window[i]) + (coefficient * s1) - s2;
            s2 = s1;
            s1 = s0;
        }

        real = s1 - (s2 * cosine);
        imag = s2 * sine;
    }

    private void Remember(int candidate, double power)
    {
        double smoothed = _smoothed[candidate];
        _smoothed[candidate] = _havePower
            ? smoothed + (PowerSmoothing * (power - smoothed))
            : power;
        _history[(candidate * _historyBlocks) + _historyCursor] = power;
        _quantiles[candidate].Push(power);
    }

    // The gap from the 10th to the 90th percentile of amplitude peaks on the
    // beat between a keyed tone and a carrier of the same strength: the bin
    // halfway between them swings farther than the tone. That beat is
    // symmetric about its median. Keying is not — the median sits on the
    // key-up or key-down rail — so the farther rail is the whole on/off range
    // and only half of the beat. Rank by that rail, smoothed so one keying
    // pattern cannot walk the lock onto the beat. It is an amplitude, so a
    // 0.01-peak tone ranks like a loud one. The floor is the quiet end of this
    // same bank. A carrier with no keying has no rail gap and falls through
    // to smoothed power.
    private int SelectCandidate()
    {
        _candidateFromKeying = false;
        if (_historyCount < _modulationBlocks)
            return HoldOrStrongest();

        for (int candidate = 0; candidate < _activeCount; candidate++)
        {
            double reach = KeyingReach(candidate);
            double previous = _reaches[candidate];
            _reaches[candidate] = _haveReach
                ? previous + (_reachSmoothing * (reach - previous))
                : reach;
        }

        _haveReach = true;

        double noiseReach = NoiseFloor(_reaches, _activeCount);
        int reachIndex = _lockedIndex;
        double bestReach = _reaches[_lockedIndex];
        for (int candidate = 0; candidate < _activeCount; candidate++)
        {
            double reach = _reaches[candidate];
            if (reach > bestReach)
            {
                bestReach = reach;
                reachIndex = candidate;
            }
        }

        double reachFloor = Math.Max(noiseReach * ReachOverNoise, NumericalReachGuard);
        if (bestReach <= reachFloor)
            return HoldOrStrongest();
        if (WideSearch
            && reachIndex != _lockedIndex
            && !KeyDownClearsNoise(reachIndex, _modulationBlocks))
            return HoldOrStrongest();
        _candidateFromKeying = true;
        double lockedReach = _reaches[_lockedIndex];
        if (reachIndex != _lockedIndex && bestReach <= lockedReach * ReachSwitchAdvantage)
            return _lockedIndex;
        return reachIndex;
    }

    // Nothing is keying. Until a station is acquired, power may walk the
    // search, which is how a steady carrier is found at startup. After that,
    // a challenger that is only louder than the noise stays put. A carrier
    // clear of that floor still walks the search.
    private int HoldOrStrongest()
    {
        int strongest = StrongestPower();
        if (strongest == _lockedIndex)
            return _lockedIndex;
        if (_acquiredIndex < 0 || _acquiredIndex != _lockedIndex)
            return strongest;

        double noisePower = NoiseFloor(_smoothed, _activeCount);
        if (_smoothed[strongest] <= Math.Max(noisePower, NumericalPowerGuard) * KeyedSwing)
            return _lockedIndex;
        return strongest;
    }

    private int StrongestPower()
    {
        int bestIndex = _lockedIndex;
        double bestPower = _smoothed[_lockedIndex];
        for (int candidate = 0; candidate < _activeCount; candidate++)
        {
            double power = _smoothed[candidate];
            if (power > bestPower)
            {
                bestPower = power;
                bestIndex = candidate;
            }
        }

        if (bestIndex == _lockedIndex) return _lockedIndex;
        double lockedPower = _smoothed[_lockedIndex];
        double noisePower = NoiseFloor(_smoothed, _activeCount);
        if (bestPower <= NumericalPowerGuard) return _lockedIndex;
        double overNoise = WideSearch ? WidePowerOverNoise : PowerOverNoise;
        if (bestPower <= noisePower * overNoise) return _lockedIndex;
        if (bestPower < lockedPower * PowerSwitchAdvantage) return _lockedIndex;
        return bestIndex;
    }

    private double NoiseFloor(double[] values, int count)
    {
        for (int i = 0; i < count; i++)
            _binScratch[i] = values[i];
        HeapSort(_binScratch, count);
        return Percentile(_binScratch, count, NoisePercentile);
    }

    private double KeyingReach(int candidate)
    {
        // The ring also keeps the longer keying memory. Reach stays on the
        // modulation window, the newest samples only.
        int count = _historyCount < _modulationBlocks ? _historyCount : _modulationBlocks;
        return KeyingReach(candidate, count);
    }

    private double KeyingReach(int candidate, int count)
    {
        if (count > _historyCount) count = _historyCount;
        if (count > _historyBlocks) count = _historyBlocks;
        if (count < 2) return 0;
        double low = Math.Sqrt(_quantiles[candidate].Percentile(LowPercentile, count));
        double mid = Math.Sqrt(_quantiles[candidate].Percentile(MedianPercentile, count));
        double high = Math.Sqrt(_quantiles[candidate].Percentile(HighPercentile, count));
        double above = high - mid;
        double below = mid - low;
        double reach = above > below ? above : below;
        return reach > 0 ? reach : 0;
    }

    private static double Percentile(double[] sorted, int count, double percentile)
    {
        double rank = (count - 1) * percentile;
        int low = (int)rank;
        int high = low + 1;
        if (high >= count) return sorted[low];
        double fraction = rank - low;
        return (sorted[low] * (1.0 - fraction)) + (sorted[high] * fraction);
    }

    private static int HistoryBlocksFor(int sampleRateHz)
    {
        int blocks = BlocksForSeconds(sampleRateHz, ModulationWindowSeconds);
        if (blocks < LockConfirmationBlocks) return LockConfirmationBlocks;
        return blocks;
    }

    private static int BlocksForSeconds(int sampleRateHz, double seconds)
    {
        int blocks = (int)Math.Round(
            seconds * sampleRateHz / BlockSize,
            MidpointRounding.AwayFromZero);
        if (blocks < 2) return 2;
        if (blocks > 4096) return 4096;
        return blocks;
    }

    private static int HalfStepsFor(double halfWidthHz)
    {
        if (halfWidthHz <= 0 || double.IsNaN(halfWidthHz)) return 0;
        int steps = (int)Math.Round(halfWidthHz / SearchStepHz, MidpointRounding.AwayFromZero);
        if (steps < 0) return 0;
        if (steps > MaxSearchHalfWidthSteps) return MaxSearchHalfWidthSteps;
        return steps;
    }

    private double DetectPowerAt(ReadOnlySpan<float> samples, double coefficient)
    {
        double s1 = 0;
        double s2 = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            double s0 = samples[i] * _window[i] + (coefficient * s1) - s2;
            s2 = s1;
            s1 = s0;
        }
        double power = s1 * s1 + s2 * s2 - coefficient * s1 * s2;
        return Math.Max(power / (BlockSize * BlockSize), 1e-20);
    }

    private void TrackStableCandidate(int bestIndex)
    {
        if (bestIndex != _lockedIndex)
        {
            if (bestIndex == _pendingIndex)
                _pendingBlocks++;
            else
            {
                _pendingIndex = bestIndex;
                _pendingBlocks = 1;
            }

            if (_pendingBlocks >= LockConfirmationBlocks)
            {
                _lockedIndex = _pendingIndex;
                _pendingIndex = -1;
                _pendingBlocks = 0;
                // This block is the first one on the new bin. The rise that
                // earned the move is older and must not count as keying.
                _blocksOnLockedBin = 1;
                // The bin we left is no longer the held station. The next
                // block picks the hold up only if this bin is keying, so a
                // walk onto noise or a steady carrier does not keep it.
                _acquiredIndex = -1;
                return;
            }
        }
        else
        {
            _pendingIndex = -1;
            _pendingBlocks = 0;
        }

        if (_blocksOnLockedBin < _keyingMemoryBlocks) _blocksOnLockedBin++;
    }

    private static void HeapSort(double[] values, int count)
    {
        for (int start = (count / 2) - 1; start >= 0; start--)
            SiftDown(values, start, count);
        for (int end = count - 1; end > 0; end--)
        {
            double root = values[0];
            values[0] = values[end];
            values[end] = root;
            SiftDown(values, 0, end);
        }
    }

    private static void SiftDown(double[] values, int start, int end)
    {
        while (true)
        {
            int child = (start * 2) + 1;
            if (child >= end) return;
            int swap = start;
            if (values[swap] < values[child]) swap = child;
            int right = child + 1;
            if (right < end && values[swap] < values[right]) swap = right;
            if (swap == start) return;
            double parent = values[start];
            values[start] = values[swap];
            values[swap] = parent;
            start = swap;
        }
    }
}
