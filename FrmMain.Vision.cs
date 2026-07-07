using System;
using System.Drawing;
using System.Windows.Forms;

namespace NanotecController
{
    // FrmMain — vision (camera) integration. Stage A: a standalone live-view test window.
    // The button is camera-only, so it stays enabled regardless of drive connection.
    // (Partial of FrmMain.)
    public partial class FrmMain
    {
        private FrmVision? _visionWindow;
        private Button visionButton = null!;

        private void BuildVisionButton()
        {
            visionButton = new Button
            {
                Text = "Vision...",
                Location = new Point(694, 500),
                Size = new Size(168, 40),
            };
            visionButton.Click += visionButton_Click;
            leftPanel.Controls.Add(visionButton);
        }

        private void visionButton_Click(object? sender, EventArgs e)
        {
            if (_visionWindow == null || _visionWindow.IsDisposed)
                _visionWindow = new FrmVision(this);
            _visionWindow.Show();
            _visionWindow.BringToFront();
        }

        // --- Drift-corrected vision jog -------------------------------------------
        // The vision window computes X/Y velocities from the camera-scale calibration so the
        // on-screen motion is purely horizontal/vertical. Deliberately does NOT use the
        // movement-inversion toggle (InvertDir). Soft-limit blocking is honoured like the
        // on-screen puck (CommandVel).
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
