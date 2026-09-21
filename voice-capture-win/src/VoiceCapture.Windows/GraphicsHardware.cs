using System.Runtime.InteropServices;

namespace VoiceCapture.Windows;

public static class GraphicsHardware
{
    // Enumerates adapters, including those not currently driving a monitor; no WMI/PowerShell process.
    public static HardwareProfile Detect()
    {
        var names = new List<string>();
        try
        {
            for (uint index = 0; index < 64; index++)
            {
                var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
                if (!EnumDisplayDevices(null, index, ref device, 0)) break;
                if ((device.Flags & 8) != 0 || string.IsNullOrWhiteSpace(device.Description)) continue; // mirroring driver
                if (!names.Contains(device.Description, StringComparer.OrdinalIgnoreCase)) names.Add(device.Description);
            }
        }
        catch { return new(Array.Empty<string>()); }
        return new(names.AsReadOnly());
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice info, uint flags);
}
