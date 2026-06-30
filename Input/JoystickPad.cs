using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NanotecController
{
    /// <summary>
    /// On-screen (mouse-driven) virtual joystick. Drag the puck to deflect; the
    /// normalized <see cref="Vector"/> (x = right+, y = up+, magnitude 0..1) carries
    /// both ANGLE and DISTANCE, so it's a true analog input — the owner maps it
    /// proportionally to axis velocities. Releasing the mouse springs the puck back to
    /// centre → Vector (0,0) → stop (momentary, like a spring-return stick).
    ///
    /// The control only reports state; the owner polls <see cref="Vector"/> on a timer
    /// and owns the motion/safety policy. Disabling the control re-centres it.
    /// </summary>
    public sealed class JoystickPad : Control
    {
        private bool _dragging;
        private PointF _vec; // normalized, y up positive

        /// <summary>Current deflection: x right+, y up+, each in [-1, 1]; (0,0) = centre.</summary>
        public PointF Vector => _vec;

        public JoystickPad()
        {
            DoubleBuffered = true;
            Size = new Size(150, 150);
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        private float Radius => Math.Min(Width, Height) / 2f - 10f;
        private PointF Center => new(Width / 2f, Height / 2f);

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (!Enabled) return;
            _dragging = true;
            Capture = true;            // keep tracking even if the mouse leaves the control
            UpdateFromMouse(e.Location);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragging) UpdateFromMouse(e.Location);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            Capture = false;
            _vec = PointF.Empty;       // spring back to centre → stop
            Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            if (!Enabled) { _dragging = false; _vec = PointF.Empty; }
            Invalidate();
        }

        private void UpdateFromMouse(Point p)
        {
            float r = Radius;
            float dx = p.X - Center.X;
            float dy = p.Y - Center.Y;
            float mag = (float)Math.Sqrt(dx * dx + dy * dy);
            if (mag > r && mag > 0) { dx *= r / mag; dy *= r / mag; }   // clamp to the circle
            _vec = new PointF(dx / r, -dy / r);                         // screen y is down → invert
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float r = Radius;
            PointF c = Center;
            Color ring = Enabled ? Color.DimGray : Color.Gainsboro;
            Color puck = Enabled ? Color.SteelBlue : Color.LightGray;

            using var ringPen = new Pen(ring, 2f);
            g.DrawEllipse(ringPen, c.X - r, c.Y - r, 2 * r, 2 * r);
            g.DrawLine(ringPen, c.X - r, c.Y, c.X + r, c.Y);
            g.DrawLine(ringPen, c.X, c.Y - r, c.X, c.Y + r);

            float px = c.X + _vec.X * r;
            float py = c.Y - _vec.Y * r;   // normalized → screen
            const float pr = 16f;
            using var puckBrush = new SolidBrush(puck);
            g.FillEllipse(puckBrush, px - pr, py - pr, 2 * pr, 2 * pr);
        }
    }
}
