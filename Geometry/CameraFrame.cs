using System;

namespace NanotecController
{
    /// <summary>
    /// The camera's own orientation in the machine, and the conversion between a bearing in the LAB
    /// frame (user frame: 0° along +X, increasing towards +Y) and the bearing an operator reads off
    /// the live view.
    ///
    /// The camera is bolted on at whatever angle the bracket gives it, and there is no reason for
    /// that to be square with the stage — on this machine it is 4.6° out. The pixel→step affine
    /// already carries the angle, so this is MEASURED rather than configured: one pixel COLUMN moves
    /// the stage (Xc, Yc) steps, and the lab bearing of that vector is where the view's horizontal
    /// points. Re-run the camera-scale calibration after a camera swap and this follows.
    ///
    /// Folded to (−90, 90]. The ~180° the camera is physically mounted at belongs to the live view's
    /// own display flip (<see cref="VisionViewControl.InvertView"/>), and counting it twice would
    /// turn every converted bearing upside down.
    ///
    /// <b>Orientation only.</b> The camera frame cannot carry POSITIONS: its origin travels with X
    /// and Y, so a point expressed in it stops meaning anything the moment the stage moves. That is
    /// why every vision measurement here is E = M + A·(p_cross − p_edge) — M, the motor position, is
    /// what anchors the image to the machine. Directions have no origin, which is exactly why they
    /// convert cleanly and positions do not.
    /// </summary>
    public static class CameraFrame
    {
        /// <summary>
        /// How far the camera is rotated from the machine's axes (degrees, +ve meaning a lab bearing
        /// reads that much LOWER on the view). Null until the affine exists.
        ///
        /// Computed in mm, because X and Y differ by 0.4 % in steps/mm and a bearing is only a
        /// bearing once that is divided out; without steps/mm it falls back to step space, which is
        /// the same angle to within 0.06°.
        /// </summary>
        public static double? TiltDeg(CalibrationStore cal)
        {
            if (cal.PixelStep is not PixelStepAffine a) return null;
            double kX = cal.For(AxisId.X).StepsPerMm ?? 1.0;
            double kY = cal.For(AxisId.Y).StepsPerMm ?? 1.0;
            if (kX <= 0) kX = 1.0;
            if (kY <= 0) kY = 1.0;

            // One pixel of COLUMN, in mm of stage: the direction the view's horizontal points.
            double cx = a.Xc / kX, cy = a.Yc / kY;
            if (Math.Abs(cx) < 1e-12 && Math.Abs(cy) < 1e-12) return null;

            double deg = Math.Atan2(cy, cx) * 180.0 / Math.PI;
            return ((deg + 90.0) % 180.0 + 180.0) % 180.0 - 90.0;   // fold to (−90, 90]
        }

        /// <summary>A lab bearing as it reads on the live view.</summary>
        public static double ToView(double labDeg, double tiltDeg) => Norm360(labDeg - tiltDeg);

        /// <summary>A bearing read off the live view, back in the lab frame.</summary>
        public static double ToLab(double viewDeg, double tiltDeg) => Norm360(viewDeg + tiltDeg);

        private static double Norm360(double deg) => ((deg % 360.0) + 360.0) % 360.0;
    }
}
