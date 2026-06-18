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
    /// The detection logic (auto_threshold → … → edges_sub_pix → … → area_center_points_xld)
    /// is UNCHANGED. The thresholds below are the originals, tuned to the sample images'
    /// resolution/magnification — they are exposed as properties because they will most
    /// likely need retuning for the live full-res camera.
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

        /// <summary>A sub-pixel point on the wafer edge, in image pixels (HALCON row/column).</summary>
        public readonly record struct EdgePoint(double Row, double Column);

        /// <summary>
        /// Runs detection and disposes the contour internally. Use this when you only need
        /// the point (e.g. calibration / centre-find), not an overlay.
        /// </summary>
        public bool TryDetect(HObject image, out EdgePoint point)
        {
            bool ok = TryDetect(image, out point, out HObject? contour);
            contour?.Dispose();
            return ok;
        }

        /// <summary>
        /// Detects the wafer-edge point in <paramref name="image"/>. On success also returns
        /// the selected edge <paramref name="contour"/> (XLD) for overlay — the CALLER OWNS it
        /// and must Dispose it. Returns false (point default, contour null) if no edge is found
        /// or a HALCON op fails; the input frame is never modified.
        /// </summary>
        public bool TryDetect(HObject image, out EdgePoint point, out HObject? contour)
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
                HOperatorSet.SelectContoursXld(split, out HObject longContours, "contour_length", 2e3, 1e7, -0.5, 0.5); temps.Add(longContours);
                HOperatorSet.SortContoursXld(longContours, out HObject sorted, "character", "true", "row"); temps.Add(sorted);

                HOperatorSet.CountObj(sorted, out HTuple number);
                if (number.I < 1) return false;

                // Same selection as the source: the last contour after sorting by row
                // (bottom-most). NOTE: revisit this if live frames yield stray contours.
                HOperatorSet.SelectObj(sorted, out HObject chosen, number);
                HOperatorSet.AreaCenterPointsXld(chosen, out HTuple _, out HTuple row, out HTuple col);
                if (row.Length < 1) { chosen.Dispose(); return false; }

                point = new EdgePoint(row.D, col.D);
                contour = chosen;   // handed to caller; deliberately NOT added to temps
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
