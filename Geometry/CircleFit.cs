using System;
using System.Collections.Generic;

namespace NanotecController
{
    /// <summary>
    /// Least-squares circle fit (algebraic / Pratt method) to ≥3 points in a plane. Used to find
    /// the chuck centre from edge points expressed in motor-step space: each edge point is a
    /// point on the chuck rim, so the circle through them is centred on the chuck centre.
    ///
    /// Pratt fixes the arbitrary scale of the algebraic circle A(x²+y²)+Bx+Cy+D=0 with the
    /// constraint B²+C²−4AD=1, which keeps the algebraic error close to the true geometric error.
    /// Unlike Kåsa (A=1) this removes the small-radius / arc bias, so partial arcs and noisy edge
    /// points are fit without systematically under-estimating the radius. Solved here by Newton's
    /// method on Pratt's characteristic polynomial (Chernov's PrattNewton) — needs only the 4×4
    /// moment matrix, no SVD or eigen-decomposition.
    ///
    /// Exact for 3 non-collinear points; averages noise for more (so the centre-find can take more
    /// than 3 captures). Rejects fewer than 3 points or collinear/coincident sets — a line has no
    /// unique circle, which is why the captures must be spread around the rim.
    /// </summary>
    public static class CircleFit
    {
        public readonly record struct Result(double CenterX, double CenterY, double Radius, double RmsError);

        /// <summary>
        /// Fits a circle to <paramref name="points"/>. Returns false (with <paramref name="error"/>)
        /// for &lt;3 points or a collinear/degenerate set. <paramref name="result"/>.RmsError is the
        /// RMS distance of the points from the fitted circle, in the same units as the input — small
        /// means they really lie on a circle (clean edge points), large means something is off.
        /// </summary>
        public static bool TryFit(IReadOnlyList<(double X, double Y)> points, out Result result, out string? error)
        {
            result = default;
            error = null;

            int n = points.Count;
            if (n < 3) { error = $"Need at least 3 edge points (have {n})."; return false; }

            // Centre the data (improves conditioning and makes the moment matrix well-scaled).
            double mx = 0, my = 0;
            foreach ((double X, double Y) p in points) { mx += p.X; my += p.Y; }
            mx /= n; my /= n;

            // Moments over centred coordinates u = x-mx, v = y-my, with z = u²+v², normalised by n.
            // Because the data is centred, Σu = Σv = 0, so those moments drop out of the system.
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

            // Collinearity guard: the centred covariance must span 2D.
            if (Muu <= 0 || Mvv <= 0 || covUV <= 1e-6 * Muu * Mvv)
            {
                error = "Edge points are collinear — spread the captures around the rim.";
                return false;
            }

            // Pratt's characteristic polynomial P(x) = A0 + A1·x + A2·x² + 4·x⁴ (Chernov). Its
            // smallest non-negative root is the eigenvalue that gives the Pratt-constrained fit.
            double Muz2 = Muz * Muz, Mvz2 = Mvz * Mvz;
            double A2 = 4 * covUV - 3 * Mz * Mz - Mzz;
            double A1 = Mzz * Mz + 4 * covUV * Mz - Muz2 - Mvz2 - Mz * Mz * Mz;
            double A0 = Muz2 * Mvv + Mvz2 * Muu - Mzz * covUV - 2 * Muz * Mvz * Muv + Mz * Mz * covUV;
            double A22 = A2 + A2;

            // Newton iteration for that root, starting from x = 0.
            double x = 0, yPrev = double.MaxValue;
            for (int iter = 0; iter < 99; iter++)
            {
                double y = A0 + x * (A1 + x * (A2 + 4 * x * x));
                if (Math.Abs(y) >= Math.Abs(yPrev)) break;   // no longer improving — stop
                yPrev = y;
                double dy = A1 + x * (A22 + 16 * x * x);
                if (dy == 0) break;
                double xNew = x - y / dy;
                if (xNew < 0) { x = 0; break; }               // Pratt's root is non-negative
                if (Math.Abs(xNew - x) < 1e-12 * Math.Max(1, Math.Abs(xNew))) { x = xNew; break; }
                x = xNew;
            }

            double det = x * x - x * Mz + covUV;
            if (Math.Abs(det) < 1e-12) { error = "Degenerate point set (singular fit)."; return false; }

            // Centre in the centred frame, then radius (both from Chernov's closed form).
            double cu = (Muz * (Mvv - x) - Mvz * Muv) / det / 2;
            double cv = (Mvz * (Muu - x) - Muz * Muv) / det / 2;
            double r2 = cu * cu + cv * cv + Mz + 2 * x;
            if (r2 <= 0) { error = "Degenerate fit (non-positive radius)."; return false; }

            double radius = Math.Sqrt(r2);
            double cx = cu + mx, cy = cv + my;   // shift centre back out of the centred frame

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
