using System;
using System.Threading.Tasks;

namespace NanotecController
{
    // FrmMain — rotation ABOUT the camera crosshair. The Θ axis rotates the chuck about its
    // own mechanical centre; combining it with an X/Y shift (CrosshairRotation) makes the
    // chuck centre orbit the crosshair, which pins the point under the crosshair while Θ turns.
    // Runs CONTINUOUS, like the joystick: Θ jogs with a soft-ramped setpoint while a fast loop
    // steers X/Y (also in velocity mode) toward the pin position, driven by an ANALYTIC velocity
    // feedforward plus proportional trim — so all three axes move together, not step-and-settle.
    // The hot loop touches the drives with velocity-only writes and position-only reads (arming
    // mode/controlword once up front) to keep the SDO traffic — and so the loop period — down.
    // FrmMain owns this because NanoLib access must be serialized. (Partial of FrmMain.)
    public partial class FrmMain
    {
        // Θ jog velocity during a rotation (drive units = steps/s, per the K-capture).
        private int _rotateThetaSpeed = 800;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int RotateThetaSpeed
        {
            get => _rotateThetaSpeed;
            set => _rotateThetaSpeed = Math.Clamp(value, 50, 2000);
        }
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
        // held at zero velocity. GAIN/VMAX likely need tuning on hardware (units aren't mm/deg yet).
        private const int ROTATE_FOLLOW_MS = 25;
        // GAIN is velocity-units per step of error. Too high overshoots: at velocity V the axis
        // travels ~K·V·dt steps per tick, so a gain above ~1/(K·dt) ≈ 40 ("deadbeat") oscillates.
        // With FF carrying the baseline velocity, GAIN only trims the residual — any FF velocity
        // error Δv parks a standing offset of Δv/GAIN steps — but it ALSO multiplies the error
        // NOISE (read-timing skew × axis speed, so ∝ pin radius) straight into the velocity
        // command. Measured at GAIN=10 on a far-radius rotate: |Δvel| ≈ 108 vu/tick on a 574 vu
        // baseline — 19% modulation at 40 Hz, the visible jitter — while the FF gains are within
        // ~3% (K-capture), so a soft trim suffices: at 4, worst-case parked offset ≈ 3%·600/4 ≈
        // 5 steps. Raise only if the mid-rotation pin err grows; expect jitter back in return.
        private const double ROTATE_FOLLOW_GAIN = 4.0;
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
        // AXIS. The FF term is the ANALYTIC target velocity (CrosshairRotation.TryXyTargetVelocity
        // × Θ tick-rate) rather than a numeric difference of quantized targets — so it is noise-free,
        // and the old fragility (a bad estimate fighting the P-term) is gone. ENABLED: FF carries the
        // baseline follow velocity and cancels the follower's constant time-lag, which is why
        // ROTATE_LOOKAHEAD_MS is 0 (running both would double-compensate). K is a single per-axis
        // scalar — steps moved per velocity-unit per second — measured by a temporary K-capture
        // diagnostic (since removed); re-measure if the drives' velocity scaling ever changes.
        private const double ROTATE_FOLLOW_FF_X = 39.84;   // measured via K-capture (1 vu = 1 step/s ⇒ ≈40)
        private const double ROTATE_FOLLOW_FF_Y = 39.60;
        // Master (Θ) lookahead: project Θ this many ms into the future (via the COMMANDED setpoint
        // velocity) before computing the X/Y pin target. What's left for it to cover is NOT servo
        // lag (the drive-side ramps + commanded-setpoint FF reduced that to ~2 ms) but MECHANICAL
        // COMPLIANCE: under load the stage elastically lags the motor encoders by a
        // velocity-proportional twist that winds out during the move and springs back at stop —
        // invisible to every motor-side metric, so this is tuned by the CAMERA, not the log.
        // Bracketed empirically: 30 ms → feature visibly LEADS; 0 ms → visibly LAGS by about as
        // much ⇒ optimum ≈ 15. NOTE the 'mid-rotation pin err' below is motor-side and SHOULD
        // read ≈ +lookahead×velocity when the camera is happy — do not re-zero it against that.
        private const double ROTATE_LOOKAHEAD_MS = 0.0;
        // Complementary filter on the Θ used for the X/Y pin target. Each tick the model is
        // PREDICTED forward with the commanded setpoint velocity (exact and lag-free — we command
        // it) and then corrected by this fraction of the remaining measurement error (so it can
        // never drift from the real Θ). Raw Θ reads carry a few ticks of timing/quantization noise,
        // and the pin target amplifies Θ noise by the pin RADIUS — which is the command jitter seen
        // when rotating about a point far from the chuck centre. A plain EMA would LAG a moving Θ
        // and reintroduce the swing; prediction-from-the-setpoint doesn't. 0.15 ⇒ ~170 ms correction
        // time-constant (well inside the settle window) and ~7× quieter targets. 1.0 disables (raw).
        private const double ROTATE_THETA_BLEND = 0.15;
        // Drive-side profile accel/decel (0x6083/0x6084, counts/s²) applied to all three axes for
        // the duration of a rotation and restored on exit. These bound how fast the drive chases
        // each new 0x60FF target; the stored default was an unmodeled lag/jerk on every 25 ms
        // velocity step. X/Y: high enough to reach even a full VMAX step within ~half a tick
        // (3200/0.0125 ≈ 256k). Θ: high enough that the actual velocity tracks the soft-ramped
        // setpoint within a few ms — which the commanded-setpoint FF now assumes.
        private const int ROTATE_XY_ACCEL = 250000;
        private const int ROTATE_THETA_ACCEL = 20000;
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
            bool ok = await RunDriveOp(() =>
            {
                _motion!.RecoverIfQuickStopped(AxisId.Theta);
                _motion.RecoverIfQuickStopped(AxisId.X);
                _motion.RecoverIfQuickStopped(AxisId.Y);

                thetaStart = _motion.GetStatus(AxisId.Theta).Position;
                // Rotation-specific drive ramps (saved here, restored in finally) so each velocity
                // update is reached within a tick instead of chased on the drives' default ramp.
                var savedRampTheta = _motion.GetProfileRamp(AxisId.Theta);
                var savedRampX = _motion.GetProfileRamp(AxisId.X);
                var savedRampY = _motion.GetProfileRamp(AxisId.Y);
                _motion.SetProfileRamp(AxisId.Theta, ROTATE_THETA_ACCEL, ROTATE_THETA_ACCEL);
                _motion.SetProfileRamp(AxisId.X, ROTATE_XY_ACCEL, ROTATE_XY_ACCEL);
                _motion.SetProfileRamp(AxisId.Y, ROTATE_XY_ACCEL, ROTATE_XY_ACCEL);
                // Arm all three axes in profile-velocity run state ONCE; inside the loop only the
                // 0x60FF target is rewritten (UpdateJogVelocity). Re-sending mode + controlword every
                // tick was ~2/3 of the loop's SDO traffic and a big source of period jitter.
                _motion.JogAt(AxisId.Theta, thetaDir, ROTATE_THETA_MIN_SPEED);   // Θ soft-ramps inside the loop
                _motion.JogAt(AxisId.X, +1, 0);   // X/Y armed at zero velocity (servo hold, no halt)
                _motion.JogAt(AxisId.Y, +1, 0);
                _followVelX = 0; _followVelY = 0;
                try
                {
                    bool thetaDone = false;
                    int lastThetaCmd = ROTATE_THETA_MIN_SPEED;
                    int elapsed = 0, settled = 0;
                    var ffClock = System.Diagnostics.Stopwatch.StartNew();
                    long ffPrevMs = 0;                   // previous tick's timestamp, for the Θ-velocity dt
                    long prevTheta = thetaStart;         // last tick's Θ, for the velocity estimate
                    double thetaVel = 0.0;               // smoothed Θ velocity (ticks/ms) for the lookahead
                    bool velSeeded = false;
                    double thetaModel = thetaStart;      // complementary-filtered Θ for the pin target
                    double thetaCmdVelPrev = thetaDir * (double)ROTATE_THETA_MIN_SPEED / 1000.0;   // vel in force over the elapsed dt
                    double maxThetaVel = 0.0;            // peak |thetaVel| seen (ticks/ms), for the ramp-down distance
                    while (true)
                    {
                        long currentTheta = _motion.GetPosition(AxisId.Theta);
                        long nowMs = ffClock.ElapsedMilliseconds;
                        long dtMs = nowMs - ffPrevMs;

                        // Measured Θ velocity (EMA) — now ONLY feeds the ramp-down distance below.
                        // The FF and the lookahead use the COMMANDED setpoint instead:
                        // differentiating the quantized position over a jittery Sleep period put
                        // noise on every X/Y velocity command (scaled by the pin radius — the
                        // visible jitter), and the EMA trailed the soft-ramps (the start/stop
                        // swing). The setpoint is noise-free and lag-free, and Θ actually tracks it
                        // now that its drive-side ramp (ROTATE_THETA_ACCEL) is explicit.
                        if (dtMs > 0)
                        {
                            double rawVel = (double)(currentTheta - prevTheta) / dtMs;
                            thetaVel = velSeeded ? 0.5 * thetaVel + 0.5 * rawVel : rawVel;
                            velSeeded = true;
                        }
                        prevTheta = currentTheta;

                        // Filtered Θ for the pin target: predict with the velocity that was in force
                        // over the elapsed interval, then blend toward the measurement (see
                        // ROTATE_THETA_BLEND — this is what de-noises the target at large pin radius).
                        thetaModel += thetaCmdVelPrev * dtMs;
                        thetaModel += ROTATE_THETA_BLEND * (currentTheta - thetaModel);

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
                            int thetaCmd = (int)Math.Max(ROTATE_THETA_MIN_SPEED, _rotateThetaSpeed * Math.Min(upFrac, downFrac));
                            if (thetaCmd != lastThetaCmd)
                            {
                                _motion.UpdateJogVelocity(AxisId.Theta, thetaDir, thetaCmd);
                                lastThetaCmd = thetaCmd;
                            }
                        }
                        // Θ velocity for the FF and the lookahead: the commanded setpoint (see the
                        // EMA note above), in ticks/ms (1 vu = 1 tick/s). Zero once Θ is stopped.
                        double thetaCmdVel = thetaDone ? 0.0 : thetaDir * lastThetaCmd / 1000.0;
                        thetaCmdVelPrev = thetaCmdVel;
                        long prog = (long)(thetaModel + thetaCmdVel * ROTATE_LOOKAHEAD_MS) - thetaStart;
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
                        long curXUser = ToUser(AxisId.X, _motion.GetPosition(AxisId.X));
                        long curYUser = ToUser(AxisId.Y, _motion.GetPosition(AxisId.Y));
                        long errX = txUser - curXUser;
                        long errY = tyUser - curYUser;
                        lastErrX = errX; lastErrY = errY;

                        // ANALYTIC velocity feedforward (gains per axis — see the FF_X/FF_Y note). The pin
                        // target's exact step-velocity is d(target)/d(angle) · d(angle)/dt, where the
                        // geometry derivative is closed-form (not a diff of quantized targets) and
                        // d(angle)/dt = ROTATE_RADPERTICK · the COMMANDED Θ rate. anglePerTick is that
                        // angular increment over one nominal loop tick, so ffX/ffY come out in steps-per-
                        // tick — the same units as the P-term's error. Skipped while the gains are 0.
                        ffPrevMs = nowMs;
                        double ffX = 0.0, ffY = 0.0;
                        if ((ROTATE_FOLLOW_FF_X != 0.0 || ROTATE_FOLLOW_FF_Y != 0.0) &&
                            CrosshairRotation.TryXyTargetVelocity(a, cx, cy, s0x, s0y, deltaRad * frac, sign,
                                                                  out double dXda, out double dYda))
                        {
                            double anglePerTick = ROTATE_RADPERTICK * thetaCmdVel * ROTATE_FOLLOW_MS;
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
                    try
                    {
                        _motion.SetProfileRamp(AxisId.Theta, savedRampTheta.Accel, savedRampTheta.Decel);
                        _motion.SetProfileRamp(AxisId.X, savedRampX.Accel, savedRampX.Decel);
                        _motion.SetProfileRamp(AxisId.Y, savedRampY.Accel, savedRampY.Decel);
                    }
                    catch (DriveException) { }
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

                long s0x = ToUser(AxisId.X, _motion.GetPosition(AxisId.X));   // live start pose (USER frame)
                long s0y = ToUser(AxisId.Y, _motion.GetPosition(AxisId.Y));
                long thetaStart = _motion.GetStatus(AxisId.Theta).Position;
                // Rotation-specific drive ramps (saved here, restored in finally) so each velocity
                // update is reached within a tick instead of chased on the drives' default ramp.
                var savedRampTheta = _motion.GetProfileRamp(AxisId.Theta);
                var savedRampX = _motion.GetProfileRamp(AxisId.X);
                var savedRampY = _motion.GetProfileRamp(AxisId.Y);
                _motion.SetProfileRamp(AxisId.Theta, ROTATE_THETA_ACCEL, ROTATE_THETA_ACCEL);
                _motion.SetProfileRamp(AxisId.X, ROTATE_XY_ACCEL, ROTATE_XY_ACCEL);
                _motion.SetProfileRamp(AxisId.Y, ROTATE_XY_ACCEL, ROTATE_XY_ACCEL);
                // Arm all three once (see the fixed-angle rotate); the loop then only rewrites
                // 0x60FF. Θ soft-ramps up over RAMP_MS and — on release — back down over RAMP_MS
                // before halting, so hold-to-rotate no longer swings out at either end.
                _motion.JogAt(AxisId.Theta, thetaDir, ROTATE_THETA_MIN_SPEED);
                _motion.JogAt(AxisId.X, +1, 0);   // X/Y armed at zero velocity (servo hold, no halt)
                _motion.JogAt(AxisId.Y, +1, 0);
                _followVelX = 0; _followVelY = 0;
                try
                {
                    // On release, Θ's setpoint ramps down and is then stopped, but we keep following
                    // X/Y to Θ's ACTUAL final angle (including its decel coast) until X/Y catches up —
                    // same settle as the fixed rotate, so the feature ends pinned instead of drifting.
                    bool releasing = false;            // Θ halted; X/Y settling onto the final angle
                    long rampDownStartMs = -1;         // ≥0 once the release ramp-down has begun
                    int cmdAtRelease = 0;              // Θ setpoint level the ramp-down starts from
                    int lastThetaCmd = ROTATE_THETA_MIN_SPEED;
                    int elapsed = 0, settled = 0;
                    var ffClock = System.Diagnostics.Stopwatch.StartNew();
                    long ffPrevMs = 0;                   // previous tick's timestamp, for the filter dt
                    double thetaModel = thetaStart;      // complementary-filtered Θ for the pin target
                    double thetaCmdVelPrev = thetaDir * (double)ROTATE_THETA_MIN_SPEED / 1000.0;   // vel in force over the elapsed dt
                    while (true)
                    {
                        long currentTheta = _motion.GetPosition(AxisId.Theta);
                        long nowMs = ffClock.ElapsedMilliseconds;
                        long dtMs = nowMs - ffPrevMs;
                        ffPrevMs = nowMs;

                        // Filtered Θ for the pin target: predict with the velocity that was in force
                        // over the elapsed interval, then blend toward the measurement (see
                        // ROTATE_THETA_BLEND — de-noises the target at large pin radius).
                        thetaModel += thetaCmdVelPrev * dtMs;
                        thetaModel += ROTATE_THETA_BLEND * (currentTheta - thetaModel);

                        if (_holdRotateStop && rampDownStartMs < 0)
                        {
                            rampDownStartMs = nowMs;      // begin the Θ ramp-down; X/Y keeps following
                            cmdAtRelease = lastThetaCmd;
                        }

                        // Θ setpoint soft-ramp (same reason as the fixed-angle rotate: accel/decel
                        // kept within the follower's bandwidth kills the swing-out): up over the
                        // first RAMP_MS; on release, down over RAMP_MS from wherever it was, THEN halt.
                        if (!releasing)
                        {
                            int thetaCmd;
                            if (rampDownStartMs >= 0)
                            {
                                double downFrac = ROTATE_THETA_RAMP_MS > 0
                                    ? 1.0 - (nowMs - rampDownStartMs) / ROTATE_THETA_RAMP_MS
                                    : 0.0;
                                thetaCmd = (int)(cmdAtRelease * Math.Max(0.0, downFrac));
                                if (thetaCmd < ROTATE_THETA_MIN_SPEED)
                                {
                                    _motion.Stop(AxisId.Theta);   // ramp done; X/Y settles onto the coast
                                    releasing = true;
                                    thetaCmd = lastThetaCmd;      // no further Θ writes
                                }
                            }
                            else
                            {
                                double upFrac = ROTATE_THETA_RAMP_MS > 0
                                    ? Math.Clamp(nowMs / ROTATE_THETA_RAMP_MS, 0.0, 1.0)
                                    : 1.0;
                                thetaCmd = (int)Math.Max(ROTATE_THETA_MIN_SPEED, _rotateThetaSpeed * upFrac);
                            }
                            if (thetaCmd != lastThetaCmd)
                            {
                                _motion.UpdateJogVelocity(AxisId.Theta, thetaDir, thetaCmd);
                                lastThetaCmd = thetaCmd;
                            }
                        }

                        // Θ velocity for the FF and the lookahead: the COMMANDED setpoint, in
                        // ticks/ms (1 vu = 1 tick/s) — noise-free and lag-free where differencing
                        // the quantized position over a jittery Sleep period was neither (see the
                        // fixed-angle loop). Zero once Θ has been halted (releasing).
                        double thetaCmdVel = releasing ? 0.0 : thetaDir * lastThetaCmd / 1000.0;
                        thetaCmdVelPrev = thetaCmdVel;

                        // Angle Θ is PREDICTED to reach a lookahead into the future; X/Y pins the
                        // crosshair for THAT, cancelling the follower's transport delay.
                        double lookaheadTheta = thetaModel + thetaCmdVel * ROTATE_LOOKAHEAD_MS;
                        double angleRad = ROTATE_RADPERTICK * (lookaheadTheta - thetaStart);
                        if (!CrosshairRotation.TryXyTarget(a, cx, cy, s0x, s0y, angleRad, sign, out long txUser, out long tyUser))
                            throw new DriveException("calibration affine is degenerate — recalibrate the camera scale.");
                        RejectIfOutOfTravel(AxisId.X, ToRaw(AxisId.X, txUser));
                        RejectIfOutOfTravel(AxisId.Y, ToRaw(AxisId.Y, tyUser));

                        long errX = txUser - ToUser(AxisId.X, _motion.GetPosition(AxisId.X));
                        long errY = tyUser - ToUser(AxisId.Y, _motion.GetPosition(AxisId.Y));

                        // ANALYTIC velocity feedforward (gains per axis — see the FF_X/FF_Y note). Exact
                        // pin-target step-velocity from the closed-form geometry derivative × the
                        // COMMANDED Θ rate; noise-free, in steps per nominal tick. Skipped while gains are 0.
                        double ffX = 0.0, ffY = 0.0;
                        if ((ROTATE_FOLLOW_FF_X != 0.0 || ROTATE_FOLLOW_FF_Y != 0.0) &&
                            CrosshairRotation.TryXyTargetVelocity(a, cx, cy, s0x, s0y, angleRad, sign,
                                                                  out double dXda, out double dYda))
                        {
                            double anglePerTick = ROTATE_RADPERTICK * thetaCmdVel * ROTATE_FOLLOW_MS;
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
                    try
                    {
                        _motion.SetProfileRamp(AxisId.Theta, savedRampTheta.Accel, savedRampTheta.Decel);
                        _motion.SetProfileRamp(AxisId.X, savedRampX.Accel, savedRampX.Decel);
                        _motion.SetProfileRamp(AxisId.Y, savedRampY.Accel, savedRampY.Decel);
                    }
                    catch (DriveException) { }
                }
            });
            AppendLog(ok ? "Rotate (hold) stopped." : "Rotate (hold) FAILED — see error above.");
        }

        /// <summary>Ends a <see cref="HoldRotateAsync"/> (call from the button MouseUp / on focus loss).</summary>
        public void StopHoldRotate() => _holdRotateStop = true;

        // X/Y follow velocity (signed): analytic feedforward (the target's own velocity) carries
        // the baseline, proportional trim corrects the residual error. Hold only when the target
        // is parked; otherwise clamp magnitude to [MinVel, Vmax].
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

        // Last velocity commanded to each follow axis, so ticks where it hasn't changed skip the
        // SDO write entirely. Reset to 0 when the axes are armed at the start of a rotation.
        private int _followVelX, _followVelY;

        // Commands a raw-frame signed velocity as a 0x60FF-only update (X/Y have
        // InvertDirection=false, so the sign is the raw direction). Zero ramps the axis to a servo
        // hold WITHOUT the halt bit — the old stop/restart flipping around zero, where a reversing
        // axis crosses twice per revolution, was itself a jitter source. The axis must be armed
        // (JogAt) before the loop starts.
        private void CommandFollow(AxisId id, int signedVel)
        {
            if (id == AxisId.X) { if (signedVel == _followVelX) return; _followVelX = signedVel; }
            else                { if (signedVel == _followVelY) return; _followVelY = signedVel; }
            _motion!.UpdateJogVelocity(id, Math.Sign(signedVel), Math.Abs(signedVel));
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
