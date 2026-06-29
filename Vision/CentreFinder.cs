using System;
using System.Collections.Generic;

namespace MotorControlApp
{
    /// <summary>
    /// Accumulates rim points (USER-frame motor steps) and circle-fits them to a centre. Both the
    /// chuck centre-find and the wafer centre-find use one of these; they differ only in which edge
    /// detector feeds it and which CalibrationStore field the result persists to, so the point
    /// conversion + circle fit live here once instead of being duplicated per feature.
    ///
    /// A rim point is the MOTOR position that would bring the detected edge pixel onto the crosshair:
    ///   E = M + A·(p_cross − p_edge),  M = current motor (X,Y), A = pixel→step affine, p = (row,col).
    /// The circle through those points is centred on the feature centre, so the fit centre IS the
    /// motor position that puts the feature centre under the crosshair.
    /// </summary>
    public sealed class CentreFinder
    {
        private readonly List<(double X, double Y)> _points = new();

        public IReadOnlyList<(double X, double Y)> Points => _points;
        public int Count => _points.Count;
        public void Clear() => _points.Clear();

        /// <summary>Converts a detected edge pixel to a user-frame step point and stores it; returns it.</summary>
        public (double X, double Y) Add(
            double edgeRow, double edgeCol, double crossRow, double crossCol,
            PixelStepAffine a, long motorX, long motorY)
        {
            double dRow = crossRow - edgeRow, dCol = crossCol - edgeCol;
            double ex = motorX + a.Xr * dRow + a.Xc * dCol;
            double ey = motorY + a.Yr * dRow + a.Yc * dCol;
            _points.Add((ex, ey));
            return (ex, ey);
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
