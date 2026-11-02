using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace ScreenshareHelper.VirtualMonitor
{
    /// <summary>
    /// A monitor of the open-source Virtual Display Driver (MIT, installed separately). Its monitors exist as long as the
    /// driver runs, so this attaches one to the desktop while in use and detaches it again afterwards, like
    /// "Disconnect this display" in Windows Settings (no administrator rights needed). After a crash it stays attached
    /// until the next use. The driver settings (C:\VirtualDisplayDriver\vdd_settings.xml) are left alone.
    /// </summary>
    public sealed class VddVirtualDisplay : VirtualDisplay
    {
        public const string DriverUrl = "https://github.com/VirtualDrivers/Virtual-Display-Driver";

        // EDID manufacturer "MTT", product 0x1337: part of the monitor device path and of DISPLAY_DEVICE.DeviceID
        internal const string MonitorId = "MTT1337";

        private readonly LUID adapterId;
        private readonly uint targetId;
        private bool disposed;

        /// <summary>True if the driver is running and offers a monitor.</summary>
        public static bool IsDriverInstalled()
        {
            try
            {
                return QueryConfig(QDC_ALL_PATHS, out _)
                    .Where(p => p.targetInfo.targetAvailable != 0)
                    .Select(p => p.targetInfo)
                    .DistinctBy(t => (t.adapterId.LowPart, t.adapterId.HighPart, t.id))
                    .Any(IsVddTarget);
            }
            catch (Win32Exception ex)
            {
                Debug.WriteLine(ex);
                return false;
            }
        }

        /// <summary>Attaches a monitor of the driver to the desktop (or takes the attached one) and waits until Windows has set it up.</summary>
        public VddVirtualDisplay()
        {
            var path = FindPath() ?? throw new InvalidOperationException("The Virtual Display Driver offers no monitor.");
            adapterId = path.targetInfo.adapterId;
            targetId = path.targetInfo.id;
            if ((path.flags & DISPLAYCONFIG_PATH_ACTIVE) == 0)
                Attach(path);

            // Windows needs a moment to attach the monitor
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 10000)
            {
                var name = ActiveGdiName();
                if (name != null && DisplayNames(MonitorId).Contains(name))
                {
                    DeviceName = name;
                    return;
                }
                Thread.Sleep(100);
            }
            Dispose();
            throw new InvalidOperationException("The virtual display was attached, but Windows did not add it to the desktop.");
        }

        public override bool IsAlive => !disposed && DeviceName != null && DisplayNames(MonitorId).Contains(DeviceName);

        public override void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            try
            {
                Detach();
            }
            catch (Win32Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        /// <summary>The path of an attached monitor of the driver, otherwise one that can be attached. Null if there is none.</summary>
        private static DISPLAYCONFIG_PATH_INFO? FindPath()
        {
            var active = QueryConfig(QDC_ONLY_ACTIVE_PATHS, out _);
            foreach (var path in active)
                if (IsVddTarget(path.targetInfo))
                    return path;

            foreach (var path in QueryConfig(QDC_ALL_PATHS, out _))
            {
                if (path.targetInfo.targetAvailable != 0 && IsVddTarget(path.targetInfo)
                    && !active.Any(a => SameAdapter(a.sourceInfo.adapterId, path.sourceInfo.adapterId) && a.sourceInfo.id == path.sourceInfo.id))
                    return path;
            }
            return null;
        }

        private static void Attach(DISPLAYCONFIG_PATH_INFO path)
        {
            var paths = QueryConfig(QDC_ONLY_ACTIVE_PATHS, out var modes).ToList();
            // Windows picks the modes and the position (SDC_ALLOW_CHANGES), Configure moves it to the corner
            path.flags = DISPLAYCONFIG_PATH_ACTIVE;
            path.sourceInfo.modeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
            path.targetInfo.modeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
            paths.Add(path);
            Apply(paths.ToArray(), modes);
        }

        private void Detach()
        {
            var active = QueryConfig(QDC_ONLY_ACTIVE_PATHS, out var modes);
            var keep = active.Where(p => !IsOurTarget(p.targetInfo)).ToArray();
            if (keep.Length == active.Length || keep.Length == 0)
                return;

            // pass only the modes the remaining paths use
            var keptModes = new List<DISPLAYCONFIG_MODE_INFO>();
            var newIndex = new Dictionary<uint, uint>();
            uint Remap(uint index)
            {
                if (index == DISPLAYCONFIG_PATH_MODE_IDX_INVALID)
                    return index;
                if (!newIndex.TryGetValue(index, out uint mapped))
                {
                    mapped = (uint)keptModes.Count;
                    keptModes.Add(modes[index]);
                    newIndex[index] = mapped;
                }
                return mapped;
            }
            for (int i = 0; i < keep.Length; i++)
            {
                keep[i].sourceInfo.modeInfoIdx = Remap(keep[i].sourceInfo.modeInfoIdx);
                keep[i].targetInfo.modeInfoIdx = Remap(keep[i].targetInfo.modeInfoIdx);
            }
            Apply(keep, keptModes.ToArray());
        }

        /// <summary>GDI device name (\\.\DISPLAYn) of our monitor if it is attached.</summary>
        private string ActiveGdiName()
        {
            foreach (var path in QueryConfig(QDC_ONLY_ACTIVE_PATHS, out _))
            {
                if (!IsOurTarget(path.targetInfo))
                    continue;
                var name = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                name.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                name.header.size = Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
                name.header.adapterId = path.sourceInfo.adapterId;
                name.header.id = path.sourceInfo.id;
                return DisplayConfigGetDeviceInfo(ref name) == 0 ? name.viewGdiDeviceName : null;
            }
            return null;
        }

        private bool IsOurTarget(DISPLAYCONFIG_PATH_TARGET_INFO target) => SameAdapter(target.adapterId, adapterId) && target.id == targetId;

        private static bool SameAdapter(LUID a, LUID b) => a.LowPart == b.LowPart && a.HighPart == b.HighPart;

        private static bool IsVddTarget(DISPLAYCONFIG_PATH_TARGET_INFO target)
        {
            var name = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
            name.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
            name.header.size = Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
            name.header.adapterId = target.adapterId;
            name.header.id = target.id;
            return DisplayConfigGetDeviceInfo(ref name) == 0
                && (name.monitorDevicePath ?? "").Contains(MonitorId, StringComparison.OrdinalIgnoreCase);
        }

        private static DISPLAYCONFIG_PATH_INFO[] QueryConfig(uint flags, out DISPLAYCONFIG_MODE_INFO[] modes)
        {
            while (true)
            {
                int result = GetDisplayConfigBufferSizes(flags, out uint pathCount, out uint modeCount);
                if (result != 0)
                    throw new Win32Exception(result);
                var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
                modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
                result = QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (result == ERROR_INSUFFICIENT_BUFFER)
                    continue; // the configuration changed in between
                if (result != 0)
                    throw new Win32Exception(result);
                Array.Resize(ref paths, (int)pathCount);
                Array.Resize(ref modes, (int)modeCount);
                return paths;
            }
        }

        private static void Apply(DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes)
        {
            int result = SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes,
                SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE);
            if (result != 0)
                throw new Win32Exception(result);
        }

        #region Win32
        private const uint QDC_ALL_PATHS = 0x1;
        private const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
        private const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20;
        private const uint SDC_APPLY = 0x80;
        private const uint SDC_SAVE_TO_DATABASE = 0x200;
        private const uint SDC_ALLOW_CHANGES = 0x400;
        private const uint DISPLAYCONFIG_PATH_ACTIVE = 0x1;
        private const uint DISPLAYCONFIG_PATH_MODE_IDX_INVALID = 0xffffffff;
        private const int DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
        private const int DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
        private const int ERROR_INSUFFICIENT_BUFFER = 122;

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public int outputTechnology;
            public int rotation;
            public int scaling;
            public uint refreshRateNumerator;
            public uint refreshRateDenominator;
            public int scanLineOrdering;
            public int targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        // only passed through: the 48-byte union (target, source or desktop image mode) is not needed
        [StructLayout(LayoutKind.Sequential, Size = 64)]
        private struct DISPLAYCONFIG_MODE_INFO
        {
            public int infoType;
            public uint id;
            public LUID adapterId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public int type;
            public int size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint flags;
            public int outputTechnology;
            public ushort edidManufactureId;
            public ushort edidProductCodeId;
            public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
        }

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
            ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        private static extern int SetDisplayConfig(uint numPathArrayElements, [In] DISPLAYCONFIG_PATH_INFO[] pathArray,
            uint numModeInfoArrayElements, [In] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, uint flags);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);
        #endregion
    }
}
