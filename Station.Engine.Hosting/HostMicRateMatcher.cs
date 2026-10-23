// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Dsp.Wdsp;
namespace Zeus.Server;

internal interface IHostMicRateMatcher : IDisposable
{
    void Push(ReadOnlySpan<float> samples);
    void Pull(Span<float> samples);
    (int Underflows, int Overflows, double Ratio, int Capacity, int Occupancy) Diagnostics();
}
internal sealed class HostMicRateMatcher : IHostMicRateMatcher
{
    private readonly WdspRateMatcher _native = new();
    public void Push(ReadOnlySpan<float> samples) => _native.Push(samples);
    public void Pull(Span<float> samples) => _native.Pull(samples);
    public (int Underflows, int Overflows, double Ratio, int Capacity, int Occupancy) Diagnostics() => _native.Diagnostics();
    public void Dispose() => _native.Dispose();
}
