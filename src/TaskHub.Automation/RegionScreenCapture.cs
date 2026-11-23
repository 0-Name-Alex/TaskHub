using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using TaskHub.Core.Automation;
using TaskHub.Core.Hosting;

namespace TaskHub.Automation;

/// <summary>
/// Freezes the whole desktop (all monitors) into a bitmap, shows it full-screen dimmed, and lets the
/// user drag a rectangle. Enter/mouse-up accepts, Esc or right-click cancels. Works in physical
/// pixels throughout (the app is per-monitor DPI aware), so mixed-DPI setups crop correctly.
/// </summary>
public sealed class RegionScreenCapture(IUiDispatcher ui, ILogger<RegionScreenCapture> log) : IScreenCapture
{
    public Task<byte[]?> SelectRegionAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Post(() =>
        {
            try
            {
                tcs.TrySetResult(ct.IsCancellationRequested ? null : SelectOnUiThread());
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Screenshot failed");
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    private static byte[]? SelectOnUiThread()
    {
        var bounds = SystemInformation.VirtualScreen;
        using var desktop = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(desktop))
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);

        using var overlay = new SelectionForm(desktop, bounds);
        if (overlay.ShowDialog() != DialogResult.OK || overlay.Selection is not { Width: > 2, Height: > 2 } rect)
            return null;

        using var cropped = desktop.Clone(rect, PixelFormat.Format32bppArgb);
        using var stream = new MemoryStream();
        cropped.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private sealed class SelectionForm : Form
    {
        private const int WmDpiChanged = 0x02E0;
        private readonly Bitmap _desktop;
        private readonly Bitmap _dimmed;
        private Point _start;
        private bool _dragging;
        private Rectangle _rect;

        public SelectionForm(Bitmap desktop, Rectangle bounds)
        {
            _desktop = desktop;
            _dimmed = new Bitmap(desktop);
            using (var g = Graphics.FromImage(_dimmed))
            using (var shade = new SolidBrush(Color.FromArgb(110, 0, 0, 0)))
                g.FillRectangle(shade, 0, 0, _dimmed.Width, _dimmed.Height);

            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            DoubleBuffered = true;
            Cursor = Cursors.Cross;
            Text = "TaskHub screenshot";
            Bounds = bounds;
        }

        /// <summary>The selected area in desktop-bitmap coordinates.</summary>
        public Rectangle? Selection { get; private set; }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Bounds = SystemInformation.VirtualScreen; // in case Windows moved/resized us while showing
            Activate();
            Focus();
        }

        protected override void WndProc(ref Message m)
        {
            // Spanning monitors with different DPIs: don't let Windows resize us when the "current" monitor changes.
            if (m.Msg == WmDpiChanged)
            {
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.DrawImage(_dimmed, e.ClipRectangle, e.ClipRectangle, GraphicsUnit.Pixel);
            if (_rect.Width > 0 && _rect.Height > 0)
            {
                g.DrawImage(_desktop, _rect, _rect, GraphicsUnit.Pixel);
                using var pen = new Pen(Color.FromArgb(255, 0, 120, 215), 2);
                g.DrawRectangle(pen, _rect.X, _rect.Y, _rect.Width - 1, _rect.Height - 1);
                var label = $"{_rect.Width} × {_rect.Height}";
                using var font = new Font("Segoe UI", 10f);
                var size = g.MeasureString(label, font);
                var y = _rect.Top - size.Height - 6 >= 0 ? _rect.Top - size.Height - 6 : _rect.Bottom + 6;
                using var back = new SolidBrush(Color.FromArgb(200, 32, 32, 32));
                g.FillRectangle(back, _rect.Left, y, size.Width + 8, size.Height + 2);
                g.DrawString(label, font, Brushes.White, _rect.Left + 4, y + 1);
            }
            else
            {
                const string hint = "Drag to capture an area · Esc to cancel";
                using var font = new Font("Segoe UI", 14f);
                var size = g.MeasureString(hint, font);
                var screen = Screen.FromPoint(MousePosition).Bounds;
                var x = screen.Left - Bounds.Left + (screen.Width - size.Width) / 2;
                var y = screen.Top - Bounds.Top + 60;
                using var back = new SolidBrush(Color.FromArgb(200, 32, 32, 32));
                g.FillRectangle(back, x - 14, y - 8, size.Width + 28, size.Height + 16);
                g.DrawString(hint, font, Brushes.White, x, y);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right)
            {
                DialogResult = DialogResult.Cancel;
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            _dragging = true;
            _start = e.Location;
            _rect = Rectangle.Empty;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging) return;
            var previous = _rect;
            _rect = Rectangle.FromLTRB(
                Math.Min(_start.X, e.X), Math.Min(_start.Y, e.Y),
                Math.Max(_start.X, e.X), Math.Max(_start.Y, e.Y));
            // Repaint only around the old and new rectangles (plus room for the size label).
            var dirty = Rectangle.Union(previous, _rect);
            dirty.Inflate(8, 40);
            Invalidate(dirty);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!_dragging || e.Button != MouseButtons.Left) return;
            _dragging = false;
            if (_rect.Width > 2 && _rect.Height > 2)
            {
                Selection = Rectangle.Intersect(_rect, new Rectangle(0, 0, _desktop.Width, _desktop.Height));
                DialogResult = DialogResult.OK;
            }
            else
            {
                _rect = Rectangle.Empty;
                Invalidate();
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _dimmed.Dispose();
            base.Dispose(disposing);
        }
    }
}
