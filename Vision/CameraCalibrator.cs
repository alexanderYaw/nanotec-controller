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
    ///
    /// The affine alone carries no physical length — it relates pixels to steps and nothing to mm.
    /// The fiducial's KNOWN DIAMETER supplies that one missing scale, which is why each sample also
    /// records the detected radius: see <see cref="ScaleResult"/>.
    /// </summary>
    public sealed class CameraCalibrator
    {
        /// <summary>One captured pairing. <paramref name="Radius"/> is the fiducial's detected radius
        /// in pixels, which the affine itself does not use — it is what turns the affine into mm.</summary>
        public readonly record struct Sample(double Row, double Column, double Radius, long X, long Y);

        private readonly List<Sample> _samples = new();
        public IReadOnlyList<Sample> Samples => _samples;
        public int Count => _samples.Count;

        /// <summary>Known diameter of the calibration fiducial, in mm. This is the ONLY physical length
        /// anywhere in the calibration — every steps/mm downstream traces back to it, so changing the
        /// target without changing this silently rescales the machine.</summary>
        public double FiducialDiameterMm { get; set; } = 1.0;

        /// <summary>Why the last <see cref="TrySolve"/> could not derive a scale; null when it did. The
        /// affine still solves without one, so this is a note rather than an error.</summary>
        public string? LastScaleNote { get; private set; }

        /// <summary>Physical scale recovered from the fiducial. <paramref name="SpreadPercent"/> is the
        /// sample-to-sample scatter of the measured radius and carries straight through to the
        /// steps/mm, so it IS the error bar. <paramref name="SkewDeg"/> is how far the affine's two
        /// pixel axes land from perpendicular once scaled to mm — nothing in the solve forces it to
        /// zero, so it is an independent check that the affine really is a rotation plus scale.</summary>
        public readonly record struct ScaleResult(
            double UmPerPixel, double SpreadPercent, int RadiusCount,
            double StepsPerMmX, double StepsPerMmY, double SkewDeg);

        public void Add(double row, double column, double radius, long x, long y)
            => _samples.Add(new Sample(row, column, radius, x, y));
        public void Clear() => _samples.Clear();

        /// <summary>Solves for the affine, and for the steps/mm it implies given the fiducial's known
        /// diameter. False (with <paramref name="error"/>) for fewer than 3 samples or a set that
        /// doesn't span two dimensions. <paramref name="residualSteps"/> is the RMS fit error in steps
        /// — small means the relationship really is linear. <paramref name="scale"/> is null, with the
        /// reason in <see cref="LastScaleNote"/>, when the affine solved but the scale could not: the
        /// affine is still valid and still worth saving.</summary>
        public bool TrySolve(out PixelStepAffine affine, out double residualSteps,
                             out ScaleResult? scale, out string? error)
        {
            affine = new PixelStepAffine();
            residualSteps = 0;
            scale = null;
            error = null;
            LastScaleNote = null;

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

            scale = TrySolveScale(affine);
            return true;
        }

        /// <summary>Turns the affine into steps/mm using the fiducial's known diameter.
        ///
        /// One pixel of ROW motion and one pixel of COLUMN motion each cover the same physical
        /// distance s (the pixels being square), which written out is two equations linear in
        /// u = 1/kX² and v = 1/kY²:
        ///
        ///     Xr²·u + Yr²·v = s²
        ///     Xc²·u + Yc²·v = s²
        ///
        /// Degenerate only near a 45° camera rotation, where both pixel axes feed both stage axes
        /// equally and the two equations stop being independent.
        /// </summary>
        private ScaleResult? TrySolveScale(PixelStepAffine a)
        {
            if (FiducialDiameterMm <= 0) { LastScaleNote = "fiducial diameter must be positive."; return null; }

            // Mean radius over the samples. Averaging beats one measurement, and the SPREAD is the
            // honest error bar: the blob's rim moves with the chosen gray cut, so a radius is far more
            // threshold-sensitive than the centroid the affine is fitted to.
            double sum = 0;
            int count = 0;
            foreach (Sample s in _samples)
                if (s.Radius > 0 && !double.IsNaN(s.Radius)) { sum += s.Radius; count++; }
            if (count < 1) { LastScaleNote = "no sample carried a fiducial radius."; return null; }

            double meanRadius = sum / count;
            double sse = 0;
            foreach (Sample s in _samples)
                if (s.Radius > 0 && !double.IsNaN(s.Radius)) sse += (s.Radius - meanRadius) * (s.Radius - meanRadius);
            double spreadPercent = count > 1 ? 100.0 * Math.Sqrt(sse / (count - 1)) / meanRadius : 0;

            double mmPerPixel = FiducialDiameterMm / (2 * meanRadius);
            double s2 = mmPerPixel * mmPerPixel;

            double xr2 = a.Xr * a.Xr, xc2 = a.Xc * a.Xc, yr2 = a.Yr * a.Yr, yc2 = a.Yc * a.Yc;
            double det = xr2 * yc2 - yr2 * xc2;
            double norm = xr2 + xc2 + yr2 + yc2;
            if (Math.Abs(det) <= 1e-3 * norm * norm)
            {
                LastScaleNote = "camera sits too near 45° to the stage to separate X from Y.";
                return null;
            }

            double u = s2 * (yc2 - yr2) / det;
            double v = s2 * (xr2 - xc2) / det;
            if (u <= 0 || v <= 0) { LastScaleNote = "solve gave a non-physical scale — check the affine."; return null; }

            // Scaled to mm the affine should be a pure rotation (or reflection), so its two pixel axes
            // come out perpendicular. Nothing above forces that, which is what makes it a real check.
            double dot = a.Xr * a.Xc * u + a.Yr * a.Yc * v;
            double skewDeg = 90.0 - Math.Acos(Math.Clamp(dot / s2, -1.0, 1.0)) * 180.0 / Math.PI;

            return new ScaleResult(
                mmPerPixel * 1000.0, spreadPercent, count,
                1.0 / Math.Sqrt(u), 1.0 / Math.Sqrt(v), skewDeg);
        }
    }
}
