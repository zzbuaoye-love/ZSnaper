namespace ZSnaper.Plugins;

/// <summary>Optional API 1.1 capability; API 1.0 plugins keep using IZSnaperHost.</summary>
public interface IZSnaperOverlayHost : IZSnaperHost
{
    IZSnaperOverlayApi Overlay { get; }
}

public interface IZSnaperOverlayApi
{
    PluginArrowRegistration RegisterArrow(PluginArrowToolDefinition tool, PluginArrowRenderer renderer);
    bool UnregisterArrow(string id);
}

public sealed class PluginArrowToolDefinition
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public int Order { get; init; }
}

public sealed record PluginArrowRegistration(string Id);
public readonly record struct PluginPoint(float X, float Y);
public readonly record struct PluginArrowContext(PluginPoint Start, PluginPoint End, float Width);

/// <summary>Image-pixel coordinates; the host supplies color and stroke width.</summary>
public sealed class PluginArrowPath
{
    public required IReadOnlyList<PluginPoint> Points { get; init; }
    public bool Closed { get; init; }
    public bool Filled { get; init; }
}

/// <summary>Pure geometry callback on the UI thread; do not perform I/O or retain host objects.</summary>
public delegate IReadOnlyList<PluginArrowPath> PluginArrowRenderer(PluginArrowContext context);
