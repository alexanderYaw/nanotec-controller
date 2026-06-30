using System;
using System.Drawing;

namespace NanotecController
{
    /// <summary>
    /// Shared GDI overlays drawn onto a full-resolution capture bitmap (HALCON row = y, col = x).
    /// Sizes scale with the image width so they look consistent at any capture resolution in the
    /// SizeMode.Zoom pane. Used by the calibration-sample, chuck-edge, and wafer-edge views.
    /// </summary>
    public static class VisionOverlay
    {
        /// <summary>Pen width that scales with image width (min 2 px).</summary>
        public static float PenWidth(int imageWidth) => Math.Max(2f, imageWidth / 400f);

        /// <summary>Frame-centre crosshair: two short lines through (row, col), length ~width/15.</summary>
        public static void DrawCrosshair(Graphics g, int imageWidth, double row, double col, Color color)
        {
            float half = imageWidth / 30f;
            using var pen = new Pen(color, PenWidth(imageWidth));
            g.DrawLine(pen, (float)col, (float)row - half, (float)col, (float)row + half);
            g.DrawLine(pen, (float)col - half, (float)row, (float)col + half, (float)row);
        }

        /// <summary>Marks a detected point with a circle of radius <paramref name="radius"/> px.</summary>
        public static void DrawPoint(Graphics g, double row, double col, float radius, Color color, float penWidth)
        {
            using var pen = new Pen(color, penWidth);
            g.DrawEllipse(pen, (float)col - radius, (float)row - radius, 2 * radius, 2 * radius);
        }

        /// <summary>Draws an XLD contour (pixel rows/cols, equal length) as a polyline. No-op for &lt;2 points.</summary>
        public static void DrawContour(Graphics g, double[] rows, double[] cols, Color color, float penWidth)
        {
            if (rows.Length < 2) return;
            var pts = new PointF[rows.Length];
            for (int k = 0; k < rows.Length; k++) pts[k] = new PointF((float)cols[k], (float)rows[k]);
            using var pen = new Pen(color, penWidth);
            g.DrawLines(pen, pts);
        }
    }
}
