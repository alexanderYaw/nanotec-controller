using System;

namespace NanotecController
{
    /// <summary>
    /// Where to stand to watch the wafer rim go past, and what angle of the wafer is being watched.
    /// A 200 mm rim circle exceeds the X/Y travel, so the wafer is TURNED past a fixed station whose
    /// only freedom is Y. Because the centre-find already measured the eccentric orbit
    /// (WaferOffsetX/Y + WaferRadius), that Y can be COMPUTED for any Θ rather than hunted frame by
    /// frame — which is what makes a continuous sweep possible.
    ///
    /// Everything is computed in MILLIMETRES and returned in steps: X and Y differ in StepsPerMm, so
    /// the rim is only a circle once that anisotropy is divided out. Matches
    /// <see cref="WaferCentreScan"/>, which measured the offset the same way.
    /// See Developer Guide, NotchSearch.md.
    /// </summary>
    public static class RimStation
    {
        #region Rim crossings

        /// <summary>Angular step for the full-revolution walk in <see cref="TryStationYRange"/>. 1° is
        /// ~1.7 mm of rim and moves the station Y by at most 114 steps, far finer than the ~10 mm
        /// peak-to-peak the path covers, so no extreme is stepped over.</summary>
        private const double RANGE_WALK_DEG = 1.0;

        private const double CoincidentTolerance = 1e-9;

        /// <summary>The station Y (motor steps) putting the camera on the wafer rim at
        /// <paramref name="chuckAngleDeg"/>, with X pinned at <paramref name="stationX"/>. The line
        /// crosses the rim TWICE; pass the previous step's Y as <paramref name="preferY"/> so a sweep
        /// follows one branch instead of jumping across the wafer.</summary>
        public static bool TryStationY(
            CalibrationStore cal, double chuckAngleDeg, double stationX, double preferY,
            out double y, out string? error)
        {
            y = preferY;
            if (!TryCrossings(cal, chuckAngleDeg, stationX, out double up, out double down, out error))
                return false;
            y = Math.Abs(up - preferY) <= Math.Abs(down - preferY) ? up : down;
            return true;
        }

        /// <summary>BOTH points where the station line crosses the rim, in USER-frame steps,
        /// <paramref name="up"/> the greater. Exposed because <see cref="TryChooseBranch"/> cannot get
        /// them by calling <see cref="TryStationY"/> twice with extreme preferences — those
        /// differences overflow to infinity and compare equal, returning the same crossing.</summary>
        public static bool TryCrossings(
            CalibrationStore cal, double chuckAngleDeg, double stationX,
            out double up, out double down, out string? error)
        {
            up = down = 0;
            if (!TryGeometry(cal, chuckAngleDeg, out double kX, out double kY, out double radiusMm,
                             out double centreXMm, out double centreYMm, out error))
                return false;

            // Half-chord of the station line across the rim circle, in mm.
            double dx = stationX / kX - centreXMm;
            double disc = radiusMm * radiusMm - dx * dx;
            if (disc <= 0)
            {
                error = $"The station line X={stationX:F0} stands {Math.Abs(dx):F1} mm from the wafer " +
                        $"centre at Θ={chuckAngleDeg:F1}°, which is outside the {radiusMm:F1} mm rim — " +
                        "it never crosses. Check the station X and the stored wafer radius.";
                return false;
            }

            double half = Math.Sqrt(disc);
            up = (centreYMm + half) * kY;
            down = (centreYMm - half) * kY;
            return true;
        }

        /// <summary>Picks WHICH crossing to sweep on: the one whose entire revolution fits inside
        /// <paramref name="travelMinY"/>..<paramref name="travelMaxY"/>, nearer <paramref name="preferY"/>
        /// if both fit. It cannot be guessed from the travel limits — "nearest Y max", the obvious
        /// rule, picks a branch that leaves travel 42% of the way round. See NotchSearch.md §branch.</summary>
        public static bool TryChooseBranch(
            CalibrationStore cal, double stationX, double startAngleDeg,
            double travelMinY, double travelMaxY, double preferY,
            out double startY, out double minY, out double maxY, out string? error)
        {
            startY = minY = maxY = 0;
            if (!TryCrossings(cal, startAngleDeg, stationX, out double up, out double down, out error))
                return false;

            string report = "";
            double bestGap = double.MaxValue;
            bool found = false;
            foreach ((string name, double candidate) in new[] { ("upper", up), ("lower", down) })
            {
                if (!TryStationYRange(cal, stationX, startAngleDeg, candidate,
                                      out double lo, out double hi, out error))
                    return false;
                bool fits = lo >= travelMinY && hi <= travelMaxY;
                double over = Math.Max(0, hi - travelMaxY) + Math.Max(0, travelMinY - lo);
                report += $"\n  {name}: {lo:F0}..{hi:F0}" + (fits ? " — fits" : $" — leaves travel by {over:F0} steps");
                if (!fits) continue;

                double gap = Math.Abs(candidate - preferY);
                if (!found || gap < bestGap)
                { found = true; bestGap = gap; startY = candidate; minY = lo; maxY = hi; }
            }

            if (!found)
            {
                error = $"Neither rim crossing stays inside Y travel {travelMinY:F0}..{travelMaxY:F0}:{report}";
                return false;
            }
            error = null;
            return true;
        }

        /// <summary>The Y extremes the station sweeps over one full revolution on the branch
        /// <paramref name="startY"/> picks. Walked rather than solved, because the branch choice is
        /// what makes the path continuous. Call BEFORE a sweep — discovering the path leaves travel
        /// half a revolution in wastes a minute and strands the stage on the rim.</summary>
        public static bool TryStationYRange(
            CalibrationStore cal, double stationX, double startAngleDeg, double startY,
            out double minY, out double maxY, out string? error)
        {
            minY = maxY = startY;
            double prev = startY;
            for (double d = 0; d < 360.0; d += RANGE_WALK_DEG)
            {
                if (!TryStationY(cal, startAngleDeg + d, stationX, prev, out double at, out error))
                    return false;
                prev = at;
                if (at < minY) minY = at;
                if (at > maxY) maxY = at;
            }
            error = null;
            return true;
        }

        #endregion

        #region Bearings

        /// <summary>Which way the camera station bears from the WAFER centre at
        /// <paramref name="chuckAngleDeg"/> — a LAB bearing, the convention the notch datum uses. Not
        /// a constant: the bearing drifts by ~±atan(e/R) as the wafer centre orbits, and one degree is
        /// 1.75 mm of rim against a ~4.9 mm frame.</summary>
        public static bool TryStationBearing(
            CalibrationStore cal, double chuckAngleDeg, double stationX, double preferY,
            out double bearingDeg, out double stationY, out string? error)
        {
            bearingDeg = 0;
            if (!TryStationY(cal, chuckAngleDeg, stationX, preferY, out stationY, out error))
                return false;
            if (!TryGeometry(cal, chuckAngleDeg, out double kX, out double kY, out _,
                             out double centreXMm, out double centreYMm, out error))
                return false;

            double bx = stationX / kX - centreXMm, by = stationY / kY - centreYMm;
            if (Math.Abs(bx) < CoincidentTolerance && Math.Abs(by) < CoincidentTolerance)
            {
                error = "The station coincides with the wafer centre, so it has no bearing.";
                return false;
            }
            bearingDeg = Math.Atan2(by, bx) * 180.0 / Math.PI;
            if (bearingDeg < 0) bearingDeg += 360.0;
            return true;
        }

        /// <summary>The angle of a rim point in the CHUCK's rotating frame — an angle fixed to the
        /// wafer, which is what a notch position has to be. De-rotates about the chuck centre by
        /// −sign·θ as <see cref="WaferCentreScan"/> does, then bears from the WAFER centre, not the
        /// chuck centre (the eccentricity would tilt the answer by up to ~1.5°). Returns 0–360°
        /// increasing the same way <see cref="CalibrationStore.WaferCentreAt"/> takes its angle, so
        /// notchAngle − currentΘ is directly a Θ move.</summary>
        public static bool TryChuckFrameAngle(
            CalibrationStore cal, double chuckAngleDeg, double pointX, double pointY,
            out double angleDeg, out string? error)
        {
            angleDeg = 0;
            if (!TryGeometry(cal, chuckAngleDeg, out double kX, out double kY, out _,
                             out _, out _, out error))
                return false;
            if (cal.ChuckCenterX is not long ccx || cal.ChuckCenterY is not long ccy ||
                cal.WaferOffsetX is not long ox || cal.WaferOffsetY is not long oy ||
                cal.WaferFitSign is not int sign)
            {
                error = "The chuck-frame angle needs the chuck centre and a wafer Θ scan.";
                return false;
            }

            // Into the chuck frame, in mm.
            double vx = (pointX - ccx) / kX, vy = (pointY - ccy) / kY;
            double rad = -sign * chuckAngleDeg * Math.PI / 180.0;
            double c = Math.Cos(rad), s = Math.Sin(rad);
            double rx = c * vx - s * vy, ry = s * vx + c * vy;

            // Bearing from the wafer centre, which in that frame is the stored offset.
            double bx = rx - ox / kX, by = ry - oy / kY;
            if (Math.Abs(bx) < CoincidentTolerance && Math.Abs(by) < CoincidentTolerance)
            {
                error = "The rim point coincides with the wafer centre, so it has no bearing.";
                return false;
            }

            angleDeg = Math.Atan2(by, bx) * 180.0 / Math.PI;
            if (angleDeg < 0) angleDeg += 360.0;
            error = null;
            return true;
        }

        #endregion

        #region Shared geometry

        /// <summary>The per-axis scales, rim radius and wafer centre at this angle, all in mm — one
        /// place to fail, with a message per missing prerequisite rather than a bare false.</summary>
        private static bool TryGeometry(
            CalibrationStore cal, double chuckAngleDeg,
            out double kX, out double kY, out double radiusMm,
            out double centreXMm, out double centreYMm, out string? error)
        {
            kX = kY = radiusMm = centreXMm = centreYMm = 0;
            error = null;

            kX = cal.For(AxisId.X).StepsPerMm ?? 0;
            kY = cal.For(AxisId.Y).StepsPerMm ?? 0;
            if (kX <= 0 || kY <= 0)
            {
                error = "X and Y both need steps-per-mm before the rim station can be computed.";
                return false;
            }
            if (cal.WaferRadius is not long r || r <= 0)
            {
                error = "No wafer radius stored — run the automatic wafer centre-find first.";
                return false;
            }
            // Fitted in mm and scaled by the MEAN steps/mm, a radius being isotropic. Undo it the same way.
            radiusMm = r / ((kX + kY) / 2.0);

            if (cal.WaferCentreAt(chuckAngleDeg) is not (long cx, long cy))
            {
                error = "The wafer centre for this Θ is unavailable — the wafer centre-find has not run, " +
                        "or the chuck centre is missing.";
                return false;
            }
            centreXMm = cx / kX;
            centreYMm = cy / kY;
            return true;
        }

        #endregion
    }
}
