using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using HalconDotNet;

namespace MotorControlApp
{
    /// <summary>
    /// Converts a HALCON image (HObject) to a System.Drawing.Bitmap so it can be shown in a
    /// plain WinForms PictureBox.
    ///
    /// Why not HWindowControl: only the .NET-Framework HALCON builds (dotnet20/dotnet35) are
    /// installed, and their HWindowControl derives from the Framework's System.Windows.Forms
    /// — not loadable in this .NET 10 WinForms app. The headless HObject/HOperatorSet types
    /// load fine, so we render through a Bitmap instead.
    /// </summary>
    public static class HalconBitmap
    {
        /// <summary>Converts the image to an 8-bit Bitmap (grayscale or 24-bit colour), full size.</summary>
        public static Bitmap ToBitmap(HObject image) => ToBitmap(image, 0, 0);

        /// <summary>
        /// Converts to an 8-bit Bitmap, first shrinking (in HALCON) to fit within
        /// <paramref name="maxWidth"/>×<paramref name="maxHeight"/> if larger. Downscaling
        /// natively before the managed pixel copy is the cheapest big win for live view on a
        /// large sensor. Pass 0,0 to keep full resolution.
        /// </summary>
        public static Bitmap ToBitmap(HObject image, int maxWidth, int maxHeight)
        {
            // Normalise to 8-bit so cameras delivering e.g. uint16 still display sensibly.
            HOperatorSet.ConvertImageType(image, out HObject img8, "byte");
            HObject work = img8;
            try
            {
                if (maxWidth > 0 && maxHeight > 0)
                {
                    HOperatorSet.GetImageSize(img8, out HTuple w, out HTuple h);
                    if (w.I > maxWidth || h.I > maxHeight)
                    {
                        double f = Math.Min((double)maxWidth / w.I, (double)maxHeight / h.I);
                        int nw = Math.Max(1, (int)(w.I * f)), nh = Math.Max(1, (int)(h.I * f));
                        HOperatorSet.ZoomImageSize(img8, out HObject scaled, nw, nh, "constant");
                        work = scaled;
                    }
                }
                HOperatorSet.CountChannels(work, out HTuple channels);
                return channels.I >= 3 ? FromRgb(work) : FromGray(work);
            }
            finally
            {
                if (!ReferenceEquals(work, img8)) work.Dispose();
                img8.Dispose();
            }
        }

        private static Bitmap FromGray(HObject image)
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
                // HALCON rows are contiguous (width bytes); the Bitmap stride is padded.
                var row = new byte[width];
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(IntPtr.Add(ptr.IP, y * width), row, 0, width);
                    Marshal.Copy(row, 0, IntPtr.Add(bd.Scan0, y * bd.Stride), width);
                }
            }
            finally { bmp.UnlockBits(bd); }
            return bmp;
        }

        private static Bitmap FromRgb(HObject image)
        {
            HOperatorSet.GetImagePointer3(image, out HTuple r, out HTuple g, out HTuple b,
                out _, out HTuple w, out HTuple h);
            int width = w.I, height = h.I, n = width * height;

            // HALCON stores the three channels as separate planes; interleave to BGR.
            var rr = new byte[n]; Marshal.Copy(r.IP, rr, 0, n);
            var gg = new byte[n]; Marshal.Copy(g.IP, gg, 0, n);
            var bb = new byte[n]; Marshal.Copy(b.IP, bb, 0, n);

            var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                var line = new byte[bd.Stride];
                for (int y = 0; y < height; y++)
                {
                    int o = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        int p = o + x, q = x * 3;
                        line[q] = bb[p];      // B
                        line[q + 1] = gg[p];  // G
                        line[q + 2] = rr[p];  // R
                    }
                    Marshal.Copy(line, 0, IntPtr.Add(bd.Scan0, y * bd.Stride), bd.Stride);
                }
            }
            finally { bmp.UnlockBits(bd); }
            return bmp;
        }
    }
}
