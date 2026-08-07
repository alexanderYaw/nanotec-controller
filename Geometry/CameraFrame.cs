using System;

namespace NanotecController
{
    /// <summary>
    /// Converts a bearing between the LAB frame (0° along +X, increasing towards +Y) and the bearing
    /// an operator reads off the live view. The camera's mounting angle is MEASURED from the
    /// pixel→step affine rather than configured, so it follows a re-run of the camera-scale
    /// calibration. ORIENTATION ONLY — the camera frame cannot carry positions, because its origin
    /// travels with X and Y. See Developer Guide, NotchSearch.md §"Camera frame".
    /// </summary>
    public static class CameraFrame
    {
        private const double DegenerateAffine = 1e-12;

        /// <summary>How far the camera is rotated from the machine's axes (degrees; +ve means a lab
        /// bearing reads that much LOWER on the view), folded to (−90, 90] so the view's own display
        /// flip isn't counted twice. Null until the affine exists. Computed in mm, since X and Y
        /// differ by 0.4% in steps/mm and a bearing is only a bearing once that is divided out.</summary>
        public static double? TiltDeg(CalibrationStore cal)
        {
            if (cal.PixelStep is not PixelStepAffine a) return null;
            double kX = cal.For(AxisId.X).StepsPerMm ?? 1.0;
            double kY = cal.For(AxisId.Y).StepsPerMm ?? 1.0;
            if (kX <= 0) kX = 1.0;
            if (kY <= 0) kY = 1.0;

            // One pixel of COLUMN, in mm of stage: where the view's horizontal points.
            double cx = a.Xc / kX, cy = a.Yc / kY;
            if (Math.Abs(cx) < DegenerateAffine && Math.Abs(cy) < DegenerateAffine) return null;

            double deg = Math.Atan2(cy, cx) * 180.0 / Math.PI;
            return ((deg + 90.0) % 180.0 + 180.0) % 180.0 - 90.0;   // fold to (−90, 90]
        }

        /// <summary>A lab bearing as it reads on the live view.</summary>
        public static double ToView(double labDeg, double tiltDeg) => Norm360(labDeg - tiltDeg);

        /// <summary>A bearing read off the live view, back in the lab frame.</summary>
        public static double ToLab(double viewDeg, double tiltDeg) => Norm360(viewDeg + tiltDeg);

        /// <summary>Where the notch datum's 0° points, as a bearing in the view frame. The view's own
        /// 0° reads WEST on screen; the datum is quoted from NORTH instead (user convention,
        /// 2026-08-07), so 0 = north, 90 = west, 180 = south, 270 = east. The dial keeps the view
        /// frame's own direction of travel, so it runs anticlockwise on screen — NOT a compass.</summary>
        private const double DatumZeroInView = 270.0;

        /// <summary>A datum the operator typed, as a lab bearing.</summary>
        public static double DatumToLab(double datumDeg, double tiltDeg)
            => ToLab(datumDeg + DatumZeroInView, tiltDeg);

        /// <summary>A lab bearing, as the datum reading for the same direction.</summary>
        public static double LabToDatum(double labDeg, double tiltDeg)
            => Norm360(ToView(labDeg, tiltDeg) - DatumZeroInView);

        private static double Norm360(double deg) => ((deg % 360.0) + 360.0) % 360.0;
    }
}
