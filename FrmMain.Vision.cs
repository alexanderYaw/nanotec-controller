using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace NanotecController
{
    // FrmMain — vision (camera) integration. The live view now lives on the MAIN screen (right
    // column), owned here: FrmMain opens/closes the camera and owns the VisionViewControl, and
    // hands that control (as IVisionFrameSource) to the on-demand protocols window. The camera is
    // independent of the drives — a camera-open failure must never block motion. (Partial of FrmMain.)
    public partial class FrmMain
    {
        private readonly VisionViewControl _visionView = new();
        private ComboBox _visionZoom = null!;
        private Button _crosshairBtn = null!;
        private Button _invertBtn = null!;
        private Button _monoBtn = null!;
        private Button _measureBtn = null!;
        private Button _captureBtn = null!;
        private Button _saveBtn = null!;
        private Button _retryCameraBtn = null!;
        private Label _fpsLabel = null!;
        private PictureBox _previewBox = null!;
        private Bitmap? _lastCapture;   // full-res; shown scaled in the preview, saved at full res

        /// <summary>The shared live camera, handed to the protocols window (which owns no camera).</summary>
        public IVisionFrameSource VisionSource => _visionView;

        // Builds the right-column vision cluster into visionHostPanel: a top toolbar
        // (zoom / crosshair / invert / mono / capture / save / fps), the live view filling the
        // middle, and a bottom strip with the quick-capture preview + a Retry button.
        private void BuildVisionColumn()
        {
            visionHostPanel.Controls.Remove(visionPlaceholder);

            // --- top toolbar ---
            var toolbar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = SystemColors.Control };
            toolbar.Controls.Add(new Label { Text = "Zoom:", Location = new Point(4, 12), AutoSize = true });
            _visionZoom = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(52, 8), Size = new Size(56, 24), Enabled = false };
            foreach (int z in VisionViewControl.ZoomFactors) _visionZoom.Items.Add($"{z}x");
            _visionZoom.SelectedIndex = Array.IndexOf(VisionViewControl.ZoomFactors, _visionView.ZoomFactor);
            _visionZoom.SelectedIndexChanged += (s, e) =>
            {
                if (_visionZoom.SelectedIndex >= 0) _visionView.ZoomFactor = VisionViewControl.ZoomFactors[_visionZoom.SelectedIndex];
            };
            toolbar.Controls.Add(_visionZoom);

            _crosshairBtn = ToolbarButton("Crosshair", 112, 74);
            _crosshairBtn.Click += (s, e) =>
            {
                _visionView.ShowCrosshair = !_visionView.ShowCrosshair;
                SetToggle(_crosshairBtn, _visionView.ShowCrosshair);
            };
            SetToggle(_crosshairBtn, _visionView.ShowCrosshair);

            _invertBtn = ToolbarButton("Invert", 190, 58);
            _invertBtn.Click += (s, e) =>
            {
                _visionView.InvertView = !_visionView.InvertView;
                SetToggle(_invertBtn, _visionView.InvertView);
            };
            SetToggle(_invertBtn, _visionView.InvertView);

            _monoBtn = ToolbarButton("Mono", 252, 50);
            _monoBtn.Click += (s, e) =>
            {
                _visionView.MonoView = !_visionView.MonoView;
                SetToggle(_monoBtn, _visionView.MonoView);
            };
            SetToggle(_monoBtn, _visionView.MonoView);

            _measureBtn = ToolbarButton("Measure", 306, 74);
            _measureBtn.Click += (s, e) =>
            {
                _visionView.MeasureEnabled = !_visionView.MeasureEnabled;
                SetToggle(_measureBtn, _visionView.MeasureEnabled);
            };
            SetToggle(_measureBtn, _visionView.MeasureEnabled);

            _captureBtn = ToolbarButton("Capture", 384, 66, enabled: false);
            _captureBtn.Click += (s, e) => CaptureFrame();
            _saveBtn = ToolbarButton("Save", 454, 50, enabled: false);
            _saveBtn.Click += (s, e) => SaveCapture();

            Button ToolbarButton(string text, int x, int w, bool enabled = true)
            {
                var b = new Button { Text = text, Location = new Point(x, 6), Size = new Size(w, 28), Enabled = enabled };
                toolbar.Controls.Add(b);
                return b;
            }

            static void SetToggle(Button b, bool active)
            {
                if (active) b.BackColor = Color.LightGreen;
                else { b.UseVisualStyleBackColor = true; b.BackColor = SystemColors.Control; }
            }

            // --- bottom strip: quick-capture preview + Retry ---
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 132, BackColor = SystemColors.Control };
            bottom.Controls.Add(new Label { Text = "Last capture", Location = new Point(4, 4), AutoSize = true });
            _previewBox = new PictureBox
            {
                Location = new Point(4, 24),
                Size = new Size(160, 100),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Black,
                BorderStyle = BorderStyle.FixedSingle,
            };
            bottom.Controls.Add(_previewBox);
            _retryCameraBtn = new Button { Text = "Retry camera", Location = new Point(176, 24), Size = new Size(120, 32), Visible = false };
            _retryCameraBtn.Click += (s, e) => { _visionView.StartCamera(); RefreshCameraUi(); };
            bottom.Controls.Add(_retryCameraBtn);
            _fpsLabel = new Label { AutoSize = true, ForeColor = Color.DimGray, Location = new Point(176, 70) };
            bottom.Controls.Add(_fpsLabel);

            // --- live view fills the middle ---
            _visionView.Dock = DockStyle.Fill;
            _visionView.StatusChanged += OnVisionStatus;
            _visionView.FpsChanged += text => { if (!IsDisposed) _fpsLabel.Text = text; };
            _visionView.CameraStateChanged += RefreshCameraUi;
            _visionView.TickScaleProvider = () => VisionViewControl.MmPerPixel(_calib);

            // Fill added first (docks last → fills what the Top/Bottom bars leave).
            visionHostPanel.Controls.Add(_visionView);
            visionHostPanel.Controls.Add(toolbar);
            visionHostPanel.Controls.Add(bottom);

            // Open the camera once the form's handle exists (the grab thread marshals via BeginInvoke).
            Load += (s, e) => { _visionView.StartCamera(); RefreshCameraUi(); };
        }

        // Camera status text from the control (open failures, zoom changes) → the log/strip.
        private void OnVisionStatus(string text) => AppendLog("Camera: " + text);

        // Reflects camera-open state onto the toolbar: zoom/capture need a live camera; the Retry
        // button appears only while it's shut. Motion is unaffected either way.
        private void RefreshCameraUi()
        {
            if (IsDisposed) return;
            bool open = _visionView.IsCameraOpen;
            _visionZoom.Enabled = open;
            _captureBtn.Enabled = open;
            _retryCameraBtn.Visible = !open;
            _saveBtn.Enabled = _lastCapture != null;
        }

        private void CaptureFrame()
        {
            if (!_visionView.IsCameraOpen) return;
            _visionView.CaptureFullRes(SetCapture);
        }

        // UI thread: takes ownership of the full-res capture, previews it (scaled), keeps it for Save.
        private void SetCapture(Bitmap full)
        {
            if (IsDisposed) { full.Dispose(); return; }
            _previewBox.Image = null;
            _lastCapture?.Dispose();
            _lastCapture = full;
            _previewBox.Image = full;   // SizeMode.Zoom scales it into the thumbnail; Save keeps full res
            _saveBtn.Enabled = true;
        }

        // Saves the last capture as a PNG under Desktop\images (created if missing).
        private void SaveCapture()
        {
            if (_lastCapture == null) { AppendLog("Capture an image first."); return; }
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "images");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir,
                    "capture_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png");
                _lastCapture.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                AppendLog("Saved " + path);
            }
            catch (Exception ex)
            {
                AppendLog("Save failed: " + ex.Message);
            }
        }

        // --- Drift-corrected vision jog -------------------------------------------
        // The protocols window computes X/Y velocities from the camera-scale calibration so the
        // on-screen motion is purely horizontal/vertical. Deliberately does NOT use the
        // movement-inversion toggle (InvertDir). Soft-limit blocking is honoured like the
        // on-screen puck (CommandAxisVelocity).
        //
        // Y SIGN: determined EMPIRICALLY, not by the user-frame convention. With the
        // "user +Y = raw -Y" flip, a pure-X vision jog drifted at exactly 2x the uncorrected
        // angle — the signature of a wrong-sign compensation (right magnitude, doubling the
        // drift instead of cancelling it). So Y is commanded WITHOUT the extra flip. (The
        // calibration sampled Y via TryCurrentUser's negation, but the round-trip through the
        // Y velocity command evidently cancels it, so no second negation is needed here.)
        // NOTE: this also sets the ▲/▼ direction — verify ▲ moves up after changing it.

        public void VisionJogUser(int vxUser, int vyUser)
        {
            if (_motion == null || !_drivesEnabled || _busy) return;
            CommandAxisVelocity(AxisId.X, vxUser, honorSoftLimit: true);
            CommandAxisVelocity(AxisId.Y, vyUser, honorSoftLimit: true);
        }

        public void VisionStop()
        {
            if (_motion == null) return;
            CommandAxisVelocity(AxisId.X, 0, honorSoftLimit: true);
            CommandAxisVelocity(AxisId.Y, 0, honorSoftLimit: true);
        }
    }
}
