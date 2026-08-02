// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;
using Zeus.Protocol1.Discovery;

namespace Zeus.Server;

internal sealed record P1ConnectionAttempt(
    string Endpoint,
    int SampleRate,
    HpsdrBoardKind DiscoveredKind,
    string? Firmware,
    PhysicalAddress? Mac,
    long Generation,
    CancellationToken OperatorActionToken,
    bool IsAutomaticRetry);

internal sealed class P1StartFailureRecovery
{
    private readonly IRadioDiscovery _discovery;
    private readonly ILogger _log;
    private readonly TimeSpan _timeout;

    public P1StartFailureRecovery(
        IRadioDiscovery discovery,
        ILogger<P1StartFailureRecovery> log,
        TimeSpan? timeout = null)
    {
        _discovery = discovery;
        _log = log;
        _timeout = timeout ?? TimeSpan.FromMilliseconds(1500);
    }

    internal async Task HandleAsync(
        bool startHandshakeFailed,
        long framesDelivered,
        P1ConnectionAttempt failedAttempt,
        Func<P1ConnectionAttempt, CancellationToken, Task> retry,
        Func<bool> isCurrentOperatorAction,
        CancellationToken cancellationToken)
    {
        if (!startHandshakeFailed)
            return;

        // A session that ever delivered a frame genuinely started — even if the
        // handshake watchdog had already given up when the first frame landed.
        // Its later death is a mid-session teardown, not an initial-start
        // failure, and must not trigger rediscovery or an automatic retry.
        if (framesDelivered > 0)
            return;

        if (!CanContinue())
            return;

        if (failedAttempt.IsAutomaticRetry)
        {
            _log.LogWarning(
                "p1.start.rediscover automatic retry also failed at {Endpoint}; no further retries will be attempted",
                failedAttempt.Endpoint);
            return;
        }

        if (!RadioService.TryParseEndpoint(failedAttempt.Endpoint, out var originalEndpoint))
            return;

        IReadOnlyList<DiscoveredRadio> radios;
        try
        {
            radios = await _discovery.DiscoverAsync(_timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            if (!CanContinue())
                return;
            _log.LogWarning(
                ex,
                "p1.start.rediscover post-mortem discovery failed for {Endpoint}; power-cycle the radio and check the network link",
                failedAttempt.Endpoint);
            return;
        }

        if (!CanContinue())
            return;

        var sameEndpoint = radios.FirstOrDefault(radio => radio.Ip.Equals(originalEndpoint.Address));
        var matchingIdentity = HasMac(failedAttempt.Mac)
            ? radios.FirstOrDefault(radio =>
                radio.Mac.Equals(failedAttempt.Mac)
                && radio.Ip.Equals(originalEndpoint.Address))
              ?? radios.FirstOrDefault(radio => radio.Mac.Equals(failedAttempt.Mac))
            : null;

        DiscoveredRadio? moved = null;
        if (matchingIdentity is not null && !matchingIdentity.Ip.Equals(originalEndpoint.Address))
        {
            moved = matchingIdentity;
        }
        else if (!HasMac(failedAttempt.Mac)
                 && sameEndpoint is null
                 && failedAttempt.DiscoveredKind != HpsdrBoardKind.Unknown)
        {
            var sameBoardElsewhere = radios
                .Where(radio => radio.Board == failedAttempt.DiscoveredKind
                                && !radio.Ip.Equals(originalEndpoint.Address))
                .Take(2)
                .ToArray();
            if (sameBoardElsewhere.Length == 1)
                moved = sameBoardElsewhere[0];
        }

        if (moved is null)
        {
            if (sameEndpoint is not null)
            {
                _log.LogWarning(
                    "p1.start.rediscover radio still answers discovery at {Endpoint} but never started its EP6 stream; " +
                    "a host firewall may be blocking the incoming data stream or the radio may be wedged. " +
                    "Power-cycle the radio and check host firewall rules",
                    failedAttempt.Endpoint);
            }
            else
            {
                _log.LogWarning(
                    "p1.start.rediscover radio no longer answers discovery at {Endpoint}; power-cycle the radio and check the network link",
                    failedAttempt.Endpoint);
            }
            return;
        }

        if (!CanContinue())
            return;

        var newEndpoint = new IPEndPoint(moved.Ip, originalEndpoint.Port).ToString();
        _log.LogWarning(
            "p1.start.rediscover radio moved {OldEndpoint} -> {NewEndpoint}; retrying connection once",
            failedAttempt.Endpoint,
            newEndpoint);

        try
        {
            await retry(
                failedAttempt with
                {
                    Endpoint = newEndpoint,
                    IsAutomaticRetry = true,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer operator action owns the connection now.
        }
        catch (Exception ex)
        {
            if (!CanContinue())
                return;
            _log.LogWarning(
                ex,
                "p1.start.rediscover automatic retry also failed at {Endpoint}; no further retries will be attempted",
                newEndpoint);
        }

        bool CanContinue() =>
            !cancellationToken.IsCancellationRequested && isCurrentOperatorAction();
    }

    private static bool HasMac(PhysicalAddress? mac) =>
        mac is not null && mac.GetAddressBytes().Length != 0;
}
