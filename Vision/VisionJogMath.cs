using System;

namespace MotorControlApp
{
    /// <summary>
    /// Drift-corrected vision-jog maths: maps a SCREEN-space jog direction through the pixel→step
    /// affine so the on-screen motion follows that direction (cancelling the camera↔stage rotation),
    /// shared by the joystick-puck poll and the discrete d-pad. Pure + testable; no UI/motor state.
    /// </summary>
    public static class VisionJogMath
    {
        /// <summary>
        /// Maps a screen direction (<paramref name="dCol"/> = right+, <paramref name="dRow"/> = down+,
        /// in raw pixels) through affine <paramref name="a"/> to a USER-frame X/Y velocity, normalised
        /// so the faster axis runs at <paramref name="speed"/>. Returns false (vx = vy = 0) if the
        /// calibration is degenerate (both components ~0).
        /// </summary>
        public static bool TryUserVelocity(PixelStepAffine a, double dCol, double dRow, double speed,
                                           out int vx, out int vy)
        {
            vx = vy = 0;
            double vxUser = a.Xr * dRow + a.Xc * dCol;   // steps/pixel · pixel direction
            double vyUser = a.Yr * dRow + a.Yc * dCol;
            double m = Math.Max(Math.Abs(vxUser), Math.Abs(vyUser));
            if (m < 1e-9) return false;
            vx = (int)Math.Round(vxUser / m * speed);
            vy = (int)Math.Round(vyUser / m * speed);
            return true;
        }
    }
}
