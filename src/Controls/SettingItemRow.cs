using System.Drawing.Drawing2D;
using ZSnaper.Models;
using ZSnaper.Helpers;
using ZSnaper.Services;

namespace ZSnaper.Controls;

public class SettingItemRow : Control
{
    private string _title = "设置项";
    private string _description = string.Empty;
    private bool _showDivider = true;
    private Control? _actionControl;
    private bool _isHovered;
    private readonly Font _titleFont = new("Microsoft YaHei UI", 10f);
    private readonly Font _descriptionFont = new("Microsoft YaHei UI", 8.5f);

    public string Title
    {
        get => _title;
        set
        {
            _title = value;
            if (_actionControl is not null) _actionControl.AccessibleName = value;
            Invalidate();
        }
    }

    public string Description
    {
        get => _description;
        set
        {
            _description = value;
            if (_actionControl is not null) _actionControl.AccessibleDescription = value;
            Invalidate();
        }
    }

    public bool ShowDivider
    {
        get => _showDivider;
        set
        {
            _showDivider = value;
            Invalidate();
        }
    }

    public Control? ActionControl
    {
        get => _actionControl;
        set
        {
            if (_actionControl != value)
            {
                if (_actionControl != null)
                {
                    _actionControl.SizeChanged -= ActionSizeChanged;
                    Controls.Remove(_actionControl);
                }
                _actionControl = value;
                if (_actionControl != null)
                {
                    Controls.Add(_actionControl);
                    _actionControl.SizeChanged += ActionSizeChanged;
                    if (string.IsNullOrWhiteSpace(_actionControl.AccessibleName)) _actionControl.AccessibleName = _title;
                    _actionControl.AccessibleDescription = _description;
                    PositionActionControl();
                }
            }
        }
    }

    public SettingItemRow()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Color.Transparent;
        Size = new Size(400, 52);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        PositionActionControl();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _isHovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _isHovered = ClientRectangle.Contains(PointToClient(MousePosition));
        Invalidate();
    }

    private void ActionSizeChanged(object? sender, EventArgs e) => PositionActionControl();

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        PositionActionControl();
    }

    private void PositionActionControl()
    {
        if (_actionControl != null)
        {
            _actionControl.Location = new Point(Width - _actionControl.Width - GraphicsHelper.Dip(this, 16), (Height - _actionControl.Height) / 2);
            _actionControl.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var palette = ThemeManager.Palette;
        int D(int value) => GraphicsHelper.Dip(this, value);

        if (_isHovered)
        {
            Color hoverColor = palette.Mode == ThemeMode.Dark
                ? Color.FromArgb(16, 255, 255, 255)
                : Color.FromArgb(9, 15, 23, 42);
            using var hoverBrush = new SolidBrush(hoverColor);
            g.FillRectangle(hoverBrush, 1, 1, Math.Max(0, Width - 2), Math.Max(0, Height - 2));
        }

        int textWidth = Math.Max(0, (_actionControl?.Left ?? Width - D(16)) - D(32));
        const TextFormatFlags textFlags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding |
            TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
        if (string.IsNullOrEmpty(_description))
        {
            // 单行标题居中
            TextRenderer.DrawText(g, _title, _titleFont, new Rectangle(D(16), 0, textWidth, Height),
                palette.TextPrimary, textFlags);
        }
        else
        {
            // 主标题 + 副标题
            TextRenderer.DrawText(g, _title, _titleFont, new Rectangle(D(16), D(6), textWidth, D(20)),
                palette.TextPrimary, textFlags);
            TextRenderer.DrawText(g, _description, _descriptionFont, new Rectangle(D(16), D(28), textWidth, D(18)),
                palette.TextMuted, textFlags);
        }

        // 底部分割线
        if (_showDivider)
        {
            using var pen = new Pen(palette.CardBorder, 1f);
            g.DrawLine(pen, D(16), Height - 1, Width - D(16), Height - 1);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _titleFont.Dispose();
            _descriptionFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
