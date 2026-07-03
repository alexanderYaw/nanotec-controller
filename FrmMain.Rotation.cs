using System;
using System.Threading.Tasks;

namespace NanotecController
{
    // FrmMain — rotation ABOUT the camera crosshair. The Θ axis rotates the chuck about its
    // own mechanical centre; combining it with an X/Y shift (CrosshairRotation) makes the
    // chuck centre orbit the crosshair, which pins the point under the crosshair while Θ turns.
    // Runs CONTINUOUS, like the joystick: Θ jogs at a constant velocity while a fast loop steers
    // X/Y (also in velocity mode) toward the position that pins the crosshair for Θ's PREDICTED
    // angle (a short lookahead cancels the follower's time-lag) — so all three axes move together,
    // not step-and-settle. FrmMain owns this because NanoLib access must be serialized. (Partial of FrmMain.)
    public partial class FrmMain
    {
        // Θ jog velocity during a rotation (drive units = steps/s, per the K-capture).
        private const int ROTATE_THETA_SPEED = 800;
        // Soft-start/stop: ramp the Θ velocity SETPOINT up over this long at the start, and back down
        // as it nears the target, so Θ never accelerates faster than the X/Y follower can track — this
        // is what negates the start/stop swing-out. Larger = gentler (and a slightly slower move). If a
        // residual swing-out remains at the ends, raise this.
        private const double ROTATE_THETA_RAMP_MS = 400.0;
        private const int ROTATE_THETA_MIN_SPEED = 40;   // floor so the ramp ends don't stall Θ (steps/s)
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
        // well below that and raise it until X/Y just keeps up with Θ without overshooting. Note a
        // proportional-only loop tracks the ramp with a CONSTANT time-lag 1/(K·GAIN); the lookahead
        // (below) cancels that, so a higher GAIN here lets you use a smaller lookahead.
        private const double ROTATE_FOLLOW_GAIN = 3.0;
        private const int ROTATE_FOLLOW_VMAX = 3200;   // X/Y follow speed cap (velocity units)
        // Smallest commanded follow velocity (1 vu = 1 step/s). With FF supplying a steady baseline
        // velocity, a low floor is safe and lets the slow/reversing axis creep instead of bang-banging
        // at 40 (= 1 step/tick), which was the jitter. The earlier MINVEL=12 jitter was pure-P commands
        // oscillating around zero — FF removes that. Raise if the drive stalls at very low velocity.
        private const int ROTATE_FOLLOW_MINVEL = 10;
        private const long ROTATE_FOLLOW_DEADBAND = 15;
        // Radians of CHUCK rotation per motor tick (2π / ticks-per-rev). Converts the measured Θ
        // tick-rate into the angular rate the analytic velocity feedforward needs.
        private static readonly double ROTATE_RADPERTICK = 2.0 * Math.PI / CrosshairRotation.ChuckTicksPerRev;
        // Feedforward gain K (drive-velocity-units per step-per-tick of the target's motion), PER
        // AXIS. The FF term is now the ANALYTIC target velocity (CrosshairRotation.TryXyTargetVelocity
        // × Θ tick-rate) rather than a numeric difference of quantized targets — so it is noise-free,
        // and the old fragility (a bad estimate fighting the P-term) is gone. Still DISABLED (0) by
        // default because ROTATE_LOOKAHEAD_MS already cancels the follower's constant lag; enabling
        // FF as well would double-compensate. Turn on ONLY if you also reduce the lookahead — its
        // value then lets you drop ROTATE_FOLLOW_GAIN (less noise amplification). K is a single
        // per-axis scalar: jog X (then Y) at a known drive velocity, measure steps/s, K = vel/(steps/s).
        private const double ROTATE_FOLLOW_FF_X = 39.84;   // measured via K-capture (1 vu = 1 step/s ⇒ ≈40)
        private const double ROTATE_FOLLOW_FF_Y = 39.60;
        // Master (Θ) lookahead: project Θ this many ms into the future (via its smoothed velocity)
        // before computing the X/Y pin target. Cancels the follower's total CONSTANT time-lag —
        // transport/comms delay PLUS the P-loop's own 1/(K·GAIN) lag — which is what made the feature
        // orbit an offset point mid-rotation and snap back at stop. Tune to zero the 'mid-rotation
        // pin err' logged below: feature LEADS (swings the opposite way) → lower it; LAGS (swings off
        // in the direction of travel) → raise it. Start near one loop period. If X and Y disagree
        // (their K differ), split into per-axis constants.
        // FF now cancels the follower's velocity-tracking lag, so the lookahead (which did the same
        // job by shifting the target) is off — running both would double-compensate. If X shows a
        // small RESIDUAL lag with FF on, that's transport/comms delay; add a little lookahead back.
        private const double ROTATE_LOOKAHEAD_MS = 0.0;
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
        /// for Θ's PREDICTED angle (short lookahead) — so all three move together (no step-and-settle).
        /// Uses the stored handedness, or +1 if uncalibrated. Aborts cleanly if X/Y leave the stored
        /// travel limits or fall too far behind Θ; always stops all three on exit.
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
            AppendLog($"Rotate {deltaDegrees:+0.##;-0.##}° about crosshair (continuous; centre X={cx:N0} Y={cy:N0}, sign {sign:+0;-0}, lookahead {ROTATE_LOOKAHEAD_MS:0}ms)...");
            long thetaStart = 0, thetaEnd = 0, lastErrX = 0, lastErrY = 0;
            long peakErrX = 0, peakErrY = 0;   // largest follow error seen — dominated by the start/stop transient
            long pinSumX = 0, pinSumY = 0; int pinN = 0;   // signed PIN error (actual − current-angle target) over the middle
            bool velSaturated = false;         // X/Y ever hit the velocity cap (can't keep up)
            double kDispX = 0, kVtX = 0, kDispY = 0, kVtY = 0;   // TEMP K-capture: Σ|Δpos| and Σ|cmdVel|·dt per axis
            bool ok = await RunDriveOp(() =>
            {
                _motion!.RecoverIfQuickStopped(AxisId.Theta);
                _motion.RecoverIfQuickStopped(AxisId.X);
                _motion.RecoverIfQuickStopped(AxisId.Y);

                thetaStart = _motion.GetStatus(AxisId.Theta).Position;
                _motion.JogAt(AxisId.Theta, thetaDir, ROTATE_THETA_MIN_SPEED);   // Θ soft-ramps inside the loop
                try
                {
                    bool thetaDone = false;
                    int elapsed = 0, settled = 0;
                    var ffClock = System.Diagnostics.Stopwatch.StartNew();
                    long ffPrevMs = 0;                   // previous tick's timestamp, for the Θ-velocity dt
                    long prevTheta = thetaStart;         // last tick's Θ, for the velocity estimate
                    double thetaVel = 0.0;               // smoothed Θ velocity (ticks/ms) for the lookahead
                    bool velSeeded = false;
                    long prevXUser = 0, prevYUser = 0; int prevVelX = 0, prevVelY = 0;   // TEMP K-capture
                    bool kSeeded = false;
                    double maxThetaVel = 0.0;            // peak |thetaVel| seen (ticks/ms), for the ramp-down distance
                    while (true)
                    {
                        long currentTheta = _motion.GetStatus(AxisId.Theta).Position;
                        long nowMs = ffClock.ElapsedMilliseconds;
                        long dtMs = nowMs - ffPrevMs;

                        // Smoothed Θ velocity for the lookahead AND the analytic feedforward (EMA to
                        // reject Sleep-jitter/quantization spikes). 0.5 is the validated baseline;
                        // heavier smoothing (0.2) was tried to cut the swing but lagged the estimate
                        // through accel and made the swing WORSE — reverted.
                        if (dtMs > 0)
                        {
                            double rawVel = (double)(currentTheta - prevTheta) / dtMs;
                            thetaVel = velSeeded ? 0.5 * thetaVel + 0.5 * rawVel : rawVel;
                            velSeeded = true;
                        }
                        prevTheta = currentTheta;

                        // Θ SETPOINT soft-ramp: rise over the first RAMP_MS, fall over the last
                        // ramp-distance (0.5·cruise·RAMP_MS ticks) so accel/decel stay within the
                        // follower's bandwidth (kills the start/stop swing-out). Hard-stop on ACTUAL
                        // progress remains the exact terminator. X/Y target still tracks measured Θ.
                        if (Math.Abs(thetaVel) > maxThetaVel) maxThetaVel = Math.Abs(thetaVel);
                        long actualProg = currentTheta - thetaStart;
                        long remaining = Math.Abs(totalThetaTicks) - Math.Abs(actualProg);
                        if (!thetaDone && remaining <= 0)
                        {
                            _motion.Stop(AxisId.Theta);
                            thetaDone = true;
                        }
                        else if (!thetaDone)
                        {
                            double upFrac = ROTATE_THETA_RAMP_MS > 0 ? Math.Clamp(nowMs / ROTATE_THETA_RAMP_MS, 0.0, 1.0) : 1.0;
                            double rampDist = 0.5 * maxThetaVel * ROTATE_THETA_RAMP_MS;
                            double downFrac = rampDist > 1.0 ? Math.Clamp(remaining / rampDist, 0.0, 1.0) : 1.0;
                            int thetaCmd = (int)Math.Max(ROTATE_THETA_MIN_SPEED, ROTATE_THETA_SPEED * Math.Min(upFrac, downFrac));
                            _motion.JogAt(AxisId.Theta, thetaDir, thetaCmd);
                        }
                        long prog = (currentTheta + (long)(thetaVel * ROTATE_LOOKAHEAD_MS)) - thetaStart;
                        double frac = Math.Clamp((double)prog / totalThetaTicks, 0.0, 1.0);

                        // X/Y target that pins the crosshair for the angle Θ is PREDICTED to reach.
                        if (!CrosshairRotation.TryXyTarget(a, cx, cy, s0x, s0y, deltaRad * frac, sign,
                                                           out long txUser, out long tyUser))
                            throw new DriveException("calibration affine is degenerate — recalibrate the camera scale.");
                        RejectIfOutOfTravel(AxisId.X, ToRaw(AxisId.X, txUser));
                        RejectIfOutOfTravel(AxisId.Y, ToRaw(AxisId.Y, tyUser));

                        // Follow in the USER frame — velocity-mode jog uses the user frame (the
                        // vision jog commands Y WITHOUT the raw flip). A raw-frame error would
                        // invert Y (userY = −rawY) and drive it the WRONG way → runaway.
                        long curXUser = ToUser(AxisId.X, _motion.GetStatus(AxisId.X).Position);
                        long curYUser = ToUser(AxisId.Y, _motion.GetStatus(AxisId.Y).Position);
                        long errX = txUser - curXUser;
                        long errY = tyUser - curYUser;
                        lastErrX = errX; lastErrY = errY;

                        // TEMP K-capture (FF calibration): pair THIS tick's user displacement with the
                        // velocity commanded LAST tick, integrated over cruise. m = Σ|Δpos| / Σ|vel|·dt
                        // (steps per velocity-unit·ms); suggested FF gain = 1/(m·ROTATE_FOLLOW_MS).
                        if (kSeeded && !thetaDone && dtMs > 0)
                        {
                            if (prevVelX != 0) { kDispX += Math.Abs(curXUser - prevXUser); kVtX += Math.Abs((double)prevVelX) * dtMs; }
                            if (prevVelY != 0) { kDispY += Math.Abs(curYUser - prevYUser); kVtY += Math.Abs((double)prevVelY) * dtMs; }
                        }
                        prevXUser = curXUser; prevYUser = curYUser; kSeeded = true;

                        // ANALYTIC velocity feedforward (0 by default — see the FF_X/FF_Y note). The pin
                        // target's exact step-velocity is d(target)/d(angle) · d(angle)/dt, where the
                        // geometry derivative is closed-form (not a diff of quantized targets) and
                        // d(angle)/dt = ROTATE_RADPERTICK · thetaVel. anglePerTick is that angular
                        // increment over one nominal loop tick, so ffX/ffY come out in steps-per-tick —
                        // the same units as the P-term's error. Skipped entirely while the gains are 0.
                        ffPrevMs = nowMs;
                        double ffX = 0.0, ffY = 0.0;
                        if ((ROTATE_FOLLOW_FF_X != 0.0 || ROTATE_FOLLOW_FF_Y != 0.0) &&
                            CrosshairRotation.TryXyTargetVelocity(a, cx, cy, s0x, s0y, deltaRad * frac, sign,
                                                                  out double dXda, out double dYda))
                        {
                            double anglePerTick = ROTATE_RADPERTICK * thetaVel * ROTATE_FOLLOW_MS;
                            ffX = ROTATE_FOLLOW_FF_X * dXda * anglePerTick;
                            ffY = ROTATE_FOLLOW_FF_Y * dYda * anglePerTick;
                        }

                        // Head start: hold X/Y for the first ROTATE_XY_DELAY_MS so Θ visibly leads
                        // (Θ keeps jogging; X/Y catches up once it engages). Follow regardless once
                        // Θ is done, so a tiny angle that completes during the head start still pins.
                        if (elapsed >= ROTATE_XY_DELAY_MS || thetaDone)
                        {
                            if (Math.Abs(errX) > ROTATE_FOLLOW_MAXERR || Math.Abs(errY) > ROTATE_FOLLOW_MAXERR)
                                throw new DriveException($"X/Y fell too far behind Θ (err {errX:N0},{errY:N0}) — aborting. " +
                                                         "Lower Θ speed, or the handedness/polarity is wrong.");

                            int velX = FollowVel(errX, ffX), velY = FollowVel(errY, ffY);
                            CommandFollow(AxisId.X, velX);
                            CommandFollow(AxisId.Y, velY);
                            prevVelX = velX; prevVelY = velY;   // TEMP K-capture: for next tick's m estimate

                            // Tuning diagnostic. Peak error is dominated by the start/stop accel
                            // transient (the lookahead can't fully null it — soften Θ accel for that).
                            // What the lookahead DOES control is the sustained offset over the
                            // constant-velocity middle, so sample the PIN error — actual X/Y minus the
                            // target for Θ's TRUE CURRENT angle (NOT the lookahead target, whose own
                            // tracking error stays ≈ the loop lag by design) — over frac∈[0.4,0.6] and
                            // tune the lookahead to zero it.
                            if (!thetaDone)
                            {
                                if (Math.Abs(errX) > peakErrX) peakErrX = Math.Abs(errX);
                                if (Math.Abs(errY) > peakErrY) peakErrY = Math.Abs(errY);
                                if (Math.Abs(velX) >= ROTATE_FOLLOW_VMAX || Math.Abs(velY) >= ROTATE_FOLLOW_VMAX)
                                    velSaturated = true;
                                double curFrac = Math.Clamp((double)actualProg / totalThetaTicks, 0.0, 1.0);
                                if (curFrac >= 0.4 && curFrac <= 0.6 &&
                                    CrosshairRotation.TryXyTarget(a, cx, cy, s0x, s0y, deltaRad * curFrac, sign,
                                                                  out long curTxUser, out long curTyUser))
                                {
                                    long actualUserX = txUser - errX, actualUserY = tyUser - errY;
                                    pinSumX += actualUserX - curTxUser;
                                    pinSumY += actualUserY - curTyUser;
                                    pinN++;
                                }
                            }

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
            {
                long pinAvgX = pinN > 0 ? pinSumX / pinN : 0;
                long pinAvgY = pinN > 0 ? pinSumY / pinN : 0;
                AppendLog($"  Θ {thetaStart:N0} → {thetaEnd:N0} (Δ {thetaEnd - thetaStart:+0;-0}); final X/Y follow err {lastErrX:N0},{lastErrY:N0}.");
                AppendLog($"  mid-rotation pin err {pinAvgX:+0;-0},{pinAvgY:+0;-0}; peak follow {peakErrX:N0},{peakErrY:N0}{(velSaturated ? " — SATURATED: lower Θ speed / raise VMAX" : "")}.");
                AppendLog($"  → tune ROTATE_LOOKAHEAD_MS to zero the pin err (feature leads → lower; lags → raise). Split per-axis if X and Y disagree.");
                double mX = kVtX > 0 ? kDispX / kVtX : 0, mY = kVtY > 0 ? kDispY / kVtY : 0;
                double ffXsug = mX > 0 ? 1.0 / (mX * ROTATE_FOLLOW_MS) : 0, ffYsug = mY > 0 ? 1.0 / (mY * ROTATE_FOLLOW_MS) : 0;
                AppendLog($"  TEMP K-capture: m {mX:0.0000},{mY:0.0000} steps/(vu·ms) → set ROTATE_FOLLOW_FF_X={ffXsug:0.0000}, ROTATE_FOLLOW_FF_Y={ffYsug:0.0000}.");
            }
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

            int thetaDir = Math.Sign(direction);

            _holdRotateStop = false;
            using var busyScope = BeginBusy();
            AppendLog($"Rotate {(thetaDir > 0 ? "⟳" : "⟲")} about crosshair (hold; centre X={cx:N0} Y={cy:N0}, sign {sign:+0;-0}, lookahead {ROTATE_LOOKAHEAD_MS:0}ms)...");
            bool ok = await RunDriveOp(() =>
            {
                _motion!.RecoverIfQuickStopped(AxisId.Theta);
                _motion.RecoverIfQuickStopped(AxisId.X);
                _motion.RecoverIfQuickStopped(AxisId.Y);

                long s0x = ToUser(AxisId.X, _motion.GetStatus(AxisId.X).Position);   // live start pose (USER frame)
                long s0y = ToUser(AxisId.Y, _motion.GetStatus(AxisId.Y).Position);
                long thetaStart = _motion.GetStatus(AxisId.Theta).Position;
                _motion.JogAt(AxisId.Theta, thetaDir, ROTATE_THETA_SPEED);   // Θ runs until released
                try
                {
                    // On release, Θ is stopped but we keep following X/Y to Θ's ACTUAL final angle
                    // (including its decel coast) until X/Y catches up — same settle as the fixed
                    // rotate, so the feature ends pinned instead of drifting further on release.
                    bool releasing = false;
                    int elapsed = 0, settled = 0;
                    var ffClock = System.Diagnostics.Stopwatch.StartNew();
                    long ffPrevMs = 0;                   // previous tick's timestamp, for the Θ-velocity dt
                    long prevTheta = thetaStart;         // last tick's Θ, for the velocity estimate
                    double thetaVel = 0.0;               // smoothed Θ velocity (ticks/ms) for the lookahead
                    bool velSeeded = false;
                    while (true)
                    {
                        if (_holdRotateStop && !releasing)
                        {
                            _motion.Stop(AxisId.Theta);   // begin Θ deceleration; X/Y keeps following
                            releasing = true;
                        }

                        long currentTheta = _motion.GetStatus(AxisId.Theta).Position;
                        long nowMs = ffClock.ElapsedMilliseconds;
                        long dtMs = nowMs - ffPrevMs;

                        // Smoothed Θ velocity for the lookahead AND the analytic feedforward (EMA to
                        // reject Sleep-jitter/quantization spikes). 0.5 is the validated baseline;
                        // heavier smoothing (0.2) lagged the estimate and made the swing worse.
                        if (dtMs > 0)
                        {
                            double rawVel = (double)(currentTheta - prevTheta) / dtMs;
                            thetaVel = velSeeded ? 0.5 * thetaVel + 0.5 * rawVel : rawVel;
                            velSeeded = true;
                        }
                        prevTheta = currentTheta;

                        // Angle Θ is PREDICTED to reach a lookahead into the future; X/Y pins the
                        // crosshair for THAT, cancelling the follower's constant time-lag.
                        long lookaheadTheta = currentTheta + (long)(thetaVel * ROTATE_LOOKAHEAD_MS);
                        double angleRad = ROTATE_RADPERTICK * (lookaheadTheta - thetaStart);
                        if (!CrosshairRotation.TryXyTarget(a, cx, cy, s0x, s0y, angleRad, sign, out long txUser, out long tyUser))
                            throw new DriveException("calibration affine is degenerate — recalibrate the camera scale.");
                        RejectIfOutOfTravel(AxisId.X, ToRaw(AxisId.X, txUser));
                        RejectIfOutOfTravel(AxisId.Y, ToRaw(AxisId.Y, tyUser));

                        long errX = txUser - ToUser(AxisId.X, _motion.GetStatus(AxisId.X).Position);
                        long errY = tyUser - ToUser(AxisId.Y, _motion.GetStatus(AxisId.Y).Position);

                        // ANALYTIC velocity feedforward (0 by default — see the FF_X/FF_Y note). Exact
                        // pin-target step-velocity from the closed-form geometry derivative × the Θ
                        // tick-rate; noise-free, in steps per nominal tick. Skipped while gains are 0.
                        ffPrevMs = nowMs;
                        double ffX = 0.0, ffY = 0.0;
                        if ((ROTATE_FOLLOW_FF_X != 0.0 || ROTATE_FOLLOW_FF_Y != 0.0) &&
                            CrosshairRotation.TryXyTargetVelocity(a, cx, cy, s0x, s0y, angleRad, sign,
                                                                  out double dXda, out double dYda))
                        {
                            double anglePerTick = ROTATE_RADPERTICK * thetaVel * ROTATE_FOLLOW_MS;
                            ffX = ROTATE_FOLLOW_FF_X * dXda * anglePerTick;
                            ffY = ROTATE_FOLLOW_FF_Y * dYda * anglePerTick;
                        }

                        // Head start: hold X/Y for the first ROTATE_XY_DELAY_MS so Θ visibly leads.
                        // Once releasing, follow regardless so the release-settle pins the feature.
                        if (elapsed >= ROTATE_XY_DELAY_MS || releasing)
                        {
                            if (Math.Abs(errX) > ROTATE_FOLLOW_MAXERR || Math.Abs(errY) > ROTATE_FOLLOW_MAXERR)
                                throw new DriveException($"X/Y fell too far behind Θ (err {errX:N0},{errY:N0}) — aborting. " +
                                                         "Lower Θ speed, or the handedness/polarity is wrong.");
                            CommandFollow(AxisId.X, FollowVel(errX, ffX));
                            CommandFollow(AxisId.Y, FollowVel(errY, ffY));

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

        // X/Y follow velocity (signed): feedforward (the target's own per-tick motion) plus
        // proportional trim on the residual error. With FF disabled this is a pure P loop; the
        // lookahead pre-shifts the target to cancel the P loop's constant time-lag. Hold only when
        // the target is parked; otherwise clamp magnitude to [MinVel, Vmax].
        private static int FollowVel(long errRaw, double velFf)
        {
            // Parked: target not moving and error within the dead-band → hold (no dither).
            if (Math.Abs(velFf) < 1.0 && Math.Abs(errRaw) <= ROTATE_FOLLOW_DEADBAND) return 0;
            double vel = velFf + ROTATE_FOLLOW_GAIN * errRaw;
            double mag = Math.Abs(vel);
            if (mag < 1.0) return 0;
            mag = Math.Min(ROTATE_FOLLOW_VMAX, Math.Max(ROTATE_FOLLOW_MINVEL, mag));
            return Math.Sign(vel) * (int)mag;
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
