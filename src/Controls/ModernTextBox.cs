using System.Drawing.Drawing2D;
using ZSnaper.Helpers;
using ZSnaper.Models;
using ZSnaper.Services;

namespace ZSnaper.Controls;

public sealed class ModernTextBox : Control
{
    private readonly TextBox _editor;
    private bool _hovered;

    public int CornerRadius { get; set; } = 7;

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text
    {
        get => _editor.Text;
        set => _editor.Text = value ?? string.Empty;
    }

    public string PlaceholderText
    {
        get => _editor.PlaceholderText;
        set => _editor.PlaceholderText = value ?? string.Empty;
    }

    public bool UseSystemPasswordChar
    {
        get => _editor.UseSystemPasswordChar;
        set => _editor.UseSystemPasswordChar = value;
    }

    public bool Multiline
    {
        get => _editor.Multiline;
        set
        {
            _editor.Multiline = value;
            _editor.AcceptsReturn = value;
            _editor.WordWrap = true;
            _editor.ScrollBars = ScrollBars.None;
            UpdateEditorBounds();
        }
    }

    public new bool Enabled
    {
        get => base.Enabled;
        set
        {
            base.Enabled = value;
            _editor.Enabled = value;
            ApplyTheme();
            Invalidate();
        }
    }

    public void Clear() => _editor.Clear();
    public new bool Focus() => _editor.Focus();
    public void SelectAll() => _editor.SelectAll();

    public ModernTextBox()
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
        Size = new Size(300, 32);

        _editor = new TextBox
        {
            BorderStyle = BorderStyle.None,
            Font = new Font("Microsoft YaHei UI", 8.8f),
            Location = new Point(10, 7)
        };
        _editor.GotFocus += (_, _) => Invalidate();
        _editor.LostFocus += (_, _) => Invalidate();
        _editor.TextChanged += (_, _) => OnTextChanged(EventArgs.Empty);
        _editor.KeyDown += (_, e) => OnKeyDown(e);
        _editor.KeyUp += (_, e) => OnKeyUp(e);

        Controls.Add(_editor);

        MouseEnter += (_, _) => { _hovered = true; Invalidate(); };
        MouseLeave += (_, _) => { _hovered = false; Invalidate(); };
        MouseDown += (_, _) => _editor.Focus();

        ThemeManager.ThemeChanged += ApplyTheme;
        Disposed += (_, _) => ThemeManager.ThemeChanged -= ApplyTheme;
        ApplyTheme();
    }

    public void ApplyTheme()
    {
        if (IsDisposed) return;
        ThemePalette palette = ThemeManager.Palette;
        _editor.BackColor = Enabled ? palette.InputBg : palette.CardBg;
        _editor.ForeColor = Enabled ? palette.TextPrimary : palette.TextMuted;
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateEditorBounds();
    }

    private void UpdateEditorBounds()
    {
        if (_editor == null) return;
        if (_editor.Multiline)
        {
            _editor.SetBounds(10, 8, Math.Max(10, Width - 20), Math.Max(10, Height - 16));
        }
        else
        {
            int top = Math.Max(4, (Height - _editor.PreferredHeight) / 2);
            _editor.SetBounds(10, top, Math.Max(10, Width - 20), _editor.PreferredHeight);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        ThemePalette palette = ThemeManager.Palette;
        Rectangle rect = new(0, 0, Width - 1, Height - 1);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        using GraphicsPath path = GraphicsHelper.GetRoundedRectangle(rect, CornerRadius);

        Color bg = Enabled ? palette.InputBg : Color.FromArgb(120, palette.CardBg);
        using (var fillBrush = new SolidBrush(bg))
        {
            g.FillPath(fillBrush, path);
        }

        Color borderColor;
        float penWidth = 1f;
        if (!Enabled)
        {
            borderColor = Color.FromArgb(40, palette.CardBorder);
        }
        else if (_editor.Focused)
        {
            borderColor = palette.AccentColor;
            penWidth = 1.4f;
        }
        else if (_hovered)
        {
            borderColor = Color.FromArgb(160, palette.TextSecondary);
        }
        else
        {
            borderColor = palette.CardBorder;
        }

        using var pen = new Pen(borderColor, penWidth);
        g.DrawPath(pen, path);
    }
}

