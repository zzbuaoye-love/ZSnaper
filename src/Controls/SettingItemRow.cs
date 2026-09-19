using System.Drawing.Drawing2D;
using ZSnaper.Models;
using ZSnaper.Services;

namespace ZSnaper.Controls;

public class SettingItemRow : Control
{
    private string _title = "设置项";
    private string _description = string.Empty;
    private bool _showDivider = true;
    private Control? _actionControl;
    private bool _isHovered;

    public string Title
    {
        get => _title;
        set
        {
            _title = value;
            Invalidate();
        }
    }

    public string Description
    {
        get => _description;
        set
        {
            _description = value;
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
                if (_actionControl != null) Controls.Remove(_actionControl);
                _actionControl = value;
                if (_actionControl != null)
                {
                    Controls.Add(_actionControl);
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

    private void PositionActionControl()
    {
        if (_actionControl != null)
        {
            _actionControl.Location = new Point(Width - _actionControl.Width - 16, (Height - _actionControl.Height) / 2);
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

        if (_isHovered)
        {
            Color hoverColor = palette.Mode == ThemeMode.Dark
                ? Color.FromArgb(16, 255, 255, 255)
                : Color.FromArgb(9, 15, 23, 42);
            using var hoverBrush = new SolidBrush(hoverColor);
            g.FillRectangle(hoverBrush, 1, 1, Math.Max(0, Width - 2), Math.Max(0, Height - 2));
        }

        if (string.IsNullOrEmpty(_description))
        {
            // 单行标题居中
            using var titleFont = new Font("Microsoft YaHei UI", 9.2f, FontStyle.Regular);
            using var titleBrush = new SolidBrush(palette.TextPrimary);
            g.DrawString(_title, titleFont, titleBrush, 16, (Height - 18) / 2);
        }
        else
        {
            // 主标题 + 副标题
            using var titleFont = new Font("Microsoft YaHei UI", 9.2f, FontStyle.Regular);
            using var titleBrush = new SolidBrush(palette.TextPrimary);
            g.DrawString(_title, titleFont, titleBrush, 16, 7);

            using var descFont = new Font("Microsoft YaHei UI", 8f, FontStyle.Regular);
            using var descBrush = new SolidBrush(palette.TextMuted);
            g.DrawString(_description, descFont, descBrush, 16, 28);
        }

        // 底部分割线
        if (_showDivider)
        {
            using var pen = new Pen(palette.CardBorder, 1f);
            g.DrawLine(pen, 16, Height - 1, Width - 16, Height - 1);
        }
    }
}
