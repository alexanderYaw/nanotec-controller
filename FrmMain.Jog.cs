using System;
using System.Windows.Forms;

namespace NanotecController
{
    // FrmMain — per-axis hold-to-jog buttons, the live status poll, and the soft-limit
    // guard (stop-when-jogging-past-a-stored-limit + the same-direction jog block).
    // (Partial of FrmMain.)
    public partial class FrmMain
    {
        // --- Per-axis hold-to-jog (individual control) ----------------------------
        // Quick SDO writes done on the UI thread so press/release ordering is exact.

        // Movement-inversion toggle (visual-centering): when on, flips the commanded jog
        // direction for the in-plane / rotary axes so the controls match the inverted camera
        // view. Applied at every manual-jog entry point (d-pad, USB joystick, on-screen puck)
        // BEFORE the soft-limit logic, so the SoftLimitTracker direction state stays in true
        // command space. Z is excluded (depth axis, unaffected by an in-plane view flip).
        private bool _invertMovement;

        private int InvertDir(AxisId id, int dir)
            => (_invertMovement && (id == AxisId.X || id == AxisId.Y || id == AxisId.Theta)) ? -dir : dir;

        private void StartJog(AxisId id, int direction)
        {
            if (_motion == null || !_drivesEnabled || _busy) return;
            direction = InvertDir(id, direction);
            if (_softLimits.IsBlocked(id, direction))
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
                _softLimits.RecordCommand(id, direction);
                AppendLog($"Jog {id} {(direction > 0 ? "+" : "−")} at {speed}.");
            }
            catch (DriveException ex)
            {
                AppendLog($"ERROR: jog {id} failed: {ex.Message}");
            }
        }

        private void StopJog(AxisId id)
        {
            if (_motion == null) return;
            try { _motion.Stop(id); _softLimits.RecordCommand(id, 0); }
            catch (DriveException ex) { AppendLog($"ERROR: stop {id} failed: {ex.Message}"); }
        }

        // --- Live status poll -----------------------------------------------------

        private void statusTimer_Tick(object? sender, EventArgs e)
        {
            if (_motion == null) return;
            try
            {
                foreach (AxisId id in _motion.Axes)
                {
                    AxisDriver.AxisStatus st = _motion.GetStatus(id);
                    _lastPos[id] = st.Position;   // cache raw; Position Map reads it in the user frame
                    long shown = ToUser(id, st.Position);   // user frame (Y inverted) for an intuitive readout
                    _axisRows[id].Status.Text = $"{shown,12:N0}   {st.State}{(st.HasFault ? "  [Fault]" : "")}";
                    EnforceSoftLimits(id, st.Position);
                }
                _statusFailures = 0;
            }
            catch (DriveException ex)
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
            SoftLimitTracker.Decision d = _softLimits.Evaluate(id, pos, _calib, _drivesEnabled);
            if (d.Stop) { try { _motion!.Stop(id); } catch (DriveException) { } }
            if (d.Log != null) AppendLog(d.Log);
        }

        /// <summary>Clears soft-limit tracking so a stale position delta can't trigger a false stop.</summary>
        private void ResetSoftLimitTracking()
        {
            _softLimits.Reset();
            _lastPos.Clear();
        }
    }
}
