using System;
using System.Collections.Generic;
using HalconDotNet;

namespace NanotecController
{
    /// <summary>
    /// Locates the chuck's INNER circular edge — the boundary between the brightly-lit machined chuck
    /// face and the near-black region beside it — and returns the point on it nearest a reference
    /// pixel. The inner circle is used rather than the outer rim because the rim is not a clean
    /// circle: two sections on OPPOSITE sides carry no usable edge, and the scan probes in opposite
    /// PAIRS, so one gap pair takes out both ends of a pair at once.
    ///
    /// The pipeline cuts on GREY LEVEL, not focus: a flat saturated area has zero gradient, so a
    /// focus map reads the middle of the bright face as blurry and segmenting on it punches holes
    /// through the face. The old focus-based outer-rim detector is preserved in
    /// Halcon/chuck edge detector.hdev — do not restore it here.
    ///
    /// Pass the FULL-RESOLUTION frame. Tunables mirror Halcon/innerCircleDetection.hdev, which
    /// carries the full parameter sweeps; tune there first, then copy across. TUNED ON ONE FRAME.
    /// See Developer Guide, ChuckCenterFindingAutomation.md.
    /// </summary>
    public sealed class ChuckEdgeDetector
    {
        #region Tunables

        /// <summary>&lt;= 1 disables smoothing, the default: the cut sits below the step's midpoint, so
        /// blurring walks the crossing outward into the dark, and it spreads the stray reflection blob
        /// until OpenRadius can no longer erase it.</summary>
        public int SmoothWindow { get; set; } = 1;

        /// <summary>Grey cut separating face from dark region; negative = Otsu. DELIBERATELY FIXED —
        /// these grey levels are set by the illumination and material and do not move with framing,
        /// whereas Otsu is a RELATIVE split that shifts as the stage scans. A fixed cut is also what
        /// makes the face-fraction gate a meaningful "is the boundary in view" test.</summary>
        public double BrightThreshold { get; set; } = 80;

        /// <summary>Bridges the dark pits biting into the lip of the face — THE parameter that
        /// matters. At 0 the region fragments into 7 pieces and the fit is meaningless. It also seals
        /// any genuine notch narrower than ~2x this, which is why this detector must not be pointed
        /// back at the gapped outer rim.</summary>
        public int CloseRadius { get; set; } = 105;

        /// <summary>Drops isolated specks stranded in the dark region. Nearly inert, since keeping the
        /// largest component already discards them; kept small as insurance against specks the one
        /// tuning frame does not show.</summary>
        public int OpenRadius { get; set; } = 15;

        /// <summary>Rejects a frame carrying only ONE of the two populations, i.e. the boundary is not
        /// in view. Deliberately wide — it catches NO BOUNDARY AT ALL, not bad framing; an arc just
        /// entering a corner is legitimately a few percent, and firing on those would silently cost
        /// the scan its rim points.</summary>
        public double MinFaceFraction { get; set; } = 0.02;
        public double MaxFaceFraction { get; set; } = 0.98;

        /// <summary>The real arc spans the frame; stubs left after clipping are a few hundred px.</summary>
        public double MinArcLength { get; set; } = 800;

        /// <summary>The region's outline also runs along the image frame. Clipping to a rectangle
        /// inset by this much throws those runs away and leaves the arc.</summary>
        public int BorderMargin { get; set; } = 3;

        private const int LargestComponentPercentile = 70;
        private const double MaxContourLength = 1e9;

        #endregion

        #region Detection

        /// <summary>A point on the chuck edge, in image pixels (HALCON row/column).</summary>
        public readonly record struct EdgePoint(double Row, double Column);

        /// <summary>Detects and disposes the contour internally. Returns the edge point nearest the crosshair.</summary>
        public bool TryDetect(HObject image, double crossRow, double crossCol, out EdgePoint point)
        {
            bool ok = TryDetect(image, crossRow, crossCol, out point, out HObject? contour);
            contour?.Dispose();
            return ok;
        }

        /// <summary>Detects the edge point nearest (<paramref name="crossRow"/>,
        /// <paramref name="crossCol"/>). On success also returns the arc <paramref name="contour"/> the
        /// point lies on, for overlay — CALLER OWNS it and must Dispose it. False if nothing is found
        /// or a HALCON op fails; the input frame is never modified.</summary>
        public bool TryDetect(HObject image, double crossRow, double crossCol, out EdgePoint point, out HObject? contour)
        {
            point = default;
            contour = null;
            var temps = new List<HObject>();
            try
            {
                HObject gray = Preprocess(image); temps.Add(gray);
                HOperatorSet.GetImageSize(gray, out HTuple width, out HTuple height);

                // 1. Optional smoothing before the cut.
                HObject cutInput = gray;
                if (SmoothWindow > 1)
                {
                    HOperatorSet.MeanImage(gray, out HObject smoothed, SmoothWindow, SmoothWindow);
                    temps.Add(smoothed);
                    cutInput = smoothed;
                }

                // 2. Split the two sides on grey level.
                HObject faceRaw;
                if (BrightThreshold < 0)
                    HOperatorSet.BinaryThreshold(cutInput, out faceRaw, "max_separability", "light", out HTuple _);
                else
                    HOperatorSet.Threshold(cutInput, out faceRaw, BrightThreshold, 255);
                temps.Add(faceRaw);

                // 3. Reject a one-population frame, before the expensive morphology below. AreaCenter
                //    returns one entry per connected region; an empty tuple is the all-dark case.
                HOperatorSet.AreaCenter(faceRaw, out HTuple rawAreas, out HTuple _, out HTuple _);
                double faceArea = rawAreas.Length == 0 ? 0.0 : rawAreas.TupleSum().D;
                double faceFraction = faceArea / (width.D * height.D);
                if (faceFraction < MinFaceFraction || faceFraction > MaxFaceFraction) return false;

                // 4. Clean up to ONE solid region. The closing repairs pits ON the face's edge, which
                //    is where nearly all the accuracy is won; FillUp handles the ones INSIDE it.
                HOperatorSet.ClosingCircle(faceRaw, out HObject faceClosed, CloseRadius); temps.Add(faceClosed);
                HOperatorSet.OpeningCircle(faceClosed, out HObject faceOpened, OpenRadius); temps.Add(faceOpened);
                HOperatorSet.Connection(faceOpened, out HObject faceParts); temps.Add(faceParts);
                HOperatorSet.CountObj(faceParts, out HTuple faceCount);
                if (faceCount.I < 1) return false;
                HOperatorSet.SelectShapeStd(faceParts, out HObject faceBiggest, "max_area", LargestComponentPercentile); temps.Add(faceBiggest);
                HOperatorSet.FillUp(faceBiggest, out HObject face); temps.Add(face);

                // 5. The edge = that region's outline, minus the stretches running along the image
                //    frame. Clipping severs those; the length filter drops the stubs it leaves.
                HOperatorSet.GenContourRegionXld(face, out HObject outline, "border"); temps.Add(outline);
                HOperatorSet.ClipContoursXld(outline, out HObject clipped, BorderMargin, BorderMargin,
                    height.D - 1 - BorderMargin, width.D - 1 - BorderMargin); temps.Add(clipped);
                HOperatorSet.SelectContoursXld(clipped, out HObject arcs, "contour_length",
                    MinArcLength, MaxContourLength, -0.5, 0.5); temps.Add(arcs);

                HOperatorSet.CountObj(arcs, out HTuple number);
                if (number.I < 1) return false;

                // 6. The arc point nearest the crosshair.
                double bestD2 = double.MaxValue, bestRow = 0, bestCol = 0;
                int bestIdx = -1;
                for (int i = 1; i <= number.I; i++)
                {
                    HOperatorSet.SelectObj(arcs, out HObject one, i);
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
                HOperatorSet.SelectObj(arcs, out HObject chosen, bestIdx);
                contour = chosen;   // the arc the nearest point lies on; caller disposes
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

        #endregion

        #region Helpers

        /// <summary>Independent single-channel byte image. Mono passes through; a colour frame yields
        /// its RED channel.</summary>
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
