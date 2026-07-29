using System;
using System.Collections.Generic;
using HalconDotNet;

namespace NanotecController
{
    /// <summary>
    /// Locates the chuck EDGE point nearest a reference pixel, using a FOCUS/TEXTURE-RIDGE approach
    /// (verified in Halcon/chuck edge detector.hdev on Edge_sample*.png).
    ///
    /// Across the rim there are three zones: the in-focus, sharply-textured chuck face; a thin in-focus
    /// DARK BAND; and beyond it the out-of-focus (blurry) background. The true edge is the boundary
    /// between the in-focus and out-of-focus sides. Neither brightness (both sides read similar) nor
    /// coarse focus-energy (which smears the thin band into the blur) pins it cleanly — but in a
    /// FINE-scale sharpness map (gradient magnitude pooled over a SMALL window) that boundary shows up
    /// as a thin, continuous BRIGHT RIDGE: sharp on the chuck side, dark on the blurry side.
    ///
    /// So: build the fine sharpness map, extract bright curvilinear ridges with lines_gauss (sub-pixel),
    /// keep only long contours (drops the short texture responses), and return the ridge point NEAREST
    /// the crosshair — a true sub-pixel point on the rim, which the centre-find needs.
    ///
    /// Pass the FULL-RESOLUTION frame. Tunables match the .hdev script.
    /// </summary>
    public sealed class ChuckEdgeDetector
    {
        public int SobelWidth { get; set; } = 3;          // gradient filter size (odd: 3,5,7)
        public int FineWindow { get; set; } = 9;          // small pooling window for the fine sharpness map
        public double LineSigma { get; set; } = 1.5;      // lines_gauss scale ≈ ridge half-width
        public double LineLow { get; set; } = 0.5;        // lines_gauss lower hysteresis on line response
        public double LineHigh { get; set; } = 1.5;       // lines_gauss upper hysteresis on line response
        public double MinLineLength { get; set; } = 500;  // drop ridge fragments shorter than this (px)

        /// <summary>A sub-pixel point on the chuck edge, in image pixels (HALCON row/column).</summary>
        public readonly record struct EdgePoint(double Row, double Column);

        /// <summary>Detects and disposes the contour internally. Returns the edge point nearest the crosshair.</summary>
        public bool TryDetect(HObject image, double crossRow, double crossCol, out EdgePoint point)
        {
            bool ok = TryDetect(image, crossRow, crossCol, out point, out HObject? contour);
            contour?.Dispose();
            return ok;
        }

        /// <summary>
        /// Detects the chuck-edge point nearest (<paramref name="crossRow"/>, <paramref name="crossCol"/>).
        /// On success also returns the ridge <paramref name="contour"/> the point lies on (XLD) for overlay
        /// — CALLER OWNS it and must Dispose it. Returns false if nothing is found or a HALCON op fails;
        /// the input frame is never modified.
        /// </summary>
        public bool TryDetect(HObject image, double crossRow, double crossCol, out EdgePoint point, out HObject? contour)
        {
            point = default;
            contour = null;
            var temps = new List<HObject>();
            try
            {
                HObject gray = Preprocess(image); temps.Add(gray);

                // Fine sharpness map: gradient magnitude pooled over a SMALL window → the in-focus/
                // out-of-focus boundary becomes a thin bright ridge (coarse pooling would smear it away).
                HOperatorSet.SobelAmp(gray, out HObject amp, "sum_abs", SobelWidth); temps.Add(amp);
                HOperatorSet.MeanImage(amp, out HObject fineSharp, FineWindow, FineWindow); temps.Add(fineSharp);

                // Extract bright curvilinear ridges (sub-pixel), then keep only long ones so the short
                // texture responses below the ridge are dropped. What survives is the chuck edge.
                HOperatorSet.LinesGauss(fineSharp, out HObject ridgeLines, LineSigma, LineLow, LineHigh,
                    "light", "true", "bar-shaped", "true"); temps.Add(ridgeLines);
                HOperatorSet.SelectContoursXld(ridgeLines, out HObject ridge, "contour_length",
                    MinLineLength, 1e9, -0.5, 0.5); temps.Add(ridge);

                HOperatorSet.CountObj(ridge, out HTuple number);
                if (number.I < 1) return false;

                double bestD2 = double.MaxValue, bestRow = 0, bestCol = 0;
                int bestIdx = -1;
                for (int i = 1; i <= number.I; i++)
                {
                    HOperatorSet.SelectObj(ridge, out HObject one, i);
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
                HOperatorSet.SelectObj(ridge, out HObject chosen, bestIdx);
                contour = chosen;   // the ridge the nearest point lies on; caller disposes
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

        // Independent single-channel byte image; red channel for colour frames (red-lit scene).
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
