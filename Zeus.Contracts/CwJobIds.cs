// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.

namespace Zeus.Contracts;

/// <summary>
/// Opaque id for one CW send. A canonical UUID, 36 ASCII characters.
/// Omitted or empty means the legacy send with no identity. Anything else
/// is rejected and is not treated as a global abort.
/// </summary>
public static class CwJobIds
{
    public const int MaxLength = 36;

    public static bool IsValid(string? id)
    {
        if (id is null || id.Length != MaxLength) return false;
        for (int i = 0; i < id.Length; i++)
        {
            char c = id[i];
            if (i is 8 or 13 or 18 or 23)
            {
                if (c != '-') return false;
                continue;
            }
            bool hex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!hex) return false;
        }
        return true;
    }
}
