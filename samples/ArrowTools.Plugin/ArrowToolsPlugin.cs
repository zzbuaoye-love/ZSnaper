using ZSnaper.Plugins;

namespace ArrowTools.Plugin;

public sealed class ArrowToolsPlugin : IZSnaperPlugin
{
    private IZSnaperOverlayApi? _overlay;
    private readonly List<PluginArrowRegistration> _registrations = [];
    public PluginManifest Manifest { get; } = new()
    {
        Id = "zsnaper.arrow-tools", Name = "箭头扩展", Version = "1.0.0"
    };

    public ValueTask InitializeAsync(IZSnaperHost host, CancellationToken cancellationToken = default)
    {
        _overlay = (host as IZSnaperOverlayHost)?.Overlay
            ?? throw new NotSupportedException("箭头扩展需要 Plugin API 1.1 或更高版本");
        return ValueTask.CompletedTask;
    }

    public ValueTask EnableAsync(CancellationToken cancellationToken = default)
    {
        if (_overlay is null) throw new InvalidOperationException("插件尚未初始化");
        if (_registrations.Count > 0) return ValueTask.CompletedTask;
        _registrations.Add(_overlay.RegisterArrow(new() { Id = "hollow", Label = "空心箭头" }, DrawHollow));
        _registrations.Add(_overlay.RegisterArrow(new() { Id = "elbow", Label = "折线箭头", Order = 1 }, DrawElbow));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisableAsync(CancellationToken cancellationToken = default)
    {
        if (_overlay is not null)
            foreach (var registration in _registrations) _overlay.UnregisterArrow(registration.Id);
        _registrations.Clear();
        return ValueTask.CompletedTask;
    }

    public async ValueTask ShutdownAsync(CancellationToken cancellationToken = default)
    {
        await DisableAsync(cancellationToken);
        _overlay = null;
    }

    private static IReadOnlyList<PluginArrowPath> DrawHollow(PluginArrowContext context)
    {
        float dx = context.End.X - context.Start.X, dy = context.End.Y - context.Start.Y;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 1f) return [];
        float ux = dx / length, uy = dy / length;
        float shaft = MathF.Min(MathF.Max(context.Width * 1.5f, 4f), length * .12f);
        float headLength = MathF.Min(MathF.Max(context.Width * 8f, 26f), length * .45f);
        float headWidth = MathF.Min(shaft * 2.8f, headLength * .7f);
        PluginPoint P(float along, float across) => new(
            context.Start.X + ux * along - uy * across,
            context.Start.Y + uy * along + ux * across);
        return [new() { Closed = true, Points = [
            P(0, -shaft), P(length - headLength, -shaft), P(length - headLength, -headWidth),
            context.End, P(length - headLength, headWidth), P(length - headLength, shaft), P(0, shaft)] }];
    }

    private static IReadOnlyList<PluginArrowPath> DrawElbow(PluginArrowContext context)
    {
        float dx = context.End.X - context.Start.X, dy = context.End.Y - context.Start.Y;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 1f) return [];
        bool horizontalLast = Math.Abs(dx) >= Math.Abs(dy);
        PluginPoint corner = horizontalLast ? new(context.Start.X, context.End.Y) : new(context.End.X, context.Start.Y);
        float angle = horizontalLast ? (dx >= 0 ? 0 : MathF.PI) : (dy >= 0 ? MathF.PI / 2 : -MathF.PI / 2);
        float head = MathF.Min(MathF.Max(context.Width * 4.2f, 12f), length * .4f);
        PluginPoint Side(float spread) => new(context.End.X - head * MathF.Cos(angle + spread),
            context.End.Y - head * MathF.Sin(angle + spread));
        return [new() { Points = [context.Start, corner, context.End] },
            new() { Points = [Side(-.58f), context.End, Side(.58f)] }];
    }
}
