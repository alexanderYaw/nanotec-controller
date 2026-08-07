using System;
using System.Collections.Generic;

namespace NanotecController
{
    /// <summary>
    /// Builds the pixel→step affine from manually-captured samples, each pairing a detected fiducial
    /// pixel with the motor position the frame was grabbed at. Fits steps as a linear function of
    /// pixels by least squares:
    ///
    ///     X = Xr·row + Xc·col + eX
    ///     Y = Yr·row + Yc·col + eY
    ///
    /// The slopes ARE the steps-per-pixel matrix (scale + camera/stage rotation). The offsets are fit
    /// but discarded, since only displacements are used downstream. Needs ≥3 samples spanning BOTH
    /// axes; collinear samples are rejected.
    /// </summary>
    public sealed class CameraCalibrator
    {
        public readonly record struct Sample(double Row, double Column, long X, long Y);

        private readonly List<Sample> _samples = new();
        public IReadOnlyList<Sample> Samples => _samples;
        public int Count => _samples.Count;

        public void Add(double row, double column, long x, long y) => _samples.Add(new Sample(row, column, x, y));
        public void Clear() => _samples.Clear();

        /// <summary>Solves for the affine. False (with <paramref name="error"/>) for fewer than 3
        /// samples or a set that doesn't span two dimensions. <paramref name="residualSteps"/> is the
        /// RMS fit error in steps — small means the relationship really is linear.</summary>
        public bool TrySolve(out PixelStepAffine affine, out double residualSteps, out string? error)
        {
            affine = new PixelStepAffine();
            residualSteps = 0;
            error = null;

            int n = _samples.Count;
            if (n < 3) { error = $"Need at least 3 samples (have {n})."; return false; }

            // Sums for centred least squares.
            double sr = 0, sc = 0, sx = 0, sy = 0;
            double srr = 0, scc = 0, src = 0;
            double sxr = 0, sxc = 0, syr = 0, syc = 0;
            foreach (Sample s in _samples)
            {
                sr += s.Row; sc += s.Column; sx += s.X; sy += s.Y;
                srr += s.Row * s.Row; scc += s.Column * s.Column; src += s.Row * s.Column;
                sxr += s.Row * s.X; sxc += s.Column * s.X;
                syr += s.Row * s.Y; syc += s.Column * s.Y;
            }
            double mr = sr / n, mc = sc / n, mx = sx / n, my = sy / n;

            // Centred 2×2 covariance of the pixel coordinates.
            double drr = srr - sr * mr;
            double dcc = scc - sc * mc;
            double drc = src - sr * mc;
            double det = drr * dcc - drc * drc;
            if (drr <= 0 || dcc <= 0 || det <= 1e-6 * drr * dcc)
            {
                error = "Samples are collinear — move the table in BOTH X and Y between captures.";
                return false;
            }

            // Centred cross-covariances pixel↔step.
            double drX = sxr - sr * mx, dcX = sxc - sc * mx;
            double drY = syr - sr * my, dcY = syc - sc * my;

            // Solve [[drr,drc],[drc,dcc]]·[slopeRow;slopeCol] = [d*; d*] for X and Y.
            affine.Xr = (dcc * drX - drc * dcX) / det;
            affine.Xc = (drr * dcX - drc * drX) / det;
            affine.Yr = (dcc * drY - drc * dcY) / det;
            affine.Yc = (drr * dcY - drc * drY) / det;
            affine.SampleCount = n;

            // RMS residual in steps (uses the implied offsets eX/eY = mean − slope·meanPixel).
            double sse = 0;
            foreach (Sample s in _samples)
            {
                double predX = mx + affine.Xr * (s.Row - mr) + affine.Xc * (s.Column - mc);
                double predY = my + affine.Yr * (s.Row - mr) + affine.Yc * (s.Column - mc);
                sse += (predX - s.X) * (predX - s.X) + (predY - s.Y) * (predY - s.Y);
            }
            residualSteps = Math.Sqrt(sse / n);
            affine.ResidualSteps = residualSteps;
            affine.Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            return true;
        }
    }
}
