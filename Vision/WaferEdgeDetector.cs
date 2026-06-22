using System;
using System.Collections.Generic;
using HalconDotNet;

namespace MotorControlApp
{
    /// <summary>
    /// Stage B: locates a single sub-pixel point on the wafer edge in ONE camera frame.
    ///
    /// This is a direct port of the op-chain in <c>Halcon/wafer center.cs</c> (its
    /// <c>action()</c> body), lifted out of the HDevelop harness that wrapped it:
    ///  - reads a LIVE <see cref="HObject"/> frame instead of looping over image files;
    ///  - runs headlessly — no <c>open_window</c>/<c>disp_obj</c> (HALCON's HWindow can't
    ///    load in this net10 app, see [[toolchain]]); the caller overlays the returned
    ///    contour on its own PictureBox if it wants to show the edge;
    ///  - returns the result instead of accumulating it into tuples.
    ///
    /// The region/edge pipeline (auto_threshold → … → edges_sub_pix → select_contours) is the
    /// original; the final stage was changed from area_center_points_xld to "the edge point
    /// NEAREST a reference pixel" (the rim point by the crosshair), which is a true point on the
    /// rim and is what the centre-find needs. The thresholds are the sample-image originals and
    /// WILL need retuning for the live chuck-edge arc (tune in HDevelop, like the mark).
    ///
    /// Pass the FULL-RESOLUTION frame (not the downscaled live-view bitmap) for accuracy.
    /// </summary>
    public sealed class WaferEdgeDetector
    {
        // --- Tunables (defaults = the constants from wafer center.cs) ------------------
        public double AreaMin { get; set; } = 1e5;
        public double AreaMax { get; set; } = 1e7;
        public double InnerRadiusMin { get; set; } = 95;
        public double InnerRadiusMax { get; set; } = 150;
        public double GrayMeanMin { get; set; } = 0;
        public double GrayMeanMax { get; set; } = 90;
        public double ClosingRadius { get; set; } = 35;
        public double DilationRadius { get; set; } = 55;
        public double CannyAlpha { get; set; } = 10;   // edges_sub_pix smoothing
        public double CannyLow { get; set; } = 20;
        public double CannyHigh { get; set; } = 40;
        public double MinContourLength { get; set; } = 2e3;   // drop short/noise edge fragments

        /// <summary>A sub-pixel point on the wafer edge, in image pixels (HALCON row/column).</summary>
        public readonly record struct EdgePoint(double Row, double Column);

        /// <summary>
        /// Runs detection and disposes the contour internally. Use this when you only need
        /// the point (e.g. centre-find), not an overlay. <paramref name="crossRow"/>/<paramref
        /// name="crossCol"/> are the reference (crosshair) pixel; the returned point is the
        /// detected edge point NEAREST that reference.
        /// </summary>
        public bool TryDetect(HObject image, double crossRow, double crossCol, out EdgePoint point)
        {
            bool ok = TryDetect(image, crossRow, crossCol, out point, out HObject? contour);
            contour?.Dispose();
            return ok;
        }

        /// <summary>
        /// Detects the wafer-edge point nearest the reference pixel (<paramref name="crossRow"/>,
        /// <paramref name="crossCol"/>) — i.e. the rim point closest to the crosshair, which is a
        /// genuine point ON the rim (unlike the arc centroid, which sits inside it). On success
        /// also returns the edge <paramref name="contour"/> it lies on (XLD) for overlay — the
        /// CALLER OWNS it and must Dispose it. Returns false if no edge is found or a HALCON op
        /// fails; the input frame is never modified.
        /// </summary>
        public bool TryDetect(HObject image, double crossRow, double crossCol, out EdgePoint point, out HObject? contour)
        {
            point = default;
            contour = null;
            var temps = new List<HObject>();   // every intermediate; disposed in finally
            try
            {
                HObject gray = Preprocess(image); temps.Add(gray);

                HOperatorSet.AutoThreshold(gray, out HObject regions, 5); temps.Add(regions);
                HOperatorSet.FillUp(regions, out HObject filled); temps.Add(filled);
                HOperatorSet.Connection(filled, out HObject connected); temps.Add(connected);
                HOperatorSet.SelectShape(connected, out HObject byArea, "area", "and", AreaMin, AreaMax); temps.Add(byArea);
                HOperatorSet.SelectShape(byArea, out HObject byRadius, "inner_radius", "and", InnerRadiusMin, InnerRadiusMax); temps.Add(byRadius);
                HOperatorSet.SelectGray(byRadius, gray, out HObject byGray, "mean", "and", GrayMeanMin, GrayMeanMax); temps.Add(byGray);

                // Nothing survived the wafer-region selection → no edge to look for.
                HOperatorSet.CountObj(byGray, out HTuple regionCount);
                if (regionCount.I < 1) return false;

                HOperatorSet.ClosingCircle(byGray, out HObject closed, ClosingRadius); temps.Add(closed);
                HOperatorSet.DilationCircle(closed, out HObject dilated, DilationRadius); temps.Add(dilated);
                HOperatorSet.ReduceDomain(gray, dilated, out HObject reduced); temps.Add(reduced);
                HOperatorSet.EdgesSubPix(reduced, out HObject edges, "canny", CannyAlpha, CannyLow, CannyHigh); temps.Add(edges);
                HOperatorSet.SegmentContoursXld(edges, out HObject split, "lines", 5, 100, 2); temps.Add(split);
                HOperatorSet.SelectContoursXld(split, out HObject longContours, "contour_length", MinContourLength, 1e7, -0.5, 0.5); temps.Add(longContours);

                HOperatorSet.CountObj(longContours, out HTuple number);
                if (number.I < 1) return false;

                // Find the sub-pixel edge point NEAREST the crosshair, scanning every surviving
                // contour. That point lies on the rim (the rim's closest approach to the
                // reference), which is what the centre-find needs — not the arc centroid.
                double bestD2 = double.MaxValue, bestRow = 0, bestCol = 0;
                int bestIdx = -1;
                for (int i = 1; i <= number.I; i++)
                {
                    HOperatorSet.SelectObj(longContours, out HObject one, i);
                    try
                    {
                        HOperatorSet.GetContourXld(one, out HTuple rows, out HTuple cols);
                        double[] ra = rows.ToDArr(), ca = cols.ToDArr();
                        for (int k = 0; k < ra.Length; k++)
                        {
                            double dr = ra[k] - crossRow, dc = ca[k] - crossCol;
                            double d2 = dr * dr + dc * dc;
                            if (d2 < bestD2) { bestD2 = d2; bestRow = ra[k]; bestCol = ca[k]; bestIdx = i; }
                        }
                    }
                    finally { one.Dispose(); }
                }
                if (bestIdx < 0) return false;

                point = new EdgePoint(bestRow, bestCol);
                HOperatorSet.SelectObj(longContours, out HObject chosen, bestIdx);
                contour = chosen;   // the contour the nearest point lies on; caller disposes
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

        // Returns an independent single-channel byte image. Colour frames are reduced to gray;
        // any pixel type is normalised to byte so the byte-range thresholds (gray 0..90, canny
        // 10/20/40) mean what they did on the sample images. The input frame is not modified.
        private static HObject Preprocess(HObject image)
        {
            HOperatorSet.CountChannels(image, out HTuple channels);
            if (channels.I >= 3)
            {
                HOperatorSet.Rgb1ToGray(image, out HObject gray);
                try { HOperatorSet.ConvertImageType(gray, out HObject gray8, "byte"); return gray8; }
                finally { gray.Dispose(); }
            }
            HOperatorSet.ConvertImageType(image, out HObject byteImg, "byte");
            return byteImg;
        }
    }
}
