using System;
using System.Collections.Generic;
using System.Text;
using HalconDotNet;

namespace NanotecController
{
    /// <summary>
    /// Locates the wafer EDGE point nearest a reference pixel by segmenting the OFF-WAFER side — the
    /// region beyond the rim, which reads near-black. Segmenting the bright side does not work: the
    /// bevel is a mid-grey band that can read either side of a global cut, and when it reads dark it
    /// splits the wafer into two blobs whose shared boundary is nearer the crosshair than the rim.
    ///
    /// The cut is two-stage Otsu, the second taken inside the darker part alone, which separates the
    /// sustained black beyond the rim from mid-grey bevel and background. Otsu always returns a cut
    /// even on a frame with no rim, so candidates are then filtered by AREA, MEAN GREY relative to
    /// the cut, and FLANK CONTRAST — all three are needed, because the machined chuck's shadow
    /// troughs are as large as a real gap and only the flank test separates them. Filters run PER
    /// REGION and the rim ring is assembled from those that pass; merging first would let one trough
    /// contribute boundary that wins the nearest-point search.
    ///
    /// Radially the scene runs wafer - bevel - gap - chuck, and it is the GAP-TO-CHUCK boundary that
    /// is measured, the chuck being in focus and so the sharper of the two. The side is chosen by
    /// taking a collar either side and keeping the DARKER one.
    ///
    /// Contrast <see cref="ChuckEdgeDetector"/>, whose FIXED threshold works because its grey levels
    /// are set by illumination and material rather than exposure. Pass the FULL-RESOLUTION frame.
    /// See Developer Guide, WaferCentreByRotation.md.
    /// </summary>
    public sealed class WaferEdgeDetector
    {
        #region Tunables

        /// <summary>True when the wafer reads BRIGHTER than the off-wafer background (the usual case);
        /// false if the lighting makes the wafer the darker side.</summary>
        public bool WaferIsBrighter { get; set; } = true;

        /// <summary>Opening radius to erase speckle in the off-wafer region before selecting the blob.</summary>
        public double CleanRadius { get; set; } = 7;

        /// <summary>Closing radius to bridge bright specks (dust, debris) inside the off-wafer region
        /// so it forms ONE solid blob whose border is the clean rim.</summary>
        public double CloseRadius { get; set; } = 21;

        /// <summary>Opening radius applied AFTER the closing, severing dark structures narrower than
        /// twice this. The chuck's machined gashes read below the cut and the closing bridges the near
        /// ones into the gap as tendrils; the band is ~345 px wide and the tendrils a few tens, so
        /// this cuts cleanly between them.</summary>
        public double SeverRadius { get; set; } = 35;

        /// <summary>Ignore regions smaller than this (px²) — dark die structures, wafer shadows, and
        /// the smaller chuck troughs are never the rim.</summary>
        public double MinArea { get; set; } = 2e5;

        /// <summary>A region is only the off-wafer gap if genuinely BLACK, not merely below the cut.
        /// Taken as a FRACTION of the stage-2 cut, because that cut is relative by design.</summary>
        public double MaxMeanFraction { get; set; } = 0.6;

        /// <summary>Width (px) of the collar sampled either side of the gap. Wide enough to average
        /// over the chuck's texture, narrow enough that the wafer-side collar stays on the bevel glint
        /// rather than reaching the darker wafer beyond it.</summary>
        public double SideProbeRadius { get; set; } = 50;

        /// <summary>Darker collar mean over brighter must be at or below this. A rim gap has the
        /// bevel's glint on one flank and chuck on the other; a shadow trough has chuck on BOTH and
        /// reads near 1. This is the only test that separates them.</summary>
        public double MaxSideContrast { get; set; } = 0.80;

        /// <summary>Boundary points this close to the frame edge are the frame, not the rim.</summary>
        private const double FrameMarginPx = 2;

        /// <summary>Collar fragments below this (px²) are discretisation slivers, not a side of the gap.</summary>
        private const double MinCollarAreaPx = 5000;

        /// <summary>Grows the chosen side back far enough to overlap the gap's boundary ring.
        /// Deliberately tiny — a large value would reach across a narrow gap and re-admit the far side.</summary>
        private const double SideGrowRadiusPx = 2;

        private const double MaxRegionArea = 1e9;

        #endregion

        #region Detection

        /// <summary>A point on the wafer edge, in image pixels (HALCON row/column).</summary>
        public readonly record struct EdgePoint(double Row, double Column);

        /// <summary>What the last <see cref="TryDetect"/> saw: the cut, and per candidate region its
        /// area, mean and flank means with the verdict. The diagnostic to read when a live scan reports
        /// a point off the rim — it says WHICH filter let the wrong region through. Overwritten every
        /// call, so it belongs to one detection at a time.</summary>
        public string LastReport { get; private set; } = "";

        /// <summary>Detects and disposes the contour internally. Returns the edge point nearest the crosshair.</summary>
        public bool TryDetect(HObject image, double crossRow, double crossCol, out EdgePoint point)
        {
            bool ok = TryDetect(image, crossRow, crossCol, out point, out HObject? contour);
            contour?.Dispose();
            return ok;
        }

        /// <summary>Detects the wafer-edge point nearest (<paramref name="crossRow"/>,
        /// <paramref name="crossCol"/>). On success also returns the boundary <paramref name="contour"/>
        /// the point lies on, for overlay — CALLER OWNS it and must Dispose it. False if nothing is
        /// found or a HALCON op fails; the input frame is never modified.</summary>
        public bool TryDetect(HObject image, double crossRow, double crossCol, out EdgePoint point, out HObject? contour)
        {
            point = default;
            contour = null;
            LastReport = "";
            var log = new StringBuilder();
            var temps = new List<HObject>();
            try
            {
                HObject gray = Preprocess(image); temps.Add(gray);
                HOperatorSet.GetImageSize(gray, out HTuple width, out HTuple height);

                HOperatorSet.GetDomain(gray, out HObject domain); temps.Add(domain);
                HOperatorSet.GrayHisto(domain, gray, out HTuple absolute, out HTuple _);
                double[] histogram = absolute.ToDArr();

                // Two-stage Otsu: split off the lit wafer, then split again inside the dark side.
                int split = Otsu(histogram, 0, 255);
                int cut = WaferIsBrighter ? Otsu(histogram, 0, split) : Otsu(histogram, split, 255);
                HOperatorSet.Threshold(gray, out HObject off,
                    WaferIsBrighter ? 0 : cut, WaferIsBrighter ? cut : 255); temps.Add(off);

                // Clean speckle, then close specks so the off-wafer region is one solid blob.
                // Deliberately NOT filled: a hole would only ever be debris beyond the rim.
                HOperatorSet.OpeningCircle(off, out HObject opened, CleanRadius); temps.Add(opened);
                HOperatorSet.ClosingCircle(opened, out HObject closed, CloseRadius); temps.Add(closed);
                // Sever the chuck's gashes AFTER the closing, not via a larger CleanRadius: opening
                // that wide first leaves dust specks too big for the closing to fill.
                HOperatorSet.OpeningCircle(closed, out HObject severed, SeverRadius); temps.Add(severed);
                HOperatorSet.Connection(severed, out HObject conn); temps.Add(conn);
                HOperatorSet.SelectShape(conn, out HObject byArea, "area", "and", MinArea, MaxRegionArea); temps.Add(byArea);

                // A FILTER rather than a verdict, so a real gap still wins when a brighter blob
                // happens to sit nearer the crosshair.
                double loMean = 0, hiMean = MaxMeanFraction * cut;
                if (!WaferIsBrighter) { loMean = 255 - MaxMeanFraction * (255 - cut); hiMean = 255; }
                HOperatorSet.SelectGray(byArea, gray, out HObject byGray, "mean", "and", loMean, hiMean);
                temps.Add(byGray);

                HOperatorSet.CountObj(conn, out HTuple partCount);
                HOperatorSet.CountObj(byArea, out HTuple bigCount);
                HOperatorSet.CountObj(byGray, out HTuple regionCount);
                log.Append($"cut={cut} parts={partCount.I} big={bigCount.I} dark={regionCount.I}");
                if (regionCount.I < 1) return false;

                HObject? rim = SelectRim(byGray, regionCount.I, gray, log, temps);
                if (rim == null) return false;
                temps.Add(rim);
                HOperatorSet.GetRegionPoints(rim, out HTuple rows, out HTuple cols);

                double[] ra = rows.ToDArr(), ca = cols.ToDArr();
                if (ra.Length < 1) return false;

                double maxRow = height.D - 1 - FrameMarginPx, maxCol = width.D - 1 - FrameMarginPx;
                double bestD2 = double.MaxValue, bestRow = 0, bestCol = 0;
                bool got = false;
                for (int k = 0; k < ra.Length; k++)
                {
                    if (ra[k] < FrameMarginPx || ca[k] < FrameMarginPx || ra[k] > maxRow || ca[k] > maxCol)
                        continue;
                    double dr = ra[k] - crossRow, dc = ca[k] - crossCol;
                    double d2 = dr * dr + dc * dc;
                    if (d2 < bestD2) { bestD2 = d2; bestRow = ra[k]; bestCol = ca[k]; got = true; }
                }
                if (!got) return false;

                point = new EdgePoint(bestRow, bestCol);
                HOperatorSet.GenContourRegionXld(rim, out HObject chosen, "center");
                contour = chosen;   // the chuck-side boundary the point lies on; caller disposes
                return true;
            }
            catch (HOperatorException)
            {
                contour?.Dispose();
                contour = null;
                return false;
            }
            finally
            {
                LastReport = log.ToString();
                foreach (HObject t in temps) t.Dispose();
            }
        }

        #endregion

        #region Region selection

        /// <summary>The rim ring: per candidate region, the part of its boundary facing the chuck.
        /// Regions failing the flank test contribute nothing, so one bad region cannot pull the reported
        /// point off a good one. Null if none qualifies. Intermediates go on the caller's list; the
        /// result is a fresh handle for the caller to add.</summary>
        private HObject? SelectRim(HObject regions, int count, HObject gray, StringBuilder log, List<HObject> temps)
        {
            HObject? rim = null;
            for (int i = 1; i <= count; i++)
            {
                HOperatorSet.SelectObj(regions, out HObject one, i); temps.Add(one);
                HOperatorSet.AreaCenter(one, out HTuple oneArea, out _, out _);
                HOperatorSet.Intensity(one, gray, out HTuple oneMean, out _);
                log.Append($" | r{i} a={oneArea.D:F0} m={oneMean.D:F1}");

                HObject? side = SelectChuckSide(one, gray, log, temps);
                if (side == null) continue;
                temps.Add(side);

                HOperatorSet.Boundary(one, out HObject border, "inner"); temps.Add(border);
                HOperatorSet.Intersection(border, side, out HObject part); temps.Add(part);
                if (rim == null) { rim = part; continue; }
                HOperatorSet.Union2(rim, part, out HObject merged); temps.Add(merged);
                rim = merged;
            }
            if (rim == null) return null;
            HOperatorSet.CopyObj(rim, out HObject result, 1, -1);
            return result;
        }

        /// <summary>Which side of this region faces the chuck: the DARKER of its two flanks, the other
        /// carrying the bevel's specular glint. Returned grown just far enough to overlap the region's
        /// boundary ring, or null if the region is not a rim gap at all.</summary>
        private HObject? SelectChuckSide(HObject gap, HObject gray, StringBuilder log, List<HObject> temps)
        {
            HOperatorSet.DilationCircle(gap, out HObject grown, SideProbeRadius); temps.Add(grown);
            HOperatorSet.Difference(grown, gap, out HObject collar); temps.Add(collar);
            HOperatorSet.Connection(collar, out HObject pieces); temps.Add(pieces);
            HOperatorSet.SelectShape(pieces, out HObject sides, "area", "and", MinCollarAreaPx, MaxRegionArea);
            temps.Add(sides);
            HOperatorSet.CountObj(sides, out HTuple sideCount);
            // Fewer than two sides in frame means the wafer side is out of view, so there is no
            // evidence which boundary faces the chuck — refuse rather than guess. A refused sample
            // costs one search hop; the wrong side biases the fit by the gap's width, uncaught.
            if (sideCount.I < 2) { log.Append(" DROP(1 flank)"); return null; }

            // The two LARGEST pieces are the two sides; the rest is fragmentation along a ragged
            // outline. Comparing exactly two needs no threshold — and a threshold against the
            // brightest piece would admit a bevel-side sliver, straddling the rim ring across the gap.
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
            log.Append($" flanks={lo:F1}/{hi:F1} c={(hi > 0 ? lo / hi : 1):F2}");
            if (hi <= 0 || lo / hi > MaxSideContrast) { log.Append(" DROP(trough)"); return null; }
            log.Append(" KEEP");

            HOperatorSet.SelectObj(two, out HObject chuck, avg[0] <= avg[1] ? 1 : 2); temps.Add(chuck);

            HOperatorSet.DilationCircle(chuck, out HObject reach, SideGrowRadiusPx);
            return reach;
        }

        #endregion

        #region Helpers

        /// <summary>Otsu's threshold over histogram bins [lo, hi]. Matches HALCON's "max_separability"
        /// over the full range; doing it here is what allows restricting the range.</summary>
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
                // Normalised by total: same argmax, but the raw product runs to ~4e17.
                double between = (weightLow / total) * (weightHigh / total) * (meanLow - meanHigh) * (meanLow - meanHigh);
                if (between > best) { best = between; threshold = i; }
            }
            return threshold;
        }

        /// <summary>Independent single-channel byte image; red channel for the red-lit scene. The input
        /// frame is never modified.</summary>
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

        #endregion
    }
}
