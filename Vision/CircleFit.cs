using System;
using System.Collections.Generic;

namespace MotorControlApp
{
    /// <summary>
    /// Least-squares circle fit (algebraic / Kåsa method) to ≥3 points in a plane. Used to find
    /// the chuck centre from edge points expressed in motor-step space: each edge point is a
    /// point on the chuck rim, so the circle through them is centred on the chuck centre.
    ///
    /// Exact for 3 non-collinear points; averages noise for more (so the centre-find can take
    /// more than 3 captures). Rejects fewer than 3 points or collinear/coincident sets — a line
    /// has no unique circle, which is why the captures must be spread around the rim.
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

            // Centre the data (improves conditioning and makes the collinearity test meaningful).
            double mx = 0, my = 0;
            foreach ((double X, double Y) p in points) { mx += p.X; my += p.Y; }
            mx /= n; my /= n;

            // Sums over the centred coordinates u = x-mx, v = y-my, with z = u²+v².
            double Suu = 0, Suv = 0, Svv = 0, Su = 0, Sv = 0, Suz = 0, Svz = 0, Sz = 0;
            foreach ((double X, double Y) p in points)
            {
                double u = p.X - mx, v = p.Y - my, z = u * u + v * v;
                Suu += u * u; Suv += u * v; Svv += v * v; Su += u; Sv += v;
                Suz += u * z; Svz += v * z; Sz += z;
            }

            // Collinearity guard: the centred pixel covariance must span 2D.
            if (Suu <= 0 || Svv <= 0 || Suu * Svv - Suv * Suv <= 1e-6 * Suu * Svv)
            {
                error = "Edge points are collinear — spread the captures around the rim.";
                return false;
            }

            // Solve [[Suu,Suv,Su],[Suv,Svv,Sv],[Su,Sv,n]]·[D;E;F] = -[Suz;Svz;Sz].
            double[,] m =
            {
                { Suu, Suv, Su, -Suz },
                { Suv, Svv, Sv, -Svz },
                { Su,  Sv,  n,  -Sz  },
            };
            if (!Solve3(m, out double d, out double e, out double f))
            {
                error = "Degenerate point set (singular fit).";
                return false;
            }

            double cu = -d / 2, cv = -e / 2;
            double r2 = cu * cu + cv * cv - f;
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

        // Gaussian elimination with partial pivoting on an augmented 3×4 matrix. False if singular.
        private static bool Solve3(double[,] m, out double x0, out double x1, out double x2)
        {
            x0 = x1 = x2 = 0;
            for (int col = 0; col < 3; col++)
            {
                int piv = col;
                for (int r = col + 1; r < 3; r++)
                    if (Math.Abs(m[r, col]) > Math.Abs(m[piv, col])) piv = r;
                if (Math.Abs(m[piv, col]) < 1e-12) return false;
                if (piv != col)
                    for (int c = 0; c < 4; c++) (m[col, c], m[piv, c]) = (m[piv, c], m[col, c]);
                for (int r = 0; r < 3; r++)
                {
                    if (r == col) continue;
                    double factor = m[r, col] / m[col, col];
                    for (int c = col; c < 4; c++) m[r, c] -= factor * m[col, c];
                }
            }
            x0 = m[0, 3] / m[0, 0];
            x1 = m[1, 3] / m[1, 1];
            x2 = m[2, 3] / m[2, 2];
            return true;
        }
    }
}
