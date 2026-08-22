using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace FlowType.Core;

/// <summary>
/// Cheap look at the installed display adapters, used only to pick a sensible
/// default model. Deliberately avoids WMI (slow, needs System.Management) —
/// EnumDisplayDevices answers in microseconds.
/// </summary>
public static class GpuInfo
{
    private static readonly Lazy<string[]> Adapters = new(Enumerate);

    /// <summary>Adapter names as Windows reports them, e.g. "NVIDIA GeForce RTX 4070".</summary>
    public static IReadOnlyList<string> AdapterNames => Adapters.Value;

    /// <summary>
    /// True when a dedicated GPU is present. Integrated parts ("AMD Radeon(TM)
    /// Graphics", "Intel(R) UHD Graphics", "Intel(R) Iris(R) Xe") run whisper
    /// slower than the CPU next to them, so they deliberately don't count.
    /// </summary>
    public static bool HasDiscreteGpu => Adapters.Value.Any(IsDiscrete);

    internal static bool IsDiscrete(string name) =>
        Regex.IsMatch(name, @"NVIDIA|GeForce|Quadro|Radeon\s+(RX|PRO|VII)|Arc\s+[AB]\d",
            RegexOptions.IgnoreCase)
        && !Regex.IsMatch(name, @"Radeon\(TM\)\s+Graphics|Integrated|UHD|Iris",
            RegexOptions.IgnoreCase);

    private static string[] Enumerate()
    {
        var names = new List<string>();
        try
        {
            var device = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            for (uint i = 0; EnumDisplayDevices(null, i, ref device, 0); i++)
            {
                if (!string.IsNullOrWhiteSpace(device.DeviceString)
                    && !names.Contains(device.DeviceString))
                {
                    names.Add(device.DeviceString);
                }
                device.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
            }
        }
        catch
        {
            // No adapter info is not an error; the caller falls back to the CPU pick.
        }
        return names.ToArray();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(
        string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);
}
