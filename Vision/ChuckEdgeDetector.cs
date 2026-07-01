using System;
using System.Collections.Generic;
using HalconDotNet;

namespace NanotecController
{
    /// <summary>
    /// Locates the chuck EDGE point nearest a reference pixel, using a FOCUS/TEXTURE approach
    /// (verified in Halcon/chuck edge detector.hdev on Edge_sample*.png).
    ///
    /// The two sides of the chuck edge are nearly the same colour, so brightness can't separate
    /// them — but one side is in focus (sharp, high-frequency texture) and the other is blurry.
    /// So: build a "focus energy" map (gradient magnitude, smoothed) that is bright where the
    /// image is sharp; auto-threshold it to keep the in-focus side; the boundary of that region
    /// is the chuck edge. We return the boundary point NEAREST the crosshair — a true point on
    /// the rim, which the centre-find needs.
    ///
    /// Pass the FULL-RESOLUTION frame. Tunables match the .hdev script.
    /// </summary>
    public sealed class ChuckEdgeDetector
    {
        public int SobelWidth { get; set; } = 3;          // gradient filter size (odd: 3,5,7)
        public int EnergyWindow { get; set; } = 41;        // smoothing window for the focus map
        public double CleanRadius { get; set; } = 5;       // opening radius to remove specks
        public bool InFocusIsBright { get; set; } = true;  // set false if the blurry side reads brighter

        /// <summary>A sub-pixel-ish point on the chuck edge, in image pixels (HALCON row/column).</summary>
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
        /// On success also returns the boundary <paramref name="contour"/> the point lies on (XLD) for
        /// overlay — CALLER OWNS it and must Dispose it. Returns false if nothing is found or a HALCON
        /// op fails; the input frame is never modified.
        /// </summary>
        public bool TryDetect(HObject image, double crossRow, double crossCol, out EdgePoint point, out HObject? contour)
        {
            point = default;
            contour = null;
            var temps = new List<HObject>();
            try
            {
                HObject gray = Preprocess(image); temps.Add(gray);

                // Focus-energy map: gradient magnitude (high where sharp), smoothed over a window
                // → bright on the in-focus side, dark on the blurry side.
                HOperatorSet.SobelAmp(gray, out HObject amp, "sum_abs", SobelWidth); temps.Add(amp);
                HOperatorSet.MeanImage(amp, out HObject energy, EnergyWindow, EnergyWindow); temps.Add(energy);

                // Split the two sides by auto-thresholding the energy; keep the in-focus side.
                HOperatorSet.BinaryThreshold(energy, out HObject sharp, "max_separability",
                    InFocusIsBright ? "light" : "dark", out HTuple _); temps.Add(sharp);
                HOperatorSet.OpeningCircle(sharp, out HObject opened, CleanRadius); temps.Add(opened);
                HOperatorSet.FillUp(opened, out HObject filled); temps.Add(filled);
                HOperatorSet.Connection(filled, out HObject conn); temps.Add(conn);

                HOperatorSet.CountObj(conn, out HTuple regionCount);
                if (regionCount.I < 1) return false;
                HOperatorSet.SelectShapeStd(conn, out HObject biggest, "max_area", 0); temps.Add(biggest);

                // Region boundary = chuck edge (+ frame-border segments, which are far from the
                // crosshair and so are never the nearest point).
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
                contour = chosen;   // caller disposes
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
