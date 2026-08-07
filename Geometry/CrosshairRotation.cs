using System;

namespace NanotecController
{
    /// <summary>
    /// Geometry for rotating the chuck ABOUT the camera crosshair rather than its own mechanical
    /// centre C. Θ only rotates about C, so pivoting about the crosshair means adding an X/Y shift
    /// that makes C orbit the crosshair by the same angle: S' = C + A·R(φ)·A⁻¹·(S − C), φ = sign·θ,
    /// with A the stored pixel→step affine. All motor coordinates are in the USER frame.
    /// <c>sign</c> (±1) is the image handedness of a positive Θ move — not derivable from a
    /// translation-only affine, so it is fixed empirically and passed in.
    /// </summary>
    public static class CrosshairRotation
    {
        #region Crosshair pin geometry

        /// <summary>Absolute USER-frame X/Y target that, paired with rotating Θ by
        /// <paramref name="angleRad"/>, keeps the point under the crosshair fixed. Pass the ORIGINAL
        /// S₀ as <paramref name="startX"/>/<paramref name="startY"/> on every incremental step so
        /// error never accumulates. False if the affine is degenerate.</summary>
        public static bool TryXyTarget(
            PixelStepAffine a,
            long centerX, long centerY,
            long startX, long startY,
            double angleRad, int sign,
            out long targetX, out long targetY)
        {
            targetX = startX; targetY = startY;

            double det = a.Xr * a.Yc - a.Xc * a.Yr;
            if (Math.Abs(det) < 1e-9) return false;   // degenerate calibration

            double dX = startX - centerX, dY = startY - centerY;

            // r = A⁻¹·(S − C): chuck-centre offset from crosshair, in pixels (row, col).
            double row = ( a.Yc * dX - a.Xc * dY) / det;
            double col = (-a.Yr * dX + a.Xr * dY) / det;

            // r' = R(φ)·r.
            double phi = sign * angleRad;
            double c = Math.Cos(phi), s = Math.Sin(phi);
            double rowP = c * row - s * col;
            double colP = s * row + c * col;

            // ΔS = A·(r' − r); target = S + ΔS.
            double dRow = rowP - row, dCol = colP - col;
            targetX = startX + (long)Math.Round(a.Xr * dRow + a.Xc * dCol);
            targetY = startY + (long)Math.Round(a.Yr * dRow + a.Yc * dCol);
            return true;
        }

        /// <summary>Derivative of the pin target w.r.t. the unsigned rotation angle, in STEPS PER
        /// RADIAN — the noise-free velocity feedforward, since row/col are constant over a rotation
        /// and no numeric differencing of quantized targets is needed. Multiply by d(angleRad)/dt to
        /// get step-velocity. False if the affine is degenerate.</summary>
        public static bool TryXyTargetVelocity(
            PixelStepAffine a,
            long centerX, long centerY,
            long startX, long startY,
            double angleRad, int sign,
            out double dTargetXdAngle, out double dTargetYdAngle)
        {
            dTargetXdAngle = 0.0; dTargetYdAngle = 0.0;

            double det = a.Xr * a.Yc - a.Xc * a.Yr;
            if (Math.Abs(det) < 1e-9) return false;   // degenerate calibration

            double dX = startX - centerX, dY = startY - centerY;
            double row = ( a.Yc * dX - a.Xc * dY) / det;
            double col = (-a.Yr * dX + a.Xr * dY) / det;

            // d/dφ of R(φ)·r, with R'(φ) = [[−sinφ, −cosφ],[cosφ, −sinφ]].
            double phi = sign * angleRad;
            double c = Math.Cos(phi), s = Math.Sin(phi);
            double dRow = -s * row - c * col;
            double dCol =  c * row - s * col;

            // Chain rule: φ = sign·angleRad ⇒ d/d(angleRad) = sign·d/dφ. A maps pixels→steps.
            dTargetXdAngle = sign * (a.Xr * dRow + a.Xc * dCol);
            dTargetYdAngle = sign * (a.Yr * dRow + a.Yc * dCol);
            return true;
        }

        #endregion

        #region Chuck angle

        /// <summary>Motor encoder ticks per ONE full CHUCK revolution — NOT the motor's 40000/rev,
        /// because the chuck turns through a ≈9:1 reduction. Measured over multiple revolutions.</summary>
        public const long ChuckTicksPerRev = 359859;

        /// <summary>Motor ticks to rotate the CHUCK by <paramref name="degrees"/> (through the gear),
        /// given the measured/assumed <paramref name="ticksPerRev"/>.</summary>
        public static long DegreesToChuckTicks(double degrees, long ticksPerRev)
            => (long)Math.Round(degrees / 360.0 * ticksPerRev);

        /// <summary>Absolute CHUCK angle in [0, 360) for a raw Θ motor position, folded through the
        /// gear reduction — the inverse of <see cref="DegreesToChuckTicks"/> and the ONLY correct way
        /// to read a chuck angle. Dividing by the motor's 40000 ticks/rev wraps nine times per chuck
        /// revolution, so an absolute "rotate to" computed that way targets the wrong angle.</summary>
        public static double ChuckTicksToDegrees(long ticks, long ticksPerRev)
        {
            double angle = (double)(ticks % ticksPerRev) / ticksPerRev * 360.0;
            return angle < 0 ? angle + 360.0 : angle;
        }

        #endregion
    }
}
