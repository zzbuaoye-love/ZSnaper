using System.Drawing.Drawing2D;
using ZSnaper.Helpers;
using ZSnaper.Interop;
using ZSnaper.Services;

namespace ZSnaper.Controls;

public sealed class ModernTextEditor : Control
{
    private const int EmGetFirstVisibleLine = 0x00CE;
    private const int EmGetLineCount = 0x00BA;
    private const int EmLineScroll = 0x00B6;

    private readonly ScrollAwareTextBox _editor;
    private readonly ModernScrollBar _scrollBar;
    private readonly PlaceholderLabel _placeholder;
    private string _placeholderText = string.Empty;
    private bool _syncingScroll;

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text
    {
        get => _editor.Text;
        set => _editor.Text = value ?? string.Empty;
    }

    public string PlaceholderText
    {
        get => _placeholderText;
        set
        {
            _placeholderText = value ?? string.Empty;
            _placeholder.Text = _placeholderText;
            UpdatePlaceholder();
        }
    }

    public int TextLength => _editor.TextLength;

    public ModernTextEditor()
    {
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.SupportsTransparentBackColor |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        BackColor = Color.Transparent;
        Size = new Size(420, 220);

        _editor = new ScrollAwareTextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.None,
            BorderStyle = BorderStyle.None,
            AcceptsReturn = true,
            AcceptsTab = true,
            WordWrap = true,
            Font = new Font("Consolas", 10f),
            Location = new Point(14, 12)
        };
        _editor.TextChanged += (_, _) =>
        {
            UpdatePlaceholder();
            QueueScrollSync();
            OnTextChanged(EventArgs.Empty);
        };
        _editor.ScrollChanged += (_, _) => QueueScrollSync();
        _editor.GotFocus += (_, _) => Invalidate();
        _editor.LostFocus += (_, _) => Invalidate();

        _scrollBar = new ModernScrollBar
        {
            Width = 14,
            SmallChange = 3
        };
        _scrollBar.ValueChanged += (_, _) => ScrollEditorTo(_scrollBar.Value);

        _placeholder = new PlaceholderLabel
        {
            AutoSize = false,
            Enabled = false,
            TabStop = false,
            Font = new Font("Microsoft YaHei UI", 8.8f),
            Location = new Point(15, 12),
            Height = 24
        };

        Controls.Add(_editor);
        Controls.Add(_placeholder);
        Controls.Add(_scrollBar);
        _scrollBar.BringToFront();
        _placeholder.BringToFront();

        ThemeManager.ThemeChanged += ApplyTheme;
        ApplyTheme();
    }

    public void Clear() => _editor.Clear();

    public void ApplyTheme()
    {
        var palette = ThemeManager.Palette;
        _editor.BackColor = palette.InputBg;
        _editor.ForeColor = palette.TextPrimary;
        _placeholder.BackColor = palette.InputBg;
        _placeholder.ForeColor = palette.TextMuted;
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        QueueScrollSync();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
        {
            QueueScrollSync();
        }
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        if (_editor != null)
        {
            _editor.Font = Font;
            QueueScrollSync();
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_scrollBar == null || _editor == null || _placeholder == null)
        {
            return;
        }

        int scrollWidth = 16;
        _scrollBar.Bounds = new Rectangle(Width - scrollWidth - 3, 8, scrollWidth, Math.Max(1, Height - 16));
        _editor.Bounds = new Rectangle(14, 12, Math.Max(1, Width - scrollWidth - 26), Math.Max(1, Height - 24));
        _placeholder.Width = Math.Max(1, _editor.Width - 4);
        QueueScrollSync();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!_scrollBar.Bounds.Contains(e.Location))
        {
            _editor.Focus();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var palette = ThemeManager.Palette;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        using GraphicsPath path = GraphicsHelper.GetRoundedRectangle(rect, 12);
        using (var fillBrush = new SolidBrush(palette.InputBg))
        {
            graphics.FillPath(fillBrush, path);
        }

        Color border = _editor.Focused
            ? Color.FromArgb(150, palette.AccentColor)
            : palette.InputBorder;
        using var borderPen = new Pen(border, _editor.Focused ? 1.4f : 1f);
        graphics.DrawPath(borderPen, path);
    }

    private void UpdatePlaceholder()
    {
        _placeholder.Visible = _editor.TextLength == 0 && !string.IsNullOrWhiteSpace(_placeholderText);
    }

    private void QueueScrollSync()
    {
        if (!IsHandleCreated || IsDisposed) return;
        BeginInvoke(SyncScrollBar);
    }

    private void SyncScrollBar()
    {
        if (_syncingScroll || !_editor.IsHandleCreated || _editor.IsDisposed) return;

        int lineCount = Math.Max(1, (int)NativeMethods.SendMessage(_editor.Handle, EmGetLineCount, 0, 0));
        int visibleLines = Math.Max(1, (int)Math.Floor(_editor.ClientSize.Height / Math.Max(1f, _editor.Font.GetHeight())));
        int firstVisibleLine = Math.Max(0, (int)NativeMethods.SendMessage(_editor.Handle, EmGetFirstVisibleLine, 0, 0));

        _syncingScroll = true;
        try
        {
            _scrollBar.LargeChange = visibleLines;
            _scrollBar.Maximum = Math.Max(0, lineCount - visibleLines);
            _scrollBar.Value = Math.Min(firstVisibleLine, _scrollBar.Maximum);
            _scrollBar.Visible = _scrollBar.Maximum > 0;
        }
        finally
        {
            _syncingScroll = false;
        }
    }

    private void ScrollEditorTo(int targetLine)
    {
        if (_syncingScroll || !_editor.IsHandleCreated) return;
        int currentLine = (int)NativeMethods.SendMessage(_editor.Handle, EmGetFirstVisibleLine, 0, 0);
        int delta = targetLine - currentLine;
        if (delta == 0) return;

        NativeMethods.SendMessage(_editor.Handle, EmLineScroll, 0, delta);
        QueueScrollSync();
    }

    public void ScrollByLines(int lines)
    {
        if (_scrollBar == null || !_scrollBar.Visible || _scrollBar.Maximum <= 0) return;
        int newValue = Math.Clamp(_scrollBar.Value + lines, 0, _scrollBar.Maximum);
        if (newValue != _scrollBar.Value)
        {
            _scrollBar.Value = newValue;
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int lines = -Math.Sign(e.Delta) * Math.Max(1, SystemInformation.MouseWheelScrollLines);
        ScrollByLines(lines);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ThemeManager.ThemeChanged -= ApplyTheme;
        }

        base.Dispose(disposing);
    }

    private sealed class PlaceholderLabel : Label
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                ClientRectangle,
                ForeColor,
                TextFormatFlags.Left |
                TextFormatFlags.Top |
                TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPadding);
        }
    }

    private sealed class ScrollAwareTextBox : TextBox
    {
        private const int WmVScroll = 0x0115;
        private const int WmMouseWheel = 0x020A;
        private const int WmKeyUp = 0x0101;
        private const int WmChar = 0x0102;

        public event EventHandler? ScrollChanged;

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmMouseWheel)
            {
                short delta = (short)((long)message.WParam >> 16);
                int lines = -Math.Sign(delta) * Math.Max(1, SystemInformation.MouseWheelScrollLines);
                if (Parent is ModernTextEditor editor)
                {
                    editor.ScrollByLines(lines);
                    message.Result = IntPtr.Zero;
                    return;
                }
            }

            base.WndProc(ref message);
            if (message.Msg is WmVScroll or WmMouseWheel or WmKeyUp or WmChar)
            {
                ScrollChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
