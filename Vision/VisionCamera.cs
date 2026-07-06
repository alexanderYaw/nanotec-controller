using System;
using HalconDotNet;

namespace NanotecController
{
    /// <summary>
    /// Thin wrapper over the HALCON USB3Vision framegrabber: open once, grab frames on
    /// demand, close on dispose. Stage A of the vision integration — proves the camera
    /// path works inside the app before any edge-detection / centre-finding is layered on.
    ///
    /// Mirrors the HDevelop acquisition snippet (open_framegrabber → grab_image_start →
    /// grab_image_async). Async double-buffered grabbing: GrabImageStart primes the first
    /// acquisition; each GrabImageAsync returns the latest frame AND queues the next, so the
    /// camera streams free-running rather than start/stop per frame.
    /// </summary>
    public sealed class VisionCamera : IDisposable
    {
        private HTuple? _acq;

        public bool IsOpen => _acq != null;

        /// <summary>Which stream buffer mode actually took effect (shown in the live view).</summary>
        public string BufferMode { get; private set; } = "queued (driver default)";

        /// <summary>Camera-reported exposure / resulting frame rate, if it exposes them.</summary>
        public string CameraInfo { get; private set; } = "";

        /// <summary>
        /// Centred-ROI digital zoom: the sensor streams only the middle 1/Zoom of each axis.
        /// The frame centre stays the same physical point and the pixel size is unchanged, so
        /// the crosshair, pixel→step calibration, and edge detection remain valid at any zoom.
        /// </summary>
        public int Zoom { get; private set; } = 2;

        /// <summary>
        /// Changes the zoom by re-opening the framegrabber (ROI size can't change while the
        /// acquisition is streaming). Call from the thread that owns the grabbing — the feed
        /// blinks for the reopen. Throws like <see cref="Open"/> if the reopen fails.
        /// </summary>
        public void SetZoom(int factor)
        {
            factor = Math.Max(1, factor);
            if (factor == Zoom) return;
            Zoom = factor;
            if (_acq == null) return;
            try { HOperatorSet.CloseFramegrabber(_acq); }
            catch (HalconException) { /* reopening anyway */ }
            _acq = null;
            Open();
        }

        /// <summary>
        /// Opens the framegrabber and primes async acquisition. Throws
        /// <see cref="HOperatorException"/> if no camera is present / the interface fails.
        /// </summary>
        public void Open()
        {
            if (_acq != null) return;
            HOperatorSet.OpenFramegrabber("USB3Vision", 0, 0, 0, 0, 0, 0, "progressive", -1,
                "default", -1, "false", "default", "0", 0, -1, out HTuple acq);
            // Keep only the newest frame so the displayed image can't fall behind real time
            // (best-effort: not every driver exposes this GenICam param under this name).
            try
            {
                HOperatorSet.SetFramegrabberParam(acq, "[Stream]StreamBufferHandlingMode", "NewestOnly");
                BufferMode = "NewestOnly";
            }
            catch (HOperatorException)
            {
                try
                {
                    HOperatorSet.SetFramegrabberParam(acq, "StreamBufferHandlingMode", "NewestOnly");
                    BufferMode = "NewestOnly";
                }
                catch (HOperatorException) { /* unsupported on this transport/driver */ }
            }

            // Centred ROI (see Zoom): streaming fewer pixels lifts the USB-bandwidth fps cap
            // toward the sensor's own limit. Sizes rounded to multiples of 16 and offsets to
            // multiples of 4 to satisfy the camera's increment/Bayer-alignment constraints.
            // Offsets are zeroed FIRST so a shrunken previous ROI (params persist in the camera
            // until power-cycle) never blocks growing the window back. Best-effort.
            try
            {
                HOperatorSet.GetFramegrabberParam(acq, "WidthMax", out HTuple wmax);
                HOperatorSet.GetFramegrabberParam(acq, "HeightMax", out HTuple hmax);
                long w = (wmax.L / Zoom) & ~15L;
                long h = (hmax.L / Zoom) & ~15L;
                HOperatorSet.SetFramegrabberParam(acq, "OffsetX", 0);
                HOperatorSet.SetFramegrabberParam(acq, "OffsetY", 0);
                HOperatorSet.SetFramegrabberParam(acq, "Width", w);
                HOperatorSet.SetFramegrabberParam(acq, "Height", h);
                HOperatorSet.SetFramegrabberParam(acq, "OffsetX", ((wmax.L - w) / 2) & ~3L);
                HOperatorSet.SetFramegrabberParam(acq, "OffsetY", ((hmax.L - h) / 2) & ~3L);
            }
            catch (HalconException) { /* ROI unsupported; stream the full frame */ }

            // Full-res 8-bit Bayer frames are ~20 MB, so the camera's default 360 MB/s USB limit
            // caps the stream at 18 fps. Ask for more; the camera clamps to what the link allows.
            try { HOperatorSet.SetFramegrabberParam(acq, "DeviceLinkThroughputLimit", 440000000.0); }
            catch (HalconException) { /* camera clamps or rejects; keep its default */ }

            // What the camera thinks it can deliver (GenICam-standard names; best-effort).
            // HalconException (not just HOperatorException) because the returned tuple type
            // varies per camera — e.g. a boolean feature may come back as int or string, and
            // a wrong accessor throws HTupleAccessException.
            static string Text(HTuple t) => t.Type == HTupleType.STRING ? t.S : t.D.ToString("0.###");
            var info = new System.Text.StringBuilder();
            try { HOperatorSet.GetFramegrabberParam(acq, "ResultingFrameRate", out HTuple fr); info.Append($"cam {fr.D:0.0} fps"); }
            catch (HalconException) { }
            try { HOperatorSet.GetFramegrabberParam(acq, "ExposureTime", out HTuple exp); info.Append($"  exp {exp.D / 1000.0:0.0} ms"); }
            catch (HalconException) { }
            try
            {
                HOperatorSet.GetFramegrabberParam(acq, "Width", out HTuple iw);
                HOperatorSet.GetFramegrabberParam(acq, "Height", out HTuple ih);
                info.Append($"  {iw.I}x{ih.I}");
            }
            catch (HalconException) { }
            try { HOperatorSet.GetFramegrabberParam(acq, "PixelFormat", out HTuple pf); info.Append($" {Text(pf)}"); }
            catch (HalconException) { }
            CameraInfo = info.ToString();

            HOperatorSet.GrabImageStart(acq, -1);
            _acq = acq;
        }

        /// <summary>
        /// Grabs the latest frame and queues the next. The caller owns the returned HObject
        /// and must Dispose it.
        /// </summary>
        public HObject GrabImage()
        {
            if (_acq == null) throw new InvalidOperationException("Camera is not open.");
            // Reject frames older than 100 ms so the view can't trail real time even if the
            // NewestOnly buffer mode isn't supported by the driver.
            HOperatorSet.GrabImageAsync(out HObject image, _acq, 100);
            return image;
        }

        public void Dispose()
        {
            if (_acq == null) return;
            try { HOperatorSet.CloseFramegrabber(_acq); }
            catch (HOperatorException) { /* shutting down anyway */ }
            _acq = null;
        }
    }
}
