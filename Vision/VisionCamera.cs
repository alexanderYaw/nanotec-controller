using System;
using HalconDotNet;

namespace MotorControlApp
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
            try { HOperatorSet.SetFramegrabberParam(acq, "StreamBufferHandlingMode", "NewestOnly"); }
            catch (HOperatorException) { /* unsupported on this transport/driver */ }
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
            HOperatorSet.GrabImageAsync(out HObject image, _acq, -1);
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
