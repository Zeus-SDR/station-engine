// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Server;

/// <summary>The native analyzer can retain an unread frame across a TX edge.
/// Only the preview discards that first fresh flag; existing display readers keep it.</summary>
public sealed class TxSpectrumEpochPrimer
{
    private readonly object _sync = new();
    private object? _context;

    public void Reset() { lock (_sync) _context = null; }

    public bool Accept(object context, bool nativeFresh)
    {
        lock (_sync)
        {
            if (!nativeFresh) return false;
            if (Equals(context, _context)) return true;
            _context = context;
            return false;
        }
    }
}
