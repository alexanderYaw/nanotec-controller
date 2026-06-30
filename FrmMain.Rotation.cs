using System;
using System.Threading.Tasks;

namespace NanotecController
{
    // FrmMain — rotation ABOUT the camera crosshair. The Θ axis rotates the chuck about its
    // own mechanical centre; combining it with an X/Y shift (CrosshairRotation) makes the
    // chuck centre orbit the crosshair, which pins the point under the crosshair while Θ turns.
    // Runs CONTINUOUS, like the joystick: Θ jogs at a constant velocity while a fast loop steers
    // X/Y (also in velocity mode) toward the position that pins the crosshair for Θ's CURRENT
    // angle — so all three axes move together, not step-and-settle. FrmMain owns this because
    // NanoLib access must be serialized. (Partial of FrmMain.)
    public partial class FrmMain
    {
        // Θ jog velocity during a rotation (drive units; matches Theta's jog default).
        private const int ROTATE_THETA_SPEED = 1600;
        // Head start: Θ jogs this long before X/Y compensates. 0 = OFF (X/Y pins from the first
        // instant). A nonzero value makes Θ visibly lead, but the feature swings off the crosshair
        // during the uncompensated window and is yanked back — worse for visual centering, so OFF.
        private const int ROTATE_XY_DELAY_MS = 0;
        // X/Y position-follow loop: period, proportional gain (velocity units per step of error),
        // velocity clamp, smallest commanded velocity, and the dead-band under which an axis is
        // stopped. GAIN/VMAX likely need tuning on hardware (units aren't mm/deg yet).
        private const int ROTATE_FOLLOW_MS = 25;
        // GAIN is velocity-units per step of error. Too high overshoots: at velocity V the axis
        // travels ~K·V·dt steps per tick, so a gain above ~1/(K·dt) ("deadbeat") oscillates. Start
        // well below that and raise it until X/Y just keeps up with Θ without overshooting.
        private const double ROTATE_FOLLOW_GAIN = 3.0;
        private const int ROTATE_FOLLOW_VMAX = 3200;   // X/Y follow speed cap (velocity units)
        private const int ROTATE_FOLLOW_MINVEL = 40;
        private const long ROTATE_FOLLOW_DEADBAND = 15;
        // Safety: if X/Y fall this far behind Θ, abort (can't keep up, or wrong handedness/polarity
        // → a velocity loop would otherwise run away). After Θ stops, keep following up to settle-ms.
        private const long ROTATE_FOLLOW_MAXERR = 4000;
        private const int ROTATE_SETTLE_MS = 1500;
        private const int ROTATE_MAX_MS = 180000;

        // Set true (StopHoldRotate) to end a hold-to-rotate; the hold loop checks it each tick.
        private volatile bool _holdRotateStop;

        /// <summary>Rotation needs the drives enabled/idle AND a full calibration (affine +
        /// chuck centre). The sign may still be unset — the test run is how it gets fixed.</summary>
        public bool CanRotate =>
            CanMoveCalibration
            && _calib.PixelStep != null
            && _calib.ChuckCenterX.HasValue && _calib.ChuckCenterY.HasValue;

        /// <summary>The persisted image handedness, or null until the sign test has fixed it.</summary>
        public int? RotationSign => _calib.RotationSign;

        /// <summary>Stores the crosshair-rotation handedness (+1/-1) and persists it.</summary>
        public void SetRotationSign(int sign)
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
        public async Task RotateToAngleAsync(double targetDegrees)
        {
            if (!CanRotate)
            {
                AppendLog("Rotate: needs drives enabled, the camera-scale calibration, and a chuck centre.");
                return;
            }
            double current;
            try { current = _motion!.GetStatus(AxisId.Theta).AngleDegrees; }
            catch (DriveException ex) { AppendLog($"Rotate: read Θ failed: {ex.Message}"); return; }

            // Shortest signed delta into (-180, +180].
            double delta = (targetDegrees - current) % 360.0;
            if (delta > 180.0) delta -= 360.0;
            else if (delta <= -180.0) delta += 360.0;
            await RotateAboutCrosshairAsync(delta);
        }

        /// <summary>
        /// Rotates the chuck by <paramref name="deltaDegrees"/> about the crosshair, keeping the
        /// point currently under the crosshair fixed. CONTINUOUS: Θ jogs at a constant velocity
        /// while a fast loop steers X/Y (velocity mode) toward the position that pins the crosshair
        /// for Θ's CURRENT angle — so all three move together (no step-and-settle). Uses the stored
        /// handedness, or +1 if uncalibrated. Aborts cleanly if X/Y leave the stored travel limits
        /// or fall too far behind Θ; always stops all three on exit.
        /// </summary>
        public async Task RotateAboutCrosshairAsync(double deltaDegrees)
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

            // Start pose S0 in the USER frame (matches the chuck centre).
            if (!TryCurrentUser(AxisId.X, out long s0x) || !TryCurrentUser(AxisId.Y, out long s0y))
            {
                AppendLog("Rotate: X/Y position not available yet.");
                return;
            }

            double deltaRad = deltaDegrees * Math.PI / 180.0;
            long totalThetaTicks = CrosshairRotation.DegreesToChuckTicks(deltaDegrees, CrosshairRotation.ChuckTicksPerRev);
            if (totalThetaTicks == 0) { AppendLog("Rotate: angle too small to move Θ."); return; }
            int thetaDir = Math.Sign(totalThetaTicks);

            using var busyScope = BeginBusy();
            AppendLog($"Rotate {deltaDegrees:+0.##;-0.##}° about crosshair (continuous; centre X={cx:N0} Y={cy:N0}, sign {sign:+0;-0})...");
            long thetaStart = 0, thetaEnd = 0, lastErrX = 0, lastErrY = 0;
            bool ok = await RunDriveOp(() =>
            {
                _motion!.RecoverIfQuickStopped(AxisId.Theta);
                _motion.RecoverIfQuickStopped(AxisId.X);
                _motion.RecoverIfQuickStopped(AxisId.Y);

                thetaStart = _motion.GetStatus(AxisId.Theta).Position;
                _motion.JogAt(AxisId.Theta, thetaDir, ROTATE_THETA_SPEED);   // Θ runs continuously
                try
                {
                    bool thetaDone = false;
                    int elapsed = 0, settled = 0;
                    while (true)
                    {
                        long prog = _motion.GetStatus(AxisId.Theta).Position - thetaStart;
                        if (!thetaDone && Math.Abs(prog) >= Math.Abs(totalThetaTicks))
                        {
                            _motion.Stop(AxisId.Theta);
                            thetaDone = true;
                        }
                        double frac = Math.Clamp((double)prog / totalThetaTicks, 0.0, 1.0);

                        // X/Y target that pins the crosshair for the angle Θ has ACTUALLY reached.
                        if (!CrosshairRotation.TryXyTarget(a, cx, cy, s0x, s0y, deltaRad * frac, sign,
                                                           out long txUser, out long tyUser))
                            throw new DriveException("calibration affine is degenerate — recalibrate the camera scale.");
                        RejectIfOutOfTravel(AxisId.X, txUser);    // X: user == raw
                        RejectIfOutOfTravel(AxisId.Y, -tyUser);   // Y: raw == −user

                        // Follow in the USER frame — velocity-mode jog uses the user frame (the
                        // vision jog commands Y WITHOUT the raw flip). A raw-frame error would
                        // invert Y (userY = −rawY) and drive it the WRONG way → runaway.
                        long errX = txUser - _motion.GetStatus(AxisId.X).Position;        // X: user == raw
                        long errY = tyUser - (-_motion.GetStatus(AxisId.Y).Position);     // Y: user == −raw
                        lastErrX = errX; lastErrY = errY;

                        // Head start: hold X/Y for the first ROTATE_XY_DELAY_MS so Θ visibly leads
                        // (Θ keeps jogging; X/Y catches up once it engages). Follow regardless once
                        // Θ is done, so a tiny angle that completes during the head start still pins.
                        if (elapsed >= ROTATE_XY_DELAY_MS || thetaDone)
                        {
                            if (Math.Abs(errX) > ROTATE_FOLLOW_MAXERR || Math.Abs(errY) > ROTATE_FOLLOW_MAXERR)
                                throw new DriveException($"X/Y fell too far behind Θ (err {errX:N0},{errY:N0}) — aborting. " +
                                                         "Lower Θ speed, or the handedness/polarity is wrong.");

                            CommandFollow(AxisId.X, FollowVel(errX));
                            CommandFollow(AxisId.Y, FollowVel(errY));

                            if (thetaDone)
                            {
                                bool xySettled = Math.Abs(errX) <= ROTATE_FOLLOW_DEADBAND && Math.Abs(errY) <= ROTATE_FOLLOW_DEADBAND;
                                settled += ROTATE_FOLLOW_MS;
                                if (xySettled || settled >= ROTATE_SETTLE_MS) break;
                            }
                        }
                        if (elapsed >= ROTATE_MAX_MS) break;
                        System.Threading.Thread.Sleep(ROTATE_FOLLOW_MS);
                        elapsed += ROTATE_FOLLOW_MS;
                    }
                }
                finally
                {
                    try { _motion.Stop(AxisId.Theta); } catch (DriveException) { }
                    try { _motion.Stop(AxisId.X); } catch (DriveException) { }
                    try { _motion.Stop(AxisId.Y); } catch (DriveException) { }
                }
                thetaEnd = _motion.GetStatus(AxisId.Theta).Position;
            });
            if (ok)
                AppendLog($"  Θ {thetaStart:N0} → {thetaEnd:N0} (Δ {thetaEnd - thetaStart:+0;-0}); final X/Y follow err {lastErrX:N0},{lastErrY:N0}.");
            AppendLog(ok ? "Rotate complete." : "Rotate FAILED — see error above.");
        }

        /// <summary>
        /// HOLD-to-rotate about the crosshair: Θ jogs continuously in <paramref name="direction"/>
        /// (+1/−1) while X/Y follows to pin the crosshair, until <see cref="StopHoldRotate"/> is
        /// called (button released). Same controller as <see cref="RotateAboutCrosshairAsync"/> but
        /// with no target angle. Always stops all three on exit. Call from a button MouseDown.
        /// </summary>
        public async Task HoldRotateAsync(int direction)
        {
            if (!CanRotate)
            {
                AppendLog("Rotate: needs drives enabled, the camera-scale calibration, and a chuck centre.");
                return;
            }
            if (_busy || direction == 0) return;

            PixelStepAffine a = _calib.PixelStep!;
            long cx = _calib.ChuckCenterX!.Value, cy = _calib.ChuckCenterY!.Value;
            int sign = _calib.RotationSign ?? +1;
            if (!_calib.RotationSign.HasValue)
                AppendLog("Rotate: handedness not calibrated — assuming +1 (run the sign test to confirm).");
            if (!TryCurrentUser(AxisId.X, out _) || !TryCurrentUser(AxisId.Y, out _))
            {
                AppendLog("Rotate: X/Y position not available yet.");
                return;
            }

            double radPerTick = 2.0 * Math.PI / CrosshairRotation.ChuckTicksPerRev;
            int thetaDir = Math.Sign(direction);

            _holdRotateStop = false;
            using var busyScope = BeginBusy();
            AppendLog($"Rotate {(thetaDir > 0 ? "⟳" : "⟲")} about crosshair (hold; centre X={cx:N0} Y={cy:N0}, sign {sign:+0;-0})...");
            bool ok = await RunDriveOp(() =>
            {
                _motion!.RecoverIfQuickStopped(AxisId.Theta);
                _motion.RecoverIfQuickStopped(AxisId.X);
                _motion.RecoverIfQuickStopped(AxisId.Y);

                long s0x = _motion.GetStatus(AxisId.X).Position;       // live start pose (USER frame)
                long s0y = -_motion.GetStatus(AxisId.Y).Position;
                long thetaStart = _motion.GetStatus(AxisId.Theta).Position;
                _motion.JogAt(AxisId.Theta, thetaDir, ROTATE_THETA_SPEED);   // Θ runs until released
                try
                {
                    // On release, Θ is stopped but we keep following X/Y to Θ's ACTUAL final angle
                    // (including its decel coast) until X/Y catches up — same settle as the fixed
                    // rotate, so the feature ends pinned instead of drifting further on release.
                    bool releasing = false;
                    int elapsed = 0, settled = 0;
                    while (true)
                    {
                        if (_holdRotateStop && !releasing)
                        {
                            _motion.Stop(AxisId.Theta);   // begin Θ deceleration; X/Y keeps following
                            releasing = true;
                        }

                        // Actual angle Θ has swept since the press; X/Y pins the crosshair for it.
                        double angleRad = radPerTick * (_motion.GetStatus(AxisId.Theta).Position - thetaStart);
                        if (!CrosshairRotation.TryXyTarget(a, cx, cy, s0x, s0y, angleRad, sign, out long txUser, out long tyUser))
                            throw new DriveException("calibration affine is degenerate — recalibrate the camera scale.");
                        RejectIfOutOfTravel(AxisId.X, txUser);
                        RejectIfOutOfTravel(AxisId.Y, -tyUser);

                        long errX = txUser - _motion.GetStatus(AxisId.X).Position;
                        long errY = tyUser - (-_motion.GetStatus(AxisId.Y).Position);

                        // Head start: hold X/Y for the first ROTATE_XY_DELAY_MS so Θ visibly leads.
                        // Once releasing, follow regardless so the release-settle pins the feature.
                        if (elapsed >= ROTATE_XY_DELAY_MS || releasing)
                        {
                            if (Math.Abs(errX) > ROTATE_FOLLOW_MAXERR || Math.Abs(errY) > ROTATE_FOLLOW_MAXERR)
                                throw new DriveException($"X/Y fell too far behind Θ (err {errX:N0},{errY:N0}) — aborting. " +
                                                         "Lower Θ speed, or the handedness/polarity is wrong.");
                            CommandFollow(AxisId.X, FollowVel(errX));
                            CommandFollow(AxisId.Y, FollowVel(errY));

                            if (releasing)
                            {
                                bool xySettled = Math.Abs(errX) <= ROTATE_FOLLOW_DEADBAND && Math.Abs(errY) <= ROTATE_FOLLOW_DEADBAND;
                                settled += ROTATE_FOLLOW_MS;
                                if (xySettled || settled >= ROTATE_SETTLE_MS) break;
                            }
                        }
                        System.Threading.Thread.Sleep(ROTATE_FOLLOW_MS);
                        elapsed += ROTATE_FOLLOW_MS;
                    }
                }
                finally
                {
                    try { _motion.Stop(AxisId.Theta); } catch (DriveException) { }
                    try { _motion.Stop(AxisId.X); } catch (DriveException) { }
                    try { _motion.Stop(AxisId.Y); } catch (DriveException) { }
                }
            });
            AppendLog(ok ? "Rotate (hold) stopped." : "Rotate (hold) FAILED — see error above.");
        }

        /// <summary>Ends a <see cref="HoldRotateAsync"/> (call from the button MouseUp / on focus loss).</summary>
        public void StopHoldRotate() => _holdRotateStop = true;

        // X/Y follow velocity (signed) from a position error in steps: dead-band → 0,
        // else proportional, clamped to [MinVel, Vmax]. Tune ROTATE_FOLLOW_GAIN/VMAX on hardware.
        private static int FollowVel(long errRaw)
        {
            long mag = Math.Abs(errRaw);
            if (mag <= ROTATE_FOLLOW_DEADBAND) return 0;
            int v = (int)Math.Min(ROTATE_FOLLOW_VMAX, Math.Max(ROTATE_FOLLOW_MINVEL, ROTATE_FOLLOW_GAIN * mag));
            return Math.Sign(errRaw) * v;
        }

        // Commands a raw-frame signed velocity (X/Y have InvertDirection=false, so JogAt's direction
        // is the raw sign); zero stops the axis.
        private void CommandFollow(AxisId id, int signedVel)
        {
            if (signedVel == 0) _motion!.Stop(id);
            else _motion!.JogAt(id, Math.Sign(signedVel), Math.Abs(signedVel));
        }

        // Throws (aborting the rotation) if a raw target leaves an axis's stored travel limits.
        // Limits are raw-frame (as captured); a null end means that side is unbounded.
        private void RejectIfOutOfTravel(AxisId id, long rawTarget)
        {
            AxisCalibration c = _calib.For(id);
            if (c.Min.HasValue && rawTarget < c.Min.Value)
                throw new DriveException($"{id} target {rawTarget:N0} < Min {c.Min.Value:N0} — rotation needs more travel than available.");
            if (c.Max.HasValue && rawTarget > c.Max.Value)
                throw new DriveException($"{id} target {rawTarget:N0} > Max {c.Max.Value:N0} — rotation needs more travel than available.");
        }
    }
}
