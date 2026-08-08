// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.RegularExpressions;

namespace Zeus.Server;

/// <summary>
/// Identifies common virtual-audio cable endpoints from their public device
/// names. Matching is deliberately name-only: miniaudio exposes a stable id,
/// display name, and default flag, but no portable vendor identifier.
/// </summary>
internal static partial class VirtualAudioCableCatalog
{
    private const string VbCableInstallUrl = "https://vb-audio.com/Cable/";
    private const string VacInstallUrl = "https://vac.muzychenko.net/en/download.htm";

    public static VirtualAudioCableMatchDto? Match(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return null;

        // VAC documents Windows endpoint names such as
        // "Line 1 (Virtual Audio Cable)". Keep this ahead of the generic
        // CABLE pattern so the two similarly named products never collide.
        if (VirtualAudioCablePattern().IsMatch(deviceName))
        {
            return new VirtualAudioCableMatchDto(
                Vendor: "Eugene Muzychenko",
                Product: "Virtual Audio Cable",
                InstallUrl: VacInstallUrl);
        }

        // VB-Audio documents CABLE Input / CABLE Output, with optional A-D
        // cable suffixes. Some Windows APIs append the driver name in
        // parentheses, so accept that official vendor text as well.
        if (VbAudioCablePattern().IsMatch(deviceName))
        {
            return new VirtualAudioCableMatchDto(
                Vendor: "VB-Audio",
                Product: "VB-CABLE",
                InstallUrl: VbCableInstallUrl);
        }

        return null;
    }

    [GeneratedRegex(
        @"(?:\bLine\s+\d+\s*\(Virtual Audio Cable\)|\bVirtual Audio Cable\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VirtualAudioCablePattern();

    [GeneratedRegex(
        @"(?:^|\s)(?:CABLE(?:-[A-D])?|Hi-Fi Cable)\s+(?:Input|Output)(?:\s|$)|\bVB-Audio\b.*\bCABLE\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VbAudioCablePattern();
}
