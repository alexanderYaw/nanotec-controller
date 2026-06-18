using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using HalconDotNet;

namespace MotorControlApp
{
    /// <summary>
    /// Stage A live-view test: opens the HALCON USB3Vision camera, streams frames into a
    /// live pane, and a "Capture Image" button freezes the current frame into a second pane.
    /// Pure camera proof-out — no motion, no edge detection yet. Frames are shown via
    /// <see cref="HalconBitmap"/> (PictureBox) because HALCON's WinForms control isn't usable
    /// under .NET 10 here.
    ///
    /// Smoothness: a background thread grabs AND converts frames; the UI thread only paints
    /// the newest finished frame (older ones are dropped). This keeps the blocking grab and
    /// the per-pixel conversion off the UI thread, and frames are downscaled to the view size
    /// before conversion — the two things that made the original timer-on-UI version laggy.
    /// </summary>
    public sealed class FrmVisionTest : Form
    {
        private readonly VisionCamera _camera = new();
        private readonly PictureBox _liveBox = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
        private readonly PictureBox _capturedBox = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
        private readonly Button _captureBtn = new() { Text = "Capture Image", Enabled = false };
        private readonly Button _saveBtn = new() { Text = "Save Image", Enabled = false };
        private readonly Label _status = new() { Text = "Opening camera...", AutoSize = true };

        private Task? _grabTask;
        private CancellationTokenSource? _cts;
        private readonly object _frameLock = new();
        private Bitmap? _pending;          // newest converted frame not yet shown
        private bool _displayQueued;        // a BeginInvoke(ShowPending) is already in flight
        private volatile int _viewW = 480, _viewH = 440;   // downscale target (live box size)

        public FrmVisionTest()
        {
            Text = "Vision - live view test";
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F);
            ClientSize = new Size(1000, 540);
            MinimumSize = new Size(720, 440);

            var liveLabel = new Label { Text = "Live", Location = new Point(12, 8), AutoSize = true, Font = new Font("Segoe UI", 10F, FontStyle.Bold) };
            var capLabel = new Label { Text = "Captured", Location = new Point(508, 8), AutoSize = true, Font = new Font("Segoe UI", 10F, FontStyle.Bold) };

            _liveBox.Location = new Point(12, 32);
            _liveBox.Size = new Size(480, 440);
            _liveBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
            _liveBox.Resize += (s, e) => { _viewW = Math.Max(1, _liveBox.Width); _viewH = Math.Max(1, _liveBox.Height); };

            _capturedBox.Location = new Point(508, 32);
            _capturedBox.Size = new Size(480, 440);
            _capturedBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Right;

            _captureBtn.Location = new Point(12, 484);
            _captureBtn.Size = new Size(140, 40);
            _captureBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _captureBtn.Click += (s, e) => CaptureFrame();

            _saveBtn.Location = new Point(160, 484);
            _saveBtn.Size = new Size(140, 40);
            _saveBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _saveBtn.Click += (s, e) => SaveCaptured();

            _status.Location = new Point(312, 496);
            _status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;

            Controls.Add(liveLabel);
            Controls.Add(capLabel);
            Controls.Add(_liveBox);
            Controls.Add(_capturedBox);
            Controls.Add(_captureBtn);
            Controls.Add(_saveBtn);
            Controls.Add(_status);

            Load += (s, e) => StartCamera();
            FormClosing += (s, e) => Teardown();
        }

        private void StartCamera()
        {
            try { _camera.Open(); }
            catch (HOperatorException ex) { _status.Text = "Camera open failed: " + ex.Message; return; }

            _viewW = Math.Max(1, _liveBox.Width);
            _viewH = Math.Max(1, _liveBox.Height);
            _captureBtn.Enabled = true;
            _status.Text = "Live.";
            _cts = new CancellationTokenSource();
            _grabTask = Task.Run(() => GrabLoop(_cts.Token));
        }

        // Background: grab + convert as fast as the camera allows, publishing only the newest
        // frame to the UI. Runs off the UI thread so neither the blocking grab nor the pixel
        // copy can stall input/painting.
        private void GrabLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HObject frame;
                try { frame = _camera.GrabImage(); }
                catch (HOperatorException ex) { PostStatus("Live grab stopped: " + ex.Message); return; }

                Bitmap bmp;
                try { bmp = HalconBitmap.ToBitmap(frame, _viewW, _viewH); }
                catch (HOperatorException) { frame.Dispose(); continue; }   // skip a bad frame
                finally { frame.Dispose(); }

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
            }
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

        private void CaptureFrame()
        {
            if (_liveBox.Image == null) return;
            var snap = new Bitmap(_liveBox.Image);   // independent copy of the current frame
            Image? old = _capturedBox.Image;
            _capturedBox.Image = snap;
            old?.Dispose();
            _saveBtn.Enabled = true;
            _status.Text = "Captured at " + DateTime.Now.ToString("HH:mm:ss") + ".";
        }

        // Saves the captured frame as a PNG under Desktop\images (created if missing).
        private void SaveCaptured()
        {
            if (_capturedBox.Image == null) { _status.Text = "Capture an image first."; return; }
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "images");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir,
                    "capture_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png");
                _capturedBox.Image.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                _status.Text = "Saved " + path;
            }
            catch (Exception ex)
            {
                _status.Text = "Save failed: " + ex.Message;
            }
        }

        private void PostStatus(string text)
        {
            try { BeginInvoke(new Action(() => _status.Text = text)); }
            catch (InvalidOperationException) { /* closing */ }
        }

        private void Teardown()
        {
            _cts?.Cancel();
            try { _grabTask?.Wait(500); } catch { /* best effort; camera streams so grab returns fast */ }
            _camera.Dispose();
            lock (_frameLock) { _pending?.Dispose(); _pending = null; }
            _liveBox.Image?.Dispose();
            _capturedBox.Image?.Dispose();
        }
    }
}
