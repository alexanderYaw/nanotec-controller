using System.Threading.Tasks;
using System.Windows.Forms;

namespace NanotecController
{
    // FrmVision — rotate-about-crosshair UI: the handedness label and the one-time "Sign test" that
    // fixes the image handedness empirically. The actual compensated rotation runs in FrmMain
    // (serialized motion); this is just the operator surface. (Partial of FrmVision.)
    public sealed partial class FrmVision
    {
        private void RefreshSignLabel()
            => _signLabel.Text = _owner?.RotationSign is int s ? $"Handedness: {s:+0;-0}" : "Handedness: not set";

        // Fixes the image handedness empirically: rotate a small angle about the crosshair with
        // the current assumed sign, ask whether the crosshair point stayed pinned, then rotate
        // back (same sign → exact restore) and persist the confirmed/flipped sign. If the point
        // swung away instead of staying put, the sign was wrong and gets flipped.
        private async Task SignTestAsync()
        {
            if (_owner == null) return;
            const double testDeg = 6.0;
            if (MessageBox.Show(this,
                    $"Sign test rotates ~{testDeg}° about the crosshair and back.\r\n" +
                    "Watch the point under the crosshair.\r\nProceed?",
                    "Sign test", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            int assumed = _owner.RotationSign ?? +1;   // the sign used for BOTH the test and the restore
            await _owner.RotateAboutCrosshairAsync(+testDeg);

            DialogResult pinned = MessageBox.Show(this,
                "Did the point under the crosshair STAY pinned?\r\n\r\n" +
                "Yes = handedness correct\r\nNo = it swung away (will flip)",
                "Sign test", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

            // Reversing with the SAME stored sign is the exact geometric inverse, so this returns
            // to the start pose regardless of the answer. Do it BEFORE changing the sign.
            await _owner.RotateAboutCrosshairAsync(-testDeg);

            if (pinned == DialogResult.Cancel) { _status.Text = "Sign test cancelled (no change)."; return; }
            int chosen = pinned == DialogResult.Yes ? assumed : -assumed;
            _owner.SetRotationSign(chosen);
            RefreshSignLabel();
            _status.Text = $"Sign test done: handedness {chosen:+0;-0}.";
        }
    }
}
