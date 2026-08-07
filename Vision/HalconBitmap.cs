using System;
using System.Drawing;
using System.Drawing.Imaging;
using HalconDotNet;

namespace NanotecController
{
    /// <summary>
    /// Converts a HALCON HObject to a System.Drawing.Bitmap for a plain WinForms PictureBox.
    /// HWindowControl is not usable here: only the .NET-Framework HALCON builds are installed and
    /// their HWindowControl derives from the Framework's System.Windows.Forms, which this .NET 10 app
    /// cannot load. The headless HObject/HOperatorSet types load fine.
    /// </summary>
    public static class HalconBitmap
    {
        /// <summary>Converts the image to an 8-bit Bitmap (grayscale or 24-bit colour), full size.</summary>
        public static Bitmap ToBitmap(HObject image) => ToBitmap(image, 0, 0);

        /// <summary>Converts to an 8-bit Bitmap, first shrinking in HALCON to fit within
        /// <paramref name="maxWidth"/>×<paramref name="maxHeight"/>. Downscaling natively before the
        /// managed pixel copy is the cheapest big win for live view on a large sensor. Pass 0,0 to keep
        /// full resolution. <paramref name="enhanceMono"/> collapses to grey and contrast-stretches.
        /// </summary>
        public static Bitmap ToBitmap(HObject image, int maxWidth, int maxHeight, bool enhanceMono = false)
        {
            // Downscale the NATIVE frame FIRST: the type conversion and managed pixel copy then run
            // on the small image, and only the shrink touches full resolution.
            HObject? scaled = null;
            HObject? img8 = null;
            HObject? grey = null;
            HObject? stretched = null;
            try
            {
                HObject work = image;
                if (maxWidth > 0 && maxHeight > 0)
                {
                    HOperatorSet.GetImageSize(image, out HTuple w, out HTuple h);
                    if (w.I > maxWidth || h.I > maxHeight)
                    {
                        double f = Math.Min((double)maxWidth / w.I, (double)maxHeight / h.I);
                        int nw = Math.Max(1, (int)(w.I * f)), nh = Math.Max(1, (int)(h.I * f));
                        HOperatorSet.ZoomImageSize(image, out scaled, nw, nh, "constant");
                        work = scaled;
                    }
                }

                if (enhanceMono)
                {
                    HOperatorSet.CountChannels(work, out HTuple ch);
                    if (ch.I >= 3) { HOperatorSet.Rgb1ToGray(work, out grey); work = grey; }
                    HOperatorSet.ScaleImageMax(work, out stretched);   // stretch min..max -> 0..255
                    work = stretched;
                }

                // Normalise to 8-bit so cameras delivering e.g. uint16 still display sensibly.
                HOperatorSet.ConvertImageType(work, out img8, "byte");
                HOperatorSet.CountChannels(img8, out HTuple channels);
                return channels.I >= 3 ? FromRgb(img8) : FromGray(img8);
            }
            finally
            {
                stretched?.Dispose();
                grey?.Dispose();
                img8?.Dispose();
                scaled?.Dispose();
            }
        }

        private static unsafe Bitmap FromGray(HObject image)
        {
            HOperatorSet.GetImagePointer1(image, out HTuple ptr, out _, out HTuple w, out HTuple h);
            int width = w.I, height = h.I;

            var bmp = new Bitmap(width, height, PixelFormat.Format8bppIndexed);
            ColorPalette pal = bmp.Palette;                       // 8bpp needs a grayscale ramp
            for (int i = 0; i < 256; i++) pal.Entries[i] = Color.FromArgb(i, i, i);
            bmp.Palette = pal;

            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);
            try
            {
                // HALCON rows are contiguous; the Bitmap stride is padded. Copy row by row rather
                // than bouncing through a managed staging buffer.
                byte* src = (byte*)ptr.IP;
                for (int y = 0; y < height; y++)
                    Buffer.MemoryCopy(src + (long)y * width, (byte*)bd.Scan0 + (long)y * bd.Stride,
                                      bd.Stride, width);
            }
            finally { bmp.UnlockBits(bd); }
            return bmp;
        }

        // HALCON stores the three channels as separate planes; interleave them to BGR directly into
        // the locked Bitmap bits. Staging through three managed byte[width*height] planes plus a row
        // buffer put three sensor-sized arrays on the LOH per call — and the full-res path
        // (PostFrameBitmap / CaptureFullRes) runs twice per hop of the auto centre-find.
        private static unsafe Bitmap FromRgb(HObject image)
        {
            HOperatorSet.GetImagePointer3(image, out HTuple r, out HTuple g, out HTuple b,
                out _, out HTuple w, out HTuple h);
            int width = w.I, height = h.I;

            var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                byte* pr = (byte*)r.IP, pg = (byte*)g.IP, pb = (byte*)b.IP;
                for (int y = 0; y < height; y++)
                {
                    byte* dst = (byte*)bd.Scan0 + (long)y * bd.Stride;
                    int o = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        int p = o + x, q = x * 3;
                        dst[q] = pb[p];      // B
                        dst[q + 1] = pg[p];  // G
                        dst[q + 2] = pr[p];  // R
                    }
                }
            }
            finally { bmp.UnlockBits(bd); }
            return bmp;
        }
    }
}
