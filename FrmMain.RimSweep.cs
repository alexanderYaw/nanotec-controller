using System;
using System.Threading.Tasks;

namespace NanotecController
{
    /// <summary>
    /// FrmMain — CONTINUOUS Θ sweep of the wafer rim, for the notch search. Θ jogs without stopping
    /// while Y follows the rim's known path, so the whole circumference passes the camera in one
    /// revolution rather than a few hundred step-and-settle stops. A revolution is ~112 s of rotation
    /// whatever else happens, so only what gets ADDED to that is worth optimising.
    ///
    /// NOT RotateAboutCrosshairAsync — that pins the material point under the crosshair, which keeps
    /// the same patch of wafer in view and would teach the sweep nothing. Here the camera holds
    /// station on the rim CIRCLE while the wafer turns underneath, a completely different target,
    /// supplied by the caller as stationYAt (see <see cref="RimStation"/>).
    ///
    /// X does not move and is not armed: only the line X = X min crosses the rim circle, so the only
    /// freedom is Y, and its path is a ~10 mm excursion at ≤365 steps/s — the gentlest thing the
    /// follower has been asked to do.
    ///
    /// This is a SECOND LOOP rather than a refactor of the two in FrmMain.Rotation.cs. The TUNING is
    /// what is worth sharing, and being a partial of the same class this file uses those constants
    /// directly, so no measured value is duplicated. The loop bodies genuinely differ — one axis not
    /// two, an analytic pin target vs an opaque delegate, a stop condition that is a vision result
    /// rather than an angle — and folding three shapes into one parameterised loop would risk a
    /// silent regression in the joystick twist and the crosshair rotate to save a control structure.
    /// </summary>
    public partial class FrmMain
    {
        #region Sweep tuning

        /// <summary>A sweep may legitimately run a full revolution plus ramps, which ROTATE_MAX_MS is
        /// too tight for once Θ runs slower than its cap.</summary>
        private const int SWEEP_MAX_MS = 300000;

        /// <summary>Half-interval of the central difference estimating the station's velocity for the
        /// feedforward. The target is an analytic function of Θ, so this differences the FUNCTION at
        /// two angles — NOT the diff-of-quantised-commands that put noise on the crosshair rotate's
        /// velocity. Its only error is the ±1 step rounding inside WaferCentreAt.</summary>
        private const double SWEEP_FF_HALF_DEG = 1.0;

        #endregion

        private long _sweepThetaTicks;

        /// <summary>
        /// Θ as of the sweep loop's last tick, published lock-free for the frame tagger.
        ///
        /// The vision side must NOT call <see cref="TryReadThetaNow"/> during a sweep: NanoLib access
        /// is serialized on one channel, the sweep owns it for the whole revolution, and a read from
        /// the grab thread would race it. Reading this instead costs nothing and is at most one
        /// ROTATE_FOLLOW_MS tick stale — 25 ms, which at the rim's 5.6 mm/s is 0.14 mm.
        /// </summary>
        public long SweepThetaTicks => System.Threading.Volatile.Read(ref _sweepThetaTicks);

        /// <summary>Θ's velocity cap, from the axis table. Exposed because it is what floors the
        /// search time, so the caller can say so rather than guessing.</summary>
        public int ThetaSpeedMax => TableAxes.For(AxisId.Theta)?.JogVelocityMax ?? 400;

        /// <summary>What a sweep did. <paramref name="StoppedEarly"/> distinguishes "the caller's
        /// predicate fired" — a notch was seen — from "a full revolution went by and nothing did".</summary>
        public readonly record struct RimSweepResult(
            bool Completed, bool StoppedEarly, double SweptDegrees,
            long PeakFollowErr, bool Saturated);

        /// <summary>
        /// Turns Θ continuously in <paramref name="thetaDir"/> while Y follows
        /// <paramref name="stationYAt"/> — a USER-frame Y for a given CHUCK angle in degrees — until
        /// <paramref name="stopWhen"/> returns true or <paramref name="maxDegrees"/> have been swept.
        ///
        /// <paramref name="stopWhen"/> is polled on the drive thread every tick, so keep it to
        /// reading a flag another thread sets. <paramref name="stationYAt"/> returning null aborts the
        /// sweep — it means the station line no longer reaches the rim, which is a calibration fault,
        /// not something to drive through.
        ///
        /// Θ ramps down rather than halting when the predicate fires, so the stop is smooth; the
        /// caller gets the overshoot back in <see cref="RimSweepResult.SweptDegrees"/> and can back
        /// up. Always stops Θ and Y and restores their ramps on exit.
        /// </summary>
        public async Task<RimSweepResult> SweepRimAsync(
            Func<double, double?> stationYAt, Func<bool> stopWhen, int thetaDir, double maxDegrees,
            int thetaSpeed)
        {
            var failed = new RimSweepResult(false, false, 0, 0, false);
            if (!CanMoveCalibration) { AppendLog("Rim sweep: needs the drives enabled and idle."); return failed; }
            if (thetaDir == 0 || maxDegrees <= 0) { AppendLog("Rim sweep: nothing to sweep."); return failed; }
            if (!TryCurrentUser(AxisId.Y, out _)) { AppendLog("Rim sweep: Y position not available yet."); return failed; }

            int dir = Math.Sign(thetaDir);
            int spd = Math.Clamp(thetaSpeed, ROTATE_THETA_MIN_SPEED,
                                 TableAxes.For(AxisId.Theta)?.JogVelocityMax ?? 400);
            long limitTicks = Math.Abs(
                CrosshairRotation.DegreesToChuckTicks(maxDegrees, CrosshairRotation.ChuckTicksPerRev));

            using var busyScope = BeginBusy();
            AppendLog($"Rim sweep {(dir > 0 ? "⟳" : "⟲")} up to {maxDegrees:F1}° at Θ={spd} (continuous, Y-following)...");

            bool stoppedEarly = false;
            long swept = 0, peakErr = 0;
            bool saturated = false;

            bool ok = await RunDriveOp(() =>
            {
                _motion!.RecoverIfQuickStopped(AxisId.Theta);
                _motion.RecoverIfQuickStopped(AxisId.Y);

                long thetaStart = _motion.GetStatus(AxisId.Theta).Position;
                System.Threading.Volatile.Write(ref _sweepThetaTicks, thetaStart);

                // Read the ramps here, apply/restore inside the guard — same rule as the rotates:
                // nothing that MUTATES drive state or starts motion may sit outside the try, or a
                // throw part-way through arming could leave an axis turning with no finally to stop it.
                var savedRampTheta = _motion.GetProfileRamp(AxisId.Theta);
                var savedRampY = _motion.GetProfileRamp(AxisId.Y);
                try
                {
                    _motion.SetProfileRamp(AxisId.Theta, ROTATE_THETA_ACCEL, ROTATE_THETA_ACCEL);
                    _motion.SetProfileRamp(AxisId.Y, ROTATE_XY_ACCEL, ROTATE_XY_ACCEL);
                    _motion.JogAt(AxisId.Theta, dir, ROTATE_THETA_MIN_SPEED);
                    _motion.JogAt(AxisId.Y, +1, 0);   // armed at zero velocity: servo hold, not halt
                    _followVelY = 0;

                    bool releasing = false;       // Θ halted; Y settling onto the coast
                    long rampDownStartMs = -1;
                    int cmdAtRelease = 0;
                    int lastThetaCmd = ROTATE_THETA_MIN_SPEED;
                    int elapsed = 0, settled = 0;
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    long prevMs = 0;
                    double thetaModel = thetaStart;
                    double thetaCmdVelPrev = dir * (double)ROTATE_THETA_MIN_SPEED / 1000.0;

                    while (true)
                    {
                        if (_stopRequested) throw new OperationCanceledException("Rim sweep stopped by operator.");

                        long currentTheta = _motion.GetPosition(AxisId.Theta);
                        System.Threading.Volatile.Write(ref _sweepThetaTicks, currentTheta);
                        long nowMs = clock.ElapsedMilliseconds;
                        long dtMs = nowMs - prevMs;
                        prevMs = nowMs;

                        // Filtered Θ for the target: predict with the velocity that was in force over
                        // the elapsed interval, then blend toward the measurement. Same reasoning as
                        // the rotates, though the target here is far less sensitive to Θ noise —
                        // dY/dΘ peaks at 114 steps/deg against the pin target's thousands.
                        thetaModel += thetaCmdVelPrev * dtMs;
                        thetaModel += ROTATE_THETA_BLEND * (currentTheta - thetaModel);

                        swept = Math.Abs(currentTheta - thetaStart);
                        bool doneSweeping = swept >= limitTicks;
                        if ((doneSweeping || stopWhen()) && rampDownStartMs < 0)
                        {
                            stoppedEarly = !doneSweeping;
                            rampDownStartMs = nowMs;
                            cmdAtRelease = lastThetaCmd;
                        }

                        // Θ setpoint soft-ramp: up over the first RAMP_MS, and on stop down over
                        // RAMP_MS from wherever it was before halting. Keeping accel/decel inside the
                        // follower's bandwidth is what stops Y swinging out at either end.
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
                                    _motion.Stop(AxisId.Theta);
                                    releasing = true;
                                    thetaCmd = lastThetaCmd;
                                }
                            }
                            else
                            {
                                double upFrac = ROTATE_THETA_RAMP_MS > 0
                                    ? Math.Clamp(nowMs / ROTATE_THETA_RAMP_MS, 0.0, 1.0)
                                    : 1.0;
                                thetaCmd = (int)Math.Max(ROTATE_THETA_MIN_SPEED, spd * upFrac);
                            }
                            if (thetaCmd != lastThetaCmd)
                            {
                                _motion.UpdateJogVelocity(AxisId.Theta, dir, thetaCmd);
                                lastThetaCmd = thetaCmd;
                            }
                        }

                        double thetaCmdVel = releasing ? 0.0 : dir * lastThetaCmd / 1000.0;
                        thetaCmdVelPrev = thetaCmdVel;

                        // The angle Θ is predicted to reach a lookahead into the future, in CHUCK
                        // degrees — the frame stationYAt and WaferCentreAt both work in.
                        double aheadTicks = thetaModel + thetaCmdVel * ROTATE_LOOKAHEAD_MS;
                        double aheadDeg = CrosshairRotation.ChuckTicksToDegrees(
                            (long)Math.Round(aheadTicks), CrosshairRotation.ChuckTicksPerRev);

                        if (stationYAt(aheadDeg) is not double tyUser)
                            throw new DriveException(
                                "the station line no longer reaches the wafer rim — check the stored " +
                                "wafer radius and offset, and the station X.");
                        long tyUserRounded = (long)Math.Round(tyUser);
                        RejectIfOutOfTravel(AxisId.Y, ToRaw(AxisId.Y, tyUserRounded));

                        long errY = tyUserRounded - ToUser(AxisId.Y, _motion.GetPosition(AxisId.Y));

                        // Velocity feedforward: the station's own dY/dΘ, by central difference of the
                        // analytic path (see SWEEP_FF_HALF_DEG), converted to steps per radian and
                        // then to steps per nominal loop tick — the same units as the P-term's error.
                        double ffY = 0.0;
                        if (ROTATE_FOLLOW_FF_Y != 0.0 &&
                            stationYAt(aheadDeg + SWEEP_FF_HALF_DEG) is double yPlus &&
                            stationYAt(aheadDeg - SWEEP_FF_HALF_DEG) is double yMinus)
                        {
                            double dYdDeg = (yPlus - yMinus) / (2.0 * SWEEP_FF_HALF_DEG);
                            double dYdRad = dYdDeg * 180.0 / Math.PI;
                            double anglePerTick = ROTATE_RADPERTICK * thetaCmdVel * ROTATE_FOLLOW_MS;
                            ffY = ROTATE_FOLLOW_FF_Y * dYdRad * anglePerTick;
                        }

                        if (Math.Abs(errY) > ROTATE_FOLLOW_MAXERR)
                            throw new DriveException($"Y fell too far behind Θ (err {errY:N0}) — aborting. " +
                                                     "Lower the sweep speed, or the stored wafer offset is wrong.");

                        int velY = FollowVel(errY, ffY);
                        CommandFollow(AxisId.Y, velY);

                        if (!releasing)
                        {
                            if (Math.Abs(errY) > peakErr) peakErr = Math.Abs(errY);
                            if (Math.Abs(velY) >= ROTATE_FOLLOW_VMAX) saturated = true;
                        }
                        else
                        {
                            settled += ROTATE_FOLLOW_MS;
                            if (Math.Abs(errY) <= ROTATE_FOLLOW_DEADBAND || settled >= ROTATE_SETTLE_MS) break;
                        }

                        if (elapsed >= SWEEP_MAX_MS) break;
                        System.Threading.Thread.Sleep(ROTATE_FOLLOW_MS);
                        elapsed += ROTATE_FOLLOW_MS;
                    }
                }
                finally
                {
                    try { _motion.Stop(AxisId.Theta); } catch (DriveException) { }
                    try { _motion.Stop(AxisId.Y); } catch (DriveException) { }
                    try
                    {
                        _motion.SetProfileRamp(AxisId.Theta, savedRampTheta.Accel, savedRampTheta.Decel);
                        _motion.SetProfileRamp(AxisId.Y, savedRampY.Accel, savedRampY.Decel);
                    }
                    catch (DriveException) { }
                    try { System.Threading.Volatile.Write(ref _sweepThetaTicks, _motion.GetPosition(AxisId.Theta)); }
                    catch (DriveException) { }
                }
            });

            double sweptDeg = CrosshairRotation.ChuckTicksToDegrees(swept, CrosshairRotation.ChuckTicksPerRev);
            if (ok)
                AppendLog($"  swept {sweptDeg:F2}°{(stoppedEarly ? " (stopped early)" : "")}; " +
                          $"peak Y follow err {peakErr:N0}{(saturated ? " — SATURATED: lower Θ speed" : "")}.");
            else
                AppendLog("Rim sweep FAILED — see error above.");

            return new RimSweepResult(ok, ok && stoppedEarly, sweptDeg, peakErr, saturated);
        }
    }
}
