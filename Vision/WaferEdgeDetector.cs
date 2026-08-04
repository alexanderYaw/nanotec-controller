using System;
using System.Collections.Generic;
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
    /// filtered twice: by AREA, and by MEAN GREY relative to the cut. Both are needed. The chuck is
    /// machined, and under oblique light its shadow troughs segment into long dark-ish blobs of
    /// 0.7-1.5 Mpx - as large as a real gap, so area alone passes them; and they read mean 47-63
    /// against 10-34 for a real gap, so the mean is what tells them apart. The mean is taken as a
    /// FRACTION of the cut rather than absolutely, because the cut is relative by design and runs
    /// 37-96 across the captures on file.
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

        /// <summary>Ignore regions smaller than this (px²) — drops dark die structures and shadows on
        /// the wafer itself, and the smaller shadow troughs of the machined chuck surface, which are
        /// never the rim.</summary>
        public double MinArea { get; set; } = 2e5;

        /// <summary>A region is only the off-wafer gap if it is genuinely BLACK, not merely below the
        /// cut. Regions whose mean grey exceeds this fraction of the stage-2 cut are rejected. See
        /// the class remarks for why area alone cannot do this.</summary>
        public double MaxMeanFraction { get; set; } = 0.6;

        // Boundary points this close to the frame edge are not the rim, they are the frame. A frame
        // wholly off the wafer is all "off-wafer", and its only border is this one.
        private const double FrameMarginPx = 2;

        /// <summary>A point on the wafer edge, in image pixels (HALCON row/column).</summary>
        public readonly record struct EdgePoint(double Row, double Column);

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
                HOperatorSet.Connection(closed, out HObject conn); temps.Add(conn);
                HOperatorSet.SelectShape(conn, out HObject byArea, "area", "and", MinArea, 1e9); temps.Add(byArea);

                // Keep only the regions that are actually black, not merely below the cut. Applied as
                // a FILTER rather than a verdict, so a real gap still wins when a brighter blob happens
                // to sit nearer the crosshair.
                double loMean = 0, hiMean = MaxMeanFraction * cut;
                if (!WaferIsBrighter) { loMean = 255 - MaxMeanFraction * (255 - cut); hiMean = 255; }
                HOperatorSet.SelectGray(byArea, gray, out HObject byGray, "mean", "and", loMean, hiMean);
                temps.Add(byGray);

                HOperatorSet.CountObj(byGray, out HTuple regionCount);
                if (regionCount.I < 1) return false;

                HOperatorSet.GenContourRegionXld(byGray, out HObject boundary, "border"); temps.Add(boundary);
                HOperatorSet.CountObj(boundary, out HTuple number);
                if (number.I < 1) return false;

                double maxRow = height.D - 1 - FrameMarginPx, maxCol = width.D - 1 - FrameMarginPx;
                double bestD2 = double.MaxValue, bestRow = 0, bestCol = 0;
                int bestIdx = -1;
                for (int i = 1; i <= number.I; i++)
                {
                    HOperatorSet.SelectObj(boundary, out HObject one, i);
                    try
                    {
                        HOperatorSet.GetContourXld(one, out HTuple rows, out HTuple cols);
                        double[] ra = rows.ToDArr(), ca = cols.ToDArr();
                        for (int k = 0; k < ra.Length; k++)
                        {
                            if (ra[k] < FrameMarginPx || ca[k] < FrameMarginPx || ra[k] > maxRow || ca[k] > maxCol)
                                continue;
                            double dr = ra[k] - crossRow, dc = ca[k] - crossCol;
                            double d2 = dr * dr + dc * dc;
                            if (d2 < bestD2) { bestD2 = d2; bestRow = ra[k]; bestCol = ca[k]; bestIdx = i; }
                        }
                    }
                    finally { one.Dispose(); }
                }
                if (bestIdx < 0) return false;

                point = new EdgePoint(bestRow, bestCol);
                HOperatorSet.SelectObj(boundary, out HObject chosen, bestIdx);
                contour = chosen;   // the boundary the nearest point lies on; caller disposes
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
                foreach (HObject t in temps) t.Dispose();
            }
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
