using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace NanotecController
{
    // FrmMain — manual input sources (USB joystick + on-screen puck) and how their state
    // is mapped onto axis jog commands (send-on-change). (Partial of FrmMain.)
    public partial class FrmMain
    {
        // --- Joystick -------------------------------------------------------------

        private void inputSourceChanged(object? sender, EventArgs e)
        {
            // Source switch (Off / USB / On-screen, mutually exclusive): stop whatever
            // was moving, then configure the new source. Radios auto-exclude, so this
            // fires twice on a switch; recomputing from current state keeps it correct.
            StopJoyAxes();

            bool usb = rbUsb.Checked, screen = rbScreen.Checked;
            joystickPad.Enabled = screen && _drivesEnabled && !_busy;

            if (usb || screen)
            {
                ResetJoy();
                joystickTimer.Start();
                joystickStatusLabel.Text = usb ? "USB: idle" : "On-screen: idle";
                AppendLog(usb
                    ? "Input: USB joystick (hold button 1 = deadman; button 2 = fast)."
                    : "Input: on-screen joystick (drag the puck; release = stop).");
            }
            else
            {
                joystickTimer.Stop();
                joystickStatusLabel.Text = "Input: off";
            }
        }

        private void joystickTimer_Tick(object? sender, EventArgs e)
        {
            if (rbScreen.Checked) { TickOnScreen(); return; }   // on-screen puck path

            JoystickState st = _joystick.Poll();

            if (!st.Connected)
            {
                joystickStatusLabel.Text = "Joystick: NOT FOUND";
                StopJoyAxes(); // safety: a vanished joystick must not leave an axis running
                return;
            }
            joystickStatusLabel.Text = $"Joystick: {(st.Deadman ? "DEADMAN held" : "idle")}{(st.Fast ? " | FAST" : "")}";

            bool allow = st.Deadman && _drivesEnabled && !_busy && _motion != null;

            ApplyJoy(AxisId.X, allow ? st.X : 0, st.Fast);
            ApplyJoy(AxisId.Y, allow ? st.Y : 0, st.Fast);
            ApplyJoy(AxisId.Z, allow ? st.Z : 0, st.Fast);
            ApplyJoy(AxisId.Theta, allow ? st.R : 0, st.Fast);
        }

        /// <summary>
        /// On-screen joystick: map the puck's analog vector (angle + magnitude) to X/Y
        /// velocities. Rim (|component| = 1) = that axis's slider speed; centre = stop.
        /// Releasing the puck re-centres it → (0,0) → stop. No deadman — holding the
        /// mouse is the intent; releasing halts.
        /// </summary>
        private void TickOnScreen()
        {
            bool allow = _drivesEnabled && !_busy && _motion != null;
            PointF v = allow ? joystickPad.Vector : PointF.Empty;
            int vx = (int)Math.Round(v.X * _axisRows[AxisId.X].Speed.Value);
            int vy = (int)Math.Round(v.Y * _axisRows[AxisId.Y].Speed.Value);
            ApplyVector(vx, vy);
            joystickStatusLabel.Text = (vx != 0 || vy != 0) ? $"On-screen: {vx}, {vy}" : "On-screen: idle";
        }

        /// <summary>
        /// Commands one axis from the joystick, but only when the command changes
        /// (send-on-change). Speed comes from that axis's slider; the Fast button
        /// multiplies it (capped at the slider max).
        /// </summary>
        private void ApplyJoy(AxisId id, int dir, bool fast)
        {
            dir = InvertDir(id, dir);                       // movement-inversion toggle (X/Y/Θ)
            if (dir != 0 && IsJogBlocked(id, dir)) dir = 0; // soft limit -> treat as stop
            (int dir, bool fast) last = _lastJoy[id];
            if (last.dir == dir && (dir == 0 || last.fast == fast)) return; // unchanged
            try
            {
                if (dir == 0)
                {
                    _motion!.Stop(id);
                    _cmdDir[id] = 0;
                }
                else
                {
                    int speed = _axisRows[id].Speed.Value;
                    if (fast) speed = Math.Min(speed * FAST_FACTOR, _axisRows[id].Speed.Maximum);
                    _motion!.JogAt(id, dir, speed);
                    _cmdDir[id] = dir;
                }
            }
            catch (DriveException ex) { AppendLog($"ERROR: joystick {id}: {ex.Message}"); }
            _lastJoy[id] = (dir, fast);
        }

        // --- XY velocity-vector jog (on-screen joystick) --------------------------

        /// <summary>
        /// Commands the XY pair as a velocity vector (on-screen joystick), send-on-change.
        /// Vx/Vy are signed drive-velocity units; the geometric heading is exact only if
        /// X and Y share the same units/scale (true once factor-group scaling is wired in).
        /// </summary>
        private void ApplyVector(int vx, int vy)
        {
            try
            {
                if (vx != _lastVx) { CommandVel(AxisId.X, vx); _lastVx = vx; }
                if (vy != _lastVy) { CommandVel(AxisId.Y, vy); _lastVy = vy; }
            }
            catch (DriveException ex) { AppendLog($"ERROR: on-screen jog: {ex.Message}"); }
        }

        private void CommandVel(AxisId id, int v)
        {
            if (InvertDir(id, 1) < 0) v = -v;                    // movement-inversion toggle (X/Y)
            if (v != 0 && IsJogBlocked(id, Math.Sign(v))) v = 0; // soft limit -> treat as stop
            if (v == 0) { _motion!.Stop(id); _cmdDir[id] = 0; }
            else { _motion!.JogAt(id, Math.Sign(v), Math.Abs(v)); _cmdDir[id] = Math.Sign(v); }
        }

        /// <summary>Stops any axis the joystick was driving and clears its last-command cache.</summary>
        private void StopJoyAxes()
        {
            if (_motion != null)
            {
                foreach (AxisId id in _motion.Axes)
                {
                    if (_lastJoy[id].dir != 0)
                        try { _motion.Stop(id); } catch (DriveException) { }
                }
                if (_lastVx != 0) try { _motion.Stop(AxisId.X); } catch (DriveException) { }
                if (_lastVy != 0) try { _motion.Stop(AxisId.Y); } catch (DriveException) { }
            }
            ResetJoy();
        }

        private void ResetJoy()
        {
            foreach (AxisId id in new List<AxisId>(_lastJoy.Keys)) _lastJoy[id] = (0, false);
            _lastVx = _lastVy = 0;
        }
    }
}
