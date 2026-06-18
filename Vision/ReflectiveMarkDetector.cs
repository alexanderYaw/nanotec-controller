using System;
using System.Collections.Generic;
using HalconDotNet;

namespace MotorControlApp
{
    /// <summary>
    /// Locates the centre of the circular calibration fiducial — a BRIGHT RING with a darker,
    /// mottled centre on a contrasting background (see Desktop/images/capture_*.png) — in one
    /// frame, returning a sub-pixel centre in image pixels.
    ///
    /// Separate from <see cref="WaferEdgeDetector"/>: this is the 2D-localisable feature used to
    /// calibrate the pixel→step affine. The ring's disk gives a robust, rotation-free 2D point
    /// (the wafer edge can't — a smooth arc only reveals motion along its normal, the aperture
    /// problem). Method: segment the bright ring → close gaps → fill the dark centre into a
    /// SOLID disk → keep the large round region → take its centroid. Filling first makes the
    /// speckled/specular interior irrelevant: the centre is fixed by the outline, averaged over
    /// thousands of pixels, so it's robust and effectively sub-pixel.
    ///
    /// FIRST CUT: the thresholds are guesses and WILL need tuning against live frames — they're
    /// all exposed as properties. Pass the FULL-RESOLUTION frame; the input is never modified.
    /// </summary>
    public sealed class ReflectiveMarkDetector
    {
        public double MinCircularity { get; set; } = 0.7;    // 1 = perfect circle; rejects irregular background blobs
        public double MinArea { get; set; } = 5000;          // ignore specks/noise
        public double MaxArea { get; set; } = 1e9;
        public double ClosingRadius { get; set; } = 5;       // close gaps in the ring before filling

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

                // Bright ring → close its gaps → fill the dark centre → solid disk.
                HOperatorSet.BinaryThreshold(gray, out HObject bright, "max_separability", "light", out HTuple _); temps.Add(bright);
                HOperatorSet.ClosingCircle(bright, out HObject closed, ClosingRadius); temps.Add(closed);
                HOperatorSet.FillUp(closed, out HObject filled); temps.Add(filled);
                HOperatorSet.Connection(filled, out HObject conn); temps.Add(conn);

                // Keep big, round regions only — rejects vignette/background blobs.
                HTuple features = new HTuple("circularity").TupleConcat("area");
                HTuple mins = new HTuple(MinCircularity).TupleConcat(MinArea);
                HTuple maxs = new HTuple(1.0).TupleConcat(MaxArea);
                HOperatorSet.SelectShape(conn, out HObject round, features, "and", mins, maxs); temps.Add(round);

                HOperatorSet.CountObj(round, out HTuple count);
                if (count.I < 1) return false;

                // The fiducial is the largest surviving round region.
                HOperatorSet.SelectShapeStd(round, out HObject best, "max_area", 0); temps.Add(best);
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
