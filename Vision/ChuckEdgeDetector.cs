using System;
using System.Collections.Generic;
using HalconDotNet;

namespace NanotecController
{
    /// <summary>
    /// Locates the chuck EDGE point nearest a reference pixel
    /// (verified in Halcon/chuck edge detector.hdev on capture_20260731_105121_943.bmp).
    ///
    /// Across the rim there are THREE zones: the out-of-focus bright background; a near-black BAND;
    /// and the in-focus, sharply-textured chuck face. The edge returned is the OUTER one — black band
    /// against out-of-focus background — which is the chuck's machined silhouette.
    ///
    /// Getting there takes two cues, because neither alone is enough:
    ///   FOCUS separates the chuck face from everything outside it (the two sides are nearly the same
    ///     brightness, so grey level cannot do it). That yields a solid, reliable region — but its
    ///     outline is the INNER edge, band against chuck face.
    ///   GREY LEVEL then adds the band, which is near-black (~10) where nothing else in the scene is.
    ///     Union the two and the region's outline becomes the OUTER edge.
    ///
    /// The outer edge is also the better feature to track: being the machined silhouette it fits a
    /// circle to ~5 px RMS, where the inner edge manages only ~21 px because the pits in the chuck
    /// face bite into it and the focus transition is blurred by <see cref="EnergyWindow"/> pooling.
    ///
    /// NOTE ON THE OLD RIDGE METHOD (removed): this class used to pull the edge out of a FINE-scale
    /// sharpness map with lines_gauss, as a thin bright ridge. That only worked while the chuck texture
    /// was LOW-contrast (the dim colour-camera frames), where the rim was the only strong fine-scale
    /// response. On the mono acA4024 frames the texture is bright and high-contrast, so the whole chuck
    /// face lights up in that map and there is no isolated ridge: lines_gauss returns ~29000 short
    /// texture fragments and NOT ONE survives the length filter, so TryDetect returned false on every
    /// frame. Focus energy is the robust discriminator — it is a broad AREA cue, and it gets STRONGER
    /// as texture contrast rises, so it holds on both cameras.
    ///
    /// Pass the FULL-RESOLUTION frame. Tunables match the .hdev script.
    /// </summary>
    public sealed class ChuckEdgeDetector
    {
        // --- focus split: chuck face vs everything outside it ---
        public int SobelWidth { get; set; } = 3;           // gradient filter size (odd: 3,5,7)
        // Pooling window for the focus map. Large enough to average over the texture period; it blurs
        // the step by about half its width, but symmetrically, so the mid-level crossing stays put.
        public int EnergyWindow { get; set; } = 41;
        // Bridges the dark pits that bite into the rim. Sweeping it against the residual of a circle
        // fitted to the arc:  radius 25/45/65/85 -> 37.6/33.5/21.3/20.8 px RMS. 65 gets nearly all of
        // the gain; past that the arc just shortens.
        public int CloseRadius { get; set; } = 65;
        public int OpenRadius { get; set; } = 25;          // drops specks of texture stranded outside
        // Grey cut on the focus-energy map; negative = Otsu. The blurry/in-focus split is strongly
        // bimodal (mono frame: ~0-8 vs ~54-86, Otsu picks 33), so Otsu is well posed and no fixed
        // level is needed. Set a value to override.
        public double EnergyThreshold { get; set; } = -1;

        // --- black band: carries the outline out to the OUTER edge ---
        // The band cut is made on a SMOOTHED grey image. Without it the cut also grabs every dark pit
        // in the chuck texture and the band stops being a clean ribbon.
        public int SmoothWindow { get; set; } = 21;
        // Grey cut for the band. The band floor measures 8.7-13.2 across rows (worst case 13.2) and the
        // approach from the bright side is a ~200 px defocus RAMP, not a step — so this value genuinely
        // sets WHERE on the ramp the edge lands. It is placed low deliberately: down near the floor the
        // ramp is steepest, so the crossing moves only ~4-8 px for a 10-level change and the per-row
        // illumination spread (bright side varies 146-224 by row) stops mattering. 30 keeps better than
        // 2x margin over the 13.2 floor. Circle-fit residual vs this value, 20/30/40/55/70:
        //   with the collar below : 2.9 / 4.7 /  8.1 / 13.3 / 17.3 px RMS
        //   without it            : 2.9 /18.3 / 30.0 / 36.8 / 41.4 px RMS
        public int DarkThreshold { get; set; } = 30;
        // The band hugs the chuck — its outer edge sits only ~30-50 px beyond the in-focus face.
        // Confining the dark cut to a collar this wide around that face stops isolated dark blobs
        // sitting ~150 px out in the blurred background from merging in and dragging the outline inward
        // (that is the "without it" row above; worst single deviation 128 px, versus 41 px with it).
        // 100 is the middle of the usable span: 60 clips the band itself (14.1 px RMS even at
        // DarkThreshold 20), and by 250 the collar is wide enough to be no constraint at all.
        public int BandGuideRadius { get; set; } = 100;
        public int BandCloseRadius { get; set; } = 15;     // seals the seam where band meets face

        // --- outline -> arc ---
        // The real arc spans the frame (4100-8400 px on these captures); stubs left behind after
        // clipping are a few hundred at most.
        public double MinArcLength { get; set; } = 800;
        // The region's outline also runs along the image frame. Clipping to a rectangle inset by this
        // much throws those runs away and leaves the rim.
        public int BorderMargin { get; set; } = 3;

        /// <summary>A point on the chuck edge, in image pixels (HALCON row/column).</summary>
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
        /// On success also returns the arc <paramref name="contour"/> the point lies on (XLD) for overlay
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
                HOperatorSet.GetImageSize(gray, out HTuple width, out HTuple height);

                // 1. Focus-energy map: gradient magnitude (high where sharp), pooled over EnergyWindow.
                //    Bright = in focus, dark = blurry.
                HOperatorSet.SobelAmp(gray, out HObject amp, "sum_abs", SobelWidth); temps.Add(amp);
                HOperatorSet.MeanImage(amp, out HObject energy, EnergyWindow, EnergyWindow); temps.Add(energy);

                // 2. Split the two sides.
                HObject inFocusRaw;
                if (EnergyThreshold < 0)
                    HOperatorSet.BinaryThreshold(energy, out inFocusRaw, "max_separability", "light", out HTuple _);
                else
                    HOperatorSet.Threshold(energy, out inFocusRaw, EnergyThreshold, 255);
                temps.Add(inFocusRaw);

                // 3. Clean up to ONE solid region — the chuck FACE. Close the pits that bite into the
                //    rim, open away stranded specks, keep the largest component, fill its interior.
                HOperatorSet.ClosingCircle(inFocusRaw, out HObject faceClosed, CloseRadius); temps.Add(faceClosed);
                HOperatorSet.OpeningCircle(faceClosed, out HObject faceOpened, OpenRadius); temps.Add(faceOpened);
                HOperatorSet.Connection(faceOpened, out HObject faceParts); temps.Add(faceParts);
                HOperatorSet.CountObj(faceParts, out HTuple faceCount);
                if (faceCount.I < 1) return false;
                HOperatorSet.SelectShapeStd(faceParts, out HObject faceBiggest, "max_area", 70); temps.Add(faceBiggest);
                HOperatorSet.FillUp(faceBiggest, out HObject face); temps.Add(face);

                // 4. The near-black band, cut on a SMOOTHED grey image (see SmoothWindow) and confined
                //    to a collar around the face (see BandGuideRadius).
                HOperatorSet.MeanImage(gray, out HObject smoothed, SmoothWindow, SmoothWindow); temps.Add(smoothed);
                HOperatorSet.Threshold(smoothed, out HObject darkAll, 0, DarkThreshold); temps.Add(darkAll);
                HOperatorSet.DilationCircle(face, out HObject collar, BandGuideRadius); temps.Add(collar);
                HOperatorSet.Intersection(darkAll, collar, out HObject band); temps.Add(band);

                // 5. Face + band = the chuck out to its outer silhouette. The closing seals the seam.
                //    If the band came back empty (no near-black zone in this frame — true of the old dim
                //    colour captures, whose rim only reaches ~85 in red) this collapses back to the face
                //    alone and the INNER edge is reported instead.
                HOperatorSet.Union2(face, band, out HObject chuckRaw); temps.Add(chuckRaw);
                HOperatorSet.ClosingCircle(chuckRaw, out HObject chuckClosed, BandCloseRadius); temps.Add(chuckClosed);
                HOperatorSet.Connection(chuckClosed, out HObject chuckParts); temps.Add(chuckParts);
                HOperatorSet.CountObj(chuckParts, out HTuple chuckCount);
                if (chuckCount.I < 1) return false;
                HOperatorSet.SelectShapeStd(chuckParts, out HObject chuckBiggest, "max_area", 70); temps.Add(chuckBiggest);
                HOperatorSet.FillUp(chuckBiggest, out HObject chuck); temps.Add(chuck);

                // 6. The chuck edge = that region's outline, minus the stretches that merely run along
                //    the image frame. clip_contours_xld cuts the outline at a rectangle inset by
                //    BorderMargin, severing those runs; the length filter drops the stubs left behind.
                HOperatorSet.GenContourRegionXld(chuck, out HObject outline, "border"); temps.Add(outline);
                HOperatorSet.ClipContoursXld(outline, out HObject clipped, BorderMargin, BorderMargin,
                    height.D - 1 - BorderMargin, width.D - 1 - BorderMargin); temps.Add(clipped);
                HOperatorSet.SelectContoursXld(clipped, out HObject arcs, "contour_length",
                    MinArcLength, 1e9, -0.5, 0.5); temps.Add(arcs);

                HOperatorSet.CountObj(arcs, out HTuple number);
                if (number.I < 1) return false;

                // 7. The arc point nearest the crosshair.
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

        // Independent single-channel byte image. The mono camera passes straight through; a colour frame
        // yields its RED channel (the old colour-camera scenes were red-lit).
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
