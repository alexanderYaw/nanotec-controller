using System;
using System.Collections.Generic;
using System.Text;
using HalconDotNet;

namespace NanotecController
{
    /// <summary>
    /// Locates the wafer EDGE point nearest a reference pixel by segmenting the OFF-WAFER side —
    /// the region beyond the rim, which is unlit and reads near-black. The rim is the boundary of
    /// that region nearest the crosshair.
    ///
    /// Segmenting the bright side instead does not work: the bevel is a mid-grey band a few
    /// hundred pixels wide that can read either side of a single global cut, and when it reads
    /// dark it splits the wafer into two blobs whose shared boundary is nearer the crosshair than
    /// the rim is. Cutting on the dark side sidesteps that — the bevel is never black, only the
    /// world beyond the wafer is. See the Developer Guide, "Wafer Centre-Finding by Rotation".
    ///
    /// The cut is two-stage Otsu: the first split isolates the lit wafer, the second is taken
    /// inside the darker part alone, which is what separates the sustained black beyond the rim
    /// from mid-grey bevel and background texture. Both stages track exposure.
    ///
    /// Otsu always returns a cut, even on a frame with no rim in it, so the surviving regions are
    /// filtered three times: by AREA, by MEAN GREY relative to the cut, and by FLANK CONTRAST. All
    /// three are needed. The chuck is machined, and under oblique light its shadow troughs segment
    /// into long dark-ish blobs of 0.7-1.5 Mpx - as large as a real gap, so area alone passes them;
    /// they read mean 47-63 against 10-34 for a real gap, which catches most of them. The mean is
    /// taken as a FRACTION of the cut rather than absolutely, because the cut is relative by design
    /// and runs 37-96 across the captures on file.
    ///
    /// FLANK CONTRAST is what catches the rest, and it is the test that matters when the crosshair
    /// sits over the chuck rather than the wafer. A rim gap has the bevel's glint on one flank and
    /// the chuck on the other; a trough has chuck on BOTH, so its two collar means are nearly equal.
    /// Measured over the captures on file, darker-over-brighter runs 0.46-0.73 for rim gaps against
    /// 0.89-0.99 for troughs. Nothing else separates them once a trough is near the crosshair: the
    /// reported point is the boundary point NEAREST the crosshair, so with the rim far away a trough
    /// wins outright, and the scan's radius band cannot object because a trough beside the crosshair
    /// sits at very nearly the station's own radius.
    ///
    /// The three filters are applied PER REGION, and the rim ring is assembled from the regions that
    /// pass. Merging first and testing the merger would let one trough contribute boundary that then
    /// wins the nearest-point search.
    ///
    /// The gap has TWO boundaries and only one of them is wanted. Radially the scene runs
    /// wafer - bevel - gap - chuck, and it is the GAP-TO-CHUCK boundary that is measured, not the
    /// gap-to-bevel one: the chuck surface is in focus, so that boundary is the sharper and more
    /// repeatable of the two. The side is chosen by BRIGHTNESS - a collar is taken either side of
    /// the gap and the DARKER one is the chuck. The bevel is specular and throws a near-saturated
    /// glint that hugs the gap, while the chuck is diffuse and mid-grey; measured over the captures
    /// on file the chuck collar runs 0.4-0.8x the wafer collar's mean, at every collar width.
    ///
    /// The comparison is between the TWO LARGEST collar pieces only, and it is a comparison rather
    /// than a threshold. A ragged gap outline breaks the collar into six to thirteen fragments, and
    /// a bevel-side sliver reads darker than the bevel proper, so any cut-off against the brightest
    /// piece admits fragments from both sides at once and the rim ring ends up straddling the gap.
    /// Two candidates need no cut-off. A frame showing only ONE side is refused: the wafer is out of
    /// view, so nothing in the frame says which boundary faces the chuck.
    ///
    /// Texture cannot make this call, counter-intuitive though that is: the glint is a saturated
    /// ridge speckled with dark pits, so the WAFER collar is the rougher of the two on both grey
    /// deviation and local gradient - the opposite of what the eye reports. Note also that this
    /// polarity belongs to the current optics; captures from before the chuck was brought into
    /// focus show it reversed. See the Developer Guide.
    ///
    /// Contrast <see cref="ChuckEdgeDetector"/>, which cuts on brightness with a FIXED threshold
    /// because its two grey levels are set by the illumination and the material rather than by
    /// exposure.
    ///
    /// Pass the FULL-RESOLUTION frame (not the downscaled live-view bitmap) for accuracy.
    /// </summary>
    public sealed class WaferEdgeDetector
    {
        /// <summary>True when the wafer reads BRIGHTER than the off-wafer background (the usual case
        /// here); set false if your lighting makes the wafer the darker side.</summary>
        public bool WaferIsBrighter { get; set; } = true;

        /// <summary>Opening radius to erase speckle in the off-wafer region before selecting the blob.</summary>
        public double CleanRadius { get; set; } = 7;

        /// <summary>Closing radius to bridge bright specks (dust, debris) inside the off-wafer region
        /// so it forms ONE solid blob whose border is the clean rim.</summary>
        public double CloseRadius { get; set; } = 21;

        /// <summary>Opening radius applied AFTER the closing, to sever dark structures narrower than
        /// twice this from the band. The chuck's machined gashes read below the cut and the closing
        /// bridges the near ones into the gap, growing tendrils that reach hundreds of pixels across
        /// the chuck; they are the rim's boundary as far as everything downstream is concerned. The
        /// band is ~345 px wide and the tendrils a few tens, so this cuts cleanly between them.</summary>
        public double SeverRadius { get; set; } = 35;

        /// <summary>Ignore regions smaller than this (px²) — drops dark die structures and shadows on
        /// the wafer itself, and the smaller shadow troughs of the machined chuck surface, which are
        /// never the rim.</summary>
        public double MinArea { get; set; } = 2e5;

        /// <summary>A region is only the off-wafer gap if it is genuinely BLACK, not merely below the
        /// cut. Regions whose mean grey exceeds this fraction of the stage-2 cut are rejected. See
        /// the class remarks for why area alone cannot do this.</summary>
        public double MaxMeanFraction { get; set; } = 0.6;

        /// <summary>Width (px) of the collar sampled either side of the gap to decide which side faces
        /// the chuck. Wide enough to average over the chuck's texture, narrow enough that the wafer-side
        /// collar stays on the bevel glint rather than reaching the darker wafer surface beyond it.</summary>
        public double SideProbeRadius { get; set; } = 50;

        /// <summary>A dark region is only the rim gap if its two flanks differ in brightness by at least
        /// this much — darker collar mean over brighter must be at or below it. See the class remarks:
        /// this is what tells the rim from a shadow trough in the machined chuck.</summary>
        public double MaxSideContrast { get; set; } = 0.80;

        // Boundary points this close to the frame edge are not the rim, they are the frame. A frame
        // wholly off the wafer is all "off-wafer", and its only border is this one.
        private const double FrameMarginPx = 2;

        // Collar fragments smaller than this (px²) are discretisation slivers, not a side of the gap.
        private const double MinCollarAreaPx = 5000;

        // The collar starts one pixel clear of the gap; this grows the chosen side back far enough to
        // overlap the gap's own boundary ring. Deliberately tiny - a large value would reach across a
        // narrow gap and re-admit the far side.
        private const double SideGrowRadiusPx = 2;

        /// <summary>A point on the wafer edge, in image pixels (HALCON row/column).</summary>
        public readonly record struct EdgePoint(double Row, double Column);

        /// <summary>What the last <see cref="TryDetect"/> saw, in the same terms as steps 4-5 of
        /// `Halcon/wafer center.hdev`: the cut, and per candidate region its area, mean, and flank
        /// means with the verdict. This is the diagnostic to read when a live scan reports a point off
        /// the rim — it says WHICH filter let the wrong region through. Overwritten every call, so it
        /// belongs to one detection at a time (the grab thread runs them serially).</summary>
        public string LastReport { get; private set; } = "";

        /// <summary>Detects and disposes the contour internally. Returns the edge point nearest the crosshair.</summary>
        public bool TryDetect(HObject image, double crossRow, double crossCol, out EdgePoint point)
        {
            bool ok = TryDetect(image, crossRow, crossCol, out point, out HObject? contour);
            contour?.Dispose();
            return ok;
        }

        /// <summary>
        /// Detects the wafer-edge point nearest (<paramref name="crossRow"/>, <paramref name="crossCol"/>).
        /// On success also returns the boundary <paramref name="contour"/> the point lies on (XLD) for
        /// overlay — CALLER OWNS it and must Dispose it. Returns false if nothing is found or a HALCON op
        /// fails; the input frame is never modified.
        /// </summary>
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

                // Clean speckle, then close specks inside the off-wafer region so it's one solid blob.
                // Deliberately NOT filled: a hole would only ever be debris beyond the rim.
                HOperatorSet.OpeningCircle(off, out HObject opened, CleanRadius); temps.Add(opened);
                HOperatorSet.ClosingCircle(opened, out HObject closed, CloseRadius); temps.Add(closed);
                // Then sever the chuck's gashes back off. This has to come AFTER the closing, not
                // instead of a larger CleanRadius: opening that wide first would leave dust specks too
                // big for the closing to fill, and it lets bare-chuck frames segment into something
                // that survives the area gate.
                HOperatorSet.OpeningCircle(closed, out HObject severed, SeverRadius); temps.Add(severed);
                HOperatorSet.Connection(severed, out HObject conn); temps.Add(conn);
                HOperatorSet.SelectShape(conn, out HObject byArea, "area", "and", MinArea, 1e9); temps.Add(byArea);

                // Keep only the regions that are actually black, not merely below the cut. Applied as
                // a FILTER rather than a verdict, so a real gap still wins when a brighter blob happens
                // to sit nearer the crosshair.
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

        // The rim ring: per candidate region, the part of its boundary that faces the chuck. Regions
        // that fail the flank test contribute nothing, so one bad region cannot pull the reported point
        // off a good one. Returns null if none qualifies. Intermediates go on the caller's list; the
        // result is a fresh handle for the caller to add.
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

        // Which side of this region faces the chuck: the DARKER of its two flanks, the other carrying
        // the bevel's specular glint. Returns it grown just far enough to overlap the region's boundary
        // ring, or null if the region is not a rim gap at all. Intermediates go on the caller's list.
        private HObject? SelectChuckSide(HObject gap, HObject gray, StringBuilder log, List<HObject> temps)
        {
            HOperatorSet.DilationCircle(gap, out HObject grown, SideProbeRadius); temps.Add(grown);
            HOperatorSet.Difference(grown, gap, out HObject collar); temps.Add(collar);
            HOperatorSet.Connection(collar, out HObject pieces); temps.Add(pieces);
            HOperatorSet.SelectShape(pieces, out HObject sides, "area", "and", MinCollarAreaPx, 1e9);
            temps.Add(sides);
            HOperatorSet.CountObj(sides, out HTuple sideCount);
            // The gap has exactly two sides. Fewer in frame means the wafer side is out of view, and
            // then there is no evidence at all about which boundary faces the chuck — refuse rather
            // than guess. A refused sample costs the scan one search hop; a boundary picked off the
            // wrong side biases the fit by the gap's width and nothing downstream would catch it.
            if (sideCount.I < 2) { log.Append(" DROP(1 flank)"); return null; }

            // The two LARGEST pieces are the two sides; anything else is fragmentation along a ragged
            // outline. Deciding between exactly two needs no threshold, and it keeps slivers — whose
            // grey means nothing — out of both the comparison and the result. A threshold against the
            // brightest piece cannot do this: a bevel-side sliver reads darker than the bevel proper
            // and is admitted, putting boundary from BOTH sides into the rim ring.
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

            // The two flanks must differ. A rim gap has the bevel's glint on one and the chuck on the
            // other; a shadow trough in the machined chuck has chuck on BOTH, and reads near 1. This is
            // what makes a frame whose crosshair sits over the chuck report the rim instead of the
            // nearest trough — the area and mean filters cannot, because a trough runs to 1.5 Mpx and
            // the scan's radius band cannot either, a trough by the crosshair sitting at very nearly
            // the station's own radius. Measured over the captures on file: rim gaps 0.46-0.73,
            // troughs 0.89-0.99.
            double lo = Math.Min(avg[0], avg[1]), hi = Math.Max(avg[0], avg[1]);
            log.Append($" flanks={lo:F1}/{hi:F1} c={(hi > 0 ? lo / hi : 1):F2}");
            if (hi <= 0 || lo / hi > MaxSideContrast) { log.Append(" DROP(trough)"); return null; }
            log.Append(" KEEP");

            HOperatorSet.SelectObj(two, out HObject chuck, avg[0] <= avg[1] ? 1 : 2); temps.Add(chuck);

            HOperatorSet.DilationCircle(chuck, out HObject reach, SideGrowRadiusPx);
            return reach;
        }

        // Otsu's threshold over histogram bins [lo, hi]. Matches HALCON's "max_separability" when
        // run over the full range; the point of doing it here is being able to restrict the range.
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
                // Weights normalised by total: same argmax, but the raw product runs to ~4e17 and
                // there is no reason to work that close to a double's precision.
                double between = (weightLow / total) * (weightHigh / total) * (meanLow - meanHigh) * (meanLow - meanHigh);
                if (between > best) { best = between; threshold = i; }
            }
            return threshold;
        }

        // Independent single-channel byte image; red channel for the red-lit scene (matches
        // ChuckEdgeDetector). The input frame is never modified.
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
