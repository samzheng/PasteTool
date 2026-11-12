using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PasteTool;

/// <summary>
/// 隐藏主窗体：托盘图标、粘贴板队列，以及全局 Ctrl+V。
/// </summary>
internal sealed class PasteHostForm : Form
{
    private readonly Queue<string> _queue = new();
    private readonly Native.LowLevelKeyboardProc _hookProc;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripLabel _countItem;
    private readonly Icon _trayIcon;

    private IntPtr _hook;
    private EmptyToastForm? _toast;
    private uint _lastCapturedSequence;
    private string? _lastEnqueuedText;
    private long _lastEnqueuedTick;
    private bool _listenerAdded;
    private bool _settingOwnClipboard;
    private bool _started;
    private bool _closing;
    private bool _vHeld;
    private bool _swallowVUp;
    private bool _injecting;
    private bool _creatingHandle;

    public PasteHostForm()
    {
        _hookProc = OnKeyboardHook;
        _trayIcon = CreateTrayIcon();

        _countItem = new ToolStripLabel("队列是空的");
        var exitItem = new ToolStripMenuItem("退出");

        _menu = new ContextMenuStrip { ShowImageMargin = false };
        _menu.Items.Add(_countItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(exitItem);
        _menu.Opening += (_, _) => UpdateQueueStatus();

        _notifyIcon = new NotifyIcon
        {
            Icon = _trayIcon,
            Text = "PasteTool — 队列是空的",
            ContextMenuStrip = _menu,
            Visible = true
        };
        exitItem.Click += (_, _) =>
        {
            _notifyIcon.Visible = false;
            Close();
        };

        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        Size = new Size(1, 1);
        Text = "PasteTool";

        Application.Idle += OnFirstIdle;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x00000080;
            const int WS_EX_NOACTIVATE = 0x08000000;
            CreateParams createParams = base.CreateParams;
            createParams.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return createParams;
        }
    }

    protected override void SetVisibleCore(bool value)
    {
        if (!IsHandleCreated && !_creatingHandle)
        {
            _creatingHandle = true;
            CreateHandle();
            _creatingHandle = false;
        }

        // 启动后不显示窗口，只留托盘图标。
        base.SetVisibleCore(false);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case Native.WM_CLIPBOARDUPDATE:
                CaptureClipboardText();
                break;
            case Native.WM_PASTE_FROM_QUEUE:
                PasteNext();
                break;
        }

        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _closing = true;
        ReleaseSystemHooks();
        _notifyIcon.Visible = false;
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Application.Idle -= OnFirstIdle;
            ReleaseSystemHooks();
            _toast?.Dispose();
            _notifyIcon.Dispose();
            _menu.Dispose();
            _trayIcon.Dispose();
        }
        else
        {
            ReleaseSystemHooks();
        }

        base.Dispose(disposing);
    }

    private void OnFirstIdle(object? sender, EventArgs e)
    {
        Application.Idle -= OnFirstIdle;
        StartMonitoring();
    }

    private void StartMonitoring()
    {
        if (_started || _closing || !IsHandleCreated)
        {
            return;
        }

        _started = true;
        _lastCapturedSequence = Native.GetClipboardSequenceNumber();
        _listenerAdded = Native.AddClipboardFormatListener(Handle);
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _hookProc, Native.GetModuleHandle(null), 0);

        var problems = new List<string>();
        if (!_listenerAdded)
        {
            problems.Add("无法监听粘贴板，复制的文字不会进入队列。");
        }

        if (_hook == IntPtr.Zero)
        {
            problems.Add("无法接管 Ctrl+V。");
        }

        if (problems.Count > 0)
        {
            MessageBox.Show(
                string.Join(Environment.NewLine, problems),
                "PasteTool",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void CaptureClipboardText()
    {
        if (_settingOwnClipboard)
        {
            _lastCapturedSequence = Native.GetClipboardSequenceNumber();
            return;
        }

        uint sequence = Native.GetClipboardSequenceNumber();
        if (sequence == _lastCapturedSequence)
        {
            return;
        }

        if (!TryReadClipboardText(out string? text))
        {
            return;
        }

        _lastCapturedSequence = sequence;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // 同一次复制经常会连续写入好几次相同文字，120 毫秒内的重复内容只保留一条。
        long now = Environment.TickCount64;
        if (text == _lastEnqueuedText && now - _lastEnqueuedTick < 120)
        {
            return;
        }

        _lastEnqueuedText = text;
        _lastEnqueuedTick = now;
        _queue.Enqueue(text);
        UpdateQueueStatus();
    }

    private void PasteNext()
    {
        if (_closing)
        {
            return;
        }

        if (_queue.Count == 0)
        {
            ShowEmptyToast();
            return;
        }

        string text = _queue.Dequeue();
        UpdateQueueStatus();
        if (!TrySetClipboardText(text))
        {
            RequeueFront(text);
            UpdateQueueStatus();
            return;
        }

        InjectPasteKey();
    }

    private void ShowEmptyToast()
    {
        if (_toast == null || _toast.IsDisposed)
        {
            _toast = new EmptyToastForm();
        }

        _toast.ShowForASecond();
    }

    private bool TrySetClipboardText(string text)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                _settingOwnClipboard = true;
                Clipboard.SetText(text);
                _lastCapturedSequence = Native.GetClipboardSequenceNumber();
                return true;
            }
            catch (ExternalException)
            {
                if (attempt == 4)
                {
                    return false;
                }

                Thread.Sleep(20);
            }
            finally
            {
                _settingOwnClipboard = false;
            }
        }

        return false;
    }

    private void RequeueFront(string text)
    {
        string[] pending = _queue.ToArray();
        _queue.Clear();
        _queue.Enqueue(text);
        foreach (string item in pending)
        {
            _queue.Enqueue(item);
        }
    }

    private void UpdateQueueStatus()
    {
        int count = _queue.Count;
        _countItem.Text = count == 0 ? "队列是空的" : $"队列中有 {count} 条";
        _notifyIcon.Text = count == 0 ? "PasteTool — 队列是空的" : $"PasteTool — 队列 {count} 条";
    }

    private void InjectPasteKey()
    {
        bool ctrlDown = IsKeyDown(Native.VK_CONTROL);
        byte scanV = (byte)Native.MapVirtualKey(Native.VK_V, Native.MAPVK_VK_TO_VSC);
        byte scanCtrl = (byte)Native.MapVirtualKey((uint)Native.VK_CONTROL, Native.MAPVK_VK_TO_VSC);

        _injecting = true;
        try
        {
            if (!ctrlDown)
            {
                Native.keybd_event((byte)Native.VK_CONTROL, scanCtrl, 0, UIntPtr.Zero);
            }

            Native.keybd_event((byte)Native.VK_V, scanV, 0, UIntPtr.Zero);
            Native.keybd_event((byte)Native.VK_V, scanV, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);

            if (!ctrlDown)
            {
                Native.keybd_event((byte)Native.VK_CONTROL, scanCtrl, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
        }
        finally
        {
            _injecting = false;
        }
    }

    private IntPtr OnKeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode == Native.HC_ACTION && !_closing && !_injecting && lParam != IntPtr.Zero)
            {
                var info = Marshal.PtrToStructure<Native.KbdLlHookStruct>(lParam);
                bool injected = (info.flags & Native.LLKHF_INJECTED) != 0;
                if (!injected && info.vkCode == Native.VK_V)
                {
                    bool keyUp = (info.flags & Native.LLKHF_UP) != 0;
                    if (keyUp)
                    {
                        _vHeld = false;
                        if (_swallowVUp)
                        {
                            _swallowVUp = false;
                            return (IntPtr)1;
                        }
                    }
                    else
                    {
                        bool firstDown = !_vHeld;
                        _vHeld = true;
                        if (_swallowVUp || (firstDown && IsPlainCtrlDown()))
                        {
                            if (firstDown && IsHandleCreated)
                            {
                                _swallowVUp = true;
                                Native.PostMessage(Handle, Native.WM_PASTE_FROM_QUEUE, IntPtr.Zero, IntPtr.Zero);
                            }

                            return (IntPtr)1;
                        }
                    }
                }
            }
        }
        catch
        {
            // 钩子回调不能抛异常，否则系统会卸掉钩子。
        }

        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void ReleaseSystemHooks()
    {
        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        if (_listenerAdded && IsHandleCreated)
        {
            Native.RemoveClipboardFormatListener(Handle);
        }

        _listenerAdded = false;
    }

    private static bool IsPlainCtrlDown()
    {
        return IsKeyDown(Native.VK_CONTROL)
            && !IsKeyDown(Native.VK_SHIFT)
            && !IsKeyDown(Native.VK_MENU)
            && !IsKeyDown(Native.VK_LWIN)
            && !IsKeyDown(Native.VK_RWIN);
    }

    private static bool IsKeyDown(int virtualKey)
    {
        return (Native.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    private static bool TryReadClipboardText(out string? text)
    {
        text = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (!Clipboard.ContainsText())
                {
                    return true;
                }

                text = Clipboard.GetText();
                return true;
            }
            catch (ExternalException)
            {
                Thread.Sleep(20);
            }
        }

        return false;
    }

    private static Icon CreateTrayIcon()
    {
        const int size = 64;
        try
        {
            using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.Clear(Color.Transparent);
                DrawLargeV(graphics, size);
            }

            return IconFromBitmap(bitmap);
        }
        catch
        {
            return (Icon)SystemIcons.Application.Clone();
        }
    }

    private static void DrawLargeV(Graphics graphics, int size)
    {
        using FontFamily family = CreateIconFont();
        using var path = new GraphicsPath();
        path.AddString("V", family, (int)FontStyle.Bold, 100, Point.Empty, StringFormat.GenericDefault);

        RectangleF bounds = path.GetBounds();
        const float margin = 1f;
        float available = size - margin * 2;
        float scale = Math.Min(available / bounds.Width, available / bounds.Height);
        using var matrix = new Matrix();
        matrix.Translate(-bounds.X, -bounds.Y);
        matrix.Scale(scale, scale, MatrixOrder.Append);
        float dx = margin + (available - bounds.Width * scale) / 2f;
        float dy = margin + (available - bounds.Height * scale) / 2f;
        matrix.Translate(dx, dy, MatrixOrder.Append);
        path.Transform(matrix);

        using var brush = new SolidBrush(Color.FromArgb(0, 51, 102));
        graphics.FillPath(brush, path);
    }

    private static FontFamily CreateIconFont()
    {
        try
        {
            return new FontFamily("Segoe UI");
        }
        catch (ArgumentException)
        {
            return new FontFamily(FontFamily.GenericSansSerif.Name);
        }
    }

    private static Icon IconFromBitmap(Bitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        int xorStride = width * 4;
        int xorSize = xorStride * height;
        int maskStride = ((width + 31) / 32) * 4;
        int maskSize = maskStride * height;
        int imageSize = 40 + xorSize + maskSize;

        using var stream = new MemoryStream();
        var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write((byte)(width >= 256 ? 0 : width));
        writer.Write((byte)(height >= 256 ? 0 : height));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(imageSize);
        writer.Write(22);

        writer.Write(40);
        writer.Write(width);
        writer.Write(height * 2);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(xorSize + maskSize);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);

        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[xorStride];
            for (int y = height - 1; y >= 0; y--)
            {
                Marshal.Copy(data.Scan0 + (y * data.Stride), row, 0, xorStride);
                writer.Write(row);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        writer.Write(new byte[maskSize]);
        writer.Flush();
        stream.Position = 0;
        using var icon = new Icon(stream);
        Icon clone = (Icon)icon.Clone();
        writer.Dispose();
        return clone;
    }
}
