using System;

namespace MotorControlApp
{
    /// <summary>
    /// Geometry for rotating the chuck ABOUT the camera crosshair instead of about the
    /// chuck's own mechanical centre. The Θ axis only ever rotates about the chuck centre
    /// C; to pivot about the crosshair we add an X/Y shift that makes the chuck centre
    /// orbit the crosshair by the same angle — which is exactly what keeps the wafer point
    /// under the crosshair pinned.
    ///
    /// All motor coordinates are in the USER frame (the frame C, the affine, and
    /// TryCurrentUser all use). With the stored pixel→step affine A and the offset of the
    /// current position from the chuck centre, the new X/Y target for a rotation of θ is:
    ///
    ///     S' = C + A·R(φ)·A⁻¹·(S − C),   φ = sign·θ
    ///
    /// A⁻¹·(S−C) is where the chuck centre sits relative to the crosshair, in pixels; R(φ)
    /// orbits it; A maps the result back to steps. <paramref name="sign"/> (±1) is the image
    /// handedness of a positive Θ move — NOT derivable from the translation-only affine, so
    /// it is fixed empirically (Stage 2) and passed in here.
    /// </summary>
    public static class CrosshairRotation
    {
        /// <summary>
        /// Absolute USER-frame X/Y target that, paired with rotating Θ by <paramref name="angleRad"/>
        /// from the start angle, keeps the point under the crosshair fixed. <paramref name="startX"/>/
        /// <paramref name="startY"/> are the ORIGINAL position the rotation began from (pass S₀ on every
        /// incremental step so error never accumulates). Returns false if the affine is degenerate.
        ///
        /// Uses the camera affine A: ΔS = A·(R(φ)−I)·A⁻¹·(S−C). A carries BOTH the per-axis scale
        /// AND the camera↔stage orientation, so the correction cross-couples X and Y correctly —
        /// which a bare steps/mm ratio cannot (it assumes the image axes line up with the stage).
        /// </summary>
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

        /// <summary>
        /// Motor encoder ticks per ONE full CHUCK revolution. The chuck turns through a gear
        /// reduction, so this is NOT the motor's 40000/rev. Measured over multiple full
        /// revolutions: 359859 ticks/rev (≈9:1 reduction).
        /// </summary>
        public const long ChuckTicksPerRev = 359859;

        /// <summary>Motor ticks to rotate the CHUCK by <paramref name="degrees"/> (through the gear),
        /// given the measured/assumed <paramref name="ticksPerRev"/>.</summary>
        public static long DegreesToChuckTicks(double degrees, long ticksPerRev)
            => (long)Math.Round(degrees / 360.0 * ticksPerRev);
    }
}
