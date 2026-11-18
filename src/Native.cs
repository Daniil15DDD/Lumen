using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Lumen
{
    static class Native
    {
        // ---------- files / HID ----------
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
                NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("hid.dll")] public static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid.dll")] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
        [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr pp);
        [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr hwnd, int flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid g, int index, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr devInfo);
        [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        // ---------- timing / memory / window ----------
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateWaitableTimerExW(IntPtr attrs, string name, uint flags, uint access);
        [DllImport("kernel32.dll")]
        public static extern bool SetWaitableTimer(IntPtr h, ref long due, int period, IntPtr r, IntPtr a, bool resume);
        [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("psapi.dll")] public static extern bool EmptyWorkingSet(IntPtr h);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);

        // ---------- D3D / DXGI ----------
        [DllImport("d3d11.dll")]
        public static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr sw, uint flags, IntPtr levels, uint nLevels, uint sdk, out IntPtr dev, out int fl, out IntPtr ctx);
        [DllImport("dxgi.dll")]
        public static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
    }

    /// <summary>Tiny helper for calling COM methods by vtable slot (keeps us free of SharpDX etc).</summary>
    static class Com
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate uint D_Release(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int D_QI(IntPtr self, ref Guid iid, out IntPtr obj);

        static readonly Dictionary<KeyValuePair<IntPtr, Type>, Delegate> cache = new Dictionary<KeyValuePair<IntPtr, Type>, Delegate>();

        public static T Fn<T>(IntPtr obj, int slot) where T : class
        {
            IntPtr vt = Marshal.ReadIntPtr(obj);
            IntPtr f = Marshal.ReadIntPtr(vt, slot * IntPtr.Size);
            var key = new KeyValuePair<IntPtr, Type>(f, typeof(T));
            Delegate d;
            lock (cache)
            {
                if (!cache.TryGetValue(key, out d))
                {
                    d = Marshal.GetDelegateForFunctionPointer(f, typeof(T));
                    cache[key] = d;
                }
            }
            return d as T;
        }

        public static void Release(ref IntPtr p)
        {
            if (p != IntPtr.Zero) { Fn<D_Release>(p, 2)(p); p = IntPtr.Zero; }
        }

        public static IntPtr QI(IntPtr p, Guid iid)
        {
            IntPtr o;
            int hr = Fn<D_QI>(p, 0)(p, ref iid, out o);
            return hr < 0 ? IntPtr.Zero : o;
        }
    }

    /// <summary>High-resolution sleep without changing the global timer resolution.</summary>
    sealed class PreciseSleeper : IDisposable
    {
        IntPtr timer;
        public PreciseSleeper()
        {
            timer = Native.CreateWaitableTimerExW(IntPtr.Zero, null, 0x2 /*HIGH_RESOLUTION*/, 0x1F0003);
        }
        public void Sleep(double ms)
        {
            if (ms <= 0) return;
            if (timer == IntPtr.Zero) { System.Threading.Thread.Sleep((int)Math.Ceiling(ms)); return; }
            long due = -(long)(ms * 10000.0);
            if (Native.SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                Native.WaitForSingleObject(timer, 0xFFFFFFFF);
            else
                System.Threading.Thread.Sleep((int)Math.Ceiling(ms));
        }
        public void Dispose()
        {
            if (timer != IntPtr.Zero) { Native.CloseHandle(timer); timer = IntPtr.Zero; }
        }
    }
}
