using System;
using System.Collections.Generic;
using HalconDotNet;

namespace NanotecController
{
    /// <summary>
    /// Locates the wafer EDGE point nearest a reference pixel by BRIGHTNESS. In the live scene
    /// (see Desktop/images/wafer_edge.png) the lit wafer reads clearly BRIGHTER than the
    /// off-wafer background, with the bevel a bright diagonal — so the rim is the boundary of the
    /// bright region. Threshold the bright side, keep its largest connected blob (filling the
    /// dies/droplets/texture inside it), and return the boundary point NEAREST the crosshair — a
    /// true point on the rim, which the centre-find needs.
    ///
    /// Same shape as <see cref="ChuckEdgeDetector"/>, which separates the two sides by FOCUS
    /// instead; use that one when the two sides are equally bright but differ in sharpness.
    /// Thresholding is auto-adaptive (max_separability) so it tracks exposure; the morphology
    /// radii / min-area are the levers that may need light tuning for the live arc.
    ///
    /// Pass the FULL-RESOLUTION frame (not the downscaled live-view bitmap) for accuracy.
    /// </summary>
    public sealed class WaferEdgeDetector
    {
        /// <summary>True when the wafer reads BRIGHTER than the background (the usual case here);
        /// set false if your lighting makes the wafer the darker side.</summary>
        public bool WaferIsBrighter { get; set; } = true;

        /// <summary>Opening radius to erase background speckle / fine texture before selecting the blob.</summary>
        public double CleanRadius { get; set; } = 7;

        /// <summary>Closing radius to bridge gaps inside the wafer region (dies, droplets, the bevel
        /// reading a different brightness) so it forms ONE solid blob whose border is the clean rim.</summary>
        public double CloseRadius { get; set; } = 21;

        /// <summary>Ignore regions smaller than this (px²) — drops stray bright blobs that aren't the wafer.</summary>
        public double MinArea { get; set; } = 5e4;

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

                // Bright wafer vs dark background → auto-threshold the brighter (or darker) side.
                HOperatorSet.BinaryThreshold(gray, out HObject wafer, "max_separability",
                    WaferIsBrighter ? "light" : "dark", out HTuple _); temps.Add(wafer);

                // Clean speckle, then close gaps inside the wafer so it's one solid blob, then fill.
                HOperatorSet.OpeningCircle(wafer, out HObject opened, CleanRadius); temps.Add(opened);
                HOperatorSet.ClosingCircle(opened, out HObject closed, CloseRadius); temps.Add(closed);
                HOperatorSet.FillUp(closed, out HObject filled); temps.Add(filled);
                HOperatorSet.Connection(filled, out HObject conn); temps.Add(conn);
                HOperatorSet.SelectShape(conn, out HObject byArea, "area", "and", MinArea, 1e9); temps.Add(byArea);

                HOperatorSet.CountObj(byArea, out HTuple regionCount);
                if (regionCount.I < 1) return false;
                HOperatorSet.SelectShapeStd(byArea, out HObject biggest, "max_area", 0); temps.Add(biggest);

                // Boundary of the wafer region = the rim (+ any frame-border segments, which are
                // far from the crosshair and so are never the nearest point).
                HOperatorSet.GenContourRegionXld(biggest, out HObject boundary, "border"); temps.Add(boundary);
                HOperatorSet.CountObj(boundary, out HTuple number);
                if (number.I < 1) return false;

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
