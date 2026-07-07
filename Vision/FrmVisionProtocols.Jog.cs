using System;
using System.Drawing;

namespace NanotecController
{
    // FrmVision — drift-corrected vision jog: maps a screen-space direction (analog puck poll or the
    // discrete d-pad) through the pixel→step affine so the on-screen motion follows that direction.
    // The actual axis commands run in FrmMain.VisionJogUser/VisionStop. (Partial of FrmVisionProtocols.)
    public sealed partial class FrmVisionProtocols
    {
        // Polls the joystick puck and commands a drift-corrected jog: the puck's screen direction
        // (x right+, y up+) is mapped through the calibration so the on-screen motion is along that
        // direction, and the speed scales with the puck's deflection (× the Speed control). The
        // puck is tied to the camera's NATIVE (raw) orientation — screen right = +col, up = −row;
        // the display Invert toggle is display-only and deliberately does NOT change control sense.
        // Send-on-change so a centred puck commands a single Stop and then stays silent.
        private void VisionPadTick()
        {
            PixelStepAffine? a = _owner?.Calibration.PixelStep;
            PointF v = _vPad.Vector;
            double vmag = Math.Min(1.0, Math.Sqrt(v.X * v.X + v.Y * v.Y));

            int vx = 0, vy = 0;
            if (a != null && vmag >= 0.05)   // small dead-zone around centre
                VisionJogMath.TryUserVelocity(a, v.X, -v.Y, vmag * (double)_vSpeed.Value, out vx, out vy);   // deflection scales speed

            if (vx == _vLastVx && vy == _vLastVy) return;   // send-on-change
            _vLastVx = vx; _vLastVy = vy;
            if (vx == 0 && vy == 0) _owner?.VisionStop();
            else _owner?.VisionJogUser(vx, vy);
        }

        // Discrete d-pad jog: a pure SCREEN direction (sx right+, sy up+) at the full Speed setting,
        // drift-corrected through the calibration. Coexists with the puck — the puck's send-on-change
        // poll stays silent while the puck is centred, so it won't cancel a button jog. MouseUp stops.
        private void VisionJog(int sx, int sy)
        {
            PixelStepAffine? a = _owner?.Calibration.PixelStep;
            if (a == null) { _status.Text = "Vision jog needs the camera-scale calibration first."; return; }

            // screen right = +col, up = −row (native frame)
            if (!VisionJogMath.TryUserVelocity(a, sx, -sy, (double)_vSpeed.Value, out int vx, out int vy))
            {
                _status.Text = "Calibration is degenerate; recalibrate.";
                return;
            }
            _owner!.VisionJogUser(vx, vy);
        }
    }
}
