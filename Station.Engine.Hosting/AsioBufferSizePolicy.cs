// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Server;

internal static class AsioBufferSizePolicy
{
    public static void Validate(int frames, AsioDriverProbe probe)
    {
        if (frames == 0) return; // Preserve the driver's preferred buffer.
        long size = frames;
        bool supported = size >= probe.BufferMinFrames && size <= probe.BufferMaxFrames
            && (probe.BufferGranularity switch
            {
                -1 => (frames & (frames - 1)) == 0,
                0 => size == probe.BufferPreferredFrames,
                > 0 => (size - probe.BufferMinFrames) % probe.BufferGranularity == 0,
                _ => false,
            });
        if (!supported)
            throw new InvalidOperationException("The requested ASIO buffer size is not supported by this driver. Select a supported size or driver default.");
    }
}
