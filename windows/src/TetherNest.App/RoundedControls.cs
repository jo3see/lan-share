using System.Drawing.Drawing2D;

namespace TetherNest;

internal static class RoundedShape
{
    public static GraphicsPath Path(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class RoundedCard : Panel
{
    public RoundedCard() => DoubleBuffered = true;
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedShape.Path(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 18 * DeviceDpi / 96f);
        using var fill = new SolidBrush(BackColor);
        using var border = new Pen(Color.FromArgb(48, 61, 79));
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(border, path);
    }
}

internal sealed class RoundedButton : Button
{
    private bool hovered;
    private bool pressed;
    public RoundedButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
    }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var fillColor = !Enabled ? Color.FromArgb(32, 42, 57) :
            pressed ? ControlPaint.Dark(BackColor, .12f) :
            hovered ? ControlPaint.Light(BackColor, .12f) : BackColor;
        using var path = RoundedShape.Path(new RectangleF(1, 1, Width - 2, Height - 2), 12 * DeviceDpi / 96f);
        using var fill = new SolidBrush(fillColor);
        e.Graphics.FillPath(fill, path);
        using var border = new Pen(Enabled ? Color.FromArgb(84, 113, 146) : Color.FromArgb(47, 59, 75));
        e.Graphics.DrawPath(border, path);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle,
            Enabled ? ForeColor : Color.FromArgb(143, 158, 178),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(Color.FromArgb(213, 231, 255)) { DashStyle = DashStyle.Dot };
            using var focusPath = RoundedShape.Path(new RectangleF(5, 5, Width - 10, Height - 10), 9 * DeviceDpi / 96f);
            e.Graphics.DrawPath(focus, focusPath);
        }
    }
}
