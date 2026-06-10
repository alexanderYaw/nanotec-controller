using System;
using System.Windows.Forms;

namespace MotorControlApp
{
    // FrmMain — per-axis hold-to-jog buttons, the live status poll, and the soft-limit
    // guard (stop-when-jogging-past-a-stored-limit + the same-direction jog block).
    // (Partial of FrmMain.)
    public partial class FrmMain
    {
        // --- Per-axis hold-to-jog (individual control) ----------------------------
        // Quick SDO writes done on the UI thread so press/release ordering is exact.

        private void StartJog(AxisId id, int direction)
        {
            if (_motion == null || !_drivesEnabled || _busy) return;
            if (IsJogBlocked(id, direction))
            {
                AppendLog($"{id} at soft limit - jog {(direction > 0 ? "+" : "−")} blocked (jog back into range first).");
                return;
            }
            int speed = _axisRows[id].Speed.Value;
            try
            {
                // If a limit hit left the axis in Quick-Stop-Active, clear it first so the
                // jog can drive it off the switch (a plain jog/controlword 0x0F won't recover
                // it on this drive — same reason the auto limit-find re-enables). This runs
                // on the UI thread, so there is a brief (~1 s) hitch while it re-enables, but
                // only when actually quick-stopped; a normal jog is just one extra status read.
                if (_motion.RecoverIfQuickStopped(id))
                    AppendLog($"{id} was in Quick Stop (limit) - re-enabled.");
                _motion.JogAt(id, direction, speed);
                _cmdDir[id] = direction;
                AppendLog($"Jog {id} {(direction > 0 ? "+" : "−")} at {speed}.");
            }
            catch (ChuckException ex)
            {
                AppendLog($"ERROR: jog {id} failed: {ex.Message}");
            }
        }

        private void StopJog(AxisId id)
        {
            if (_motion == null) return;
            try { _motion.Stop(id); _cmdDir[id] = 0; }
            catch (ChuckException ex) { AppendLog($"ERROR: stop {id} failed: {ex.Message}"); }
        }

        /// <summary>
        /// True if jogging <paramref name="dir"/> would push the axis further past the soft
        /// limit it is already parked against. The blocked direction is recorded in command
        /// space at the moment the limit tripped, so this never assumes a command→position
        /// polarity; jogging the opposite way (back into range) is always allowed.
        /// </summary>
        private bool IsJogBlocked(AxisId id, int dir)
            => dir != 0 && _limitBlockedDir.TryGetValue(id, out int b) && b == dir;

        // --- Live status poll -----------------------------------------------------

        private void statusTimer_Tick(object? sender, EventArgs e)
        {
            if (_motion == null) return;
            try
            {
                foreach (AxisId id in _motion.Axes)
                {
                    ChuckController.ChuckStatus st = _motion.GetStatus(id);
                    _axisRows[id].Status.Text = $"{st.Position,12:N0}   {st.State}{(st.HasFault ? "  [FAULT]" : "")}";
                    EnforceSoftLimits(id, st.Position);
                }
                _statusFailures = 0;
            }
            catch (ChuckException ex)
            {
                _statusFailures++;
                if (_statusFailures >= MAX_CONSECUTIVE_READ_FAILURES)
                {
                    statusTimer.Stop();
                    joystickTimer.Stop();
                    AppendLog($"Lost contact with a drive: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Stops an axis that is jogging past one of its stored digital limits. Direction is
        /// inferred from the position delta (so it's independent of motor/encoder polarity):
        /// a stop fires only when the axis is at/beyond a limit AND still moving further out,
        /// so jogging back into range is always allowed. Send-on-change keeps it stopped
        /// (a held button/stick won't re-command) until the user reverses. Runs at the
        /// ~200 ms status-poll rate, so there is some overshoot — the physical switches
        /// (where present) remain the real safety; this is a soft guard.
        /// </summary>
        private void EnforceSoftLimits(AxisId id, long pos)
        {
            bool hasPrev = _prevPos.TryGetValue(id, out long prev);
            _prevPos[id] = pos;
            if (!_drivesEnabled || !hasPrev) { _atSoftLimit.Remove(id); _limitBlockedDir[id] = 0; return; }

            AxisCalibration cal = _calib.For(id);
            long delta = pos - prev;
            bool outMax = cal.Max.HasValue && pos >= cal.Max.Value;
            bool outMin = cal.Min.HasValue && pos <= cal.Min.Value;

            if ((outMax && delta > 0) || (outMin && delta < 0))
            {
                try { _motion!.Stop(id); } catch (ChuckException) { }
                // Refuse further jogs in the SAME command direction that pushed it out, so a
                // held/re-pressed control can't re-lurch past the limit each poll. Reversing
                // (back into range) clears the block below.
                if (_cmdDir.TryGetValue(id, out int d) && d != 0) _limitBlockedDir[id] = d;
                _cmdDir[id] = 0;
                if (_atSoftLimit.Add(id))   // log once per approach, not every poll
                    AppendLog($"{id} soft {(outMax ? "Max" : "Min")} limit reached - jog stopped at {pos:N0}.");
            }
            else if (!outMax && !outMin)
            {
                _atSoftLimit.Remove(id);    // safely back inside the range
                _limitBlockedDir[id] = 0;   // re-allow both directions
            }
        }

        /// <summary>Clears soft-limit tracking so a stale position delta can't trigger a false stop.</summary>
        private void ResetSoftLimitTracking()
        {
            _prevPos.Clear();
            _atSoftLimit.Clear();
            _cmdDir.Clear();
            _limitBlockedDir.Clear();
        }
    }
}
