using System.Drawing.Drawing2D;
using System.Drawing.Text;
using ZSnaper.Helpers;
using ZSnaper.Models;
using ZSnaper.Services;

namespace ZSnaper.Controls;

/// <summary>
/// 与设置页主题一致的自绘下拉选择器。
/// </summary>
public sealed class ModernDropdown : Control
{
    private readonly List<string> _items = [];
    private ToolStripDropDown? _dropDown;
    private ModernDropdownList? _dropDownList;
    private int _selectedIndex = -1;
    private bool _isHovered;
    private bool _isPressed;

    public IReadOnlyList<string> Items => _items;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            int normalized = _items.Count == 0 ? -1 : Math.Clamp(value, 0, _items.Count - 1);
            if (_selectedIndex == normalized) return;

            _selectedIndex = normalized;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string? SelectedItem =>
        _selectedIndex >= 0 && _selectedIndex < _items.Count ? _items[_selectedIndex] : null;

    public event EventHandler? SelectedIndexChanged;

    public ModernDropdown()
    {
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.SupportsTransparentBackColor |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.Selectable |
            ControlStyles.ResizeRedraw,
            true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular);
        Size = new Size(128, 32);
        TabStop = true;
        AccessibleRole = AccessibleRole.ComboBox;
        ThemeManager.ThemeChanged += OnThemeChanged;
    }

    public void SetItems(IEnumerable<string> items)
    {
        CloseDropDown();
        _items.Clear();
        _items.AddRange(items.Where(item => !string.IsNullOrWhiteSpace(item)));
        _selectedIndex = _items.Count == 0 ? -1 : Math.Clamp(_selectedIndex, 0, _items.Count - 1);
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        ToggleDropDown();
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
        _isHovered = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            Focus();
            _isPressed = true;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _isPressed = false;
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            ToggleDropDown();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (_items.Count == 0 || e.KeyCode is not (Keys.Up or Keys.Down or Keys.Home or Keys.End)) return;

        int nextIndex = e.KeyCode switch
        {
            Keys.Home => 0,
            Keys.End => _items.Count - 1,
            Keys.Up => Math.Max(0, (_selectedIndex < 0 ? 0 : _selectedIndex) - 1),
            _ => Math.Min(_items.Count - 1, (_selectedIndex < 0 ? -1 : _selectedIndex) + 1)
        };
        SelectedIndex = nextIndex;
        e.Handled = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width <= 1 || Height <= 1) return;

        Graphics graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        ThemePalette palette = ThemeManager.Palette;

        Rectangle bounds = new(1, 1, Width - 3, Height - 3);
        using GraphicsPath path = GraphicsHelper.GetRoundedRectangle(bounds, GraphicsHelper.Dip(this, 12));
        using (var brush = new SolidBrush(palette.InputBg))
        {
            graphics.FillPath(brush, path);
        }
        if (_isHovered || _isPressed)
        {
            using var state = new SolidBrush(Color.FromArgb(_isPressed ? 20 : 10, palette.TextPrimary));
            graphics.FillPath(state, path);
        }
        using (var borderPen = new Pen(Focused ? palette.AccentColor : palette.InputBorder, Focused ? 2f : 1f))
        {
            graphics.DrawPath(borderPen, path);
        }

        TextRenderer.DrawText(
            graphics,
            SelectedItem ?? string.Empty,
            Font,
            new Rectangle(GraphicsHelper.Dip(this, 10), 0, Math.Max(1, Width - GraphicsHelper.Dip(this, 34)), Height),
            palette.TextPrimary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

        using var arrowPen = new Pen(palette.TextSecondary, 1.35f)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round,
            LineJoin = LineJoin.Round
        };
        float arrowX = Width - GraphicsHelper.Dip(this, 17);
        float arrowY = Height / 2f - 2f;
        graphics.DrawLine(arrowPen, arrowX - 3f, arrowY, arrowX, arrowY + 3f);
        graphics.DrawLine(arrowPen, arrowX, arrowY + 3f, arrowX + 3f, arrowY);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ThemeManager.ThemeChanged -= OnThemeChanged;
            CloseDropDown();
        }

        base.Dispose(disposing);
    }

    private void ToggleDropDown()
    {
        if (_dropDown is not null)
        {
            CloseDropDown();
            return;
        }

        if (_items.Count == 0) return;

        var list = new ModernDropdownList(_items, _selectedIndex)
        {
            Font = Font,
            Size = new Size(Width, _items.Count * ModernDropdownList.RowHeight)
        };
        list.ItemSelected += OnDropDownItemSelected;
        EventHandler escapeHandler = (_, _) => CloseDropDown();
        list.EscapePressed += escapeHandler;

        var popup = new ToolStripDropDown
        {
            AutoSize = false,
            Padding = new Padding(1),
            Margin = Padding.Empty,
            BackColor = ThemeManager.Palette.InputBg,
            ForeColor = ThemeManager.Palette.TextPrimary,
            Renderer = new ModernDropdownRenderer()
        };
        var host = new ToolStripControlHost(list)
        {
            AutoSize = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            Size = list.Size
        };
        popup.Items.Add(host);
        popup.Size = new Size(Width + 2, list.Height + 2);
        popup.Closed += (_, _) =>
        {
            _dropDown = null;
            _dropDownList = null;
            list.ItemSelected -= OnDropDownItemSelected;
            list.EscapePressed -= escapeHandler;
            Invalidate();
            Focus();
        };

        _dropDown = popup;
        _dropDownList = list;
        popup.Show(this, new Point(0, Height + 2));
        list.Focus();
    }

    private void OnDropDownItemSelected(object? sender, int index)
    {
        SelectedIndex = index;
        CloseDropDown();
    }

    private void CloseDropDown()
    {
        ToolStripDropDown? popup = _dropDown;
        _dropDown = null;
        _dropDownList = null;
        popup?.Close();
        Invalidate();
    }

    private void OnThemeChanged()
    {
        Invalidate();
        _dropDownList?.Invalidate();
        _dropDown?.Invalidate();
    }
}

internal sealed class ModernDropdownList : Control
{
    public const int RowHeight = 32;

    private readonly IReadOnlyList<string> _items;
    private int _selectedIndex;
    private int _hoveredIndex = -1;

    public event EventHandler<int>? ItemSelected;
    public event EventHandler? EscapePressed;

    public ModernDropdownList(IReadOnlyList<string> items, int selectedIndex)
    {
        _items = items;
        _selectedIndex = selectedIndex;
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.SupportsTransparentBackColor |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.Selectable |
            ControlStyles.ResizeRedraw,
            true);
        BackColor = Color.Transparent;
        TabStop = true;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hovered = HitTest(e.Location);
        if (_hoveredIndex == hovered) return;
        _hoveredIndex = hovered;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoveredIndex = -1;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;

        int selected = HitTest(e.Location);
        if (selected >= 0) ItemSelected?.Invoke(this, selected);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.KeyCode == Keys.Escape)
        {
            EscapePressed?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            int selected = _hoveredIndex >= 0 ? _hoveredIndex : _selectedIndex;
            if (selected >= 0) ItemSelected?.Invoke(this, selected);
            e.Handled = true;
            return;
        }

        if (e.KeyCode is not (Keys.Up or Keys.Down or Keys.Home or Keys.End)) return;

        int current = _hoveredIndex >= 0 ? _hoveredIndex : _selectedIndex;
        _hoveredIndex = e.KeyCode switch
        {
            Keys.Home => 0,
            Keys.End => _items.Count - 1,
            Keys.Up => Math.Max(0, current - 1),
            _ => Math.Min(_items.Count - 1, current + 1)
        };
        Invalidate();
        e.Handled = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        ThemePalette palette = ThemeManager.Palette;

        using (var background = new SolidBrush(palette.InputBg))
        {
            graphics.FillRectangle(background, ClientRectangle);
        }

        for (int index = 0; index < _items.Count; index++)
        {
            Rectangle row = new(4, index * RowHeight + 2, Math.Max(1, Width - 8), RowHeight - 4);
            bool selected = index == _selectedIndex;
            bool hovered = index == _hoveredIndex;
            Color rowColor = selected
                ? palette.PrimaryContainer
                : hovered
                    ? palette.NavItemHover
                    : Color.Transparent;

            if (rowColor != Color.Transparent)
            {
                using GraphicsPath rowPath = GraphicsHelper.GetRoundedRectangle(row, 12);
                using var rowBrush = new SolidBrush(rowColor);
                graphics.FillPath(rowBrush, rowPath);
            }

            TextRenderer.DrawText(
                graphics,
                _items[index],
                Font,
                new Rectangle(row.Left + 8, row.Top, Math.Max(1, row.Width - 16), row.Height),
                selected ? palette.OnPrimaryContainer : palette.TextPrimary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }

        using var borderPen = new Pen(palette.InputBorder, 1f);
        graphics.DrawRectangle(borderPen, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
    }

    private int HitTest(Point point)
    {
        if (point.X < 0 || point.X >= Width || point.Y < 0 || point.Y >= Height) return -1;
        int index = point.Y / RowHeight;
        return index >= 0 && index < _items.Count ? index : -1;
    }
}

internal sealed class ModernDropdownRenderer : ToolStripProfessionalRenderer
{
    public ModernDropdownRenderer()
        : base(new ModernDropdownColorTable())
    {
        RoundedEdges = false;
    }
}

internal sealed class ModernDropdownColorTable : ProfessionalColorTable
{
    private static ThemePalette Palette => ThemeManager.Palette;

    public override Color ToolStripDropDownBackground => Palette.InputBg;
    public override Color ImageMarginGradientBegin => Palette.InputBg;
    public override Color ImageMarginGradientMiddle => Palette.InputBg;
    public override Color ImageMarginGradientEnd => Palette.InputBg;
    public override Color MenuBorder => Palette.InputBorder;
    public override Color MenuItemBorder => Color.Transparent;
    public override Color MenuItemSelected => Color.Transparent;
    public override Color MenuItemSelectedGradientBegin => Color.Transparent;
    public override Color MenuItemSelectedGradientEnd => Color.Transparent;
}
