using System;
using System.Drawing;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NanotecController
{
    /// <summary>Picks a target position and moves the chuck there. Pure UI, owning no drive access:
    /// it reads live position + travel limits from <see cref="FrmMain"/> in the USER frame and executes
    /// through MoveToAsync. Clicking the grid or typing X/Y/Z only STAGES a target — nothing moves
    /// until Go. Z is numeric only; the grid greys out until both X and Y limits are set.</summary>
    public sealed class FrmPosition : Form
    {
        private readonly IMotionHost _owner;
        private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 250 };

        private readonly PositionGrid _grid;
        private readonly TextBox _x, _y, _z;
        private readonly Button _go;

        public FrmPosition(IMotionHost owner)
        {
            _owner = owner;
            Text = "Position Map - absolute XY positioning";
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F);
            ClientSize = new Size(600, 440);
            MinimumSize = new Size(520, 380);

            _grid = new PositionGrid
            {
                Location = new Point(12, 12),
                Size = new Size(380, 380),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            };
            _grid.TargetPicked += OnTargetPicked;
            Controls.Add(_grid);

            var box = new GroupBox
            {
                Text = "Target (drive units)",
                Location = new Point(404, 12),
                Size = new Size(184, 297),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
            };

            TextBox Field(string label, int yy)
            {
                box.Controls.Add(new Label { Text = label, Location = new Point(14, yy + 4), AutoSize = true });
                var tb = new TextBox { Location = new Point(40, yy), Size = new Size(126, 26) };
                box.Controls.Add(tb);
                return tb;
            }

            _x = Field("X:", 30);
            _y = Field("Y:", 64);
            _z = Field("Z:", 98);
            _x.TextChanged += (s, e) => SyncMarkerFromFields();
            _y.TextChanged += (s, e) => SyncMarkerFromFields();

            _go = new Button { Text = "Go", Location = new Point(40, 140), Size = new Size(126, 34), Enabled = false };
            _go.Click += async (s, e) => await DoGo();
            box.Controls.Add(_go);

            box.Controls.Add(new Label
            {
                Text = "Click the grid or type a target coordinate, then Go. X/Y/Z optional; blank = leave that axis as it is.",
                Location = new Point(14, 180), AutoSize = true, MaximumSize = new Size(160, 0),
                ForeColor = Color.DimGray,
            });

            Controls.Add(box);

            _refresh.Tick += (s, e) => RefreshFromOwner();
            _refresh.Start();
            RefreshFromOwner();
        }

        // A grid click stages a target by filling X/Y, which mirror the crosshair. Nothing moves.
        private void OnTargetPicked(PointF p)
        {
            _x.Text = ((long)Math.Round(p.X)).ToString(CultureInfo.InvariantCulture);
            _y.Text = ((long)Math.Round(p.Y)).ToString(CultureInfo.InvariantCulture);
        }

        private void SyncMarkerFromFields()
        {
            if (long.TryParse(_x.Text.Trim(), out long x) && long.TryParse(_y.Text.Trim(), out long y))
                _grid.SetTarget(x, y);
        }

        private async Task DoGo()
        {
            // Lock target entry while moving so a new target can't be staged mid-motion.
            // Re-enabled in finally even if the move throws.
            SetInputsEnabled(false);
            try { await _owner.MoveToAsync(_x.Text, _y.Text, _z.Text); }
            finally { if (!IsDisposed) SetInputsEnabled(true); }
        }

        private void SetInputsEnabled(bool on)
        {
            _grid.Enabled = on;
            _x.Enabled = on;
            _y.Enabled = on;
            _z.Enabled = on;
        }

        private void RefreshFromOwner()
        {
            (long min, long max)? xl = _owner.UserLimits(AxisId.X);
            (long min, long max)? yl = _owner.UserLimits(AxisId.Y);
            if (xl.HasValue && yl.HasValue)
                _grid.SetLimits(xl.Value.min, xl.Value.max, yl.Value.min, yl.Value.max);

            if (_owner.TryCurrentUser(AxisId.X, out long ux) && _owner.TryCurrentUser(AxisId.Y, out long uy))
                _grid.SetCurrent(ux, uy);

            _go.Enabled = _owner.CanMoveCalibration;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _refresh.Stop();
            base.OnFormClosing(e);
        }
    }
}
