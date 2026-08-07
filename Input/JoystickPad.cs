using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NanotecController
{
    /// <summary>
    /// On-screen virtual joystick. <see cref="Vector"/> carries both ANGLE and DISTANCE, making it a
    /// true analog input the owner maps proportionally to axis velocities. Releasing the mouse
    /// springs the puck back to centre, like a spring-return stick. Reports state only — the owner
    /// polls on a timer and owns the motion/safety policy. Disabling re-centres it.
    /// </summary>
    public sealed class JoystickPad : Control
    {
        private const float RingInset = 10f;
        private const float PuckRadius = 16f;
        private const int DefaultExtent = 150;

        private bool _dragging;
        private PointF _vec;

        /// <summary>Current deflection: x right+, y up+, each in [-1, 1]; (0,0) = centre.</summary>
        public PointF Vector => _vec;

        public JoystickPad()
        {
            DoubleBuffered = true;
            Size = new Size(DefaultExtent, DefaultExtent);
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        private float Radius => Math.Min(Width, Height) / 2f - RingInset;
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
            using var puckBrush = new SolidBrush(puck);
            g.FillEllipse(puckBrush, px - PuckRadius, py - PuckRadius, 2 * PuckRadius, 2 * PuckRadius);
        }
    }
}
