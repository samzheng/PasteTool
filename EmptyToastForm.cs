using System.Drawing.Drawing2D;

namespace PasteTool;

/// <summary>
/// 队列为空时的提示，显示 1 秒后自动消失，并且不抢走当前输入焦点。
/// </summary>
internal sealed class EmptyToastForm : Form
{
    private const string Caption = "粘贴板内容已空";
    private readonly System.Windows.Forms.Timer _timer;

    public EmptyToastForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ControlBox = false;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(32, 32, 32);
        ForeColor = Color.White;
        Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Regular, GraphicsUnit.Point);
        Padding = new Padding(18, 10, 18, 10);
        DoubleBuffered = true;

        var label = new Label
        {
            Text = Caption,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(32, 32, 32),
            AutoSize = false
        };
        Controls.Add(label);

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Hide();
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_NOACTIVATE = 0x08000000;
            const int WS_EX_TOOLWINDOW = 0x00000080;
            const int WS_EX_TOPMOST = 0x00000008;
            const int CS_DROPSHADOW = 0x00020000;
            CreateParams createParams = base.CreateParams;
            createParams.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
            createParams.ClassStyle |= CS_DROPSHADOW;
            return createParams;
        }
    }

    public void ShowForASecond()
    {
        Size textSize = TextRenderer.MeasureText(Caption, Font);
        ClientSize = new Size(textSize.Width + Padding.Horizontal + 8, textSize.Height + Padding.Vertical + 4);
        UpdateRoundRegion();

        Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
        int x = Math.Max(area.Left, area.Right - Width - 20);
        int y = Math.Max(area.Top, area.Bottom - Height - 20);
        Location = new Point(x, y);

        _timer.Stop();
        _timer.Start();
        if (!Visible)
        {
            Show();
        }

        TopMost = true;
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_MOUSEACTIVATE = 0x0021;
        const int MA_NOACTIVATE = 3;
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = (IntPtr)MA_NOACTIVATE;
            return;
        }

        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void UpdateRoundRegion()
    {
        using var outline = CreateRoundRect(new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1)), 8);
        Region? previous = Region;
        Region = new Region(outline);
        previous?.Dispose();
    }

    private static GraphicsPath CreateRoundRect(Rectangle bounds, int radius)
    {
        int diameter = radius * 2;
        var path = new GraphicsPath();
        if (diameter <= 0 || bounds.Width < diameter || bounds.Height < diameter)
        {
            path.AddRectangle(bounds);
            path.CloseFigure();
            return path;
        }

        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
