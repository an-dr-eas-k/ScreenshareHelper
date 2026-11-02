using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;

namespace ScreenshareHelper.VirtualMonitor
{
    /// <summary>
    /// A virtual monitor on the desktop while in use: from the Parsec Virtual Display Driver (<see cref="ParsecVirtualDisplay"/>)
    /// or from the open-source Virtual Display Driver (<see cref="VddVirtualDisplay"/>).
    /// </summary>
    public abstract class VirtualDisplay : IDisposable
    {
        /// <summary>GDI device name of the monitor, e.g. \\.\DISPLAY5.</summary>
        public string DeviceName { get; protected set; }

        /// <summary>False once the monitor is gone, e.g. after resume from hibernation or a driver reset.</summary>
        public abstract bool IsAlive { get; }

        /// <summary>Removes the monitor from the desktop again.</summary>
        public abstract void Dispose();

        /// <summary>True if one of the supported drivers is there.</summary>
        public static bool AnyDriverInstalled => ParsecVirtualDisplay.IsDriverInstalled() || VddVirtualDisplay.IsDriverInstalled();

        /// <summary>Uses the Parsec driver if it is installed, otherwise the Virtual Display Driver.</summary>
        public static VirtualDisplay Create() => ParsecVirtualDisplay.IsDriverInstalled() ? new ParsecVirtualDisplay() : new VddVirtualDisplay();

        /// <summary>Monitor bounds in virtual desktop coordinates (physical pixels).</summary>
        public Rectangle Bounds
        {
            get
            {
                var dm = NewDevMode();
                if (!EnumDisplaySettings(DeviceName, ENUM_CURRENT_SETTINGS, ref dm))
                    return Rectangle.Empty;
                return new Rectangle(dm.dmPositionX, dm.dmPositionY, dm.dmPelsWidth, dm.dmPelsHeight);
            }
        }

        /// <summary>
        /// Switches the monitor to <paramref name="resolution"/> (or the closest offered mode), or with null to the mode
        /// that shows <paramref name="content"/> best, and moves it to a corner of the desktop so the mouse does not wander onto it.
        /// </summary>
        public void Configure(Size content, Size? resolution)
        {
            var modes = new List<DEVMODE>();
            var dm = NewDevMode();
            for (int i = 0; EnumDisplaySettings(DeviceName, i, ref dm); i++)
            {
                if (dm.dmBitsPerPel == 32)
                    modes.Add(dm);
                dm = NewDevMode();
            }
            if (modes.Count == 0)
                return;

            // never upscale (adds no detail, costs CPU, Teams scales down anyway): among the modes that fit into
            // the area take the one with the least black bars, then the largest; if none fits, the smallest
            var fitting = modes.Where(m => m.dmPelsWidth <= content.Width && m.dmPelsHeight <= content.Height).ToList();
            var best = resolution.HasValue
                ? modes
                    .OrderBy(m => Math.Abs(m.dmPelsWidth - resolution.Value.Width) + Math.Abs(m.dmPelsHeight - resolution.Value.Height))
                    .ThenBy(m => Math.Abs(m.dmDisplayFrequency - 60))
                    .First()
                : fitting.Count > 0
                ? fitting
                    .OrderByDescending(m => Math.Round(Coverage(content, m.dmPelsWidth, m.dmPelsHeight), 2))
                    .ThenByDescending(m => m.dmPelsWidth * m.dmPelsHeight)
                    .ThenBy(m => Math.Abs(m.dmDisplayFrequency - 60))
                    .First()
                : modes
                    .OrderBy(m => m.dmPelsWidth * m.dmPelsHeight)
                    .ThenByDescending(m => Coverage(content, m.dmPelsWidth, m.dmPelsHeight))
                    .ThenBy(m => Math.Abs(m.dmDisplayFrequency - 60))
                    .First();

            best.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
            var corner = FindCornerPosition(new Size(best.dmPelsWidth, best.dmPelsHeight));
            if (corner.HasValue)
            {
                best.dmPositionX = corner.Value.X;
                best.dmPositionY = corner.Value.Y;
                best.dmFields |= DM_POSITION;
            }

            int result = ChangeDisplaySettingsEx(DeviceName, ref best, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
            if (result != DISP_CHANGE_SUCCESSFUL)
                Debug.WriteLine($"ChangeDisplaySettingsEx({DeviceName}, {best.dmPelsWidth}x{best.dmPelsHeight}) failed: {result}");
        }

        /// <summary>Fraction of the monitor that is covered by the content when scaled to fit (1 = no black bars).</summary>
        private static double Coverage(Size content, int width, int height)
        {
            double scale = Math.Min((double)width / content.Width, (double)height / content.Height);
            return Math.Round(content.Width * scale * content.Height * scale / ((double)width * height), 3);
        }

        /// <summary>
        /// Position touching the most bottom-right monitor only at its corner: Windows keeps the layout, but the
        /// mouse can't cross a corner, so it does not get lost on the invisible monitor. Null if that spot is taken.
        /// </summary>
        private Point? FindCornerPosition(Size size)
        {
            var others = AttachedDisplays()
                .Where(d => !string.Equals(d.name, DeviceName, StringComparison.OrdinalIgnoreCase))
                .Select(d => d.bounds)
                .ToList();
            if (others.Count == 0)
                return null;

            var anchor = others.OrderByDescending(r => r.Right + r.Bottom).First();
            var candidate = new Rectangle(anchor.Right, anchor.Bottom, size.Width, size.Height);
            return others.Any(r => r.IntersectsWith(candidate)) ? null : candidate.Location;
        }

        #region Display enumeration
        /// <summary>Monitors attached to the desktop: GDI device name, bounds (physical pixels), monitor DeviceID.</summary>
        internal static IEnumerable<(string name, Rectangle bounds, string monitorId)> AttachedDisplays()
        {
            var adapter = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            for (uint i = 0; EnumDisplayDevices(null, i, ref adapter, 0); i++)
            {
                if ((adapter.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0)
                {
                    var monitor = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                    var monitorId = EnumDisplayDevices(adapter.DeviceName, 0, ref monitor, 0) ? monitor.DeviceID : "";
                    var dm = NewDevMode();
                    var bounds = EnumDisplaySettings(adapter.DeviceName, ENUM_CURRENT_SETTINGS, ref dm)
                        ? new Rectangle(dm.dmPositionX, dm.dmPositionY, dm.dmPelsWidth, dm.dmPelsHeight)
                        : Rectangle.Empty;
                    yield return (adapter.DeviceName, bounds, monitorId ?? "");
                }
                adapter = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            }
        }

        /// <summary>True for a monitor (DISPLAY_DEVICE.DeviceID) created by one of the virtual display drivers.</summary>
        internal static bool IsVirtualMonitor(string monitorId) =>
            monitorId.Contains(ParsecVirtualDisplay.MonitorId, StringComparison.OrdinalIgnoreCase) ||
            monitorId.Contains(VddVirtualDisplay.MonitorId, StringComparison.OrdinalIgnoreCase);

        /// <summary>GDI device names of the attached monitors whose DeviceID contains <paramref name="monitorId"/>.</summary>
        protected static HashSet<string> DisplayNames(string monitorId) => AttachedDisplays()
            .Where(d => d.monitorId.Contains(monitorId, StringComparison.OrdinalIgnoreCase))
            .Select(d => d.name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        private static DEVMODE NewDevMode() => new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        #endregion

        #region Win32
        private const int ENUM_CURRENT_SETTINGS = -1;
        private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
        private const int DM_POSITION = 0x20;
        private const int DM_PELSWIDTH = 0x80000;
        private const int DM_PELSHEIGHT = 0x100000;
        private const int DM_DISPLAYFREQUENCY = 0x400000;
        private const int CDS_UPDATEREGISTRY = 0x1;
        private const int DISP_CHANGE_SUCCESSFUL = 0;

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

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
            public int dmICMMethod;
            public int dmICMIntent;
            public int dmMediaType;
            public int dmDitherType;
            public int dmReserved1;
            public int dmReserved2;
            public int dmPanningWidth;
            public int dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevices(string device, uint devNum, ref DISPLAY_DEVICE displayDevice, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int ChangeDisplaySettingsEx(string deviceName, ref DEVMODE devMode, IntPtr hwnd, int flags, IntPtr param);
        #endregion
    }
}
