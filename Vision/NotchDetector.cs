using System;
using System.Collections.Generic;
using System.Text;
using HalconDotNet;

namespace NanotecController
{
    /// <summary>
    /// Finds the wafer NOTCH in a frame, in two modes that answer two different questions.
    ///
    /// <see cref="TryCoarse"/> asks only "is the rim in this frame anomalous?" — the question a Θ
    /// sweep asks of every frame it passes. <see cref="TryMeasure"/> asks "where exactly is the
    /// apex?" — asked once, of a stationary frame, after the sweep has stopped on a hit.
    ///
    /// They differ in their BASELINE, and that is the whole design. Coarse fits a plain regression
    /// line to the entire rim contour; it needs no clean rim anywhere, so it still fires when the
    /// notch is only half in view, which is what a sweep meets first. Fine anchors a chord on the
    /// two ENDS of the contour, which is far more accurate but demands plain rim at both ends and
    /// refuses a partial notch. Using the fine test to hunt would step straight over the notch.
    ///
    /// Measured through HDevEngine over the twelve captures on file (see `Halcon/notch
    /// detector.hdev`, which is this class's tuning mirror and carries the full derivation):
    ///   coarse residual   plain rim 0.012-0.046 mm    notch frame 0.548 mm
    ///   fine depth        plain rim 0.006-0.032 mm    notch frame 1.002 mm (SEMI nominal 1.00)
    /// The coarse threshold of 0.12 mm sits 2.6x above the worst plain rim — a real margin, but a
    /// tenth of what the fine test enjoys, which is the price of partial-notch sensitivity.
    ///
    /// DO NOT run this on a downscaled frame to save time. Measured at 1/4 with every radius and
    /// area constant rescaled, the notch reads 0.275 mm and plain rim 0.217 mm: the separation is
    /// gone, because the opening/closing radii fall to 2 and 5 px and the gap outline goes ragged,
    /// so the residual measures raggedness rather than shape. There is no need for it either — the
    /// coarse path costs ~230 ms on a full 4016x3024 frame against a ~710 ms sweep budget.
    ///
    /// WHICH BOUNDARY, and why this differs from <see cref="WaferEdgeDetector"/>. The unlit gap past
    /// the rim has two boundaries. WaferEdgeDetector deliberately takes the CHUCK-side one, because
    /// the chuck is in focus and that makes the measured RADIUS repeatable. This class must take the
    /// WAFER-side one instead: the notch is a feature of the wafer's outline, whereas the chuck-side
    /// boundary is where the shadow ends on the chuck, whose shape is set by the illumination
    /// geometry rather than by the notch profile. The wafer side is the choice that produced the
    /// 1.002 mm depth above, against the SEMI nominal of 1.00 — which is the evidence it is tracing
    /// the real edge. Everything else about the segmentation is shared, including the flank-contrast
    /// test, which is a validity test for "is this dark region a rim gap at all" and is unaffected by
    /// which side is then taken.
    /// </summary>
    public sealed class NotchDetector
    {
        // ---- Segmentation. Mirrors WaferEdgeDetector; retune there first, then copy across. ----

        /// <summary>True when the wafer reads BRIGHTER than the off-wafer background.</summary>
        public bool WaferIsBrighter { get; set; } = true;
        public double CleanRadius { get; set; } = 7;
        public double CloseRadius { get; set; } = 21;
        public double SeverRadius { get; set; } = 35;
        public double MinArea { get; set; } = 2e5;
        public double MaxMeanFraction { get; set; } = 0.6;
        public double SideProbeRadius { get; set; } = 50;
        public double MaxSideContrast { get; set; } = 0.80;

        private const double FrameMarginPx = 2;
        private const double MinCollarAreaPx = 5000;
        private const double SideGrowRadiusPx = 2;

        // ---- Notch geometry. These ARE the .hdev's parameters; keep the two in step. ----

        /// <summary>Residual above which the sweep calls a frame anomalous and stops.
        ///
        /// Set against MEASURED full-frame values: plain rim 0.012–0.046 mm, notch 0.548 mm. At 0.30
        /// this is 6.5× the worst plain rim and still 1.8× under the notch. It does NOT need to catch
        /// a barely-visible notch, which is what an earlier 0.12 was for: the sweep grabs a frame
        /// every ~0.67 mm of rim (5.6 mm/s ÷ ~120 ms), and the notch sits wholly inside the 4.9 mm
        /// field over 2.0 mm of travel, so ~3 frames per pass see all of it. Buying sensitivity to a
        /// sliver bought nothing and cost the margin against debris.</summary>
        public double CoarseThresholdMm { get; set; } = 0.30;

        /// <summary>The deviation must PERSIST over this many contiguous contour points, not merely
        /// peak once.
        ///
        /// This is the test that separates a notch from dust on shape rather than on size, which is
        /// the honest distinction: a notch is deep AND wide (thousands of points), while debris, a
        /// skeleton spur, or a ring bridged across a break is a narrow spike that can reach any
        /// height at all. A threshold alone cannot tell them apart — it only asks how far, never how
        /// long — so raising it to reject debris also blinds the sweep to a real notch seen at an
        /// unlucky angle.</summary>
        public int CoarseMinRunPoints { get; set; } = 200;

        /// <summary>Contour points at each END that anchor the fine chord.
        ///
        /// This sets how hard the notch is to CATCH, and it is easy to make far too large. The rim
        /// contour runs 3000-4200 points depending on how the rim crosses the frame; the notch is
        /// 2.9 mm ≈ 2340 of them. On a 3138-point contour that leaves only ~800 points of clean rim,
        /// so two 300-point anchors consume 600 of them and the notch is both enclosed AND clear of
        /// both anchors over a window of just 0.14° of Θ. A search stepping 1° walks straight over
        /// that, which is exactly what was observed on hardware.
        ///
        /// At 120 the window is 0.32-1.16° over the same frames — wider than the search step, which
        /// is the condition that actually matters. Two 120-point anchors 1.6 mm apart are still an
        /// ample line fit; the cost is a little more noise in the chord, not a worse chord.</summary>
        public int EndSpanPoints { get; set; } = 120;

        /// <summary>How far the chord's end spans may depart from straight before the frame is
        /// refused.
        ///
        /// This guards the DEPTH, not the angle. A tilted baseline biases depth and width directly,
        /// but the apex comes from two straight-line fits to raw contour points, and the baseline
        /// only decides which points fall in the flank band — shifting that band along a straight
        /// flank returns the same line. So a modest tilt costs a little depth accuracy and almost no
        /// apex accuracy, and refusing such a frame outright threw away usable measurements: 0.101
        /// and 0.151 mm were both rejected on hardware while the notch was plainly in view.
        /// Its real job is to catch ends that are not rim AT ALL — chuck texture, or a notch so far
        /// off-centre that the baseline is meaningless — and 0.25 still does that. The value is
        /// logged with every result so a depth measured off a tilted chord can be recognised.</summary>
        public double MaxChordFitMm { get; set; } = 0.25;

        public double MinNotchDepthMm { get; set; } = 0.25;

        /// <summary>Deepest a real notch can read before the feature is something else.
        ///
        /// A SEMI 200 mm notch is 1.00 mm and measures 1.002 mm on the capture on file, 0.987 mm on
        /// hardware — the depth is the best-behaved number this detector produces, so a ceiling costs
        /// nothing and catches a whole class of impostor. On 2026-08-06 a vacuum port in the chuck,
        /// whose dark boundary had merged with the rim gap into one region, presented its arc as a
        /// 1.958 mm deep "notch" and was accepted. 1.5 sits 50 % above every real measurement and
        /// 24 % below that one.</summary>
        public double MaxNotchDepthMm { get; set; } = 1.5;
        public double MinNotchWidthMm { get; set; } = 1.5;
        public double MaxNotchWidthMm { get; set; } = 4.0;

        /// <summary>Fraction of full depth that counts as "in the notch" when measuring width.</summary>
        public double DeepFraction { get; set; } = 0.25;

        /// <summary>Band of each flank used for the line fits that refine the apex — clear of the
        /// rounded apex above and the shoulders below.</summary>
        public double FlankLoFraction { get; set; } = 0.25;
        public double FlankHiFraction { get; set; } = 0.85;

        /// <summary>Dust on the rim breaks the ring; this rejoins the pieces. Keep it well under the
        /// notch width or it will bridge ACROSS the notch mouth and erase it.</summary>
        public double BridgeGapPx { get; set; } = 30;

        /// <summary>A contour shorter than this cannot carry two end spans plus a notch. Also the
        /// main defence against chuck texture: a shadow trough's boundary comes out ~280 points
        /// against 3000-4200 for a real rim.</summary>
        public int MinContourPoints { get; set; } = 1500;

        /// <summary>Below this the flank fit is noise and the apex falls back to the deepest point.</summary>
        public int MinFlankPoints { get; set; } = 40;

        /// <summary>A coarse residual is only meaningful over a contour of at least this many points.
        /// Below it the line is fitted to a scrap and the number means nothing — so
        /// <see cref="TryCoarse"/> returns FALSE rather than a small residual that reads as "no
        /// notch here". That distinction matters during a sweep: a real rim in view yields 3000-4200
        /// points, so a short contour means the rim has been LOST, and a sweep that cannot tell
        /// "clean rim, no notch" from "no rim at all" would sail past the notch reporting nothing
        /// wrong for the rest of the revolution.</summary>
        public int MinCoarseContourPoints { get; set; } = 500;

        /// <summary>What a measured notch came to. <paramref name="ApexRow"/>/<paramref name="ApexCol"/>
        /// is the flank-intersection apex — the repeatable reference for the Θ angle. It is NOT where
        /// the depth is measured to: see <paramref name="VertexOffsetMm"/>.</summary>
        public readonly record struct Measurement(
            double DepthMm, double WidthMm,
            double ApexRow, double ApexCol,
            double DeepestRow, double DeepestCol,
            double VertexOffsetMm, double IncludedDeg,
            bool Enclosed, double ChordFitMm);

        /// <summary>Diagnostic from the last call, in the same terms as the .hdev's step messages.</summary>
        public string LastReport { get; private set; } = "";

        /// <summary>
        /// Sweep mode: the rim contour's greatest departure from a straight line, in mm. True when a
        /// contour was obtained at all — <paramref name="residualMm"/> is then compared against
        /// <see cref="CoarseThresholdMm"/> by the caller, so a sweep can log the value on every frame
        /// rather than only on hits.
        /// </summary>
        public bool TryCoarse(HObject image, double umPerPixel, out double residualMm, out int runPoints)
        {
            residualMm = 0;
            runPoints = 0;
            LastReport = "";
            var log = new StringBuilder();
            var temps = new List<HObject>();
            try
            {
                if (!TryContourPoints(image, log, temps, out double[] row, out double[] col))
                    return false;
                if (row.Length < MinCoarseContourPoints)
                {
                    log.Append(" FAIL(rim lost — contour too short to fit)");
                    return false;
                }

                if (!TryLineFit(row, col, 0, row.Length, out double r0, out double c0,
                                out double perpR, out double perpC))
                {
                    log.Append(" FAIL(degenerate line)");
                    return false;
                }

                // Peak departure, and the longest CONTIGUOUS stretch of contour that stays over the
                // threshold. Contiguity is measured in contour order, which is why the ring is walked
                // as an ordered skeleton rather than sampled as a point set.
                double cutPx = CoarseThresholdMm * 1000.0 / umPerPixel;
                double worst = 0;
                int run = 0;
                for (int k = 0; k < row.Length; k++)
                {
                    double d = Math.Abs(perpR * (row[k] - r0) + perpC * (col[k] - c0));
                    if (d > worst) worst = d;
                    if (d > cutPx) { run++; if (run > runPoints) runPoints = run; }
                    else run = 0;
                }
                residualMm = worst * umPerPixel / 1000.0;
                log.Append($" coarse={residualMm:F3}mm run={runPoints}");
                return true;
            }
            catch (HOperatorException) { return false; }
            finally { LastReport = log.ToString(); foreach (HObject t in temps) t.Dispose(); }
        }

        /// <summary>Is this frame worth stopping the sweep for? Deep ENOUGH and wide ENOUGH — see
        /// <see cref="CoarseMinRunPoints"/> for why one test without the other is not enough.</summary>
        public bool IsAnomalous(double residualMm, int runPoints)
            => residualMm > CoarseThresholdMm && runPoints >= CoarseMinRunPoints;

        /// <summary>
        /// Measurement mode, for a STATIONARY frame with the notch fully in view. Returns false, with
        /// the reason in <see cref="LastReport"/>, when the frame has no rim, the contour is too
        /// short, the chord's ends are not plain rim, or the shape fails any of the four notch tests.
        /// </summary>
        public bool TryMeasure(HObject image, double umPerPixel, out Measurement measurement)
        {
            measurement = default;
            LastReport = "";
            var log = new StringBuilder();
            var temps = new List<HObject>();
            try
            {
                if (!TryContourPoints(image, log, temps, out double[] row, out double[] col))
                    return false;
                int n = row.Length;
                if (n < MinContourPoints || n < 4 * EndSpanPoints)
                {
                    log.Append($" FAIL(contour {n} pts)");
                    return false;
                }

                // The baseline chord, from the two ends ONLY. A robust fit over the whole contour
                // cannot be used: in the frame on file the notch is ~3900 of ~4200 points, so it is
                // the MAJORITY and a robust fit converges onto a flank, calling the real rim the
                // outlier. What is reliable is that the rim is straight at this field of view.
                int span = EndSpanPoints;
                var endRow = new double[2 * span];
                var endCol = new double[2 * span];
                Array.Copy(row, 0, endRow, 0, span);
                Array.Copy(col, 0, endCol, 0, span);
                Array.Copy(row, n - span, endRow, span, span);
                Array.Copy(col, n - span, endCol, span, span);
                if (!TryLineFit(endRow, endCol, 0, endRow.Length, out double r0, out double c0,
                                out double perpR, out double perpC))
                {
                    log.Append(" FAIL(degenerate chord)");
                    return false;
                }

                double chordFit = 0;
                for (int k = 0; k < endRow.Length; k++)
                {
                    double d = Math.Abs(perpR * (endRow[k] - r0) + perpC * (endCol[k] - c0));
                    if (d > chordFit) chordFit = d;
                }
                double chordFitMm = chordFit * umPerPixel / 1000.0;
                log.Append($" chordfit={chordFitMm:F3}mm");
                if (chordFitMm > MaxChordFitMm)
                {
                    log.Append(" FAIL(ends are not plain rim)");
                    return false;
                }

                // Which side is INTO the wafer. The notch removes material, so its apex lies on the
                // side the contour bulges towards over its deepest excursion; a bulge the other way
                // is debris on the rim. Taken from the extreme itself rather than from a separate
                // wafer-side probe, so this stays correct whichever way the rim runs in the frame.
                double tanR = -perpC, tanC = perpR;
                int extreme = 0;
                double extremeAbs = -1;
                for (int k = 0; k < n; k++)
                {
                    double d = Math.Abs(perpR * (row[k] - r0) + perpC * (col[k] - c0));
                    if (d > extremeAbs) { extremeAbs = d; extreme = k; }
                }
                double waferSign = Math.Sign(perpR * (row[extreme] - r0) + perpC * (col[extreme] - c0));
                if (waferSign == 0) waferSign = 1;

                var inward = new double[n];
                var along = new double[n];
                for (int k = 0; k < n; k++)
                {
                    inward[k] = waferSign * (perpR * (row[k] - r0) + perpC * (col[k] - c0));
                    along[k] = tanR * (row[k] - r0) + tanC * (col[k] - c0);
                }

                int apexIdx = 0;
                for (int k = 1; k < n; k++) if (inward[k] > inward[apexIdx]) apexIdx = k;
                double depthPx = inward[apexIdx];
                double depthMm = depthPx * umPerPixel / 1000.0;

                // The mouth: the run of points more than DeepFraction of the way down.
                double deepCut = DeepFraction * depthPx;
                int first = -1, last = -1, deepCount = 0;
                double alongLo = 0, alongHi = 0;
                for (int k = 0; k < n; k++)
                {
                    if (inward[k] < deepCut) continue;
                    if (first < 0) { first = k; alongLo = alongHi = along[k]; }
                    last = k;
                    deepCount++;
                    if (along[k] < alongLo) alongLo = along[k];
                    if (along[k] > alongHi) alongHi = along[k];
                }
                if (first < 0) { log.Append(" FAIL(no excursion)"); return false; }

                double widthMm = (alongHi - alongLo) * umPerPixel / 1000.0;
                bool contiguous = last - first + 1 == deepCount;
                bool enclosed = first > 0 && last < n - 1;
                bool deepEnough = depthMm >= MinNotchDepthMm && depthMm <= MaxNotchDepthMm;
                bool widthOk = widthMm >= MinNotchWidthMm && widthMm <= MaxNotchWidthMm;

                log.Append($" depth={depthMm:F3}mm width={widthMm:F3}mm run={deepCount}" +
                           $" contig={contiguous} encl={enclosed}");
                if (!(deepEnough && widthOk && contiguous && enclosed))
                {
                    log.Append(" NO NOTCH");
                    return false;
                }

                // A steadier apex, for the ANGLE only. The deepest pixel rests on one noisy sample;
                // the flanks average hundreds. It is NOT a depth — extrapolating two straight flanks
                // past an apex that is genuinely rounded (~1.1 mm radius on the silhouette, since
                // what is traced is the gap's outer edge and it is defocused besides) overshoots the
                // tip, by 0.37 mm on the frame on file. VertexOffsetMm reports that overshoot rather
                // than letting it inflate the depth.
                double apexRow = row[apexIdx], apexCol = col[apexIdx];
                double includedDeg = -1;
                double flankLo = FlankLoFraction * depthPx, flankHi = FlankHiFraction * depthPx;
                int a1 = -1, a2 = -1, b1 = -1, b2 = -1;
                for (int k = first; k <= apexIdx; k++)
                {
                    if (a1 < 0 && inward[k] >= flankLo) a1 = k;
                    if (a2 < 0 && inward[k] >= flankHi) a2 = k;
                }
                for (int k = last; k >= apexIdx; k--)
                {
                    if (b1 < 0 && inward[k] >= flankLo) b1 = k;
                    if (b2 < 0 && inward[k] >= flankHi) b2 = k;
                }
                if (a1 >= 0 && a2 > a1 + MinFlankPoints && b1 >= 0 && b1 > b2 + MinFlankPoints &&
                    TryLineFit(row, col, a1, a2 - a1 + 1, out double ar, out double ac, out double anR, out double anC) &&
                    TryLineFit(row, col, b2, b1 - b2 + 1, out double br, out double bc, out double bnR, out double bnC))
                {
                    if (TryIntersect(ar, ac, anR, anC, br, bc, bnR, bnC, out double ir, out double ic))
                    {
                        apexRow = ir; apexCol = ic;
                        // Point both flank directions AWAY from the apex before measuring between
                        // them, or the answer depends on which way round each fitted line happens to
                        // run — and the two flanks are traversed in opposite senses.
                        double adR = -anC, adC = anR, bdR = -bnC, bdC = bnR;
                        if (adR * (ar - ir) + adC * (ac - ic) < 0) { adR = -adR; adC = -adC; }
                        if (bdR * (br - ir) + bdC * (bc - ic) < 0) { bdR = -bdR; bdC = -bdC; }
                        includedDeg = Math.Acos(Math.Clamp(adR * bdR + adC * bdC, -1.0, 1.0)) * 180.0 / Math.PI;
                    }
                    else log.Append(" (flanks parallel — raw apex)");
                }
                else log.Append(" (flanks too short — raw apex)");

                double vertexOffsetMm =
                    waferSign * (perpR * (apexRow - r0) + perpC * (apexCol - c0)) * umPerPixel / 1000.0;

                measurement = new Measurement(
                    depthMm, widthMm, apexRow, apexCol, row[apexIdx], col[apexIdx],
                    vertexOffsetMm, includedDeg, enclosed, chordFitMm);
                log.Append($" apex=({apexRow:F1},{apexCol:F1}) incl={includedDeg:F1}deg");
                return true;
            }
            catch (HOperatorException) { return false; }
            finally { LastReport = log.ToString(); foreach (HObject t in temps) t.Dispose(); }
        }

        // ---- Shared front end ------------------------------------------------------

        // Segment the gap, take the WAFER-side boundary ring, and walk it into an ordered point list.
        // gen_contour_region_xld is not used: it traces the border OF the ring, down one side and
        // back the other, giving every rim point twice. skeleton() collapses the ring to one pixel
        // wide and gen_contours_skeleton_xld walks it in order.
        private bool TryContourPoints(
            HObject image, StringBuilder log, List<HObject> temps, out double[] row, out double[] col)
        {
            row = Array.Empty<double>();
            col = Array.Empty<double>();

            HObject? rim = SegmentRim(image, log, temps);
            if (rim == null) return false;
            temps.Add(rim);

            HOperatorSet.Skeleton(rim, out HObject skel); temps.Add(skel);
            HOperatorSet.GenContoursSkeletonXld(skel, out HObject pieces, 1, "filter"); temps.Add(pieces);
            HOperatorSet.CountObj(pieces, out HTuple pieceCount);
            if (pieceCount.I < 1) { log.Append(" FAIL(no contour)"); return false; }

            HOperatorSet.UnionAdjacentContoursXld(pieces, out HObject joined, BridgeGapPx, 1, "attr_keep");
            temps.Add(joined);
            HOperatorSet.CountObj(joined, out HTuple joinedCount);
            if (joinedCount.I < 1) { log.Append(" FAIL(no contour)"); return false; }

            HOperatorSet.LengthXld(joined, out HTuple lengths);
            double[] len = lengths.ToDArr();
            int longest = 0;
            for (int i = 1; i < len.Length; i++) if (len[i] > len[longest]) longest = i;

            HOperatorSet.SelectObj(joined, out HObject contour, longest + 1); temps.Add(contour);
            HOperatorSet.GetContourXld(contour, out HTuple rows, out HTuple cols);
            row = rows.ToDArr();
            col = cols.ToDArr();
            log.Append($" pts={row.Length}");
            return row.Length >= 2;
        }

        // The gap's wafer-side boundary, clipped clear of the image frame. Same cut, cleanup and
        // per-region gates as WaferEdgeDetector; the side taken is the opposite one (see remarks).
        private HObject? SegmentRim(HObject image, StringBuilder log, List<HObject> temps)
        {
            HObject gray = Preprocess(image); temps.Add(gray);
            HOperatorSet.GetImageSize(gray, out HTuple width, out HTuple height);

            HOperatorSet.GetDomain(gray, out HObject domain); temps.Add(domain);
            HOperatorSet.GrayHisto(domain, gray, out HTuple absolute, out HTuple _);
            double[] histogram = absolute.ToDArr();

            int split = Otsu(histogram, 0, 255);
            int cut = WaferIsBrighter ? Otsu(histogram, 0, split) : Otsu(histogram, split, 255);
            HOperatorSet.Threshold(gray, out HObject off,
                WaferIsBrighter ? 0 : cut, WaferIsBrighter ? cut : 255); temps.Add(off);

            HOperatorSet.OpeningCircle(off, out HObject opened, CleanRadius); temps.Add(opened);
            HOperatorSet.ClosingCircle(opened, out HObject closed, CloseRadius); temps.Add(closed);
            HOperatorSet.OpeningCircle(closed, out HObject severed, SeverRadius); temps.Add(severed);
            HOperatorSet.Connection(severed, out HObject conn); temps.Add(conn);
            HOperatorSet.SelectShape(conn, out HObject byArea, "area", "and", MinArea, 1e9); temps.Add(byArea);

            double loMean = 0, hiMean = MaxMeanFraction * cut;
            if (!WaferIsBrighter) { loMean = 255 - MaxMeanFraction * (255 - cut); hiMean = 255; }
            HOperatorSet.SelectGray(byArea, gray, out HObject byGray, "mean", "and", loMean, hiMean);
            temps.Add(byGray);

            HOperatorSet.CountObj(byGray, out HTuple regionCount);
            log.Append($"cut={cut} dark={regionCount.I}");
            if (regionCount.I < 1) return null;

            HObject? ring = null;
            for (int i = 1; i <= regionCount.I; i++)
            {
                HOperatorSet.SelectObj(byGray, out HObject one, i); temps.Add(one);
                HObject? side = SelectWaferSide(one, gray, log, temps);
                if (side == null) continue;
                temps.Add(side);

                HOperatorSet.Boundary(one, out HObject border, "inner"); temps.Add(border);
                HOperatorSet.Intersection(border, side, out HObject part); temps.Add(part);
                if (ring == null) { ring = part; continue; }
                HOperatorSet.Union2(ring, part, out HObject merged); temps.Add(merged);
                ring = merged;
            }
            if (ring == null) return null;

            // Drop the stretches that merely run along the image frame. A frame taken wholly off the
            // wafer is all "off-wafer" and its only boundary is that one.
            HOperatorSet.GenRectangle1(out HObject inner, FrameMarginPx, FrameMarginPx,
                height.D - 1 - FrameMarginPx, width.D - 1 - FrameMarginPx); temps.Add(inner);
            HOperatorSet.Intersection(ring, inner, out HObject clipped); temps.Add(clipped);
            HOperatorSet.AreaCenter(clipped, out HTuple ringArea, out _, out _);
            if (ringArea.D < 1) { log.Append(" FAIL(ring is all frame)"); return null; }

            HOperatorSet.CopyObj(clipped, out HObject result, 1, -1);
            return result;
        }

        // The BRIGHTER of the region's two largest flanks — the bevel's specular glint, i.e. the
        // wafer side. The flank-contrast test is kept exactly as WaferEdgeDetector applies it: it
        // decides whether this dark region is a rim gap at all (a shadow trough in the machined chuck
        // has chuck on both flanks and reads near 1), which is independent of which side is taken.
        private HObject? SelectWaferSide(HObject gap, HObject gray, StringBuilder log, List<HObject> temps)
        {
            HOperatorSet.DilationCircle(gap, out HObject grown, SideProbeRadius); temps.Add(grown);
            HOperatorSet.Difference(grown, gap, out HObject collar); temps.Add(collar);
            HOperatorSet.Connection(collar, out HObject pieces); temps.Add(pieces);
            HOperatorSet.SelectShape(pieces, out HObject sides, "area", "and", MinCollarAreaPx, 1e9);
            temps.Add(sides);
            HOperatorSet.CountObj(sides, out HTuple sideCount);
            if (sideCount.I < 2) { log.Append(" DROP(1 flank)"); return null; }

            HOperatorSet.AreaCenter(sides, out HTuple areas, out _, out _);
            double[] area = areas.ToDArr();
            int first = 0;
            for (int i = 1; i < area.Length; i++) if (area[i] > area[first]) first = i;
            int second = -1;
            for (int i = 0; i < area.Length; i++)
                if (i != first && (second < 0 || area[i] > area[second])) second = i;

            HOperatorSet.SelectObj(sides, out HObject two, new HTuple(new[] { first + 1, second + 1 }));
            temps.Add(two);
            HOperatorSet.Intensity(two, gray, out HTuple mean, out HTuple _dev);
            double[] avg = mean.ToDArr();

            double lo = Math.Min(avg[0], avg[1]), hi = Math.Max(avg[0], avg[1]);
            if (hi <= 0 || lo / hi > MaxSideContrast) { log.Append(" DROP(trough)"); return null; }

            HOperatorSet.SelectObj(two, out HObject wafer, avg[0] >= avg[1] ? 1 : 2); temps.Add(wafer);
            HOperatorSet.DilationCircle(wafer, out HObject reach, SideGrowRadiusPx);
            return reach;
        }

        // ---- Small maths -----------------------------------------------------------

        // Total-least-squares line through count points starting at start, returned as a point on the
        // line plus a UNIT NORMAL. The normal is built here rather than taken from HALCON's Nr/Nc/Dist
        // because that convention's sign and offset are easy to get subtly wrong, and getting it wrong
        // would flip which side counts as "into the wafer" while still producing plausible numbers.
        private static bool TryLineFit(
            double[] row, double[] col, int start, int count,
            out double r0, out double c0, out double perpR, out double perpC)
        {
            r0 = c0 = 0; perpR = 0; perpC = 1;
            if (count < 2) return false;

            double sr = 0, sc = 0;
            for (int k = start; k < start + count; k++) { sr += row[k]; sc += col[k]; }
            r0 = sr / count; c0 = sc / count;

            double srr = 0, scc = 0, src = 0;
            for (int k = start; k < start + count; k++)
            {
                double dr = row[k] - r0, dc = col[k] - c0;
                srr += dr * dr; scc += dc * dc; src += dr * dc;
            }
            // Principal axis of the scatter; the normal is perpendicular to it.
            double theta = 0.5 * Math.Atan2(2 * src, srr - scc);
            double dirR = Math.Cos(theta), dirC = Math.Sin(theta);
            if (Math.Abs(dirR) < 1e-12 && Math.Abs(dirC) < 1e-12) return false;
            perpR = -dirC; perpC = dirR;
            return true;
        }

        // Intersection of two lines each given as (point, unit normal). False when they are parallel.
        private static bool TryIntersect(
            double ar, double ac, double anR, double anC,
            double br, double bc, double bnR, double bnC,
            out double row, out double col)
        {
            row = col = 0;
            double det = anR * bnC - anC * bnR;
            if (Math.Abs(det) < 1e-12) return false;
            double da = anR * ar + anC * ac;
            double db = bnR * br + bnC * bc;
            row = (da * bnC - anC * db) / det;
            col = (anR * db - da * bnR) / det;
            return true;
        }

        private static int Otsu(double[] histogram, int lo, int hi)
        {
            double total = 0, moment = 0;
            for (int i = lo; i <= hi; i++) { total += histogram[i]; moment += i * histogram[i]; }
            if (total <= 0) return lo;

            double weightLow = 0, momentLow = 0, best = -1;
            int threshold = lo;
            for (int i = lo; i <= hi; i++)
            {
                weightLow += histogram[i];
                if (weightLow == 0) continue;
                double weightHigh = total - weightLow;
                if (weightHigh == 0) break;
                momentLow += i * histogram[i];
                double meanLow = momentLow / weightLow, meanHigh = (moment - momentLow) / weightHigh;
                double between = (weightLow / total) * (weightHigh / total) * (meanLow - meanHigh) * (meanLow - meanHigh);
                if (between > best) { best = between; threshold = i; }
            }
            return threshold;
        }

        private static HObject Preprocess(HObject image)
        {
            HOperatorSet.CountChannels(image, out HTuple channels);
            if (channels.I >= 3)
            {
                HOperatorSet.AccessChannel(image, out HObject red, 1);
                try { HOperatorSet.ConvertImageType(red, out HObject red8, "byte"); return red8; }
                finally { red.Dispose(); }
            }
            HOperatorSet.ConvertImageType(image, out HObject byteImg, "byte");
            return byteImg;
        }
    }
}
