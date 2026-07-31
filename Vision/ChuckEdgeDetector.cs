using System;
using System.Collections.Generic;
using HalconDotNet;

namespace NanotecController
{
    /// <summary>
    /// Locates the chuck's INNER circular edge — the boundary between the brightly-lit, in-focus,
    /// machined chuck face and the large near-black region beside it — and returns the point on it
    /// nearest a reference pixel
    /// (verified in Halcon/innerCircleDetection.hdev on capture_20260731_160143_116.bmp).
    ///
    /// The centre-find only needs points on SOME circle concentric with the chuck; it does not care
    /// which one. The inner circle is used rather than the outer rim because the outer rim is not a
    /// clean circle — two sections on OPPOSITE sides carry no usable edge, and since the automatic
    /// scan probes in opposite PAIRS a gap pair takes out both ends of one pair at once.
    ///
    /// WHY THE PIPELINE IS GREY-LEVEL AND NOT FOCUS. The outer-rim detector this replaced could not
    /// use grey level at all: across that rim the two sides are nearly the same brightness, so only
    /// FOCUS separated them, and the whole thing was built around a focus-energy map. Here it is
    /// inverted — grey level separates the two sides trivially, and focus energy is actively WRONG:
    ///   GREY over the frame: dark side 72.9%, mean 13.8, 99% below 24.9
    ///                        bright side 27.1%, mean 232.9, and 19.4% of the frame SATURATED at 255
    ///   FOCUS overlaps outright, because a flat saturated area has zero gradient — the focus map
    ///     reads the middle of the bright face as "blurry". The bright side's 1st-percentile energy
    ///     is 2.8 against the dark side's mean of 2.08 and its 99% of 39.5 (the dark side scores
    ///     high AT the boundary step). Segmenting on that punches holes through the face.
    /// So none of the focus machinery is reused. Do not "restore" it here — it is preserved, with its
    /// own tuning notes, in Halcon/chuck edge detector.hdev and in git history.
    ///
    /// The boundary is also a STEP: it falls from saturation to the dark floor within ~15-20 px,
    /// where the outer rim was approached through a ~200 px defocus RAMP. That is why
    /// <see cref="BrightThreshold"/> is a MILD parameter here while the old DarkThreshold was a
    /// critical one — on a step, moving the cut barely moves the edge (across 40..230 the fitted
    /// radius shifts 0.7%).
    ///
    /// The fitted circle's CENTRE LIES OUTSIDE THE FRAME (about row -1450, col +4390, r ~4600 px on
    /// a 4016x3024 frame), so only a shallow arc is ever in view. On the tuning frame the arc is
    /// 6284 px and fits to 4.06 px RMS with a worst deviation of 13.2 px.
    ///
    /// TUNED ON ONE FRAME — there is no second capture of this scene yet. The sweeps quoted below are
    /// real full-resolution measurements, but they show how the pipeline responds to its own
    /// parameters, not how much the SCENE varies. Re-tune in the .hdev script once more captures exist.
    ///
    /// Pass the FULL-RESOLUTION frame. Tunables match the .hdev script.
    /// </summary>
    public sealed class ChuckEdgeDetector
    {
        // <= 1 disables smoothing. OFF by default, the opposite of the old outer-rim pipeline, where
        // smoothing before the grey cut was essential. Two measured reasons, both specific to cutting
        // a STEP:
        //   SmoothWindow    1     5    11    21    41
        //   fit residual  4.06  4.14  4.40  4.90  5.76  px RMS
        //   fitted radius 4597  4600  4608  4618  4627  px
        // (a) the cut at 80 sits well BELOW the step's midpoint (~123), so blurring the step walks the
        // crossing outward into the dark — a bias that grows with the window, visible as that radius
        // drift; (b) it makes the stray component WORSE, not better: the dark region holds a faint
        // reflection blob (raw grey up to 217) small enough for OpenRadius to erase, but smoothing
        // spreads it until it survives. The pits smoothing was there to suppress are already handled
        // by CloseRadius and FillUp. Turn it on only if a future capture speckles in a way those two
        // cannot absorb.
        public int SmoothWindow { get; set; } = 1;

        // Grey cut separating face from dark region; negative = Otsu. DELIBERATELY FIXED, where the
        // old pipeline defaulted to Otsu on its energy map, because these two grey levels are set by
        // the illumination and the material and do NOT move with framing — whereas Otsu, being a
        // RELATIVE split, shifts with how much of the frame each side happens to occupy, which is
        // exactly what changes as the stage scans. Otsu lands at 123 on the tuning frame. A fixed cut
        // is also what lets the face-fraction gate below be a meaningful "is the boundary in view" test.
        //   BrightThreshold  40    60    80   100   123   160   200   230
        //   fit residual   3.69  4.10  4.06  4.15  4.33  4.35  4.66  5.83  px RMS
        // Flat, as a step edge should be — the choice is about MARGIN, not residual. The dark floor is
        // ~14 with 99% below 24.9 and the bright plateau ~233, so 80 sits mid-valley with ~3x margin
        // over the dark floor and well clear of the scalloped lip a high cut bites into.
        public double BrightThreshold { get; set; } = 80;

        // Bridges the dark pits that bite into the lip of the face. As in the old pipeline this is THE
        // parameter that matters, and for the same reason — it is the same machined texture. With
        // smoothing off it is load-bearing:
        //   CloseRadius     0     15    45    65   105   160
        //   fit residual  191.0  7.22  5.35  4.73  4.06  3.62  px RMS
        //   worst dev    1498.0 36.5  21.8  19.2  13.2  13.3  px
        // At 0 the region fragments into 7 pieces and the fit is meaningless. RMS keeps falling past
        // 105, but the WORST deviation bottoms out there and then creeps back, so 105 is the knee.
        // NOTE it will also seal any genuine notch in the boundary narrower than ~2x this value — fine
        // for pits, and harmless on a feature chosen for having no gaps, but it is the reason this
        // detector must not be pointed back at the gapped outer rim.
        public int CloseRadius { get; set; } = 105;

        // Drops isolated specks stranded in the dark region — here, the reflection blob. Nearly inert
        // (4.07 px RMS at 0 vs 4.06 at 15) because keeping the largest component already discards it;
        // 25 starts eating the arc (worst deviation 13.2 -> 24.6). Kept small as insurance against
        // specks the one tuning frame does not happen to show.
        public int OpenRadius { get; set; } = 15;

        // Reject the frame when only ONE of the two populations is present, i.e. the boundary is not
        // in view. This is the analogue of the old focus-energy gate, but it gets to be a much simpler
        // test, and that is a direct consequence of the FIXED threshold above: Otsu always returns a
        // split, so on a one-population frame it manufactures a plausible boundary out of noise and
        // everything downstream still succeeds — reporting a confident, wrong edge. A fixed grey cut
        // has no such failure mode: an all-dark frame yields an EMPTY region and an all-bright frame
        // yields the whole frame. Measured on crops of the tuning capture: all-dark 0.00%, all-bright
        // 99.82%, boundary in view 27.36%. The bounds are wide on purpose — they catch NO BOUNDARY AT
        // ALL, not bad framing; a valid capture with the arc just entering a corner is legitimately a
        // few percent, and this gate firing on those would silently cost the scan its rim points.
        public double MinFaceFraction { get; set; } = 0.02;
        public double MaxFaceFraction { get; set; } = 0.98;

        // The real arc spans the frame (~6280 px on the tuning capture); stubs left behind after
        // clipping are a few hundred at most.
        public double MinArcLength { get; set; } = 800;

        // The region's outline also runs along the image frame. Clipping to a rectangle inset by this
        // much throws those runs away and leaves the arc.
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
        /// Detects the edge point nearest (<paramref name="crossRow"/>, <paramref name="crossCol"/>).
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

                // 1. Optional smoothing before the cut — off by default, see SmoothWindow.
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

                // 3. Reject a frame carrying only one of the two populations — the boundary is not in
                //    view, so there is nothing to measure. Cheap, and it runs BEFORE the expensive
                //    morphology below. AreaCenter returns one entry per connected region, so the areas
                //    are summed; an empty region gives an empty tuple, which is the all-dark case.
                HOperatorSet.AreaCenter(faceRaw, out HTuple rawAreas, out HTuple _, out HTuple _);
                double faceArea = rawAreas.Length == 0 ? 0.0 : rawAreas.TupleSum().D;
                double faceFraction = faceArea / (width.D * height.D);
                if (faceFraction < MinFaceFraction || faceFraction > MaxFaceFraction) return false;

                // 4. Clean up to ONE solid region. FillUp disposes of the pits left INSIDE the face; the
                //    closing repairs the ones ON its edge, and that second job is where nearly all of
                //    the accuracy is won (at CloseRadius 0 the region comes apart into 7 pieces and the
                //    fitted circle is off by 1498 px at worst).
                HOperatorSet.ClosingCircle(faceRaw, out HObject faceClosed, CloseRadius); temps.Add(faceClosed);
                HOperatorSet.OpeningCircle(faceClosed, out HObject faceOpened, OpenRadius); temps.Add(faceOpened);
                HOperatorSet.Connection(faceOpened, out HObject faceParts); temps.Add(faceParts);
                HOperatorSet.CountObj(faceParts, out HTuple faceCount);
                if (faceCount.I < 1) return false;
                HOperatorSet.SelectShapeStd(faceParts, out HObject faceBiggest, "max_area", 70); temps.Add(faceBiggest);
                HOperatorSet.FillUp(faceBiggest, out HObject face); temps.Add(face);

                // 5. The edge = that region's outline, minus the stretches that merely run along the
                //    image frame. ClipContoursXld cuts the outline at a rectangle inset by BorderMargin,
                //    severing those runs; the length filter drops the stubs it leaves behind.
                HOperatorSet.GenContourRegionXld(face, out HObject outline, "border"); temps.Add(outline);
                HOperatorSet.ClipContoursXld(outline, out HObject clipped, BorderMargin, BorderMargin,
                    height.D - 1 - BorderMargin, width.D - 1 - BorderMargin); temps.Add(clipped);
                HOperatorSet.SelectContoursXld(clipped, out HObject arcs, "contour_length",
                    MinArcLength, 1e9, -0.5, 0.5); temps.Add(arcs);

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
