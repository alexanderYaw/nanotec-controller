using System;
using System.Collections.Generic;

namespace NanotecController
{
    /// <summary>
    /// Least-squares circle fit (Pratt) to ≥3 rim points in motor-step space, giving the chuck
    /// centre. Pratt's B²+C²−4AD=1 constraint removes the small-radius/arc bias Kåsa suffers, so
    /// partial arcs fit without under-estimating the radius; solved by Newton on Pratt's
    /// characteristic polynomial (Chernov's PrattNewton), needing no SVD. Derivation and method
    /// comparison: Developer Guide, ChuckCenterFindingAnalysis.md §3-4.
    /// </summary>
    public static class CircleFit
    {
        private const double CollinearTolerance = 1e-6;
        private const double SingularDetTolerance = 1e-12;
        private const double NewtonConvergence = 1e-12;
        private const int NewtonMaxIterations = 99;

        public readonly record struct Result(double CenterX, double CenterY, double Radius, double RmsError);

        /// <summary>Fits a circle to <paramref name="points"/>; false (with <paramref name="error"/>)
        /// for &lt;3 points or a collinear/degenerate set. RmsError is the RMS distance of the points
        /// from the fitted circle, in input units — small means they really do lie on a circle.</summary>
        public static bool TryFit(IReadOnlyList<(double X, double Y)> points, out Result result, out string? error)
        {
            result = default;
            error = null;

            int n = points.Count;
            if (n < 3) { error = $"Need at least 3 edge points (have {n})."; return false; }

            // Centre the data: conditions the moment matrix, and Σu = Σv = 0 drops those moments.
            double mx = 0, my = 0;
            foreach ((double X, double Y) p in points) { mx += p.X; my += p.Y; }
            mx /= n; my /= n;

            // Moments over u = x-mx, v = y-my, z = u²+v², normalised by n.
            double Muu = 0, Mvv = 0, Muv = 0, Muz = 0, Mvz = 0, Mzz = 0;
            foreach ((double X, double Y) p in points)
            {
                double u = p.X - mx, v = p.Y - my, z = u * u + v * v;
                Muu += u * u; Mvv += v * v; Muv += u * v;
                Muz += u * z; Mvz += v * z; Mzz += z * z;
            }
            Muu /= n; Mvv /= n; Muv /= n; Muz /= n; Mvz /= n; Mzz /= n;

            double Mz = Muu + Mvv;                 // mean of z
            double covUV = Muu * Mvv - Muv * Muv;  // spans 2D only if the points are not collinear

            if (Muu <= 0 || Mvv <= 0 || covUV <= CollinearTolerance * Muu * Mvv)
            {
                error = "Edge points are collinear — spread the captures around the rim.";
                return false;
            }

            // Pratt's characteristic polynomial P(x) = A0 + A1·x + A2·x² + 4·x⁴; its smallest
            // non-negative root is the eigenvalue giving the Pratt-constrained fit.
            double Muz2 = Muz * Muz, Mvz2 = Mvz * Mvz;
            double A2 = 4 * covUV - 3 * Mz * Mz - Mzz;
            double A1 = Mzz * Mz + 4 * covUV * Mz - Muz2 - Mvz2 - Mz * Mz * Mz;
            double A0 = Muz2 * Mvv + Mvz2 * Muu - Mzz * covUV - 2 * Muz * Mvz * Muv + Mz * Mz * covUV;
            double A22 = A2 + A2;

            double x = 0, yPrev = double.MaxValue;
            for (int iter = 0; iter < NewtonMaxIterations; iter++)
            {
                double y = A0 + x * (A1 + x * (A2 + 4 * x * x));
                if (Math.Abs(y) >= Math.Abs(yPrev)) break;   // no longer improving
                yPrev = y;
                double dy = A1 + x * (A22 + 16 * x * x);
                if (dy == 0) break;
                double xNew = x - y / dy;
                if (xNew < 0) { x = 0; break; }              // Pratt's root is non-negative
                if (Math.Abs(xNew - x) < NewtonConvergence * Math.Max(1, Math.Abs(xNew))) { x = xNew; break; }
                x = xNew;
            }

            double det = x * x - x * Mz + covUV;
            if (Math.Abs(det) < SingularDetTolerance) { error = "Degenerate point set (singular fit)."; return false; }

            double cu = (Muz * (Mvv - x) - Mvz * Muv) / det / 2;
            double cv = (Mvz * (Muu - x) - Muz * Muv) / det / 2;
            double r2 = cu * cu + cv * cv + Mz + 2 * x;
            if (r2 <= 0) { error = "Degenerate fit (non-positive radius)."; return false; }

            double radius = Math.Sqrt(r2);
            double cx = cu + mx, cy = cv + my;   // back out of the centred frame

            double sse = 0;
            foreach ((double X, double Y) p in points)
            {
                double dist = Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy)) - radius;
                sse += dist * dist;
            }
            result = new Result(cx, cy, radius, Math.Sqrt(sse / n));
            return true;
        }
    }
}
