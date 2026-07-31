using System;
using System.Collections.Generic;
using HalconDotNet;

namespace NanotecController
{
    /// <summary>
    /// Locates the centre of the SOLID-DISK calibration fiducial in one frame, returning a
    /// sub-pixel centre in image pixels. The disk reads distinctly brighter than a uniform
    /// background, with bright diagonal scribe lines nearby that may or may not cross it.
    /// Deliberately not tuned to one lighting/camera setup: the two captures on record differ
    /// completely — the colour acA5472 saw a red-lit scene with the line cutting the disk and a
    /// large bright blob in one corner (Desktop/images/solid_circle_fiducial.png), the mono
    /// acA4024 sees a dark field with the lines clear of the disk
    /// (Desktop/images/capture_20260730_162849_830.bmp) — and this pipeline handles both.
    ///
    /// Separate from <see cref="WaferEdgeDetector"/>: this is the 2D-localisable feature used to
    /// calibrate the pixel→step affine. The disk gives a robust, rotation-free 2D point (the
    /// wafer edge can't — a smooth arc only reveals motion along its normal, the aperture
    /// problem). Method: segment the bright structures → CLOSE (bridge the rim notch where a
    /// scribe line cuts the disk, absorb dark internal streaks) → FILL (close enclosed holes) →
    /// OPEN with a disk larger than half the scribe-line width (severs/erases the thin lines),
    /// leaving a near-perfect solid circle. The disk's centroid averages over thousands of
    /// pixels, so it's sub-pixel and robust to speckle/specular texture.
    ///
    /// The fiducial is the ROUNDEST surviving blob, not the biggest: a clipped corner blob can
    /// be larger but is elongated, so it loses on circularity. Thresholds are exposed for tuning
    /// against live frames. Pass the FULL-RESOLUTION frame; the input is never modified.
    /// </summary>
    public sealed class SolidCircleDetector
    {
        // These are in PIXELS, so they assume a disk far larger than the structures being
        // removed; both captures on record satisfy that comfortably (disk r = 402 px mono,
        // 310 px colour). A heavy zoom-out would need them revisited.
        // ClosingRadius : bridge the notch where a scribe line cuts the rim; >= the widest gap.
        // OpenRadius    : sized to SEVER a line where it crosses the disk, staying well below the
        //                 disk radius. It need not erase a line outright — one that survives whole
        //                 is elongated, so MinCircularity drops it anyway (that is what happens on
        //                 the mono frames, where the ~65 px lines outlive a radius-20 opening).
        // MinCircularity: rejects those surviving lines and any non-round blob.
        public double ClosingRadius { get; set; } = 25;
        public double OpenRadius { get; set; } = 20;
        public double MinCircularity { get; set; } = 0.85;   // 1 = perfect circle
        public double MinArea { get; set; } = 5000;          // ignore specks / thin lines
        public double MaxArea { get; set; } = 1e9;
        /// <summary>
        /// Forces one specific gray cut. Null (the default) picks the cut per frame — see
        /// <see cref="Candidates"/> — which is what makes this survive a change of camera or
        /// lighting. A fixed cut encodes one camera's exposure and nothing warns you when that
        /// stops being true: the previous hard-coded 200 was measured off the colour acA5472
        /// (red channel, background ~183, disk 219-250) and silently segmented FOUR pixels of a
        /// mono acA4024 frame, whose background sits at ~53 and disk at ~120. Set this only to
        /// pin down a frame while tuning.
        /// </summary>
        public double? BrightThreshold { get; set; }

        /// <summary>The gray cut that produced the last successful detection, or NaN if the last
        /// <see cref="TryDetect"/> found nothing. Worth logging when a detection looks wrong.</summary>
        public double LastThreshold { get; private set; } = double.NaN;

        /// <summary>
        /// Why the last <see cref="TryDetect"/> returned false; null after a success. A bare
        /// "not found" is what made the mono-camera failure so slow to diagnose — the detector
        /// knew it had segmented four pixels and said nothing. This reports how close the best
        /// candidate cut got, which distinguishes "segmented nothing" (threshold wrong) from
        /// "found a blob but it wasn't round/big enough" (gates or optics wrong).
        /// </summary>
        public string? LastFailure { get; private set; }

        /// <summary>Fiducial centre + nominal radius, in image pixels (HALCON row/column).</summary>
        public readonly record struct Mark(double Row, double Column, double Radius);

        /// <summary>Detects the mark and disposes the overlay contour internally.</summary>
        public bool TryDetect(HObject image, out Mark mark)
        {
            bool ok = TryDetect(image, out mark, out HObject? contour);
            contour?.Dispose();
            return ok;
        }

        /// <summary>
        /// Detects the mark; on success also returns its boundary <paramref name="contour"/>
        /// (XLD) for overlay — the CALLER OWNS it and must Dispose it. Returns false on no
        /// region / HALCON failure; the input frame is never modified.
        /// </summary>
        public bool TryDetect(HObject image, out Mark mark, out HObject? contour)
        {
            mark = default;
            contour = null;
            LastThreshold = double.NaN;
            LastFailure = null;
            var temps = new List<HObject>();
            try
            {
                HObject gray = Preprocess(image); temps.Add(gray);

                // Try each candidate cut and keep the ROUNDEST result rather than the first that
                // passes. The shape gate is a strong validator (a big, near-perfect circle is not
                // something a wrong threshold produces by accident), so scoring on circularity
                // both picks a working cut and lands mid-band, where the segmented rim is truest
                // and the centroid most accurate.
                HObject? best = null;
                double bestCirc = -1;
                // Roundest blob seen at ANY cut, gate notwithstanding — the near-miss report.
                double seenCirc = -1, seenArea = 0, seenAt = double.NaN;
                foreach (double t in Candidates(gray))
                {
                    Attempt a = Segment(gray, t);
                    if (a.SeenCircularity > seenCirc) { seenCirc = a.SeenCircularity; seenArea = a.SeenArea; seenAt = t; }
                    if (a.Blob == null) continue;
                    if (a.Circularity > bestCirc) { best?.Dispose(); best = a.Blob; bestCirc = a.Circularity; LastThreshold = t; }
                    else a.Blob.Dispose();
                }
                if (best == null)
                {
                    LastFailure = seenCirc < 0
                        ? "no candidate cut segmented anything - check lighting/exposure"
                        : $"no round blob at any cut; closest was area={seenArea:N0} circ={seenCirc:F3} " +
                          $"at cut {seenAt:F0} (need area>={MinArea:N0}, circ>={MinCircularity:F2})";
                    return false;
                }
                temps.Add(best);

                HOperatorSet.AreaCenter(best, out HTuple area, out HTuple row, out HTuple col);
                if (row.Length < 1) { LastFailure = "selected blob had no centroid"; return false; }

                mark = new Mark(row.D, col.D, Math.Sqrt(area.D / Math.PI));
                HOperatorSet.GenContourRegionXld(best, out HObject border, "border");
                contour = border;   // handed to caller; deliberately NOT added to temps
                return true;
            }
            catch (HOperatorException ex)
            {
                contour?.Dispose();
                contour = null;
                LastFailure = "HALCON error: " + ex.GetErrorMessage();
                return false;
            }
            finally
            {
                foreach (HObject t in temps) t.Dispose();
            }
        }

        // The gray cuts to try, in no particular order (the caller scores them all). A manual
        // BrightThreshold short-circuits to exactly that value.
        //
        // Why a ladder and not one clever statistic: measured against every capture on record,
        // each frame accepts a BAND of cuts 11-15 wide, but no single statistic lands inside all
        // of them. Otsu nails the mono frames (85, band 60-130) and the clean colour frame (211,
        // band 195-245), yet reads 130 on the colour frames that contain a dark strip — it finds
        // the valley between that strip and everything else, well below their 190-245 band. The
        // 99th percentile covers the mono frames but overshoots the colour ones (251-255); the
        // 95th only scrapes the band edges. Together they cover every frame at least twice over.
        // A pass costs 1-17 ms and this runs once per Add Sample click, so the sweep is free.
        private IEnumerable<double> Candidates(HObject gray)
        {
            if (BrightThreshold is double manual) { yield return manual; yield break; }

            double otsu = double.NaN;
            try
            {
                HOperatorSet.BinaryThreshold(gray, out HObject region, "max_separability", "light", out HTuple used);
                region.Dispose();
                otsu = used.D;
            }
            catch (HOperatorException) { /* degenerate histogram; the percentiles still stand */ }
            if (!double.IsNaN(otsu)) yield return otsu;

            HOperatorSet.GrayHisto(gray, gray, out HTuple absolute, out _);
            double total = 0;
            for (int i = 0; i < absolute.Length; i++) total += absolute[i].D;
            if (total <= 0) yield break;

            // Descending percentiles: the fiducial is always a small, bright minority of the frame.
            foreach (double q in (double[])[99, 97, 95, 93, 90])
            {
                double c = 0;
                for (int i = 0; i < absolute.Length; i++)
                {
                    c += absolute[i].D;
                    if (c / total >= q / 100.0) { yield return i; break; }
                }
            }
        }

        /// <summary>
        /// One threshold attempt. <see cref="Blob"/> is non-null only when the shape gate passed,
        /// and the CALLER owns it. <see cref="SeenCircularity"/>/<see cref="SeenArea"/> describe
        /// the roundest blob this cut produced whether or not it passed (-1/0 if the cut
        /// segmented nothing), so a total failure can still say how close it came.
        /// </summary>
        private readonly record struct Attempt(
            HObject? Blob, double Circularity, double SeenCircularity, double SeenArea);

        // One pass of the morphology + shape gate at a given cut.
        private Attempt Segment(HObject gray, double threshold)
        {
            HObject? blob = null;
            double circularity = 0, seenCirc = -1, seenArea = 0;
            var temps = new List<HObject>();
            try
            {
                HOperatorSet.Threshold(gray, out HObject bright, threshold, 255); temps.Add(bright);

                // Close bridges the rim notch where a scribe line cuts the disk and absorbs dark
                // internal streaks; fill_up closes any fully-enclosed holes; the opening severs
                // thin structures from the disk, leaving a near-perfect solid circle. Whatever
                // the opening leaves standing is filtered on shape below, not here.
                HOperatorSet.ClosingCircle(bright, out HObject closed, ClosingRadius); temps.Add(closed);
                HOperatorSet.FillUp(closed, out HObject filled); temps.Add(filled);
                HOperatorSet.OpeningCircle(filled, out HObject opened, OpenRadius); temps.Add(opened);
                HOperatorSet.Connection(opened, out HObject conn); temps.Add(conn);

                HOperatorSet.CountObj(conn, out HTuple nParts);
                if (nParts.I < 1) return new Attempt(null, 0, seenCirc, seenArea);   // cut segmented nothing

                // Record the roundest blob this cut produced BEFORE gating, so a failed attempt
                // can report how close it came (the .hdev script shows the same near-miss).
                HOperatorSet.Circularity(conn, out HTuple allCirc);
                HOperatorSet.TupleSortIndex(allCirc, out HTuple allSort);
                int seenIdx = allSort[allSort.Length - 1].I;
                HOperatorSet.SelectObj(conn, out HObject roundest, seenIdx + 1); temps.Add(roundest);
                HOperatorSet.AreaCenter(roundest, out HTuple seenAreaT, out _, out _);
                seenCirc = allCirc[seenIdx].D;
                seenArea = seenAreaT.D;

                // Keep big, round blobs → drops the lines and the elongated corner blob.
                HTuple features = new HTuple("circularity").TupleConcat("area");
                HTuple mins = new HTuple(MinCircularity).TupleConcat(MinArea);
                HTuple maxs = new HTuple(1.0).TupleConcat(MaxArea);
                HOperatorSet.SelectShape(conn, out HObject round, features, "and", mins, maxs); temps.Add(round);

                HOperatorSet.CountObj(round, out HTuple count);
                if (count.I < 1) return new Attempt(null, 0, seenCirc, seenArea);

                // Pick the MOST circular so the round fiducial wins over any larger but less-round
                // blob that slips the gate. TupleSortIndex is ascending → most circular is last.
                HOperatorSet.Circularity(round, out HTuple circ);
                HOperatorSet.TupleSortIndex(circ, out HTuple sortIdx);
                int bestIdx = sortIdx[sortIdx.Length - 1].I + 1;   // SelectObj is 1-based
                HOperatorSet.SelectObj(round, out HObject best, bestIdx);
                circularity = circ[sortIdx[sortIdx.Length - 1].I].D;
                blob = best;   // handed back; deliberately NOT added to temps
                return new Attempt(blob, circularity, seenCirc, seenArea);
            }
            catch (HOperatorException)
            {
                blob?.Dispose();
                return new Attempt(null, 0, seenCirc, seenArea);
            }
            finally
            {
                foreach (HObject t in temps) t.Dispose();
            }
        }

        // Independent single-channel byte image. A MONO camera (the current acA4024) passes
        // straight through. For a COLOUR frame take the RED channel rather than a luminance
        // gray: the markers were red-lit under the previous acA5472, and luminance weights red
        // only ~0.3, throwing away most of the contrast. Input frame is never modified.
        private static HObject Preprocess(HObject image)
        {
            HOperatorSet.CountChannels(image, out HTuple channels);
            if (channels.I >= 3)
            {
                HOperatorSet.AccessChannel(image, out HObject red, 1);   // 1 = red (assumes RGB order)
                try { HOperatorSet.ConvertImageType(red, out HObject red8, "byte"); return red8; }
                finally { red.Dispose(); }
            }
            HOperatorSet.ConvertImageType(image, out HObject byteImg, "byte");
            return byteImg;
        }
    }
}
