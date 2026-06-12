using System;
using System.Drawing;
using System.Windows.Forms;

namespace MotorControlApp
{
    /// <summary>
    /// A 2-D map of the table's XY travel envelope. Renders the live chuck position as a
    /// filled dot and a user-picked target as a hollow crosshair; clicking inside the plot
    /// stages a target (clamped to the limits) and raises <see cref="TargetPicked"/>.
    ///
    /// Every coordinate exchanged with this control is in the USER frame — the same frame the
    /// readouts and Move-To fields use (Y already inverted upstream in FrmMain). The control is
    /// a pure renderer: it owns no drive access and moves nothing; the host decides what to do
    /// with a picked target. The plot preserves true XY aspect (letterboxed) so on-screen
    /// distances reflect real geometry.
    /// </summary>
    public sealed class PositionGrid : Control
    {
        private long _xMin, _xMax = 1, _yMin, _yMax = 1;
        private bool _hasLimits;
        private PointF _current;
        private bool _hasCurrent;
        private PointF _target;
        private bool _hasTarget;

        public PositionGrid()
        {
            DoubleBuffered = true;
            BackColor = Color.White;
            MinimumSize = new Size(220, 220);
        }

        /// <summary>Raised with the USER-frame coordinates of a picked target when the user clicks the plot.</summary>
        public event Action<PointF>? TargetPicked;

        /// <summary>Sets the travel envelope</summary>
        public void SetLimits(long xMin, long xMax, long yMin, long yMax)
        {
            _xMin = xMin; _xMax = xMax; _yMin = yMin; _yMax = yMax;
            _hasLimits = true;
            Invalidate();
        }

        /// <summary>Live current chuck position</summary>
        public void SetCurrent(long x, long y)
        {
            _current = new PointF(x, y);
            _hasCurrent = true;
            Invalidate();
        }

        /// <summary>Sets the user-picked target position</summary>
        public void SetTarget(long x, long y)
        {
            _target = new PointF(
                Math.Clamp(x, Math.Min(_xMin, _xMax), Math.Max(_xMin, _xMax)),
                Math.Clamp(y, Math.Min(_yMin, _yMax), Math.Max(_yMin, _yMax)));
            _hasTarget = true;
            Invalidate();
        }

        // -- geometry -----------------------------------------------
        private const int PlotMargin = 46;
        private RectangleF PlotRect()
        {
            float availW = Math.Max(1, Width - 2 * PlotMargin);
            float availH = Math.Max(1, Height - 2 * PlotMargin);
            float spanX = _xMax - _xMin;
            float spanY = _yMax - _yMin;
            if (spanX <=0 || spanY <= 0)
            {
                return new RectangleF(PlotMargin, PlotMargin, availW, availH);
            }

            float scale = Math.Min(availW / spanX, availH / spanY);
            float w = spanX * scale;
            float h = spanY * scale;
            return new RectangleF(PlotMargin + (availW - w) / 2f, PlotMargin + (availH - h) / 2f, w, h);
        }

        private PointF ToPixel(RectangleF plot, float x, float y)
        {
            return new (plot.Left + (x - _xMin) / (_xMax - _xMin) * plot.Width,
                plot.Bottom - (y - _yMin) / (_yMax - _yMin) * plot.Height);
        }

        private PointF ToCoord(RectangleF plot, float px, float py)
        {
            return new (_xMin + Clamp01((px - plot.Left) / plot.Width) * (_xMax - _xMin),
                _yMin + Clamp01((plot.Bottom - py) / plot.Height) * (_yMax - _yMin));
        }

        private static float Clamp01(float v)
        {
            return (v < 0) ? 0 : ((v > 1) ? 1 : v);
        }

        // -- input -----------------------------------------------
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!_hasLimits || e.Button != MouseButtons.Left)
            {
                return;
            }

            _target = ToCoord(PlotRect(), e.X, e.Y);
            _hasTarget = true;
            Invalidate();
            TargetPicked?.Invoke(_target);
        }

        // -- render -----------------------------------------------
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            if (!_hasLimits)
            {
                TextRenderer.DrawText(g, "Set X and Y limits to enable the grid.",
                    Font, ClientRectangle, Color.Gray,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            RectangleF plot = PlotRect();
            using (var border = new Pen(Color.FromArgb(120, 120, 120)))
            {
                g.DrawRectangle(border, plot.Left, plot.Top, plot.Width, plot.Height);
            }

            using (var grid = new Pen(Color.FromArgb(230, 230, 230)))
            {
                float cx = plot.Left + plot.Width / 2f;
                float cy = plot.Top + plot.Height / 2f;
                g.DrawLine(grid, cx, plot.Top, cx, plot.Bottom);
                g.DrawLine(grid, plot.Left, cy, plot.Right, cy);
            }

            DrawLabels(g, plot);

            if (_hasTarget)
            {
                PointF p = ToPixel(plot, _target.X, _target.Y);
                using var pen = new Pen(Color.FromArgb(200, 60, 60), 1.5f);
                g.DrawEllipse(pen, p.X - 6, p.Y - 6, 12, 12);
                g.DrawLine(pen, p.X - 9, p.Y, p.X + 9, p.Y);
                g.DrawLine(pen, p.X, p.Y - 9, p.X, p.Y + 9);
            }

            if (_hasCurrent)
            {
                PointF p = ToPixel(plot, _current.X, _current.Y);
                using var brush = new SolidBrush(Color.FromArgb(40, 110, 200));
                g.FillEllipse(brush, p.X - 5, p.Y - 5, 10, 10);
            }
        }

        private void DrawLabels(Graphics g, RectangleF plot)
        {
            Color c = Color.DimGray;
            TextRenderer.DrawText(g, _xMin.ToString("N0"), Font, new Point((int)plot.Left, (int)plot.Bottom + 4), c);
            TextRenderer.DrawText(g, _xMax.ToString("N0"), Font, new Point((int)plot.Right - 56, (int)plot.Bottom + 4), c);
            TextRenderer.DrawText(g, _yMin.ToString("N0"), Font, new Point(2, (int)plot.Bottom - 16), c);
            TextRenderer.DrawText(g, _yMax.ToString("N0"), Font, new Point(2, (int)plot.Top - 2), c);
            TextRenderer.DrawText(g, "X", Font, new Point((int)plot.Left + (int)plot.Width / 2, (int)plot.Bottom + 22), c);
            TextRenderer.DrawText(g, "Y", Font, new Point(20, (int)plot.Top + (int)plot.Height / 2), c);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Invalidate();
        }
    }
}