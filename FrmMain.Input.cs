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
                if (usb) ResetJoyCentre();   // fresh analog-source select: re-capture the centre once
                joystickTimer.Start();
                joystickStatusLabel.Text = usb ? "Joystick: idle" : "On-screen: idle";
                AppendLog(usb
                    ? "Input: analog joystick (wired to the drives; deflect to move, centre to stop)."
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
            if (rbUsb.Checked) { TickAnalogJoystick(); return; }  // analog joystick wired to the drives
        }

        /// <summary>
        /// On-screen joystick: map the puck's analog vector (angle + magnitude) to X/Y
        /// velocities. Rim (|component| = 1) = that axis's slider speed; centre = stop.
        /// Releasing the puck re-centres it → (0,0) → stop. No deadman — holding the
        /// mouse is the intent; releasing halts.
        /// </summary>
        private void TickOnScreen()
        {
            // VISION mode: the same puck drives the drift-corrected screen jog instead.
            if (_jogMode == JogMode.Vision) { VisionPadTick(); return; }
            bool allow = _drivesEnabled && !_busy && _motion != null;
            PointF v = allow ? joystickPad.Vector : PointF.Empty;
            int vx = (int)Math.Round(v.X * _axisRows[AxisId.X].Speed.Value);
            int vy = (int)Math.Round(v.Y * _axisRows[AxisId.Y].Speed.Value);
            ApplyVector(vx, vy);
            joystickStatusLabel.Text = (vx != 0 || vy != 0) ? $"On-screen: {vx}, {vy}" : "On-screen: idle";
        }

        // --- XY velocity-vector jog (on-screen joystick) --------------------------

        /// <summary>
        /// Commands the XY pair as a velocity vector (on-screen joystick), send-on-change.
        /// Vx/Vy are signed drive-velocity units; the geometric heading is exact only if
        /// X and Y share the same units/scale (true once factor-group scaling is wired in).
        /// </summary>
        private void ApplyVector(int vx, int vy)
        {
            if (vx != _lastVx) { CommandVel(AxisId.X, vx); _lastVx = vx; }
            if (vy != _lastVy) { CommandVel(AxisId.Y, vy); _lastVy = vy; }
        }

        private void CommandVel(AxisId id, int v)
        {
            if (InvertDir(id, 1) < 0) v = -v;                    // movement-inversion toggle (X/Y)
            CommandAxisVelocity(id, v, honorSoftLimit: true);
        }

        /// <summary>Stops any axis an input source was driving and clears the last-command caches.</summary>
        private void StopJoyAxes()
        {
            if (_motion != null)
            {
                // On-screen XY velocity vector.
                if (_lastVx != 0) try { _motion.Stop(AxisId.X); } catch (DriveException) { }
                if (_lastVy != 0) try { _motion.Stop(AxisId.Y); } catch (DriveException) { }
                // VISION-mode screen jog (puck or analog joystick) drives X/Y through VisionJogUser,
                // tracked separately from the raw caches above — stop those too on a source switch.
                if (_visionLastVx != 0) try { _motion.Stop(AxisId.X); } catch (DriveException) { }
                if (_visionLastVy != 0) try { _motion.Stop(AxisId.Y); } catch (DriveException) { }
                // Analog joystick axes (X / Y / Θ).
                foreach ((AxisId cmd, AxisId _, int _) in AnalogAxes)
                    if (_lastAnalogVel.TryGetValue(cmd, out int v) && v != 0)
                        try { _motion.Stop(cmd); } catch (DriveException) { }
            }
            ResetJoy();
        }

        // Clears the send-on-change caches so the next poll re-commands (the drives were stopped while
        // paused). Deliberately does NOT touch the auto-captured analog centre: that's a stable physical
        // property of the pot, so it's re-captured only on an explicit source (re)select (ResetJoyCentre),
        // NOT on every drive-op resume — which needlessly recentred after each rotation and reopened the
        // mid-twist bias window. See CaptureCentre.
        private void ResetJoy()
        {
            foreach (AxisId id in new List<AxisId>(_lastJoy.Keys)) _lastJoy[id] = (0, false);
            _lastVx = _lastVy = 0;
            _visionLastVx = _visionLastVy = 0;   // vision-puck send-on-change (VisionPadTick)
            foreach ((AxisId cmd, AxisId _, int _) in AnalogAxes) _lastAnalogVel[cmd] = 0;   // analog joystick send-on-change
            ResetAiSpans();          // TEMP Θ-wiring probe: start each min/max test fresh
            _visionView.CenteringOverlay = false;   // clear the live-view warning on any stop/source switch
        }

        // Discards the auto-captured analog centres so the next polls re-average them. Called only when
        // the analog joystick SOURCE is (re)selected — toggling the input source off and on is the
        // deliberate "recentre" gesture; routine drive ops leave the captured centre intact.
        private void ResetJoyCentre()
        {
            _aiMid.Clear();
            _aiCentreSum.Clear();
            _aiCentreCount.Clear();
            _aiCentreRange.Clear();   // spread guard that rejects a mid-twist centre
        }
    }
}
