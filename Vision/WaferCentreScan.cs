using System;
using System.Collections.Generic;

namespace NanotecController
{
    /// <summary>
    /// Turns a Θ-scan of the wafer rim into the wafer's offset from the chuck's rotation axis. The
    /// rim is far larger than the X/Y travel, so instead of circling it the stage parks on ONE
    /// reachable spot and Θ turns the wafer underneath the camera.
    ///
    /// De-rotating each sample about the chuck centre by its own θ expresses it in the chuck's
    /// rotating frame, where the samples span the full 360° despite all being measured from the same
    /// small patch of travel. Those points feed the same Pratt <see cref="CircleFit"/> the chuck
    /// centre-find uses, and its centre is the wafer's offset from the rotation axis.
    ///
    /// The maths is in mm, not steps: X and Y have different StepsPerMm, so a rotation is only a
    /// rotation once that anisotropy is divided out. See Developer Guide,
    /// WaferCentreByRotation.md, for the derivation.
    /// </summary>
    public static class WaferCentreScan
    {
        /// <summary>One rim point: the chuck angle it was taken at, and the rim point itself in
        /// user-frame motor steps (E = M + A·(p_cross − p_edge)).</summary>
        public readonly record struct Sample(double ThetaDeg, double X, double Y);

        /// <summary>
        /// Offset is the wafer centre relative to the chuck centre, in the CHUCK frame — i.e.
        /// de-rotated to θ = 0. It is not a motor position; pair it with a Θ angle through
        /// <see cref="CalibrationStore.WaferCentreAt"/> to get one.
        /// </summary>
        public readonly record struct Result(
            double OffsetXSteps, double OffsetYSteps,
            double RadiusSteps, double RadiusMm,
            double RmsMm, double RmsSteps,
            int Used, int Sign,
            IReadOnlyList<double> DroppedAngles);

        /// <summary>Residuals below this (mm) are never treated as outliers however tight the fit is,
        /// so a clean scan cannot shed good points to its own noise floor. Measured hardware scans run
        /// an RMS of 0.076 mm, so this is ~2x the noise; it was 0.3 mm until 2026-08-07, which kept
        /// anything within 4x the noise — the scale of a bevel or partial-notch mis-latch.</summary>
        private const double OUTLIER_FLOOR_MM = 0.15;

        /// <summary>Residual multiple of the RMS above which a point is dropped and the fit redone
        /// (3.0 until 2026-08-07).</summary>
        private const double OUTLIER_SIGMA = 2.5;

        /// <summary>Hard ceiling on the cut (mm), whatever the RMS says. A multiple of the RMS cannot
        /// catch an outlier at small N: with 9 points one bad sample drags the circle onto itself and
        /// inflates the very RMS that sets the cut, so it ends up inside its own threshold and
        /// survives — measured, a 2 mm outlier at N=8 was kept at any sigma. A rim point is a physical
        /// thing: hardware scans fit to an RMS of 0.076 mm, so 0.5 mm is ~6x the noise and nothing
        /// genuine reaches it. This ceiling, not the sigma, is what rejects outliers on a short scan.</summary>
        private const double OUTLIER_MAX_MM = 0.5;

        /// <summary>Drop-and-refit passes. More than one because the cut is set by an RMS the outliers
        /// themselves inflate: one gross point can widen the first cut past the second-worst point, so
        /// each refit shrinks the RMS and tightens the next pass. Bounded — this is a cleanup, not a
        /// search, and a scan needing four passes has something wrong with it that the log should show.</summary>
        private const int OUTLIER_MAX_PASSES = 3;

        /// <summary>A refit is only accepted while at least this many points survive. Three is the
        /// algebraic minimum, and a 3-point fit is exact — it would report a flattering RMS on what is
        /// really no longer a measurement.</summary>
        private const int OUTLIER_MIN_KEPT = 5;

        /// <summary>The two handednesses are only judged by RMS when the loser is at least this bad
        /// (mm). Any 3 points lie exactly on some circle, so at small N both signs fit perfectly and
        /// the tie would otherwise be broken by floating-point noise, mirroring the answer.</summary>
        private const double SIGN_SEPARATION_MM = 0.05;

        /// <summary>…and the winner must beat the loser by at least this factor, not merely edge it.</summary>
        private const double SIGN_MARGIN = 0.5;

        /// <summary>Fits <paramref name="samples"/> to a wafer offset + radius. Both de-rotation
        /// handednesses are tried and the clearly better one wins, a wrong sign yielding a plausible
        /// but mirrored centre. When the scan is too short to tell them apart,
        /// <paramref name="expectedSign"/> (±1, or 0) breaks the tie — an ambiguous scan with no
        /// expectation is an error rather than a guess. False for &lt;3 samples, a degenerate set, or
        /// unresolvable handedness.</summary>
        public static bool TryFit(
            IReadOnlyList<Sample> samples,
            long centreX, long centreY,
            double stepsPerMmX, double stepsPerMmY,
            int expectedSign,
            out Result result, out string? error)
        {
            result = default;
            error = null;

            if (samples.Count < 3) { error = $"Need at least 3 rim samples (have {samples.Count})."; return false; }
            if (stepsPerMmX <= 0 || stepsPerMmY <= 0) { error = "X and Y need StepsPerMm before a wafer scan can be fitted."; return false; }

            // The wrong handedness scatters the de-rotated points instead of forming a circle, so
            // the RMS separates them by orders of magnitude.
            bool okPos = TryFitWithSign(samples, +1, centreX, centreY, stepsPerMmX, stepsPerMmY,
                                        out CircleFit.Result fitPos, out List<(double X, double Y)> ptsPos, out string? errPos);
            bool okNeg = TryFitWithSign(samples, -1, centreX, centreY, stepsPerMmX, stepsPerMmY,
                                        out CircleFit.Result fitNeg, out List<(double X, double Y)> ptsNeg, out string? errNeg);

            int sign;
            if (okPos && okNeg)
            {
                double better = Math.Min(fitPos.RmsError, fitNeg.RmsError);
                double worse = Math.Max(fitPos.RmsError, fitNeg.RmsError);
                if (worse > SIGN_SEPARATION_MM && better <= SIGN_MARGIN * worse)
                    sign = fitPos.RmsError <= fitNeg.RmsError ? +1 : -1;
                else if (expectedSign != 0)
                    sign = Math.Sign(expectedSign);
                else
                {
                    error = $"Both de-rotation handednesses fit equally well (RMS {fitPos.RmsError:F4} vs " +
                            $"{fitNeg.RmsError:F4} mm), so the scan cannot tell them apart. Take more samples, " +
                            "or run the crosshair sign test so a handedness is known.";
                    return false;
                }
            }
            else if (okPos) sign = +1;
            else if (okNeg) sign = -1;
            else { error = "Fit failed for both de-rotation signs: " + (errPos ?? errNeg); return false; }

            CircleFit.Result fit = sign > 0 ? fitPos : fitNeg;
            List<(double X, double Y)> pts = sign > 0 ? ptsPos : ptsNeg;

            // Drop outliers (a sample that landed on the notch, or a bad detection) and refit. Each
            // pass re-cuts against the tighter RMS the previous refit produced, so an outlier that hid
            // inside a cut its own residual had widened is caught on the next one. A pass that would
            // leave too few points to be a measurement is abandoned whole: the fit and the reported
            // drops then stay in step, rather than reporting drops that never went into the fit.
            var dropped = new List<double>();
            var kept = new List<int>(pts.Count);
            for (int i = 0; i < pts.Count; i++) kept.Add(i);

            for (int pass = 0; pass < OUTLIER_MAX_PASSES; pass++)
            {
                // Every pass re-judges EVERY sample against the current fit, not just the survivors.
                // The first fit is dragged towards a gross outlier, so good points on the far side can
                // land outside that pass's cut; once the refit is clean they belong again, and a set
                // that only ever shrank would have thrown them away for the outlier's mistake.
                double cut = Math.Clamp(OUTLIER_SIGMA * fit.RmsError, OUTLIER_FLOOR_MM, OUTLIER_MAX_MM);
                var keepNow = new List<int>(pts.Count);
                var dropNow = new List<double>();
                for (int i = 0; i < pts.Count; i++)
                {
                    double dx = pts[i].X - fit.CenterX, dy = pts[i].Y - fit.CenterY;
                    if (Math.Abs(Math.Sqrt(dx * dx + dy * dy) - fit.Radius) > cut) dropNow.Add(samples[i].ThetaDeg);
                    else keepNow.Add(i);
                }

                if (SameSet(keepNow, kept)) break;                  // converged — this pass changes nothing
                if (keepNow.Count < OUTLIER_MIN_KEPT) break;        // too few left to still be a measurement

                var keptPts = new List<(double X, double Y)>(keepNow.Count);
                foreach (int i in keepNow) keptPts.Add(pts[i]);
                if (!CircleFit.TryFit(keptPts, out CircleFit.Result refit, out _)) break;

                kept = keepNow;
                dropped = dropNow;
                fit = refit;
            }

            int used = kept.Count;

            // Back to steps. The offset is per-axis; the radius is isotropic, so it takes the mean.
            double meanSteps = (stepsPerMmX + stepsPerMmY) / 2.0;
            result = new Result(
                fit.CenterX * stepsPerMmX, fit.CenterY * stepsPerMmY,
                fit.Radius * meanSteps, fit.Radius,
                fit.RmsError, fit.RmsError * meanSteps,
                used, sign, dropped);
            return true;
        }

        // Both lists are built in ascending index order, so equality is element-wise.
        private static bool SameSet(List<int> a, List<int> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // De-rotates every sample about the chuck centre by −sign·θ, in mm, and circle-fits them.
        private static bool TryFitWithSign(
            IReadOnlyList<Sample> samples, int sign,
            long centreX, long centreY, double kX, double kY,
            out CircleFit.Result fit, out List<(double X, double Y)> points, out string? error)
        {
            points = new List<(double X, double Y)>(samples.Count);
            foreach (Sample s in samples)
            {
                double vx = (s.X - centreX) / kX, vy = (s.Y - centreY) / kY;
                double rad = -sign * s.ThetaDeg * Math.PI / 180.0;
                double c = Math.Cos(rad), sn = Math.Sin(rad);
                points.Add((c * vx - sn * vy, sn * vx + c * vy));
            }
            return CircleFit.TryFit(points, out fit, out error);
        }

        /// <summary>
        /// The de-rotation handedness implied by the stored calibration. <see cref="CalibrationStore.RotationSign"/>
        /// is the handedness in PIXEL space (that is where CrosshairRotation applies it), so mapping it
        /// into step space costs the sign of the affine's determinant. Only ever a starting expectation —
        /// <see cref="TryFit"/> settles it from the data.
        /// </summary>
        public static int ExpectedSign(PixelStepAffine a, int rotationSign)
            => Math.Sign(rotationSign) * (a.Xr * a.Yc - a.Xc * a.Yr >= 0 ? 1 : -1);
    }
}
