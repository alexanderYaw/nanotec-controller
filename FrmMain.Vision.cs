using System;
using System.Drawing;
using System.Windows.Forms;

namespace MotorControlApp
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
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
            };
            visionButton.Click += visionButton_Click;
            Controls.Add(visionButton);
        }

        private void visionButton_Click(object? sender, EventArgs e)
        {
            if (_visionWindow == null || _visionWindow.IsDisposed)
                _visionWindow = new FrmVision();
            _visionWindow.Show();
            _visionWindow.BringToFront();
        }
    }
}
