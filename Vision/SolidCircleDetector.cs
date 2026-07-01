using System;
using System.Collections.Generic;
using HalconDotNet;

namespace NanotecController
{
    /// <summary>
    /// Locates the centre of the SOLID-DISK calibration fiducial — a solid red disk, slightly
    /// brighter than the red background, crossed by bright diagonal scribe lines with a large
    /// bright blob in one corner (see Desktop/images/solid_circle_fiducial.png) — in one frame,
    /// returning a sub-pixel centre in image pixels.
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
        // ClosingRadius : bridge the rim notch / dark streaks; set >= widest gap to close.
        // OpenRadius    : must exceed half the scribe-line width, stay below the disk radius.
        // MinCircularity: reject the elongated lines and the non-round corner blob.
        public double ClosingRadius { get; set; } = 25;
        public double OpenRadius { get; set; } = 20;
        public double MinCircularity { get; set; } = 0.85;   // 1 = perfect circle
        public double MinArea { get; set; } = 5000;          // ignore specks / thin lines
        public double MaxArea { get; set; } = 1e9;

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
            var temps = new List<HObject>();
            try
            {
                HObject gray = Preprocess(image); temps.Add(gray);

                // Bright structures (disk + scribe lines + corner blob).
                HOperatorSet.BinaryThreshold(gray, out HObject bright, "max_separability", "light", out HTuple _); temps.Add(bright);

                // Close bridges the rim notch where a scribe line cuts the disk and absorbs dark
                // internal streaks; fill_up closes any fully-enclosed holes; opening with a disk
                // bigger than half the line width severs/erases the thin scribe lines, leaving a
                // near-perfect solid circle.
                HOperatorSet.ClosingCircle(bright, out HObject closed, ClosingRadius); temps.Add(closed);
                HOperatorSet.FillUp(closed, out HObject filled); temps.Add(filled);
                HOperatorSet.OpeningCircle(filled, out HObject opened, OpenRadius); temps.Add(opened);
                HOperatorSet.Connection(opened, out HObject conn); temps.Add(conn);

                HOperatorSet.CountObj(conn, out HTuple nParts);
                if (nParts.I < 1) return false;

                // Keep big, round blobs → drops the lines and the elongated corner blob.
                HTuple features = new HTuple("circularity").TupleConcat("area");
                HTuple mins = new HTuple(MinCircularity).TupleConcat(MinArea);
                HTuple maxs = new HTuple(1.0).TupleConcat(MaxArea);
                HOperatorSet.SelectShape(conn, out HObject round, features, "and", mins, maxs); temps.Add(round);

                HOperatorSet.CountObj(round, out HTuple count);
                if (count.I < 1) return false;

                // Pick the MOST circular so the round fiducial wins over any larger but less-round
                // blob that slips the gate. TupleSortIndex is ascending → most circular is last.
                HOperatorSet.Circularity(round, out HTuple circ);
                HOperatorSet.TupleSortIndex(circ, out HTuple sortIdx);
                int bestIdx = sortIdx[sortIdx.Length - 1].I + 1;   // SelectObj is 1-based
                HOperatorSet.SelectObj(round, out HObject best, bestIdx); temps.Add(best);

                HOperatorSet.AreaCenter(best, out HTuple area, out HTuple row, out HTuple col);
                if (row.Length < 1) return false;

                mark = new Mark(row.D, col.D, Math.Sqrt(area.D / Math.PI));
                HOperatorSet.GenContourRegionXld(best, out HObject border, "border");
                contour = border;   // handed to caller; deliberately NOT added to temps
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

        // Independent single-channel byte image. The markers are red-lit, so the RED channel
        // (channel 1 of an RGB frame) carries almost all the contrast — far better than a
        // luminance gray, which weights red only ~0.3. Mono frames pass through. Input frame
        // is never modified.
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
