namespace ZSnaper.Controls;

internal sealed class SegmentedControlAccessibility : Control.ControlAccessibleObject
{
    private readonly Control _control;
    private readonly IReadOnlyList<string> _labels;
    private readonly Func<int> _selected;
    private readonly Action<int> _select;
    private readonly AccessibleRole _itemRole;

    internal SegmentedControlAccessibility(Control control, IReadOnlyList<string> labels,
        Func<int> selected, Action<int> select, AccessibleRole itemRole = AccessibleRole.RadioButton) : base(control)
    {
        _control = control;
        _labels = labels;
        _selected = selected;
        _select = select;
        _itemRole = itemRole;
    }

    public override int GetChildCount() => _labels.Count;
    public override AccessibleObject? GetChild(int index) => index >= 0 && index < _labels.Count ? new Choice(this, index) : null;

    private sealed class Choice(SegmentedControlAccessibility parent, int index) : AccessibleObject
    {
        public override AccessibleObject Parent => parent;
        public override string? Name { get => parent._labels[index]; set { } }
        public override AccessibleRole Role => parent._itemRole;
        public override string DefaultAction => "选择";
        public override Rectangle Bounds => parent._control.RectangleToScreen(new Rectangle(
            index * parent._control.Width / parent._labels.Count, 0,
            parent._control.Width / parent._labels.Count, parent._control.Height));
        public override AccessibleStates State => AccessibleStates.Selectable | AccessibleStates.Focusable |
            (!parent._control.Enabled ? AccessibleStates.Unavailable : AccessibleStates.None) |
            (index == parent._selected() ? AccessibleStates.Selected |
                (Role == AccessibleRole.RadioButton ? AccessibleStates.Checked : AccessibleStates.None) : AccessibleStates.None);
        public override void DoDefaultAction()
        {
            if (!parent._control.Enabled) return;
            parent._control.Focus();
            parent._select(index);
        }
    }
}
