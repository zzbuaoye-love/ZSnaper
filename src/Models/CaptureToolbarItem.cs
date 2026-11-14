namespace ZSnaper.Models;

public enum CaptureToolbarItem
{
    Pen = 0,
    Arrow = 1,
    Text = 2,
    Mosaic = 3,
    Style = 4,
    Undo = 5,
    Cursor = 6,
    Ocr = 7,
    Copy = 8,
    Save = 9,
    Pin = 10,
    Reset = 11,
    Cancel = 12,
    Confirm = 13,
    ScrollCapture = 14,
    Redo = 15
}

public enum AnnotationToolBehavior
{
    Sticky,
    SingleUse
}

public enum AnnotationArrowStyle
{
    Open,
    Filled,
    Double
}

public enum CaptureToolbarLayout
{
    Minimal,
    Annotation,
    Recognition,
    Full,
    Custom
}

public enum ConfirmButtonBehavior
{
    Copy,
    Save,
    CopyAndSave,
    FinishOnly,
    FollowWorkflow
}

public static class CaptureToolbarDefaults
{
    public static List<CaptureToolbarItem> CreateItems() =>
    [
        CaptureToolbarItem.Pen,
        CaptureToolbarItem.Arrow,
        CaptureToolbarItem.Text,
        CaptureToolbarItem.Mosaic,
        CaptureToolbarItem.Style,
        CaptureToolbarItem.Undo,
        CaptureToolbarItem.Redo,
        CaptureToolbarItem.Cursor,
        CaptureToolbarItem.ScrollCapture,
        CaptureToolbarItem.Ocr,
        CaptureToolbarItem.Copy,
        CaptureToolbarItem.Save,
        CaptureToolbarItem.Pin,
        CaptureToolbarItem.Reset,
        CaptureToolbarItem.Cancel,
        CaptureToolbarItem.Confirm
    ];

    public static List<CaptureToolbarItem> CreateLayout(CaptureToolbarLayout layout) => layout switch
    {
        CaptureToolbarLayout.Minimal =>
        [
            CaptureToolbarItem.Pen,
            CaptureToolbarItem.Arrow,
            CaptureToolbarItem.Text,
            CaptureToolbarItem.Copy,
            CaptureToolbarItem.Confirm
        ],
        CaptureToolbarLayout.Annotation =>
        [
            CaptureToolbarItem.Pen,
            CaptureToolbarItem.Arrow,
            CaptureToolbarItem.Text,
            CaptureToolbarItem.Mosaic,
            CaptureToolbarItem.Style,
            CaptureToolbarItem.Undo,
        CaptureToolbarItem.Redo,
            CaptureToolbarItem.Copy,
            CaptureToolbarItem.Save,
            CaptureToolbarItem.Pin,
            CaptureToolbarItem.Confirm
        ],
        CaptureToolbarLayout.Recognition =>
        [
            CaptureToolbarItem.Cursor,
            CaptureToolbarItem.ScrollCapture,
            CaptureToolbarItem.Ocr,
            CaptureToolbarItem.Copy,
            CaptureToolbarItem.Save,
            CaptureToolbarItem.Pin,
            CaptureToolbarItem.Reset,
            CaptureToolbarItem.Cancel,
            CaptureToolbarItem.Confirm
        ],
        _ => CreateItems()
    };

    public static string DisplayName(this CaptureToolbarItem item) => item switch
    {
        CaptureToolbarItem.Pen => "批注画笔",
        CaptureToolbarItem.Arrow => "箭头",
        CaptureToolbarItem.Text => "文字",
        CaptureToolbarItem.Mosaic => "马赛克",
        CaptureToolbarItem.Style => "颜色与字体",
        CaptureToolbarItem.Undo => "撤销",
        CaptureToolbarItem.Redo => "重做",
        CaptureToolbarItem.Cursor => "鼠标指针",
        CaptureToolbarItem.ScrollCapture => "长截图",
        CaptureToolbarItem.Ocr => "OCR 识别",
        CaptureToolbarItem.Copy => "复制",
        CaptureToolbarItem.Save => "保存",
        CaptureToolbarItem.Pin => "贴图",
        CaptureToolbarItem.Reset => "重新选择",
        CaptureToolbarItem.Cancel => "取消",
        CaptureToolbarItem.Confirm => "完成",
        _ => item.ToString()
    };
}
