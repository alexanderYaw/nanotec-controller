using System;
using System.Collections.Generic;
using HalconDotNet;

namespace NanotecController
{
    /// <summary>
    /// Locates the centre of the SOLID-DISK calibration fiducial, sub-pixel, in image pixels. This is
    /// the 2D-localisable feature the pixel→step affine is calibrated against — the wafer edge cannot
    /// serve, a smooth arc only revealing motion along its normal (the aperture problem).
    ///
    /// Method: segment bright structures → CLOSE (bridge the notch where a scribe line cuts the rim)
    /// → FILL → OPEN with a disk larger than half the scribe-line width, leaving a near-perfect solid
    /// circle whose centroid averages over thousands of pixels. The fiducial is the ROUNDEST
    /// surviving blob, not the biggest: a clipped corner blob can be larger but is elongated.
    ///
    /// Deliberately not tuned to one lighting/camera setup — the two captures on record differ
    /// completely and this pipeline handles both. Pass the FULL-RESOLUTION frame; the input is never
    /// modified.
    /// </summary>
    public sealed class SolidCircleDetector
    {
        #region Tunables

        // In PIXELS, so they assume a disk far larger than the structures being removed. A heavy
        // zoom-out would need them revisited.

        /// <summary>Bridges the notch where a scribe line cuts the rim; >= the widest gap.</summary>
        public double ClosingRadius { get; set; } = 25;

        /// <summary>Sized to SEVER a line crossing the disk, well below the disk radius. It need not
        /// erase a line outright — a survivor is elongated, so MinCircularity drops it.</summary>
        public double OpenRadius { get; set; } = 20;
        public double MinCircularity { get; set; } = 0.85;   // 1 = perfect circle
        public double MinArea { get; set; } = 5000;          // ignore specks / thin lines
        public double MaxArea { get; set; } = 1e9;
        /// <summary>Forces one specific gray cut. Null (the default) picks the cut per frame, which is
        /// what makes this survive a change of camera or lighting — a fixed cut encodes one camera's
        /// exposure and gives no warning when that stops being true. Set only while tuning.</summary>
        public double? BrightThreshold { get; set; }

        /// <summary>The gray cut that produced the last successful detection, or NaN if the last
        /// <see cref="TryDetect"/> found nothing. Worth logging when a detection looks wrong.</summary>
        public double LastThreshold { get; private set; } = double.NaN;

        /// <summary>Why the last <see cref="TryDetect"/> returned false; null after a success. Reports
        /// how close the best candidate cut got, distinguishing "segmented nothing" (threshold wrong)
        /// from "found a blob but it wasn't round/big enough" (gates or optics wrong).</summary>
        public string? LastFailure { get; private set; }

        #endregion

        #region Detection

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

        #endregion

        #region Threshold ladder

        /// <summary>The gray cuts to try, in no particular order — the caller scores them all. A ladder
        /// rather than one clever statistic because no single statistic lands inside every frame's
        /// acceptable band: Otsu nails the mono frames but reads far low on colour frames containing a
        /// dark strip, and the percentiles overshoot elsewhere. Together they cover every capture on
        /// record at least twice over, and a pass costs 1-17 ms.</summary>
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

        #endregion

        #region Helpers

        /// <summary>Independent single-channel byte image. Mono passes straight through; a colour frame
        /// yields its RED channel rather than a luminance gray, the markers being red-lit and luminance
        /// weighting red only ~0.3. The input frame is never modified.</summary>
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

        #endregion
    }
}
