using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MotorControlApp
{
    // FrmMain — owns all calibration motion and persistence (the FrmCalibration window is
    // pure UI that calls these internal methods): Home All, Move To, Set Min/Max/Home
    // capture, Go Home, and the Y auto limit-find. FrmMain is the single owner because
    // NanoLib is single-channel and access must be serialized. (Partial of FrmMain.)
    public partial class FrmMain
    {
        // --- Calibration (separate window) ----------------------------------------

        private void calibButton_Click(object? sender, EventArgs e)
        {
            if (_calibWindow == null || _calibWindow.IsDisposed)
                _calibWindow = new FrmCalibration(this);
            _calibWindow.Show();
            _calibWindow.BringToFront();
        }

        private async void homeAllButton_Click(object? sender, EventArgs e) => await HomeAllAsync();

        /// <summary>
        /// Universal home: brings Z to its home FIRST (e.g. retract to a safe height) and
        /// waits for it, THEN moves X and Y to their homes simultaneously. Requires a home
        /// target for all three; aborts otherwise so X/Y never move before Z has retracted.
        /// </summary>
        internal async Task HomeAllAsync()
        {
            if (!CanMoveCalibration) return;
            long? zT = HomeTargetFor(AxisId.Z);
            long? xT = HomeTargetFor(AxisId.X);
            long? yT = HomeTargetFor(AxisId.Y);
            if (zT == null || xT == null || yT == null)
            {
                AppendLog("Home All: set Home for X, Y and Z first " +
                          $"(missing:{(xT == null ? " X" : "")}{(yT == null ? " Y" : "")}{(zT == null ? " Z" : "")}).");
                return;
            }

            int zSpd = HomeSpeedFor(AxisId.Z);
            int xSpd = HomeSpeedFor(AxisId.X);
            int ySpd = HomeSpeedFor(AxisId.Y);

            _busy = true; RefreshButtons();
            AppendLog($"Home All: Z → {zT.Value:N0} first, then X → {xT.Value:N0} & Y → {yT.Value:N0} together...");
            bool ok = await RunDriveOp(() =>
            {
                // 1) Z home first. ABORT before moving the table if Z does not actually
                //    arrive, so X/Y never traverse while Z is still down (collision).
                _motion!.MoveAbsolute(AxisId.Z, zT.Value, zSpd);
                if (!_motion.WaitForMotionComplete(AxisId.Z, FIND_TIMEOUT_MS))
                    throw new ChuckException("Z did not reach Home in time - aborting before X/Y move.");
                // 2) X and Y together: issue both moves, then wait for both to finish.
                _motion.MoveAbsolute(AxisId.X, xT.Value, xSpd);
                _motion.MoveAbsolute(AxisId.Y, yT.Value, ySpd);
                _motion.WaitForMotionComplete(AxisId.X, FIND_TIMEOUT_MS);
                _motion.WaitForMotionComplete(AxisId.Y, FIND_TIMEOUT_MS);
            });
            AppendLog(ok ? "Home All complete." : "Home All FAILED - see error above.");
            _busy = false; RestartTimers(); RefreshButtons();
        }

        /// <summary>
        /// Moves the table to manually-entered coordinates. Each axis is optional (blank =
        /// leave it where it is); entered values are validated against that axis's Min/Max
        /// limits and the WHOLE move is rejected if any is out of range. The entered axes
        /// move together. Coordinates are in the same drive units shown as Min/Max.
        /// </summary>
        internal async Task MoveToAsync(string xText, string yText, string zText)
        {
            if (!CanMoveCalibration) return;

            bool okX = TryCoord(AxisId.X, xText, out long? xTarget);
            bool okY = TryCoord(AxisId.Y, yText, out long? yTarget);
            bool okZ = TryCoord(AxisId.Z, zText, out long? zTarget);
            if (!okX || !okY || !okZ) return;   // bad number(s) already logged

            var targets = new List<(AxisId id, long pos)>();
            var errors = new List<string>();
            void Plan(AxisId id, long? val)
            {
                if (val == null) return;
                AxisCalibration cal = _calib.For(id);
                if (cal.Min.HasValue && val.Value < cal.Min.Value)
                    errors.Add($"{id} {val.Value:N0} < Min {cal.Min.Value:N0}");
                else if (cal.Max.HasValue && val.Value > cal.Max.Value)
                    errors.Add($"{id} {val.Value:N0} > Max {cal.Max.Value:N0}");
                else
                {
                    targets.Add((id, val.Value));
                    if (!cal.Min.HasValue || !cal.Max.HasValue)
                        AppendLog($"Note: {id} has no full limit range set - move not bounds-checked.");
                }
            }
            Plan(AxisId.X, xTarget); Plan(AxisId.Y, yTarget); Plan(AxisId.Z, zTarget);

            if (errors.Count > 0)
            {
                AppendLog("Move cancelled - out of range: " + string.Join("; ", errors));
                return;
            }
            if (targets.Count == 0) { AppendLog("Move: enter at least one coordinate."); return; }

            string desc = "";
            foreach ((AxisId id, long pos) t in targets)
                desc += (desc.Length > 0 ? ", " : "") + $"{t.id}={t.pos:N0}";

            _busy = true; RefreshButtons();
            AppendLog($"Move to: {desc}...");
            bool ok = await RunDriveOp(() =>
            {
                foreach ((AxisId id, long pos) t in targets) _motion!.MoveAbsolute(t.id, t.pos, HomeSpeedFor(t.id));
                foreach ((AxisId id, long pos) t in targets) _motion!.WaitForMotionComplete(t.id, FIND_TIMEOUT_MS);
            });
            AppendLog(ok ? "Move complete." : "Move FAILED - see error above.");
            _busy = false; RestartTimers(); RefreshButtons();
        }

        // Parses one optional coordinate field: blank → null (skip axis), valid → the value;
        // returns false (and logs) on a non-empty, unparseable entry.
        private bool TryCoord(AxisId id, string text, out long? val)
        {
            val = null;
            text = text?.Trim() ?? "";
            if (text.Length == 0) return true;
            if (long.TryParse(text, out long v)) { val = v; return true; }
            AppendLog($"Move: '{text}' is not a valid {id} coordinate.");
            return false;
        }

        /// <summary>The shared per-axis limits/home store (read by the calibration window).</summary>
        internal CalibrationStore Calibration => _calib;

        /// <summary>Reading a position needs the link up; capture is a UI-thread SDO read.</summary>
        internal bool CanCaptureCalibration => _connection.IsConnected && !_busy && _motion != null;

        /// <summary>Motion (find / go-home) needs the drives enabled and idle.</summary>
        internal bool CanMoveCalibration => _drivesEnabled && !_busy && _motion != null;

        /// <summary>Home target for an axis: centre of the limits for X/Y, explicit Home for Z.</summary>
        internal long? HomeTargetFor(AxisId id)
        {
            AxisCalibration c = _calib.For(id);
            return id == AxisId.Z ? c.Home : c.Center;
        }

        internal void SetCalibrationMin(AxisId id) => CaptureInto(id, isMax: false, isHome: false);
        internal void SetCalibrationMax(AxisId id) => CaptureInto(id, isMax: true, isHome: false);
        internal void SetCalibrationHome(AxisId id) => CaptureInto(id, isMax: false, isHome: true);

        private void CaptureInto(AxisId id, bool isMax, bool isHome)
        {
            if (!CanCaptureCalibration) return;
            long pos;
            try { pos = _motion!.GetStatus(id).Position; }
            catch (ChuckException ex) { AppendLog($"ERROR: read {id} position: {ex.Message}"); return; }

            AxisCalibration c = _calib.For(id);
            if (isHome) { c.Home = pos; AppendLog($"{id} Home set to {pos:N0}."); }
            else if (isMax) { c.Max = pos; AppendLog($"{id} Max limit set to {pos:N0}."); }
            else { c.Min = pos; AppendLog($"{id} Min limit set to {pos:N0}."); }
            TrySaveCalibration();
        }

        /// <summary>Moves an axis to its Home target (Profile Position) and waits for arrival.</summary>
        internal async Task GoHomeAsync(AxisId id)
        {
            if (!CanMoveCalibration) return;
            long? target = HomeTargetFor(id);
            if (target == null) { AppendLog($"{id}: set its limits/Home first."); return; }

            int speed = HomeSpeedFor(id);
            _busy = true; RefreshButtons();
            AppendLog($"Go Home {id} → target {target.Value:N0} at {speed}...");
            long before = 0, after = 0;
            bool reached = false;
            bool ok = await RunDriveOp(() =>
            {
                before = _motion!.GetStatus(id).Position;
                _motion.MoveAbsolute(id, target.Value, speed);
                reached = _motion.WaitForMotionComplete(id, FIND_TIMEOUT_MS);
                after = _motion.GetStatus(id).Position;
            });
            if (!ok)
                AppendLog($"{id} Go Home FAILED - see error above.");
            else
                AppendLog($"{id} Go Home: was {before:N0} → now {after:N0} (target {target.Value:N0}, " +
                          $"off by {after - target.Value:N0}){(reached ? "" : " [target-reached never set]")}" +
                          $"{(before == after ? "  *** axis did not move ***" : "")}.");
            _busy = false; RestartTimers(); RefreshButtons();
        }

        /// <summary>
        /// Auto-finds an axis's two travel limits by jogging into each switch, recording
        /// the position at the edge, and taking the pair as Min/Max (Home = centre).
        /// Only Y is wired to this today (two working switches that quick-stop).
        /// </summary>
        internal async Task FindLimitsAsync(AxisId id)
        {
            if (!CanMoveCalibration) return;
            _busy = true; RefreshButtons();
            statusTimer.Stop(); joystickTimer.Stop();
            AppendLog($"Finding {id} limits (auto, speed {FIND_LIMIT_SPEED})...");
            try
            {
                (long a, long b) = await Task.Run(() => FindLimitsCore(id));
                AxisCalibration c = _calib.For(id);
                c.Min = Math.Min(a, b);
                c.Max = Math.Max(a, b);
                TrySaveCalibration();
                AppendLog($"{id} limits: Min={c.Min:N0}, Max={c.Max:N0}, Home(centre)={c.Center:N0}.");
            }
            catch (Exception ex)
            {
                AppendLog($"Find {id} limits FAILED: {ex.Message}");
            }
            _busy = false; RestartTimers(); RefreshButtons();
        }

        // Background worker: jog to one end, recover + back off, jog to the other end.
        private (long endA, long endB) FindLimitsCore(AxisId id)
        {
            ClearAnyActiveLimit(id);              // axis may start parked on a switch
            long endA = JogUntilLimit(id, +1);
            RecoverAndBackOff(id, awayDir: -1);
            long endB = JogUntilLimit(id, -1);
            RecoverAndBackOff(id, awayDir: +1);   // leave the axis off the switch
            return (endA, endB);
        }

        // If the axis already sits on a limit switch when the find starts, JogUntilLimit
        // would never see a NEWLY-set bit and would run its full timeout driving into the
        // switch. Back off first. Polarity is unverified, so we don't know which way is
        // "away": try one direction and, if that drove further in (drive quick-stops, bit
        // stays set), recover and try the other.
        private void ClearAnyActiveLimit(AxisId id)
        {
            if ((_motion!.GetDigitalInputs(id) & SW_LIMIT_BITS) == 0) return;
            AppendLog($"{id} starts on a limit switch - backing off before find...");
            foreach (int away in new[] { -1, +1 })
            {
                _motion[id].EnableDrive(true);   // exit Quick Stop if a switch parked it there
                if ((_motion.GetDigitalInputs(id) & SW_LIMIT_BITS) == 0) return;
                _motion.JogAt(id, away, FIND_LIMIT_SPEED);
                int waited = 0;
                while ((_motion.GetDigitalInputs(id) & SW_LIMIT_BITS) != 0 && waited < BACKOFF_TIMEOUT_MS)
                {
                    System.Threading.Thread.Sleep(FIND_POLL_MS);
                    waited += FIND_POLL_MS;
                }
                _motion.Stop(id);
                if ((_motion.GetDigitalInputs(id) & SW_LIMIT_BITS) == 0) return;
            }
            throw new ChuckException($"{id} starts on a limit switch and could not be backed off either way.");
        }

        // Jogs until a limit bit (0x60FD bit 0 or 1) that wasn't already set goes high,
        // then captures the position and stops. Direction-agnostic, so the NEG/POS wiring
        // swap doesn't matter. Throws on timeout (no limit seen).
        private long JogUntilLimit(AxisId id, int direction)
        {
            long baseline = _motion!.GetDigitalInputs(id) & SW_LIMIT_BITS;
            _motion.JogAt(id, direction, FIND_LIMIT_SPEED);
            int waited = 0;
            while (waited < FIND_TIMEOUT_MS)
            {
                long now = _motion.GetDigitalInputs(id) & SW_LIMIT_BITS;
                if ((now & ~baseline) != 0)
                {
                    long pos = _motion.GetStatus(id).Position;
                    _motion.Stop(id);
                    return pos;
                }
                System.Threading.Thread.Sleep(FIND_POLL_MS);
                waited += FIND_POLL_MS;
            }
            _motion.Stop(id);
            throw new ChuckException($"no limit detected within {FIND_TIMEOUT_MS / 1000}s");
        }

        // After a limit hit the drive is in Quick Stop Active; re-enable to Operation
        // Enabled, then jog clear of the switch so the next pass starts off the limit.
        private void RecoverAndBackOff(AxisId id, int awayDir)
        {
            _motion![id].EnableDrive(true);
            if ((_motion.GetDigitalInputs(id) & SW_LIMIT_BITS) == 0) return;

            _motion.JogAt(id, awayDir, FIND_LIMIT_SPEED);
            int waited = 0;
            while ((_motion.GetDigitalInputs(id) & SW_LIMIT_BITS) != 0 && waited < FIND_TIMEOUT_MS)
            {
                System.Threading.Thread.Sleep(FIND_POLL_MS);
                waited += FIND_POLL_MS;
            }
            _motion.Stop(id);
        }

        private void TrySaveCalibration()
        {
            try { _calib.Save(); }
            catch (Exception ex) { AppendLog($"WARN: calibration save failed: {ex.Message}"); }
        }
    }
}
