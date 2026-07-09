using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        public static readonly int[] ZoomFactors = { 1, 2, 3, 5, 7, 10 };

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

        // Measurement ruler: endpoints stored as FRACTIONS of the live box [0..1] so they track the
        // image across resizes / aspect re-layouts; the length is derived from the current screen→mm
        // scale each paint, so digital zoom is accounted for automatically.
        private bool _measureOn;
        private PointF _measA = new(0.35f, 0.5f), _measB = new(0.65f, 0.5f);
        private int _measDrag;                                 // 0 none · 1 end A · 2 end B · 3 whole-line
        private PointF _measGrab, _measAAtGrab, _measBAtGrab;  // whole-line drag anchors

        // Actual grabbed frame dims: drive the aspect-correct live-box layout and the tick
        // scale. Never assume 3:2 — the zoom ROI rounds to multiples of 16, shifting the ratio.
        private volatile int _frameW, _frameH;

        public VisionViewControl()
        {
            BackColor = Color.Black;
            _liveBox.Paint += DrawOverlay;
            _liveBox.MouseDown += MeasureMouseDown;
            _liveBox.MouseMove += MeasureMouseMove;
            _liveBox.MouseUp += MeasureMouseUp;
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

        /// <summary>Raised (UI thread) with each newly-displayed frame, so a second view can mirror
        /// the live feed. The bitmap is owned by this control and only valid for the duration of the
        /// handler — a follower must clone it (see <see cref="PushFrame"/>). Not raised when there are
        /// no subscribers, so the mirror costs nothing while the protocols window is closed.</summary>
        public event Action<Bitmap>? FrameDisplayed;

        /// <summary>False for a camera-less "follower" view that only mirrors another control's frames
        /// (via <see cref="PushFrame"/>). A follower never opens the exclusive framegrabber.</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool OwnsCamera { get; init; } = true;

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

        /// <summary>Toggles the interactive measurement ruler. Turning it on (re)spawns a default
        /// horizontal segment across the middle of the pane; drag either end to re-aim it or the
        /// middle to slide it. The live length readout accounts for the current digital zoom.</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool MeasureEnabled
        {
            get => _measureOn;
            set
            {
                if (_measureOn == value) return;
                _measureOn = value;
                if (value) { _measA = new PointF(0.35f, 0.5f); _measB = new PointF(0.65f, 0.5f); }
                _measDrag = 0;
                _liveBox.Cursor = Cursors.Default;
                _liveBox.Invalidate();
            }
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
            if (!OwnsCamera || _camera.IsOpen) return;   // followers never open the exclusive camera
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

            // Mirror the frame to any follower view (protocols window). Runs synchronously here on
            // the UI thread while `next` is alive; the follower clones it. Skipped when unsubscribed.
            FrameDisplayed?.Invoke(next);
        }

        /// <summary>
        /// Follower entry point: displays a clone of a frame pushed from the primary control's
        /// <see cref="FrameDisplayed"/>. Aspect-correct like the live view; draw a crosshair with
        /// <see cref="ShowCrosshair"/>. No-op unless this is a follower (see <see cref="OwnsCamera"/>).
        /// </summary>
        public void PushFrame(Bitmap src)
        {
            if (OwnsCamera || IsDisposed) return;
            Bitmap clone;
            try { clone = (Bitmap)src.Clone(); }
            catch { return; }   // source being torn down; skip this frame

            if (clone.Width != _frameW || clone.Height != _frameH)
            {
                _frameW = clone.Width;
                _frameH = clone.Height;
                Relayout();
            }
            Image? old = _liveBox.Image;
            _liveBox.Image = clone;
            if (!ReferenceEquals(old, clone)) old?.Dispose();
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
            if (_showCrosshair) DrawCrosshair(e);
            if (_measureOn) DrawMeasure(e);
        }

        private void DrawCrosshair(PaintEventArgs e)
        {
            int cw = _liveBox.ClientSize.Width, ch = _liveBox.ClientSize.Height;
            int cx = cw / 2, cy = ch / 2;
            // The overlay is drawn in SCREEN pixels (constant across zoom). The measurement ticks are
            // bumped WITH the zoom factor so they don't look thin against the magnified image at 3x/5x.
            // The crosshair arms go the other way — slightly THINNER as zoom rises (floored at 1 px) —
            // so the full-length arms don't dominate the finer view when magnified.
            float zoom = Math.Max(1, _zoomWanted);
            using var crossPen = new Pen(CrosshairGreen, Math.Max(1f, 2f - 0.2f * (zoom - 1f)));
            using var tickPen = new Pen(CrosshairGreen, 1.5f + 0.5f * (zoom - 1f));
            e.Graphics.DrawLine(crossPen, cx, 0, cx, ch);
            e.Graphics.DrawLine(crossPen, 0, cy, cw, cy);

            (double mmPerPxCol, double mmPerPxRow)? mm = TickScaleProvider?.Invoke();
            int fw = _frameW, fh = _frameH;
            if (mm == null || fw <= 0 || fh <= 0 || cw <= 0 || ch <= 0) return;

            // mm per SCREEN pixel = mm per image pixel ÷ displayed-pixels-per-image-pixel.
            // Zoom doesn't enter: the centred ROI leaves the image pixel size unchanged, only
            // the screen scale (box px / frame px) changes.
            Font labelFont = TickFontFor(_zoomWanted);
            DrawMmTicks(e.Graphics, tickPen, labelFont, horizontal: true, cx, cy, cw, mm.Value.mmPerPxCol * fw / cw);
            DrawMmTicks(e.Graphics, tickPen, labelFont, horizontal: false, cx, cy, ch, mm.Value.mmPerPxRow * fh / ch);
        }

        // Crosshair / tick colour and a matching label brush, cached (DrawOverlay repaints per frame).
        private static readonly Color CrosshairGreen = Color.Lime;
        private static readonly Brush TickBrush = new SolidBrush(CrosshairGreen);

        // Label font, larger at higher magnification so the mm readings stay legible against the
        // zoomed image. Cached per zoom level (only a few values) so paint doesn't allocate a font.
        private static readonly Dictionary<int, Font> _tickFonts = new();
        private static Font TickFontFor(int zoom)
        {
            if (!_tickFonts.TryGetValue(zoom, out Font? f))
            {
                f = new Font("Segoe UI", 7f + 1.5f * (Math.Max(1, zoom) - 1));   // 1x:7, 2x:8.5, 3x:10, 5x:13
                _tickFonts[zoom] = f;
            }
            return f;
        }

        // Tick half-lengths (px), shortest → longest: 0.01 mm, 0.1 mm, 0.5 mm, 1 mm, 5 mm, 10 mm.
        private const int TICK_HUNDREDTH = 2, TICK_TENTH = 3, TICK_HALF = 6, TICK_MM = 10, TICK_5MM = 14, TICK_10MM = 18;

        // Markings along a crosshair arm at a FIXED 1 mm pitch: a long tick every 1 mm, longer every
        // 5 mm, longest + a distance label every 10 mm (and the first 1 mm is labelled to anchor the
        // scale). Between the centre and the first mm, 0.1 mm sub-ticks are added when zoomed in far
        // enough to resolve them, with 0.5 mm drawn a little longer. If 1 mm is too few screen px to
        // resolve (zoomed out), the mm ticks thin to 5 mm — then 10 mm — so they don't smear into a
        // bar. Zoom does not enter mmPerScreenPx (centred-ROI zoom leaves the image pixel unchanged).
        private static void DrawMmTicks(Graphics g, Pen pen, Font labelFont, bool horizontal, int cx, int cy, int extent, double mmPerScreenPx)
        {
            if (!(mmPerScreenPx > 0) || double.IsInfinity(mmPerScreenPx)) return;
            double pxPerMm = 1.0 / mmPerScreenPx;
            double half = extent / 2.0;

            // One tick of half-length len at signed pixel offset off from centre, on BOTH sides.
            void Tick(Pen p, double offPx, int len)
            {
                int off = (int)Math.Round(offPx);
                if (horizontal)
                {
                    g.DrawLine(p, cx + off, cy - len, cx + off, cy + len);
                    g.DrawLine(p, cx - off, cy - len, cx - off, cy + len);
                }
                else
                {
                    g.DrawLine(p, cx - len, cy + off, cx + len, cy + off);
                    g.DrawLine(p, cx - len, cy - off, cx + len, cy - off);
                }
            }

            // A distance label just past a tick of half-length len, on both sides.
            void Label(int d, double offPx, int len)
            {
                string s = d.ToString();
                int off = (int)Math.Round(offPx);
                int vpad = labelFont.Height / 2;   // vertically centre the label on the arm
                if (horizontal)
                {
                    g.DrawString(s, labelFont, TickBrush, cx + off + 1, cy + len);
                    g.DrawString(s, labelFont, TickBrush, cx - off + 1, cy + len);
                }
                else
                {
                    g.DrawString(s, labelFont, TickBrush, cx + len + 1, cy + off - vpad);
                    g.DrawString(s, labelFont, TickBrush, cx + len + 1, cy - off - vpad);
                }
            }

            // 0.1 mm sub-ticks between the centre and the first mm (0.5 mm a little longer), only
            // when a tenth is at least a few screen px so they don't merge.
            double pxPerTenth = pxPerMm / 10.0;
            if (pxPerTenth >= 3.0)
                for (int t = 1; t <= 9; t++)
                    if (t * pxPerTenth <= half) Tick(pen, t * pxPerTenth, t == 5 ? TICK_HALF : TICK_TENTH);

            // 0.01 mm sub-ticks between the centre and the first 0.1 mm, only at very high zoom (a
            // hundredth at least a few screen px). Drawn with a 1 px pen so the closely-spaced ticks
            // stay separate even where the mm/tenth pen is thickened at high zoom.
            double pxPerHundredth = pxPerMm / 100.0;
            if (pxPerHundredth >= 3.0)
            {
                using var finePen = new Pen(pen.Color, 1f);
                for (int t = 1; t <= 9; t++)
                    if (t * pxPerHundredth <= half) Tick(finePen, t * pxPerHundredth, TICK_HUNDREDTH);
            }

            // Millimetre ticks (thinned when 1 mm is too dense to resolve).
            int everyMm = pxPerMm >= 4.0 ? 1 : (pxPerMm * 5.0 >= 4.0 ? 5 : 10);
            bool label10 = pxPerMm * 10.0 >= 22.0;
            for (int d = everyMm; d * pxPerMm <= half; d += everyMm)
            {
                int len = d % 10 == 0 ? TICK_10MM : (d % 5 == 0 ? TICK_5MM : TICK_MM);
                Tick(pen, d * pxPerMm, len);
                if (d == 1 && pxPerMm >= 16.0) Label(1, pxPerMm, len);           // anchor the scale
                else if (d % 10 == 0 && label10) Label(d, d * pxPerMm, len);
            }
        }

        // --- measurement tool ----------------------------------------------------------

        private static readonly Color MeasureColor = Color.Cyan;
        private static readonly Font MeasureFont = new("Segoe UI", 9f, FontStyle.Bold);
        private static readonly Brush MeasureLabelBrush = new SolidBrush(MeasureColor);
        private static readonly Brush MeasureLabelBack = new SolidBrush(Color.FromArgb(170, 0, 0, 0));
        private static readonly Brush MeasureHandleFill = new SolidBrush(Color.FromArgb(90, 0, 0, 0));
        private const int MEAS_HIT = 12;   // px pick radius for an endpoint / the line body

        // Draws the ruler in SCREEN pixels (endpoints are box fractions), with a live length label
        // centred above the segment. The scale used for the label is identical to the crosshair
        // ticks (see MeasureText), so the two always agree at any zoom.
        private void DrawMeasure(PaintEventArgs e)
        {
            int cw = _liveBox.ClientSize.Width, ch = _liveBox.ClientSize.Height;
            if (cw <= 0 || ch <= 0) return;
            (PointF a, PointF b) = MeasureEndpointsPx();

            Graphics g = e.Graphics;
            System.Drawing.Drawing2D.SmoothingMode prev = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var pen = new Pen(MeasureColor, 2f))
            {
                g.DrawLine(pen, a, b);
                DrawHandle(g, pen, a);
                DrawHandle(g, pen, b);
            }

            string label = MeasureText(a, b);
            SizeF sz = g.MeasureString(label, MeasureFont);
            float mx = (a.X + b.X) / 2f, my = (a.Y + b.Y) / 2f;
            float lx = Math.Clamp(mx - sz.Width / 2f, 1f, Math.Max(1f, cw - sz.Width - 1f));
            float ly = my - sz.Height - 8f;
            if (ly < 1f) ly = my + 8f;   // flip below the line if it would clip the top edge
            g.FillRectangle(MeasureLabelBack, lx - 3f, ly - 1f, sz.Width + 6f, sz.Height + 2f);
            g.DrawString(label, MeasureFont, MeasureLabelBrush, lx, ly);
            g.SmoothingMode = prev;
        }

        private static void DrawHandle(Graphics g, Pen pen, PointF p)
        {
            const float r = 5f;
            g.FillEllipse(MeasureHandleFill, p.X - r, p.Y - r, 2 * r, 2 * r);
            g.DrawEllipse(pen, p.X - r, p.Y - r, 2 * r, 2 * r);
        }

        // Segment length as a display string in the calibrated frame; falls back to screen px until
        // the pixel scale is known. mm per SCREEN pixel = mm per image pixel × (image px / screen px)
        // per axis — the centred-ROI zoom shrinks the frame (fw/fh) while the box stays, so a more
        // magnified view yields fewer mm per screen pixel with no special-casing.
        private string MeasureText(PointF aPx, PointF bPx)
        {
            float dxPx = bPx.X - aPx.X, dyPx = bPx.Y - aPx.Y;
            (double mmPerPxCol, double mmPerPxRow)? mm = TickScaleProvider?.Invoke();
            int fw = _frameW, fh = _frameH, cw = _liveBox.ClientSize.Width, ch = _liveBox.ClientSize.Height;
            if (mm == null || fw <= 0 || fh <= 0 || cw <= 0 || ch <= 0)
                return $"{Math.Sqrt(dxPx * dxPx + dyPx * dyPx):0} px";

            double dxMm = dxPx * mm.Value.mmPerPxCol * fw / cw;
            double dyMm = dyPx * mm.Value.mmPerPxRow * fh / ch;
            double lenMm = Math.Sqrt(dxMm * dxMm + dyMm * dyMm);
            return lenMm >= 1.0 ? $"{lenMm:0.000} mm" : $"{lenMm * 1000.0:0.0} µm";
        }

        // --- measurement interaction ---------------------------------------------------

        private void MeasureMouseDown(object? sender, MouseEventArgs e)
        {
            if (!_measureOn || e.Button != MouseButtons.Left) return;
            (PointF a, PointF b) = MeasureEndpointsPx();
            if (Dist(e.Location, a) <= MEAS_HIT) _measDrag = 1;
            else if (Dist(e.Location, b) <= MEAS_HIT) _measDrag = 2;
            else if (DistToSegment(e.Location, a, b) <= MEAS_HIT)
            {
                _measDrag = 3;
                _measGrab = FracOf(e.Location);
                _measAAtGrab = _measA; _measBAtGrab = _measB;
            }
            else { _measDrag = 0; return; }
            _liveBox.Capture = true;
        }

        private void MeasureMouseMove(object? sender, MouseEventArgs e)
        {
            if (!_measureOn) return;
            if (_measDrag == 0)   // idle: just reflect what's under the cursor
            {
                (PointF a, PointF b) = MeasureEndpointsPx();
                bool onEnd = Dist(e.Location, a) <= MEAS_HIT || Dist(e.Location, b) <= MEAS_HIT;
                _liveBox.Cursor = onEnd ? Cursors.Cross
                                : DistToSegment(e.Location, a, b) <= MEAS_HIT ? Cursors.SizeAll
                                : Cursors.Default;
                return;
            }
            PointF f = FracOf(e.Location);
            if (_measDrag == 1) _measA = Clamp01(f);
            else if (_measDrag == 2) _measB = Clamp01(f);
            else   // rigid whole-line slide: clamp the delta so both ends stay in view
            {
                float dx = ClampDelta(f.X - _measGrab.X, _measAAtGrab.X, _measBAtGrab.X);
                float dy = ClampDelta(f.Y - _measGrab.Y, _measAAtGrab.Y, _measBAtGrab.Y);
                _measA = new PointF(_measAAtGrab.X + dx, _measAAtGrab.Y + dy);
                _measB = new PointF(_measBAtGrab.X + dx, _measBAtGrab.Y + dy);
            }
            _liveBox.Invalidate();
        }

        private void MeasureMouseUp(object? sender, MouseEventArgs e)
        {
            if (_measDrag == 0) return;
            _measDrag = 0;
            _liveBox.Capture = false;
            _liveBox.Invalidate();
        }

        private (PointF a, PointF b) MeasureEndpointsPx()
        {
            int cw = _liveBox.ClientSize.Width, ch = _liveBox.ClientSize.Height;
            return (new PointF(_measA.X * cw, _measA.Y * ch), new PointF(_measB.X * cw, _measB.Y * ch));
        }

        private PointF FracOf(Point p)
        {
            int cw = Math.Max(1, _liveBox.ClientSize.Width), ch = Math.Max(1, _liveBox.ClientSize.Height);
            return new PointF((float)p.X / cw, (float)p.Y / ch);
        }

        private static PointF Clamp01(PointF p)
            => new(Math.Clamp(p.X, 0f, 1f), Math.Clamp(p.Y, 0f, 1f));

        // Largest delta keeping both fractional coords p,q inside [0,1] (rigid whole-line move).
        private static float ClampDelta(float d, float p, float q)
        {
            float lo = Math.Min(p, q), hi = Math.Max(p, q);
            if (lo + d < 0f) d = -lo;
            if (hi + d > 1f) d = 1f - hi;
            return d;
        }

        private static float Dist(PointF a, PointF b)
        {
            float dx = a.X - b.X, dy = a.Y - b.Y;
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        private static float DistToSegment(PointF p, PointF a, PointF b)
        {
            float vx = b.X - a.X, vy = b.Y - a.Y;
            float len2 = vx * vx + vy * vy;
            if (len2 <= 0.0001f) return Dist(p, a);
            float t = Math.Clamp(((p.X - a.X) * vx + (p.Y - a.Y) * vy) / len2, 0f, 1f);
            return Dist(p, new PointF(a.X + t * vx, a.Y + t * vy));
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
