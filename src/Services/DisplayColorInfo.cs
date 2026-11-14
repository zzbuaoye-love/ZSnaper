using System.Runtime.InteropServices;

namespace ZSnaper.Services;

/// <summary>Reads the Windows SDR white level without changing display settings.</summary>
internal static class DisplayColorInfo
{
    internal static float GetSdrWhiteScale(string deviceName)
    {
        const uint activePaths = 2;
        if (GetDisplayConfigBufferSizes(activePaths, out uint pathCount, out uint modeCount) != 0) return 1f;
        var paths = new DisplayPath[pathCount];
        nint modes = Marshal.AllocHGlobal(checked((int)modeCount * 64)); // DISPLAYCONFIG_MODE_INFO
        try
        {
            if (QueryDisplayConfig(activePaths, ref pathCount, paths, ref modeCount, modes, 0) != 0) return 1f;
            foreach (DisplayPath path in paths.Take((int)pathCount))
            {
                var source = new SourceName
                {
                    Header = new DeviceHeader { Type = 1, Size = (uint)Marshal.SizeOf<SourceName>(),
                        Adapter = path.SourceAdapter, Id = path.SourceId },
                    Name = string.Empty
                };
                if (GetSourceName(ref source) != 0 || !string.Equals(source.Name, deviceName, StringComparison.OrdinalIgnoreCase))
                    continue;
                var white = new SdrWhiteLevel
                {
                    Header = new DeviceHeader { Type = 11, Size = (uint)Marshal.SizeOf<SdrWhiteLevel>(),
                        Adapter = path.TargetAdapter, Id = path.TargetId }
                };
                if (GetSdrWhiteLevel(ref white) == 0 && white.Level > 0) return white.Level / 1000f;
            }
        }
        finally { Marshal.FreeHGlobal(modes); }
        return 1f;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct AdapterId { public uint Low; public int High; }

    [StructLayout(LayoutKind.Explicit, Size = 72, Pack = 4)]
    internal struct DisplayPath
    {
        [FieldOffset(0)] public AdapterId SourceAdapter;
        [FieldOffset(8)] public uint SourceId;
        [FieldOffset(20)] public AdapterId TargetAdapter;
        [FieldOffset(28)] public uint TargetId;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct DeviceHeader { public uint Type, Size; public AdapterId Adapter; public uint Id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    private struct SourceName
    {
        public DeviceHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SdrWhiteLevel { public DeviceHeader Header; public uint Level; }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] DisplayPath[] paths,
        ref uint modeCount, nint modes, nint topology);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", CharSet = CharSet.Unicode)]
    private static extern int GetSourceName(ref SourceName source);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetSdrWhiteLevel(ref SdrWhiteLevel white);
}
