using System;

namespace NanotecController
{
    /// <summary>
    /// Where to stand to watch the wafer rim go past, and what angle of the wafer is being watched.
    ///
    /// A 200 mm rim circle is larger than the X/Y travel, so the camera cannot be driven around it
    /// (see FrmVisionProtocols.AutoWafer) — only the line X = X min crosses the rim at all. The
    /// wafer is therefore TURNED past a fixed station, and the station's only freedom is Y.
    ///
    /// The wafer sits eccentric on the chuck, so its centre orbits the rotation axis as Θ turns and
    /// the rim swings radially in and out. The centre-find already measured that orbit
    /// (WaferOffsetX/Y + WaferRadius), so the station's Y can be COMPUTED for any Θ rather than
    /// searched for: it is where the vertical line X = station crosses the circle of radius R about
    /// <see cref="CalibrationStore.WaferCentreAt"/>. That is what makes a continuous sweep possible
    /// — the Y path is known in advance instead of being hunted frame by frame.
    ///
    /// Everything here is computed in MILLIMETRES and returned in steps. X and Y have different
    /// StepsPerMm (1261.5 vs 1256.5), so the rim is only a circle — and a rotation only a rotation —
    /// once that anisotropy is divided out. This matches WaferCentreScan, which measured the offset
    /// the same way; doing it in steps here would disagree with the data it is built on.
    /// </summary>
    public static class RimStation
    {
        /// <summary>Angular step (degrees) used when walking a full revolution in
        /// <see cref="TryStationYRange"/>. 1° is ~1.7 mm of rim and moves the station Y by at most
        /// 114 steps, far finer than the ~10 mm peak-to-peak the path covers, so no extreme is
        /// stepped over. (Measured against the stored calibration: the path runs 58,126..70,662
        /// steps — 9.98 mm — for a 2.53 mm eccentricity, and its peak rate is 365 steps/s over a
        /// 112 s revolution, which is 1/9th of the X/Y follower's velocity cap.)</summary>
        private const double RANGE_WALK_DEG = 1.0;

        /// <summary>
        /// The station Y (motor steps) that puts the camera on the wafer rim when the chuck stands
        /// at <paramref name="chuckAngleDeg"/>, with X pinned at <paramref name="stationX"/>.
        ///
        /// The line crosses the rim circle TWICE. <paramref name="preferY"/> chooses between them —
        /// pass the previous step's Y so a sweep follows one branch continuously instead of jumping
        /// to the far side of the wafer. Returns false if the calibration is incomplete or the
        /// station line misses the rim entirely at this angle.
        /// </summary>
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

        /// <summary>
        /// BOTH points where the station line crosses the rim, in USER-frame steps —
        /// <paramref name="up"/> the greater. Exposed because choosing between them is a decision in
        /// its own right (<see cref="TryChooseBranch"/>) and cannot be made by asking
        /// <see cref="TryStationY"/> twice with extreme preferences: the differences overflow to
        /// infinity and compare equal, so both calls return the same crossing.
        /// </summary>
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

        /// <summary>
        /// Picks WHICH of the two crossings to sweep on: the one whose entire revolution fits inside
        /// <paramref name="travelMinY"/>..<paramref name="travelMaxY"/> (USER frame). If both fit, the
        /// one nearer <paramref name="preferY"/> wins, so the run does not traverse the wafer for no
        /// reason.
        ///
        /// This cannot be guessed from the travel limits. On the machine this was written for the
        /// UPPER crossing runs 58,126..70,662 and leaves travel for 42% of the revolution, while the
        /// LOWER one runs −69,438..−56,902 and fits completely — and "nearest Y max", the obvious
        /// rule, picks the upper one. The centre-find gets away with having no rule here because it
        /// rasters down from Y max until it happens to see the rim and skips whatever samples it then
        /// loses; a continuous sweep cannot skip, so the branch has to be chosen deliberately.
        /// </summary>
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

        /// <summary>
        /// The Y extremes the station sweeps over one full revolution, following the branch that
        /// <paramref name="startY"/> picks at <paramref name="startAngleDeg"/>. Walked step by step
        /// rather than solved, because the branch choice is what makes the path continuous.
        ///
        /// Call this BEFORE a sweep: the whole path has to be inside travel, and finding that out
        /// half a revolution in wastes a minute and leaves the stage out on the rim.
        /// </summary>
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

        /// <summary>
        /// Which way the camera station bears from the WAFER centre when the chuck stands at
        /// <paramref name="chuckAngleDeg"/> — a LAB bearing, the same convention the notch datum uses.
        ///
        /// It is not a constant, which is the whole reason this exists. The station rides the rim as
        /// Θ turns while the wafer centre orbits the rotation axis, so the bearing drifts by roughly
        /// ±atan(e/R) about its mean. Solving for it is what lets a check put the notch under the
        /// crosshair rather than near it: one degree is 1.75 mm of rim against a ~4.9 mm frame.
        /// </summary>
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
            if (Math.Abs(bx) < 1e-9 && Math.Abs(by) < 1e-9)
            {
                error = "The station coincides with the wafer centre, so it has no bearing.";
                return false;
            }
            bearingDeg = Math.Atan2(by, bx) * 180.0 / Math.PI;
            if (bearingDeg < 0) bearingDeg += 360.0;
            return true;
        }

        /// <summary>
        /// The angle of a rim point in the CHUCK's rotating frame — i.e. an angle fixed to the
        /// wafer, which is what a notch position has to be. <paramref name="pointX"/>/
        /// <paramref name="pointY"/> are a rim point in user-frame motor steps (as
        /// CentreFinder.ToStepPoint builds it) and <paramref name="chuckAngleDeg"/> is the angle Θ
        /// stood at when it was measured.
        ///
        /// De-rotates about the chuck centre by −sign·θ exactly as WaferCentreScan does, then
        /// measures the bearing from the WAFER centre — not the chuck centre, which is offset by
        /// the eccentricity and would tilt the answer by up to atan(2.55/100) ≈ 1.5°.
        ///
        /// The result is 0–360°, increasing the same way <see cref="CalibrationStore.WaferCentreAt"/>
        /// takes its angle, so notchAngle − currentΘ is directly a Θ move.
        /// </summary>
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
            if (Math.Abs(bx) < 1e-9 && Math.Abs(by) < 1e-9)
            {
                error = "The rim point coincides with the wafer centre, so it has no bearing.";
                return false;
            }

            angleDeg = Math.Atan2(by, bx) * 180.0 / Math.PI;
            if (angleDeg < 0) angleDeg += 360.0;
            error = null;
            return true;
        }

        // Everything the two public entry points share: the per-axis scales, the rim radius, and
        // the wafer centre at this angle — all in mm. One place to fail, with one message per
        // missing prerequisite rather than a bare false.
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
            // RadiusSteps was fitted in mm and scaled by the MEAN steps/mm (WaferCentreScan), because
            // a radius is isotropic and cannot carry a per-axis scale. Undo it the same way.
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
    }
}
