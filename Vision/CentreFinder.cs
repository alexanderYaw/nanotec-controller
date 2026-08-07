using System;
using System.Collections.Generic;

namespace NanotecController
{
    /// <summary>
    /// Accumulates rim points (USER-frame motor steps) and circle-fits them to a centre. The chuck and
    /// wafer centre-finds both use one, differing only in which edge detector feeds it and which
    /// <see cref="CalibrationStore"/> field the result persists to.
    ///
    /// A rim point is the MOTOR position that would bring the detected edge pixel onto the crosshair:
    /// E = M + A·(p_cross − p_edge). The circle through those points is centred on the feature
    /// centre, so the fit centre IS the motor position that puts that centre under the crosshair.
    /// </summary>
    public sealed class CentreFinder
    {
        private readonly List<(double X, double Y)> _points = new();

        public IReadOnlyList<(double X, double Y)> Points => _points;
        public int Count => _points.Count;
        public void Clear() => _points.Clear();

        /// <summary>Converts a detected edge pixel to the user-frame step point WITHOUT storing it.
        /// Separate from <see cref="Add"/> because the auto centre-find sanity-checks a candidate
        /// before deciding whether it belongs in the set, and that gate must not re-derive this.</summary>
        public static (double X, double Y) ToStepPoint(
            double edgeRow, double edgeCol, double crossRow, double crossCol,
            PixelStepAffine a, long motorX, long motorY)
        {
            double dRow = crossRow - edgeRow, dCol = crossCol - edgeCol;
            return (motorX + a.Xr * dRow + a.Xc * dCol,
                    motorY + a.Yr * dRow + a.Yc * dCol);
        }

        /// <summary>Converts a detected edge pixel to a user-frame step point and stores it; returns it.</summary>
        public (double X, double Y) Add(
            double edgeRow, double edgeCol, double crossRow, double crossCol,
            PixelStepAffine a, long motorX, long motorY)
        {
            (double X, double Y) e = ToStepPoint(edgeRow, edgeCol, crossRow, crossCol, a, motorX, motorY);
            _points.Add(e);
            return e;
        }

        /// <summary>Stores a rim point directly in user-frame steps. Used when the edge was jogged
        /// onto the crosshair by eye, so the point IS the current motor position (p_edge = p_cross ⇒
        /// E = M) and no pixel→step conversion is needed. Returns the stored point.</summary>
        public (double X, double Y) AddPoint(double x, double y)
        {
            _points.Add((x, y));
            return (x, y);
        }

        /// <summary>Removes the stored point at <paramref name="index"/>; out-of-range is ignored.</summary>
        public void RemoveAt(int index)
        {
            if (index >= 0 && index < _points.Count) _points.RemoveAt(index);
        }

        /// <summary>Circle-fits the stored points (centre rounded to whole steps). False (+error) for
        /// &lt;3 points or a degenerate set.</summary>
        public bool TryComputeCentre(out long centreX, out long centreY, out CircleFit.Result fit, out string? error)
        {
            centreX = centreY = 0;
            if (!CircleFit.TryFit(_points, out fit, out error)) return false;
            centreX = (long)Math.Round(fit.CenterX);
            centreY = (long)Math.Round(fit.CenterY);
            return true;
        }
    }
}
