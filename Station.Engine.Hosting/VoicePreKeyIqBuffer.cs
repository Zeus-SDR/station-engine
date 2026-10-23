// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Server;

/// <summary>Bounded IQ retention while the amplifier's pre-key gate is closed.</summary>
internal sealed class VoicePreKeyIqBuffer
{
    private float[] _samples = [];
    private int _read, _count;
    private bool _started;
    internal int Count => _count;
    internal bool Failed { get; private set; }

    internal void Configure(int capacityComponents)
    {
        if (capacityComponents <= 0 || (capacityComponents & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(capacityComponents));
        if (_samples.Length < capacityComponents) _samples = new float[capacityComponents];
        Reset();
    }

    internal void Reset()
    {
        _read = _count = 0;
        _started = false;
        Failed = false;
    }

    internal bool Render(Span<float> iq, bool muted)
    {
        if ((iq.Length & 1) != 0) throw new ArgumentException("IQ must contain complete pairs", nameof(iq));
        if (Failed) { iq.Clear(); return false; }
        if (muted)
        {
            int skip = 0;
            if (!_started)
            {
                // Only exact leading zero pairs can be discarded. Quiet
                // consonants, denormals and invalid components are not silence.
                while (skip < iq.Length && iq[skip] == 0f && iq[skip + 1] == 0f) skip += 2;
                _started = skip < iq.Length;
            }
            bool accepted = Append(iq[skip..]);
            iq.Clear();
            return accepted;
        }
        if (_count == 0) return true;
        if (!Append(iq)) { iq.Clear(); return false; }
        Read(iq);
        return true;
    }

    private bool Append(ReadOnlySpan<float> iq)
    {
        if (iq.IsEmpty) return true;
        if (iq.Length > _samples.Length - _count)
        {
            // A gate that never opens must not overwrite the opening word or
            // grow without bound. Stay silent until the next explicit epoch.
            Failed = true;
            _count = 0;
            return false;
        }
        int write = (_read + _count) % _samples.Length;
        int first = Math.Min(iq.Length, _samples.Length - write);
        iq[..first].CopyTo(_samples.AsSpan(write));
        iq[first..].CopyTo(_samples);
        _count += iq.Length;
        return true;
    }

    internal int Read(Span<float> output)
    {
        int count = Math.Min(output.Length, _count);
        if (count == 0) return 0;
        int first = Math.Min(count, _samples.Length - _read);
        _samples.AsSpan(_read, first).CopyTo(output);
        _samples.AsSpan(0, count - first).CopyTo(output[first..]);
        _read = (_read + count) % _samples.Length;
        _count -= count;
        return count;
    }
}
