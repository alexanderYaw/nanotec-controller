using System;
using System.Drawing;

namespace NanotecController
{
    // FrmVisionProtocols — the drift-corrected vision-jog maths for this window's convenience copy of
    // the VISION-mode controls. Screen input (right = +col, up = −row) is mapped through the shared
    // pixel→step affine (VisionJogMath) and executed by FrmMain via IMotionHost, which serializes all
    // motion. The main-screen motion cluster has its own copy (FrmMain.JogMode.cs); both share the same
    // pure maths and the same IMotionHost.VisionJogUser/VisionStop, so there is no duplicated logic.
    // (Partial of FrmVisionProtocols.)
    public sealed partial class FrmVisionProtocols
    {
        // Puck poll (50 ms): the puck's screen direction (x right+, y up+) is mapped through the
        // calibration so the feature under the crosshair follows it, speed scaled by deflection × the
        // Speed box. Send-on-change so a centred puck issues a single Stop then idles.
        private void VisionPadTick()
        {
            PixelStepAffine? a = _owner?.Calibration.PixelStep;
            PointF v = _vPad.Vector;
            double vmag = Math.Min(1.0, Math.Sqrt(v.X * v.X + v.Y * v.Y));

            int vx = 0, vy = 0;
            if (a != null && vmag >= 0.05)   // small dead-zone around centre
                VisionJogMath.TryUserVelocity(a, v.X, -v.Y, vmag * (double)_vSpeed.Value, out vx, out vy);

            if (vx == _vLastVx && vy == _vLastVy) return;   // send-on-change
            _vLastVx = vx; _vLastVy = vy;
            if (vx == 0 && vy == 0) _owner?.VisionStop();
            else _owner?.VisionJogUser(vx, vy);
        }

        // Discrete d-pad press (sx right+, sy up+) at the Speed-box value, drift-corrected through the
        // pixel→step affine. Reports to _status if the camera-scale calibration is missing/degenerate.
        private void VisionJog(int sx, int sy)
        {
            PixelStepAffine? a = _owner?.Calibration.PixelStep;
            if (a == null) { _status.Text = "Vision jog needs the camera-scale calibration first."; return; }
            // screen right = +col, up = −row (native frame)
            if (!VisionJogMath.TryUserVelocity(a, sx, -sy, (double)_vSpeed.Value, out int vx, out int vy))
            {
                _status.Text = "Calibration is degenerate; recalibrate the camera scale.";
                return;
            }
            _owner!.VisionJogUser(vx, vy);
        }
    }
}
