// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Server;

/// <summary>Body of the PureSignal-feedback IMD route: the tones' spacing only.</summary>
public sealed record ImdFeedbackMeasureRequest
{
    public double? ExpectedToneSpacingHz { get; init; }
}

/// <summary>Maps the engine-owned two-tone IMD measurement route.</summary>
public static class ImdMeasureEndpoints
{
    internal const string WaitingForFeedback = "waiting for amp feedback";

    public static IEndpointRouteBuilder MapImdMeasure(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/dsp/imd-measure", (
            ImdMeasureRequest request,
            ImdMeasureService service) => Results.Ok(service.Measure(request)));

        // Measures the engine's own PureSignal feedback spectrum rather than a
        // spectrum the client sends: the amplifier output on a transmitter
        // whose feedback arrives through the external pair intake.
        endpoints.MapPost("/api/dsp/imd-measure/ps-feedback", (
            ImdFeedbackMeasureRequest request,
            ImdMeasureService service,
            DspPipelineService pipeline) => Results.Ok(MeasurePsFeedback(request, service, pipeline)));

        return endpoints;
    }

    internal static ImdMeasureResult MeasurePsFeedback(
        ImdFeedbackMeasureRequest request,
        ImdMeasureService service,
        DspPipelineService pipeline) =>
        pipeline.TryGetPsFeedbackImdInput(request.ExpectedToneSpacingHz, out var input)
            ? service.Measure(input)
            : new ImdMeasureResult { Ok = false, Reason = WaitingForFeedback };
}
