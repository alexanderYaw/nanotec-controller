using System;
using System.Threading.Tasks;

namespace MotorControlApp
{
    // FrmMain — rotation ABOUT the camera crosshair. The Θ axis rotates the chuck about its
    // own mechanical centre; combining it with an X/Y shift (CrosshairRotation) makes the
    // chuck centre orbit the crosshair, which pins the point under the crosshair while Θ
    // turns. Runs as incremental step-and-settle (the soft master can't sync continuous
    // multi-axis), recomputing each absolute X/Y target from the ORIGINAL start so error
    // never accumulates. FrmMain owns this because NanoLib access must be serialized.
    // (Partial of FrmMain.)
    public partial class FrmMain
    {
        // Largest Θ increment per settle step; the X/Y chord between steps must stay a good
        // approximation of the true arc, so keep this small for the crosshair point to look
        // pinned throughout. The endpoint is exact regardless.
        private const double ROTATE_STEP_DEG = 2.0;
        // Θ profile velocity for the rotation (drive units; matches Theta's jog default).
        private const int ROTATE_THETA_SPEED = 400;

        /// <summary>Rotation needs the drives enabled/idle AND a full calibration (affine +
        /// chuck centre). The sign may still be unset — the test run is how it gets fixed.</summary>
        internal bool CanRotate =>
            CanMoveCalibration
            && _calib.PixelStep != null
            && _calib.ChuckCenterX.HasValue && _calib.ChuckCenterY.HasValue;

        /// <summary>The persisted image handedness, or null until the sign test has fixed it.</summary>
        internal int? RotationSign => _calib.RotationSign;

        /// <summary>Stores the crosshair-rotation handedness (+1/-1) and persists it.</summary>
        internal void SetRotationSign(int sign)
        {
            _calib.RotationSign = Math.Sign(sign);
            TrySaveCalibration();
            AppendLog($"Rotation handedness set to {_calib.RotationSign:+0;-0}.");
        }

        /// <summary>
        /// Rotates the chuck to an ABSOLUTE angle (0–360°) about the crosshair, taking the
        /// shortest path from the current Θ angle. Thin wrapper over
        /// <see cref="RotateAboutCrosshairAsync"/>.
        /// </summary>
        internal async Task RotateToAngleAsync(double targetDegrees)
        {
            if (!CanRotate)
            {
                AppendLog("Rotate: needs drives enabled, the camera-scale calibration, and a chuck centre.");
                return;
            }
            double current;
            try { current = _motion!.GetStatus(AxisId.Theta).AngleDegrees; }
            catch (ChuckException ex) { AppendLog($"Rotate: read Θ failed: {ex.Message}"); return; }

            // Shortest signed delta into (-180, +180].
            double delta = (targetDegrees - current) % 360.0;
            if (delta > 180.0) delta -= 360.0;
            else if (delta <= -180.0) delta += 360.0;
            await RotateAboutCrosshairAsync(delta);
        }

        /// <summary>
        /// Rotates the chuck by <paramref name="deltaDegrees"/> about the crosshair, keeping the
        /// point currently under the crosshair fixed. Θ steps in ≤<see cref="ROTATE_STEP_DEG"/>
        /// increments; at each step X/Y move to the absolute target that re-pins the crosshair.
        /// Uses the stored handedness, or +1 if it has not been calibrated yet (the sign test
        /// relies on this to perform its trial rotation). Aborts cleanly if any X/Y target would
        /// leave the stored travel limits.
        /// </summary>
        internal async Task RotateAboutCrosshairAsync(double deltaDegrees)
        {
            if (!CanRotate)
            {
                AppendLog("Rotate: needs drives enabled, the camera-scale calibration, and a chuck centre.");
                return;
            }
            if (Math.Abs(deltaDegrees) < 1e-6) { AppendLog("Rotate: angle is zero."); return; }

            PixelStepAffine a = _calib.PixelStep!;
            long cx = _calib.ChuckCenterX!.Value, cy = _calib.ChuckCenterY!.Value;
            int sign = _calib.RotationSign ?? +1;
            if (!_calib.RotationSign.HasValue)
                AppendLog("Rotate: handedness not calibrated — assuming +1 (run the sign test to confirm).");

            // Start pose: S0 in the USER frame (matches the affine and chuck centre). Θ is driven
            // RELATIVELY (never homed → absolute PP moves are rejected; relative ones work), so no
            // Θ start position is needed.
            if (!TryCurrentUser(AxisId.X, out long s0x) || !TryCurrentUser(AxisId.Y, out long s0y))
            {
                AppendLog("Rotate: X/Y position not available yet.");
                return;
            }

            int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(deltaDegrees) / ROTATE_STEP_DEG));
            double deltaRad = deltaDegrees * Math.PI / 180.0;
            long thetaPrevCum = 0;   // cumulative Θ ticks already commanded (for drift-free relative steps)

            _busy = true; RefreshButtons();
            AppendLog($"Rotate {deltaDegrees:+0.##;-0.##}° about crosshair in {steps} step(s) " +
                      $"(centre X={cx:N0} Y={cy:N0}, sign {sign:+0;-0})...");
            long thetaStart = 0, thetaEnd = 0; long thetaCommanded = 0;
            bool ok = await RunDriveOp(() =>
            {
                thetaStart = _motion!.GetStatus(AxisId.Theta).Position;
                for (int k = 1; k <= steps; k++)
                {
                    double frac = (double)k / steps;

                    if (!CrosshairRotation.TryXyTarget(a, cx, cy, s0x, s0y, deltaRad * frac, sign,
                                                       out long txUser, out long tyUser))
                        throw new ChuckException("calibration affine is degenerate — recalibrate.");

                    // USER → RAW for the drive (Y is inverted in the user frame).
                    long rawX = txUser, rawY = -tyUser;
                    RejectIfOutOfTravel(AxisId.X, rawX);
                    RejectIfOutOfTravel(AxisId.Y, rawY);

                    // Θ relative step = (cumulative ticks at this angle) − (already commanded), so
                    // rounding never accumulates across steps. Relative because Θ is unreferenced.
                    long thetaCum = CrosshairRotation.DegreesToChuckTicks(deltaDegrees * frac);
                    long thetaStep = thetaCum - thetaPrevCum;
                    thetaPrevCum = thetaCum;
                    thetaCommanded += thetaStep;

                    // Issue all three together, then wait for all three to settle.
                    _motion!.RecoverIfQuickStopped(AxisId.Theta);
                    _motion.RecoverIfQuickStopped(AxisId.X);
                    _motion.RecoverIfQuickStopped(AxisId.Y);
                    _motion.MoveRelative(AxisId.Theta, thetaStep, ROTATE_THETA_SPEED);
                    _motion.MoveAbsolute(AxisId.X, rawX, HomeSpeedFor(AxisId.X));
                    _motion.MoveAbsolute(AxisId.Y, rawY, HomeSpeedFor(AxisId.Y));
                    _motion.WaitForMotionComplete(AxisId.Theta, FIND_TIMEOUT_MS);
                    _motion.WaitForMotionComplete(AxisId.X, FIND_TIMEOUT_MS);
                    _motion.WaitForMotionComplete(AxisId.Y, FIND_TIMEOUT_MS);
                }
                thetaEnd = _motion!.GetStatus(AxisId.Theta).Position;
            });
            if (ok)
                AppendLog($"  Θ commanded {thetaCommanded:+0;-0} ticks; actual {thetaStart:N0} → {thetaEnd:N0} (Δ {thetaEnd - thetaStart:+0;-0}).");
            AppendLog(ok ? "Rotate complete." : "Rotate FAILED — see error above.");
            _busy = false; RestartTimers(); RefreshButtons();
        }

        // Throws (aborting the rotation) if a raw target leaves an axis's stored travel limits.
        // Limits are raw-frame (as captured); a null end means that side is unbounded.
        private void RejectIfOutOfTravel(AxisId id, long rawTarget)
        {
            AxisCalibration c = _calib.For(id);
            if (c.Min.HasValue && rawTarget < c.Min.Value)
                throw new ChuckException($"{id} target {rawTarget:N0} < Min {c.Min.Value:N0} — rotation needs more travel than available.");
            if (c.Max.HasValue && rawTarget > c.Max.Value)
                throw new ChuckException($"{id} target {rawTarget:N0} > Max {c.Max.Value:N0} — rotation needs more travel than available.");
        }
    }
}
