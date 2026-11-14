using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using ZSnaper.Controls;
using ZSnaper.Helpers;
using ZSnaper.Models;
using ZSnaper.Services;

namespace ZSnaper.Forms;

public partial class MainForm
{
    private const int RecentCaptureLimit = 8;
    private readonly List<RecentCaptureThumbnail> _recentThumbnails = [];
    private readonly ToolTip _recentToolTip = new();
    private ModernCard? _recentCaptureCard;

    private void InitializeRecentCaptures(ModernCard card)
    {
        _recentCaptureCard = card;
        foreach (string path in ConfigService.Current.RecentCaptureFiles.Take(RecentCaptureLimit))
            if (File.Exists(path)) TryAddThumbnail(path, insertFirst: false);
        card.SizeChanged += (_, _) => LayoutRecentCaptures();
        LayoutRecentCaptures();
    }

    private void AddRecentCapture(string path)
    {
        if (!File.Exists(path)) return;
        RecentCaptureThumbnail? previous = _recentThumbnails.Find(item => string.Equals(item.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (previous is not null) { _recentThumbnails.Remove(previous); previous.Dispose(); }
        TryAddThumbnail(path, insertFirst: true);
        while (_recentThumbnails.Count > RecentCaptureLimit)
        {
            RecentCaptureThumbnail oldest = _recentThumbnails[^1];
            _recentThumbnails.RemoveAt(_recentThumbnails.Count - 1);
            oldest.Dispose();
        }
        SaveRecentCaptures();
        LayoutRecentCaptures();
    }

    private void TryAddThumbnail(string path, bool insertFirst)
    {
        RecentCaptureThumbnail? thumbnail = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using Image original = Image.FromStream(stream);
            var small = new Bitmap(128, 96, PixelFormat.Format32bppPArgb);
            try
            {
                using Graphics graphics = Graphics.FromImage(small);
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                float scale = Math.Min(128f / original.Width, 96f / original.Height);
                int width = Math.Max(1, (int)(original.Width * scale)), height = Math.Max(1, (int)(original.Height * scale));
                graphics.DrawImage(original, new Rectangle((128 - width) / 2, (96 - height) / 2, width, height));
                thumbnail = new RecentCaptureThumbnail(path, small);
            }
            catch { small.Dispose(); throw; }
            ModernTrayMenu menu = new();
            menu.AddAction("复制图片", LucideIcon.Copy, (_, _) => RunRecentAction(path, copy: true));
            menu.AddAction("打开图片", LucideIcon.ArrowUpRight, (_, _) => RunRecentAction(path, copy: false));
            menu.AddAction("从记录移除", LucideIcon.Minus, (_, _) => RemoveRecentCapture(path, deleteFile: false));
            menu.AddAction("删除图片", LucideIcon.X, (_, _) =>
            {
                if (MessageBox.Show(this, $"删除这张图片？\n{Path.GetFileName(path)}", "删除截图",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                    RemoveRecentCapture(path, deleteFile: true);
            }, kind: TrayMenuItemKind.Destructive);
            thumbnail.ContextMenuStrip = menu;
            thumbnail.Disposed += (_, _) => menu.Dispose();
            thumbnail.Click += (_, _) => RunRecentAction(path, copy: false);
            _recentToolTip.SetToolTip(thumbnail, $"{Path.GetFileName(path)}\n单击打开 · 右键复制或删除");
            _recentCaptureCard!.Controls.Add(thumbnail);
            thumbnail.BringToFront();
            if (insertFirst) _recentThumbnails.Insert(0, thumbnail); else _recentThumbnails.Add(thumbnail);
        }
        catch (Exception exception)
        {
            thumbnail?.Dispose();
            AppDiagnostics.LogException("RecentCapture.Thumbnail", exception);
        }
    }

    private void RunRecentAction(string path, bool copy)
    {
        try
        {
            if (!File.Exists(path)) { RemoveRecentCapture(path, deleteFile: false); return; }
            if (copy)
            {
                using var image = new Bitmap(path);
                if (!CaptureService.TryCopyToClipboard(image))
                    MessageBox.Show(this, "剪贴板暂时不可用，请稍后重试", "复制截图", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"无法打开截图：{exception.Message}", "最近截图", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void RemoveRecentCapture(string path, bool deleteFile)
    {
        try
        {
            if (deleteFile) File.Delete(path);
            RecentCaptureThumbnail? thumbnail = _recentThumbnails.Find(item => string.Equals(item.FilePath, path, StringComparison.OrdinalIgnoreCase));
            if (thumbnail is not null) { _recentThumbnails.Remove(thumbnail); thumbnail.Dispose(); }
            SaveRecentCaptures();
            LayoutRecentCaptures();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"无法删除截图：{exception.Message}", "最近截图", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void SaveRecentCaptures()
    {
        ConfigService.Current.RecentCaptureFiles = _recentThumbnails.Select(item => item.FilePath).ToList();
        ConfigService.Save();
    }

    private void LayoutRecentCaptures()
    {
        if (_recentCaptureCard is null) return;
        int D(int value) => GraphicsHelper.Dip(_recentCaptureCard, value);
        for (int i = 0; i < _recentThumbnails.Count; i++)
            _recentThumbnails[i].SetBounds(D(8 + i * 52), D(4), D(48), Math.Max(1, _recentCaptureCard.Height - D(8)));
        _homeLatestResultLabel.Visible = _recentThumbnails.Count == 0;
        Control? emptyIcon = _recentCaptureCard.Controls.Cast<Control>().FirstOrDefault(control => control is Label && control != _homeLatestResultLabel);
        if (emptyIcon is not null) emptyIcon.Visible = _recentThumbnails.Count == 0;
        _homeLatestFilePath = _recentThumbnails.FirstOrDefault()?.FilePath;
        _homeOpenResultButton.Visible = _homeLatestFilePath is not null;
    }

    private sealed class RecentCaptureThumbnail(string path, Bitmap image) : Control
    {
        public string FilePath { get; } = path;
        protected override void OnCreateControl()
        {
            base.OnCreateControl();
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            TabStop = true;
            SetStyle(ControlStyles.Selectable, true);
            AccessibleName = $"截图 {Path.GetFileName(FilePath)}";
            AccessibleDescription = "单击或 Enter 打开，菜单可复制或删除";
            AccessibleRole = AccessibleRole.PushButton;
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode is Keys.Enter or Keys.Space) { OnClick(EventArgs.Empty); e.Handled = e.SuppressKeyPress = true; }
        }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Rectangle bounds = Rectangle.Inflate(ClientRectangle, -1, -1);
            using var shape = GraphicsHelper.GetRoundedRectangle(bounds, GraphicsHelper.Dip(this, 8));
            GraphicsState state = e.Graphics.Save();
            e.Graphics.SetClip(shape);
            e.Graphics.Clear(ThemeManager.Palette.SurfaceContainerHigh);
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawImage(image, bounds);
            e.Graphics.Restore(state);
            using var outline = new Pen(Focused ? ThemeManager.Palette.AccentColor : ThemeManager.Palette.CardBorder, Focused ? 2 : 1);
            e.Graphics.DrawPath(outline, shape);
        }
        protected override void Dispose(bool disposing) { if (disposing) image.Dispose(); base.Dispose(disposing); }
    }
}
