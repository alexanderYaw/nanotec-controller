using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using HalconDotNet;

namespace NanotecController
{
    /// <summary>
    /// Self-contained live-camera view: owns the <see cref="VisionCamera"/>, the grab thread,
    /// the newest-frame publish (older frames are dropped), the frame-job queue, and the
    /// crosshair/tick overlay. Extracted from FrmVision so the live view can be embedded in any
    /// window; the HOSTING FORM keeps the toolbar and the safety behaviour (Deactivate stops
    /// motion — a control can't tell a form-level focus loss from a click elsewhere on the form).
    ///
    /// Smoothness: a background thread grabs AND converts frames; the UI thread only paints the
    /// newest finished frame. Frames are downscaled to the view size before conversion — the two
    /// things that made the original timer-on-UI version laggy.
    /// </summary>
    public sealed class VisionViewControl : UserControl, IVisionFrameSource
    {
        /// <summary>Available centred-ROI digital zoom factors (see <see cref="VisionCamera.Zoom"/>).</summary>
        public static readonly int[] ZoomFactors = { 1, 2, 3, 5 };

        private readonly VisionCamera _camera = new();
        private readonly PictureBox _liveBox = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };

        // One-shot jobs to run against the next grabbed frame, on the grab thread. The UI (this
        // window or a protocols window via IVisionFrameSource) enqueues; GrabLoop drains them
        // while the frame is alive. Each job runs its own detection + bitmap conversion and
        // marshals to the UI thread itself (PostFrameBitmap).
        private readonly ConcurrentQueue<Action<HObject>> _frameJobs = new();

        private Task? _grabTask;
        private CancellationTokenSource? _cts;
        private readonly object _frameLock = new();
        private Bitmap? _pending;          // newest converted frame not yet shown
        private bool _displayQueued;       // a BeginInvoke(ShowPending) is already in flight
        private volatile int _viewW = 480, _viewH = 440;   // downscale target (live box size)

        // The UI picks a zoom factor; GrabLoop applies it between frames (the framegrabber must
        // be re-opened, and the grab thread owns the camera handle).
        private volatile int _zoomWanted = 2;
        private volatile bool _invertView = true;  // 180° flip; camera is mounted inverted
        private volatile bool _monoView;           // grey + full-range contrast stretch (display only)
        private bool _showCrosshair;

        // Actual grabbed frame dims: drive the aspect-correct live-box layout and the tick
        // scale. Never assume 3:2 — the zoom ROI rounds to multiples of 16, shifting the ratio.
        private volatile int _frameW, _frameH;

        public VisionViewControl()
        {
            BackColor = Color.Black;
            _liveBox.Paint += DrawOverlay;
            _liveBox.Resize += (s, e) => { _viewW = Math.Max(1, _liveBox.Width); _viewH = Math.Max(1, _liveBox.Height); };
            Controls.Add(_liveBox);
            Relayout();
        }

        /// <summary>Camera/stream status text (raised on the UI thread) — the host shows it.</summary>
        public event Action<string>? StatusChanged;

        /// <summary>Measured display rate + buffer mode (+ derived µm/px once both the pixel→step
        /// affine and steps/mm exist). Raised on the UI thread about once a second while live.</summary>
        public event Action<string>? FpsChanged;

        /// <summary>Raised after the camera opens or closes; read <see cref="IsCameraOpen"/>.</summary>
        public event Action? CameraStateChanged;

        /// <summary>Physical scale of one IMAGE pixel (mm along the column / row axes), or null
        /// until calibrated. Supplied lazily by the host so calibration edits apply live; drives
        /// the crosshair mm ticks and the µm/px readout. See <see cref="MmPerPixel"/>.</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Func<(double mmPerPxCol, double mmPerPxRow)?>? TickScaleProvider { get; set; }

        public bool IsCameraOpen => _camera.IsOpen;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool InvertView
        {
            get => _invertView;
            set => _invertView = value;
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool MonoView
        {
            get => _monoView;
            set => _monoView = value;
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool ShowCrosshair
        {
            get => _showCrosshair;
            set { _showCrosshair = value; _liveBox.Invalidate(); }
        }

        /// <summary>Centred-ROI digital zoom factor; applied by the grab thread between frames.</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int ZoomFactor
        {
            get => _zoomWanted;
            set => _zoomWanted = value;
        }

        /// <summary>
        /// Opens the camera and starts the grab thread. Idempotent. On failure raises
        /// <see cref="StatusChanged"/> and leaves <see cref="IsCameraOpen"/> false — the host
        /// decides whether that blocks anything (motion must keep working without a camera).
        /// </summary>
        public void StartCamera()
        {
            if (_camera.IsOpen) return;
            try { _camera.Open(); }
            catch (HOperatorException ex)
            {
                StatusChanged?.Invoke("Camera open failed: " + ex.Message);
                return;
            }
            _viewW = Math.Max(1, _liveBox.Width);
            _viewH = Math.Max(1, _liveBox.Height);
            StatusChanged?.Invoke("Live.");
            _cts = new CancellationTokenSource();
            _grabTask = Task.Run(() => GrabLoop(_cts.Token));
            CameraStateChanged?.Invoke();
        }

        /// <summary>
        /// Stops the grab thread and closes the camera. Idempotent; safe to call with the camera
        /// already closed. Hosts call this from FormClosing; <see cref="Dispose"/> is a backstop.
        /// </summary>
        public void StopCamera()
        {
            bool wasOpen = _camera.IsOpen;
            _cts?.Cancel();
            try { _grabTask?.Wait(500); } catch { /* best effort; camera streams so grab returns fast */ }
            _cts?.Dispose();
            _cts = null;
            _grabTask = null;
            _camera.Dispose();
            lock (_frameLock) { _pending?.Dispose(); _pending = null; }
            if (!IsDisposed)
            {
                _liveBox.Image?.Dispose();
                _liveBox.Image = null;
            }
            if (wasOpen) CameraStateChanged?.Invoke();
        }

        // --- IVisionFrameSource ------------------------------------------------------

        public void RequestFrame(Action<HObject> job) => _frameJobs.Enqueue(job);

        // Grab-thread helper: convert the live frame to a full-res bitmap (optionally 180°-flipped),
        // then hand it to a UI callback that takes ownership. Skips silently if the frame can't be
        // converted (user can retry); disposes the bitmap if the control is already closing.
        public void PostFrameBitmap(HObject frame, bool flip, Action<Bitmap> onUi)
        {
            Bitmap bmp;
            try { bmp = HalconBitmap.ToBitmap(frame, 0, 0); }
            catch (HOperatorException) { return; }   // skip; user can retry
            if (flip) bmp.RotateFlip(RotateFlipType.Rotate180FlipNone);
            try { BeginInvoke(new Action(() => onUi(bmp))); }
            catch (InvalidOperationException) { bmp.Dispose(); }   // closing
        }

        public void CaptureFullRes(Action<Bitmap> onUi)
        {
            if (!_camera.IsOpen) return;
            RequestFrame(frame => PostFrameBitmap(frame, _invertView, onUi));
        }

        // --- grab loop ---------------------------------------------------------------

        // Background: grab + convert as fast as the camera allows, publishing only the newest
        // frame to the UI. Runs off the UI thread so neither the blocking grab nor the pixel
        // copy can stall input/painting.
        private void GrabLoop(CancellationToken ct)
        {
            // Measured delivery rate — distinguishes a slow camera (exposure/frame-rate config)
            // from a slow conversion pipeline.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int shown = 0;

            while (!ct.IsCancellationRequested)
            {
                // Apply a pending zoom change here, between grabs — the grab thread owns the
                // camera handle, and the ROI can only change across a framegrabber reopen.
                int zoom = _zoomWanted;
                if (zoom != _camera.Zoom)
                {
                    try { _camera.SetZoom(zoom); }
                    catch (HalconException ex) { PostStatus("Zoom change failed: " + ex.Message); return; }
                    PostStatus($"Live ({zoom}x).");
                    continue;
                }

                HObject frame;
                try { frame = _camera.GrabImage(); }
                catch (HOperatorException ex) { PostStatus("Live grab stopped: " + ex.Message); return; }

                // Track the ACTUAL frame dims so the live box stays aspect-correct and the tick
                // math uses the true scale (changes on every zoom-ROI reopen).
                try
                {
                    HOperatorSet.GetImageSize(frame, out HTuple fw, out HTuple fh);
                    if (fw.I != _frameW || fh.I != _frameH)
                    {
                        _frameW = fw.I;
                        _frameH = fh.I;
                        try { BeginInvoke(new Action(Relayout)); }
                        catch (InvalidOperationException) { frame.Dispose(); return; }   // handle gone (closing)
                    }
                }
                catch (HOperatorException) { /* keep streaming even if the size read fails */ }

                // Run any queued one-shot jobs (capture / calibration sample / chuck or wafer edge)
                // against THIS frame, on the grab thread, so the HALCON acquisition handle stays
                // single-threaded. A job that throws is skipped (the user can retry).
                while (_frameJobs.TryDequeue(out Action<HObject>? job))
                {
                    try { job(frame); }
                    catch (HOperatorException) { /* detection failed on this frame; user can retry */ }
                }

                Bitmap bmp;
                try { bmp = HalconBitmap.ToBitmap(frame, _viewW, _viewH, _monoView); }
                catch (HOperatorException) { frame.Dispose(); continue; }   // skip a bad frame
                finally { frame.Dispose(); }

                // Camera is mounted inverted: flip on both axes (180°) so the view is upright.
                if (_invertView) bmp.RotateFlip(RotateFlipType.Rotate180FlipNone);

                Bitmap? dropped;
                bool queue;
                lock (_frameLock)
                {
                    dropped = _pending;        // UI never picked this up -> discard it
                    _pending = bmp;
                    queue = !_displayQueued;
                    if (queue) _displayQueued = true;
                }
                dropped?.Dispose();

                if (queue)
                {
                    try { BeginInvoke(new Action(ShowPending)); }
                    catch (InvalidOperationException) { return; }   // handle gone (closing)
                }

                shown++;
                if (sw.ElapsedMilliseconds >= 1000)
                {
                    double fps = shown * 1000.0 / sw.ElapsedMilliseconds;
                    sw.Restart(); shown = 0;
                    try { BeginInvoke(new Action(() => RaiseFps(fps))); }
                    catch (InvalidOperationException) { return; }   // handle gone (closing)
                }
            }
        }

        // UI thread: fps + buffer mode, plus the derived pixel size when calibrated — the live
        // check that the user-entered steps/mm is plausible (risk: a wrong entry skews the ticks).
        private void RaiseFps(double fps)
        {
            string text = $"{fps:0.0} fps — {_camera.BufferMode}";
            (double mmPerPxCol, double mmPerPxRow)? mm = TickScaleProvider?.Invoke();
            if (mm != null)
                text += $" — {(mm.Value.mmPerPxCol + mm.Value.mmPerPxRow) * 500.0:0.00} µm/px";
            FpsChanged?.Invoke(text);
        }

        private void ShowPending()
        {
            Bitmap? next;
            lock (_frameLock) { next = _pending; _pending = null; _displayQueued = false; }
            if (next == null) return;
            if (IsDisposed) { next.Dispose(); return; }

            Image? old = _liveBox.Image;
            _liveBox.Image = next;
            if (!ReferenceEquals(old, next)) old?.Dispose();
        }

        private void PostStatus(string text)
        {
            try { BeginInvoke(new Action(() => StatusChanged?.Invoke(text))); }
            catch (InvalidOperationException) { /* closing */ }
        }

        // --- layout ------------------------------------------------------------------

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Relayout();
        }

        // Sizes the live box to the largest rectangle of the ACTUAL frame's aspect ratio that
        // fits the client area (centred), so the image always fills its box exactly — the
        // crosshair centre and the tick scale then follow from the box dimensions alone.
        private void Relayout()
        {
            int fw = _frameW, fh = _frameH;
            Rectangle r;
            if (fw <= 0 || fh <= 0 || ClientSize.Width <= 0 || ClientSize.Height <= 0)
            {
                r = ClientRectangle;   // no frame yet: fill (black) until the first frame arrives
            }
            else
            {
                double s = Math.Min((double)ClientSize.Width / fw, (double)ClientSize.Height / fh);
                int w = Math.Max(1, (int)(fw * s));
                int h = Math.Max(1, (int)(fh * s));
                r = new Rectangle((ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h);
            }
            if (_liveBox.Bounds != r) _liveBox.Bounds = r;
        }

        // --- crosshair + mm ticks ------------------------------------------------------

        // Overlays a crosshair through the centre of the live pane. The live box is sized to the
        // frame's aspect ratio, so box-centre = frame geometric centre (and the centred-ROI zoom
        // keeps that the same physical point at any zoom). This is a visual reference only — NOT
        // the calibrated optical/Theta centre. When the physical scale is known, mm tick marks
        // are drawn along both arms; centre-symmetric, so the 180° display flip doesn't move them.
        private void DrawOverlay(object? sender, PaintEventArgs e)
        {
            if (!_showCrosshair) return;
            int cw = _liveBox.ClientSize.Width, ch = _liveBox.ClientSize.Height;
            int cx = cw / 2, cy = ch / 2;
            using var pen = new Pen(Color.Lime, 1f);
            e.Graphics.DrawLine(pen, cx, 0, cx, ch);
            e.Graphics.DrawLine(pen, 0, cy, cw, cy);

            (double mmPerPxCol, double mmPerPxRow)? mm = TickScaleProvider?.Invoke();
            int fw = _frameW, fh = _frameH;
            if (mm == null || fw <= 0 || fh <= 0 || cw <= 0 || ch <= 0) return;

            // mm per SCREEN pixel = mm per image pixel ÷ displayed-pixels-per-image-pixel.
            // Zoom doesn't enter: the centred ROI leaves the image pixel size unchanged, only
            // the screen scale (box px / frame px) changes.
            DrawTicks(e.Graphics, pen, horizontal: true, cx, cy, cw, mm.Value.mmPerPxCol * fw / cw);
            DrawTicks(e.Graphics, pen, horizontal: false, cx, cy, ch, mm.Value.mmPerPxRow * fh / ch);
        }

        // Ticks at a {1,2,5}×10ⁿ mm pitch chosen so majors sit ≥ ~45 screen px apart; minor
        // ticks at pitch/5 when they'd be ≥ 9 px apart.
        private static void DrawTicks(Graphics g, Pen pen, bool horizontal, int cx, int cy, int extent, double mmPerScreenPx)
        {
            if (!(mmPerScreenPx > 0) || double.IsInfinity(mmPerScreenPx)) return;
            double pitchPx = TickPitchMm(45.0 * mmPerScreenPx) / mmPerScreenPx;
            bool minors = pitchPx / 5.0 >= 9.0;
            double step = minors ? pitchPx / 5.0 : pitchPx;
            int perMajor = minors ? 5 : 1;

            for (int k = 1; k * step <= extent / 2.0; k++)
            {
                int len = k % perMajor == 0 ? 8 : 4;
                int off = (int)Math.Round(k * step);
                if (horizontal)
                {
                    g.DrawLine(pen, cx + off, cy - len, cx + off, cy + len);
                    g.DrawLine(pen, cx - off, cy - len, cx - off, cy + len);
                }
                else
                {
                    g.DrawLine(pen, cx - len, cy + off, cx + len, cy + off);
                    g.DrawLine(pen, cx - len, cy - off, cx + len, cy - off);
                }
            }
        }

        // Smallest {1, 2, 5}×10ⁿ mm ≥ minMm.
        private static double TickPitchMm(double minMm)
        {
            double p = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(minMm, 1e-9))));
            foreach (double m in new[] { 1.0, 2.0, 5.0 })
                if (m * p >= minMm) return m * p;
            return 10.0 * p;
        }

        /// <summary>
        /// Physical size of one image pixel from the pixel→step affine and the per-axis steps/mm:
        /// one pixel of column displacement moves the stage (Xc, Yc) steps = (Xc/kX, Yc/kY) mm, so
        /// mm-per-pixel along columns = √((Xc/kX)² + (Yc/kY)²); likewise per row with Xr/Yr.
        /// Null until both the affine and X+Y steps/mm exist.
        /// </summary>
        public static (double mmPerPxCol, double mmPerPxRow)? MmPerPixel(CalibrationStore calib)
        {
            PixelStepAffine? a = calib.PixelStep;
            double? kx = calib.For(AxisId.X).StepsPerMm;
            double? ky = calib.For(AxisId.Y).StepsPerMm;
            if (a == null || kx is not > 0 || ky is not > 0) return null;
            static double Sq(double v) => v * v;
            double col = Math.Sqrt(Sq(a.Xc / kx.Value) + Sq(a.Yc / ky.Value));
            double row = Math.Sqrt(Sq(a.Xr / kx.Value) + Sq(a.Yr / ky.Value));
            return (col, row);
        }

        // Backstop only — hosts stop the camera from FormClosing (StopCamera), where the
        // teardown ORDER relative to motion shutdown is theirs to control.
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _cts?.Cancel();
                try { _grabTask?.Wait(500); } catch { /* best effort */ }
                _camera.Dispose();
                lock (_frameLock) { _pending?.Dispose(); _pending = null; }
                _liveBox.Image?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
