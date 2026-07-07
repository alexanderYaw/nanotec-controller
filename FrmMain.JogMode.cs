using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace NanotecController
{
    // FrmMain — RAW / VISION jog mode. One motion cluster (d-pad + puck + speed sliders) drives
    // either the raw drive axes or screen-space vision motion, chosen by the mode switch. In VISION
    // mode the X/Y controls do the drift-corrected screen jog and the Θ controls rotate about the
    // crosshair (Z is always raw). The heavy motion code is shared: raw jog = StartJog/StopJog
    // (FrmMain.Jog.cs), vision X/Y = VisionJogUser (FrmMain.Vision.cs), Θ = HoldRotateAsync
    // (FrmMain.Rotation.cs). This file only routes the controls and swaps the per-mode slider ranges.
    // (Partial of FrmMain.)
    public partial class FrmMain
    {
        private enum JogMode { Raw, Vision }
        private JogMode _jogMode = JogMode.Raw;

        // Set in BuildAxisRows (the motion cluster is built in code from TableAxes.Default).
        private Button _rawModeBtn = null!;
        private Button _visionModeBtn = null!;
        private Label _thetaHeader = null!;
        private Button _invertMovementBtn = null!;
        // Dedicated VISION X/Y jog speed. Separate from the per-axis raw sliders: enabled only in
        // VISION mode (where the X/Y raw sliders grey out), greyed in RAW. Θ speed still lives on the
        // Θ row slider (jog speed in RAW, rotate-about-crosshair speed in VISION); Z is always raw.
        private TrackBar _visionSpeed = null!;
        private Label _visionSpeedValue = null!;

        // Per-axis raw slider ceiling (from AxisConfig) + the remembered slider value for each mode,
        // so switching back and forth restores what the user had. Vision X/Y = 0..6000; Θ = 50..2000.
        private readonly Dictionary<AxisId, int> _rawSpeedMax = new();
        private readonly Dictionary<AxisId, int> _rawSpeedSaved = new();
        private readonly Dictionary<AxisId, int> _visionSpeedSaved = new();
        // Last vision-puck X/Y velocity (send-on-change), separate from the raw-vector _lastVx/_lastVy.
        private int _visionLastVx, _visionLastVy;

        private static int DefaultVisionSpeed(AxisId id) => id == AxisId.Theta ? 800 : 1000;

        // Θ is the only axis whose ROW slider changes meaning/range with the mode: raw jog speed in
        // RAW, rotate-about-crosshair speed (50..2000) in VISION. X/Y/Z keep their raw jog range in
        // both modes — VISION X/Y speed lives on the dedicated _visionSpeed slider, not these rows.
        private static bool UsesVisionRange(AxisId id, JogMode mode) => mode == JogMode.Vision && id == AxisId.Theta;

        // Per-mode slider range for an axis's ROW slider.
        private (int min, int max) RangeFor(AxisId id, JogMode mode)
        {
            if (UsesVisionRange(id, mode)) return (50, 2000);
            return (0, _rawSpeedMax.GetValueOrDefault(id, 6000));
        }

        // Switches jog mode: stop everything first (safety), swap the slider ranges + labels, re-gate.
        private void ApplyJogMode(JogMode mode)
        {
            // 1. Safety: whatever was moving under the old mode must stop before the meaning changes.
            StopJoyAxes();
            VisionStop();
            StopHoldRotate();

            // 2. Remember the outgoing mode's slider values, switch, restore the incoming mode's.
            SaveSpeedsFor(_jogMode);
            _jogMode = mode;
            ApplySpeedRangesFor(mode);

            // 3. Visuals: highlight the active mode, relabel the group + Θ, disable Invert in vision
            //    (the drift-corrected jog deliberately ignores the InvertDir toggle).
            PaintModeButtons();
            axesGroup.Text = mode == JogMode.Vision
                ? "Motion — VISION: X/Y screen jog, Θ rotates about crosshair (Z raw)"
                : "Axis Jog - slider sets speed, hold an arrow for direction";
            _thetaHeader.Text = mode == JogMode.Vision ? "Θ ⟳" : "Θ";
            _invertMovementBtn.Enabled = mode == JogMode.Raw;

            // 4. In vision mode the Θ slider IS the rotate-about-crosshair speed.
            if (mode == JogMode.Vision) RotateThetaSpeed = _axisRows[AxisId.Theta].Speed.Value;

            RefreshButtons();
            AppendLog($"Jog mode: {(mode == JogMode.Vision ? "VISION (screen jog / rotate)" : "RAW (drive axes)")}.");
        }

        private void PaintModeButtons()
        {
            bool vision = _jogMode == JogMode.Vision;
            _rawModeBtn.BackColor = vision ? SystemColors.Control : SystemColors.Highlight;
            _rawModeBtn.ForeColor = vision ? SystemColors.ControlText : Color.White;
            _visionModeBtn.BackColor = vision ? SystemColors.Highlight : SystemColors.Control;
            _visionModeBtn.ForeColor = vision ? Color.White : SystemColors.ControlText;
        }

        private void SaveSpeedsFor(JogMode mode)
        {
            // Only Θ has a distinct per-mode value now (its range swaps); every other axis's row
            // slider stays raw in both modes, so it belongs to the raw set regardless of mode.
            foreach (AxisId id in _axisRows.Keys)
            {
                Dictionary<AxisId, int> dict = UsesVisionRange(id, mode) ? _visionSpeedSaved : _rawSpeedSaved;
                dict[id] = _axisRows[id].Speed.Value;
            }
        }

        private void ApplySpeedRangesFor(JogMode mode)
        {
            foreach (AxisId id in _axisRows.Keys)
            {
                (int min, int max) = RangeFor(id, mode);
                // Only Θ-in-VISION restores from the vision set; all other rows restore raw (seeded in BuildAxisRows).
                Dictionary<AxisId, int> dict = UsesVisionRange(id, mode) ? _visionSpeedSaved : _rawSpeedSaved;
                int want = dict.TryGetValue(id, out int v) ? v : DefaultVisionSpeed(id);
                AxisRow row = _axisRows[id];
                // Set Maximum before Minimum when growing (and vice-versa) so the range is never
                // momentarily inverted; the TrackBar clamps Value into the new range on each set.
                if (max >= row.Speed.Maximum) { row.Speed.Maximum = max; row.Speed.Minimum = min; }
                else { row.Speed.Minimum = min; row.Speed.Maximum = max; }
                row.Speed.Value = Math.Clamp(want, min, max);
                row.SpeedValue.Text = row.Speed.Value.ToString();
            }
        }

        // A speed slider moved: update its readout, and in vision mode keep the Θ slider synced to
        // the rotate-about-crosshair speed the hold-rotate loop reads.
        private void OnSpeedScroll(AxisId id)
        {
            AxisRow row = _axisRows[id];
            row.SpeedValue.Text = row.Speed.Value.ToString();
            if (_jogMode == JogMode.Vision && id == AxisId.Theta) RotateThetaSpeed = row.Speed.Value;
        }

        // --- d-pad arrow dispatch (mode-aware) ------------------------------------
        // Raw: hold-to-jog the drive axis. Vision: X/Y do the drift-corrected screen jog, Θ holds a
        // rotate-about-crosshair, Z stays raw. MouseUp always stops whatever the press started.

        private void ArrowDown(AxisId id, int dir)
        {
            if (_jogMode == JogMode.Vision && id != AxisId.Z)
            {
                if (id == AxisId.Theta) { _ = HoldRotateAsync(dir); }
                else VisionArrowJog(id, dir);
            }
            else StartJog(id, dir);
        }

        private void ArrowUp(AxisId id)
        {
            if (_jogMode == JogMode.Vision && id != AxisId.Z)
            {
                if (id == AxisId.Theta) StopHoldRotate();
                else VisionStop();
            }
            else StopJog(id);
        }

        // X or Y arrow in vision mode → a pure screen-axis jog (drift-corrected through the affine).
        // Screen convention: right = +col, up = −row (X+ arrow ▶ = right; Y+ arrow ▲ = up).
        private void VisionArrowJog(AxisId id, int dir)
        {
            int sx = id == AxisId.X ? dir : 0;
            int sy = id == AxisId.Y ? dir : 0;
            VisionJog(sx, sy, _visionSpeed.Value);
        }

        // --- vision-jog maths (moved here from the protocols window) ----------------

        // Discrete screen-direction jog (sx right+, sy up+) at the given speed, drift-corrected
        // through the pixel→step affine. Needs the camera-scale calibration.
        private void VisionJog(int sx, int sy, int speed)
        {
            PixelStepAffine? a = _calib.PixelStep;
            if (a == null) { AppendLog("Vision jog needs the camera-scale calibration first."); return; }
            // screen right = +col, up = −row (native frame)
            if (!VisionJogMath.TryUserVelocity(a, sx, -sy, speed, out int vx, out int vy))
            {
                AppendLog("Calibration is degenerate; recalibrate the camera scale.");
                return;
            }
            VisionJogUser(vx, vy);
        }

        // On-screen puck in vision mode: the puck's screen direction (x right+, y up+) is mapped
        // through the calibration so the on-screen motion follows it, speed scaled by deflection ×
        // the X slider (vision 0..6000). Send-on-change so a centred puck commands one Stop then idles.
        private void VisionPadTick()
        {
            PixelStepAffine? a = _calib.PixelStep;
            PointF v = joystickPad.Vector;
            double vmag = Math.Min(1.0, Math.Sqrt(v.X * v.X + v.Y * v.Y));

            int vx = 0, vy = 0;
            if (a != null && vmag >= 0.05)   // small dead-zone around centre
                VisionJogMath.TryUserVelocity(a, v.X, -v.Y, vmag * _visionSpeed.Value, out vx, out vy);

            if (vx == _visionLastVx && vy == _visionLastVy) return;   // send-on-change
            _visionLastVx = vx; _visionLastVy = vy;
            if (vx == 0 && vy == 0) VisionStop();
            else VisionJogUser(vx, vy);
        }
    }
}
