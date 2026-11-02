using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ScreenshareHelper.VirtualMonitor
{
    /// <summary>
    /// One virtual monitor provided by the Parsec Virtual Display Driver (signed IddCx driver, installed separately).
    /// The driver removes all displays about a second after the last ping, so the monitor disappears on its own
    /// when the app exits or crashes. Control protocol: https://github.com/nomi-san/parsec-vdd (core/parsec-vdd.h).
    /// </summary>
    public sealed unsafe class ParsecVirtualDisplay : VirtualDisplay
    {
        public const string DriverDownloadUrl = "https://builds.parsec.app/vdd/parsec-vdd-0.45.0.0.exe";

        // device interface of the "Parsec Virtual Display Adapter"
        private static readonly Guid AdapterGuid = new Guid("00b41627-04c4-429e-a26e-0265cf50c8fa");
        // monitor hardware id, part of DISPLAY_DEVICE.DeviceID of the monitors it creates
        internal const string MonitorId = "PSCCDD0";

        private const uint IOCTL_ADD = 0x0022e004;
        private const uint IOCTL_REMOVE = 0x0022a008;
        private const uint IOCTL_UPDATE = 0x0022a00c;

        private readonly SafeFileHandle device;
        private readonly object ioLock = new object();
        private readonly Thread pingThread;
        private volatile bool disposed;
        private readonly int index;

        public static bool IsDriverInstalled()
        {
            using var handle = OpenDevice();
            return handle != null;
        }

        /// <summary>Plugs in a new virtual monitor and waits until Windows has attached it to the desktop.</summary>
        public ParsecVirtualDisplay()
        {
            device = OpenDevice() ?? throw new InvalidOperationException("The Parsec Virtual Display Driver is not installed or not running.");

            var before = DisplayNames(MonitorId);
            index = IoControl(IOCTL_ADD);
            if (index < 0)
            {
                device.Dispose();
                throw new InvalidOperationException("The Parsec Virtual Display Driver could not add a display.");
            }
            IoControl(IOCTL_UPDATE);

            pingThread = new Thread(PingLoop) { IsBackground = true, Name = "Parsec VDD keep-alive" };
            pingThread.Start();

            // Windows needs a moment to attach the new monitor
            var sw = Stopwatch.StartNew();
            while (DeviceName == null && sw.ElapsedMilliseconds < 10000)
            {
                Thread.Sleep(100);
                DeviceName = DisplayNames(MonitorId).Except(before).FirstOrDefault();
            }
            if (DeviceName == null)
            {
                Dispose();
                throw new InvalidOperationException("The virtual display was added, but Windows did not attach it to the desktop.");
            }
        }

        /// <summary>
        /// False once the monitor is gone, e.g. after resume from hibernation or a driver reset: the driver then no longer
        /// knows our handle, but pings and Bounds fail silently.
        /// </summary>
        public override bool IsAlive => !disposed && !device.IsInvalid && !device.IsClosed
            && DisplayNames(MonitorId).Contains(DeviceName);

        private void PingLoop()
        {
            // the driver unplugs the displays if it does not hear from us for ~1 s, parsec-vdd.h says ping < 100 ms
            while (!disposed)
            {
                IoControl(IOCTL_UPDATE);
                Thread.Sleep(50);
            }
        }

        public override void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            pingThread?.Join(500);
            if (!device.IsInvalid && !device.IsClosed)
            {
                // the driver expects the index as 16-bit big endian
                IoControl(IOCTL_REMOVE, (byte)(index >> 8), (byte)index);
                IoControl(IOCTL_UPDATE);
            }
            device.Dispose();
        }

        #region Driver I/O
        private int IoControl(uint code, params byte[] data)
        {
            lock (ioLock)
            {
                if (device.IsInvalid || device.IsClosed)
                    return -1;

                byte* input = stackalloc byte[32];
                new Span<byte>(input, 32).Clear();
                data.AsSpan(0, Math.Min(data.Length, 32)).CopyTo(new Span<byte>(input, 32));
                uint output = 0;
                using var completed = new ManualResetEvent(false);
                var overlapped = new NativeOverlapped { EventHandle = completed.SafeWaitHandle.DangerousGetHandle() };

                DeviceIoControl(device, code, input, 32, &output, sizeof(uint), null, &overlapped);
                if (!GetOverlappedResultEx(device, &overlapped, out _, 5000, false))
                {
                    // don't leave the driver writing into this stack frame
                    CancelIoEx(device, &overlapped);
                    GetOverlappedResult(device, &overlapped, out _, true);
                    return -1;
                }
                return (int)output;
            }
        }

        private static SafeFileHandle OpenDevice()
        {
            var guid = AdapterGuid;
            var devInfo = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (devInfo == INVALID_HANDLE_VALUE)
                return null;
            try
            {
                var iface = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                for (uint i = 0; SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref guid, i, ref iface); i++)
                {
                    SetupDiGetDeviceInterfaceDetail(devInfo, ref iface, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                    if (size <= 0)
                        continue;
                    var detail = Marshal.AllocHGlobal(size);
                    try
                    {
                        // SP_DEVICE_INTERFACE_DETAIL_DATA_W: DWORD cbSize + WCHAR[1], padded to 8 bytes on x64
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(devInfo, ref iface, detail, size, out _, IntPtr.Zero))
                            continue;
                        var path = Marshal.PtrToStringUni(detail + 4);
                        var handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                            IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_NO_BUFFERING | FILE_FLAG_OVERLAPPED | FILE_FLAG_WRITE_THROUGH, IntPtr.Zero);
                        if (!handle.IsInvalid)
                            return handle;
                        Debug.WriteLine($"Opening {path} failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
                        handle.Dispose();
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detail);
                    }
                }
                return null;
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(devInfo);
            }
        }
        #endregion

        #region Win32
        private const int DIGCF_PRESENT = 0x2;
        private const int DIGCF_DEVICEINTERFACE = 0x10;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x1;
        private const uint FILE_SHARE_WRITE = 0x2;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        private const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
        private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        private const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, out int requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll")]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, void* inBuffer, int inBufferSize, void* outBuffer, int outBufferSize, uint* bytesReturned, NativeOverlapped* overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetOverlappedResultEx(SafeFileHandle file, NativeOverlapped* overlapped, out uint bytesTransferred, uint milliseconds, bool alertable);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetOverlappedResult(SafeFileHandle file, NativeOverlapped* overlapped, out uint bytesTransferred, bool wait);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIoEx(SafeFileHandle file, NativeOverlapped* overlapped);
        #endregion
    }
}
