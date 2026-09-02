// SPDX-License-Identifier: GPL-2.0-or-later
// Vallado/CSSI SGP4 and SDP4 facade. The numerical core follows Revisiting Spacetrack Report #3.

#if ZEUS_PRODUCT_HOST
namespace Zeus.Product.Hosting.Satellites;
#else
namespace Zeus.Server.Satellites;
#endif

public sealed class Sgp4Propagator
{
    private readonly TwoLineElement _elements;

    public Sgp4Propagator(TwoLineElement elements) => _elements = elements;
    public double MeanMotionRevolutionsPerDay => _elements.MeanMotionRevolutionsPerDay;
    public bool IsDeepSpace => 1440d / _elements.MeanMotionRevolutionsPerDay >= 225d;

    public TemeState Propagate(DateTimeOffset utc)
    {
        var minutes = (utc.ToUniversalTime() - _elements.EpochUtc).TotalMinutes;
        // Vallado's ElsetRec contains deep-space integration scratch fields.
        // Every facade for this TLE must lock the shared record, not itself.
        lock (_elements.Vallado)
        {
            try
            {
                var rv = _elements.Vallado.getRV(minutes);
                var error = _elements.Vallado.getSgp4Error();
                if (error != 0) throw new InvalidOperationException($"SGP4 propagation failed with code {error}.");
                return new TemeState(rv[0][0], rv[0][1], rv[0][2], rv[1][0], rv[1][1], rv[1][2]);
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException)
            {
                throw new InvalidOperationException("SGP4 propagation failed for a degenerate element set.", ex);
            }
        }
    }
}
