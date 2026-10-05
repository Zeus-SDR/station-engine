// SPDX-License-Identifier: GPL-2.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Zeus.Server.Diagnostics;

public static class EventLogLogging
{
    // On Windows the default host writes every Warning to the Application
    // event log. Zeus warnings, including the webview diagnostics the client
    // beacons, belong in zeus-app.log, which keeps the full trace and is what
    // "Submit an Issue" sends. The event log keeps only errors (issue 2701).
    //
    // The rule is keyed by the provider's alias, as appsettings keys it, so it
    // needs no reference to the Windows-only provider type and is a no-op on
    // other platforms. An alias rule ranks with the host default's full-name
    // rule, and the later rule wins.
    public const string ProviderAlias = "EventLog";
    public const LogLevel MinimumLevel = LogLevel.Error;

    public static void Configure(ILoggingBuilder logging) =>
        logging.Services.Configure<LoggerFilterOptions>(options =>
            options.Rules.Add(new LoggerFilterRule(
                ProviderAlias, categoryName: null, MinimumLevel, filter: null)));
}
