// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.InteropServices;
namespace Zeus.Dsp.Wdsp;

/// <summary>Single-worker owner of the same adaptive clock bridge used by Thetis VAC.</summary>
public sealed class WdspRateMatcher : IDisposable
{
    public const int BlockSamples = 64;
    private IntPtr _handle;
    private readonly double[] _buffer = new double[2 * BlockSamples];
    public WdspRateMatcher()
    {
        WdspNativeLoader.EnsureResolverRegistered();
        _handle = Create(BlockSamples, BlockSamples, 48000, 48000, 2048, 1.0);
        if (_handle == IntPtr.Zero) throw new InvalidOperationException("WDSP rate matcher allocation failed");
    }
    public void Push(ReadOnlySpan<float> samples)
    {
        ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
        if (samples.Length != BlockSamples) throw new ArgumentException("Expected 64 samples", nameof(samples));
        for (int i = 0; i < BlockSamples; i++)
        {
            _buffer[2 * i] = samples[i];
            _buffer[2 * i + 1] = 0;
        }
        Input(_handle, _buffer);
    }
    public void Pull(Span<float> samples)
    {
        ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
        if (samples.Length != BlockSamples) throw new ArgumentException("Expected 64 samples", nameof(samples));
        Output(_handle, _buffer);
        for (int i = 0; i < BlockSamples; i++) samples[i] = (float)_buffer[2 * i];
    }
    public (int Underflows, int Overflows, double Ratio, int Capacity, int Occupancy) Diagnostics()
    {
        ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
        Diags(_handle, out var under, out var over, out var ratio, out var cap, out var count);
        return (under, over, ratio, cap, count);
    }
    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        Destroy(_handle);
        _handle = IntPtr.Zero;
    }
    [DllImport("wdsp", EntryPoint = "create_rmatchV", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr Create(int input, int output, int inputRate, int outputRate, int ring, double ratio);
    [DllImport("wdsp", EntryPoint = "destroy_rmatchV", CallingConvention = CallingConvention.Cdecl)]
    private static extern void Destroy(IntPtr handle);
    [DllImport("wdsp", EntryPoint = "xrmatchIN", CallingConvention = CallingConvention.Cdecl)]
    private static extern void Input(IntPtr handle, double[] samples);
    [DllImport("wdsp", EntryPoint = "xrmatchOUT", CallingConvention = CallingConvention.Cdecl)]
    private static extern void Output(IntPtr handle, [Out] double[] samples);
    [DllImport("wdsp", EntryPoint = "getRMatchDiags", CallingConvention = CallingConvention.Cdecl)]
    private static extern void Diags(IntPtr handle, out int under, out int over, out double ratio, out int cap, out int count);
}
