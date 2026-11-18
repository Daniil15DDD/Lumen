using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Lumen
{
    public class MonitorInfo
    {
        public int Adapter, Output, Width, Height;
        public string Name;
        public override string ToString() { return Name; }
    }

    /// <summary>
    /// DXGI Desktop Duplication. The full-resolution frame never leaves the GPU:
    /// it is copied into a mip-mapped texture, the GPU box-filters it down, and only a
    /// ~160x90 mip level is read back (~55 KB) per frame.
    /// </summary>
    public sealed class Capture : IDisposable
    {
        // ---- vtable delegates ----
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_EnumAdapters1(IntPtr self, uint i, out IntPtr a);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_EnumOutputs(IntPtr self, uint i, out IntPtr o);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_GetDesc(IntPtr self, IntPtr desc);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_DuplicateOutput(IntPtr self, IntPtr device, out IntPtr dup);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_Acquire(IntPtr self, uint timeout, IntPtr info, out IntPtr res);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_ReleaseFrame(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_TexDesc(IntPtr self, IntPtr desc);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_CreateTex2D(IntPtr self, IntPtr desc, IntPtr init, out IntPtr tex);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_CreateSRV(IntPtr self, IntPtr res, IntPtr desc, out IntPtr srv);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_CopySub(IntPtr self, IntPtr dst, uint dstSub, uint x, uint y, uint z, IntPtr src, uint srcSub, IntPtr box);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_GenMips(IntPtr self, IntPtr srv);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int D_Map(IntPtr self, IntPtr res, uint sub, int type, uint flags, out MAPPED m);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void D_Unmap(IntPtr self, IntPtr res, uint sub);

        [StructLayout(LayoutKind.Sequential)] struct MAPPED { public IntPtr pData; public uint RowPitch; public uint DepthPitch; }

        static readonly Guid IID_Factory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        static readonly Guid IID_Output1 = new Guid("00cddea8-939b-4b83-a340-a685226666cc");
        static readonly Guid IID_Tex2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        const int WAIT_TIMEOUT = unchecked((int)0x887A0027);

        IntPtr device, ctx, output1, dup, mipTex, srv, staging;
        readonly IntPtr info = Marshal.AllocHGlobal(64);
        readonly IntPtr desc = Marshal.AllocHGlobal(64);
        int texW, texH, texFmt, level;
        bool haveFrame;

        /// <summary>Downscaled frame, RGB floats 0..255, row-major.</summary>
        public float[] Pixels = new float[0];
        public int W, H;

        public static List<MonitorInfo> Enumerate()
        {
            var list = new List<MonitorInfo>();
            IntPtr f;
            Guid g = IID_Factory1;
            if (Native.CreateDXGIFactory1(ref g, out f) < 0) return list;
            IntPtr d = Marshal.AllocHGlobal(128);
            try
            {
                for (uint a = 0; ; a++)
                {
                    IntPtr ad;
                    if (Com.Fn<D_EnumAdapters1>(f, 12)(f, a, out ad) < 0) break;
                    for (uint o = 0; ; o++)
                    {
                        IntPtr op;
                        if (Com.Fn<D_EnumOutputs>(ad, 7)(ad, o, out op) < 0) break;
                        Com.Fn<D_GetDesc>(op, 7)(op, d);
                        int l = Marshal.ReadInt32(d, 64), t = Marshal.ReadInt32(d, 68), r = Marshal.ReadInt32(d, 72), b = Marshal.ReadInt32(d, 76);
                        var mi = new MonitorInfo { Adapter = (int)a, Output = (int)o, Width = r - l, Height = b - t };
                        mi.Name = "Display " + (list.Count + 1) + "  ·  " + mi.Width + "×" + mi.Height;
                        list.Add(mi);
                        Com.Release(ref op);
                    }
                    Com.Release(ref ad);
                }
            }
            finally { Marshal.FreeHGlobal(d); Com.Release(ref f); }
            return list;
        }

        public bool Init(int monitorIndex)
        {
            var mons = Enumerate();
            if (mons.Count == 0) return false;
            var m = mons[Math.Max(0, Math.Min(mons.Count - 1, monitorIndex))];

            IntPtr f, ad = IntPtr.Zero, op = IntPtr.Zero;
            Guid g = IID_Factory1;
            if (Native.CreateDXGIFactory1(ref g, out f) < 0) return false;
            try
            {
                if (Com.Fn<D_EnumAdapters1>(f, 12)(f, (uint)m.Adapter, out ad) < 0) return false;
                if (Com.Fn<D_EnumOutputs>(ad, 7)(ad, (uint)m.Output, out op) < 0) return false;
                output1 = Com.QI(op, IID_Output1);
                if (output1 == IntPtr.Zero) return false;
                int fl;
                // D3D_DRIVER_TYPE_UNKNOWN (adapter given), BGRA support
                if (Native.D3D11CreateDevice(ad, 0, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out device, out fl, out ctx) < 0) return false;
            }
            finally { Com.Release(ref op); Com.Release(ref ad); Com.Release(ref f); }
            return Duplicate();
        }

        bool Duplicate()
        {
            Com.Release(ref dup);
            haveFrame = false;
            return Com.Fn<D_DuplicateOutput>(output1, 22)(output1, device, out dup) >= 0;
        }

        /// <summary>1 = new frame in Pixels, 0 = nothing new, -1 = needs re-init.</summary>
        public int Grab(uint timeoutMs)
        {
            if (dup == IntPtr.Zero && !Duplicate()) return -1;
            IntPtr res;
            int hr = Com.Fn<D_Acquire>(dup, 8)(dup, timeoutMs, info, out res);
            if (hr == WAIT_TIMEOUT) return 0;
            if (hr < 0) { Com.Release(ref dup); return -1; }

            bool got = false;
            try
            {
                long present = Marshal.ReadInt64(info, 0);
                if (present != 0 || !haveFrame)
                {
                    IntPtr tex = Com.QI(res, IID_Tex2D);
                    if (tex != IntPtr.Zero)
                    {
                        Com.Fn<D_TexDesc>(tex, 10)(tex, desc);
                        int w = Marshal.ReadInt32(desc, 0), h = Marshal.ReadInt32(desc, 4), fmt = Marshal.ReadInt32(desc, 16);
                        if (Ensure(w, h, fmt))
                        {
                            Com.Fn<D_CopySub>(ctx, 46)(ctx, mipTex, 0, 0, 0, 0, tex, 0, IntPtr.Zero);
                            got = true;
                        }
                        Com.Release(ref tex);
                    }
                }
            }
            finally
            {
                Com.Release(ref res);
                Com.Fn<D_ReleaseFrame>(dup, 14)(dup);
            }
            if (!got) return 0;

            Com.Fn<D_GenMips>(ctx, 54)(ctx, srv);
            Com.Fn<D_CopySub>(ctx, 46)(ctx, staging, 0, 0, 0, 0, mipTex, (uint)level, IntPtr.Zero);
            MAPPED m;
            if (Com.Fn<D_Map>(ctx, 14)(ctx, staging, 0, 1, 0, out m) < 0) return -1;
            try { ReadPixels(m); }
            finally { Com.Fn<D_Unmap>(ctx, 15)(ctx, staging, 0); }
            haveFrame = true;
            return 1;
        }

        unsafe void ReadPixels(MAPPED m)
        {
            int n = W * H * 3;
            if (Pixels.Length != n) Pixels = new float[n];
            fixed (float* dst0 = Pixels)
            {
                float* dst = dst0;
                for (int y = 0; y < H; y++)
                {
                    byte* row = (byte*)m.pData + (long)y * m.RowPitch;
                    if (texFmt == 10) // R16G16B16A16_FLOAT (HDR / scRGB, linear)
                    {
                        ushort* p = (ushort*)row;
                        for (int x = 0; x < W; x++, p += 4)
                        {
                            *dst++ = HdrToSdr(Half(p[0]));
                            *dst++ = HdrToSdr(Half(p[1]));
                            *dst++ = HdrToSdr(Half(p[2]));
                        }
                    }
                    else if (texFmt == 28 || texFmt == 29) // RGBA8
                    {
                        byte* p = row;
                        for (int x = 0; x < W; x++, p += 4) { *dst++ = p[0]; *dst++ = p[1]; *dst++ = p[2]; }
                    }
                    else // BGRA8 (87 / 91)
                    {
                        byte* p = row;
                        for (int x = 0; x < W; x++, p += 4) { *dst++ = p[2]; *dst++ = p[1]; *dst++ = p[0]; }
                    }
                }
            }
        }

        static float HdrToSdr(float v)
        {
            // scRGB: 1.0 = 80 nits. Treat ~200 nits as paper white, then gamma-encode.
            if (v <= 0) return 0;
            v = v / 2.5f;
            if (v > 1) v = 1;
            return (float)Math.Pow(v, 1 / 2.2) * 255f;
        }

        static float Half(ushort h)
        {
            int s = (h >> 15) & 1, e = (h >> 10) & 0x1F, f = h & 0x3FF;
            float v;
            if (e == 0) v = f / 1024f * 6.1035156e-05f;
            else if (e == 31) v = 65504f;
            else v = (float)((1 + f / 1024.0) * Math.Pow(2, e - 15));
            return s == 1 ? -v : v;
        }

        bool Ensure(int w, int h, int fmt)
        {
            if (mipTex != IntPtr.Zero && w == texW && h == texH && fmt == texFmt) return true;
            FreeTextures();
            if (fmt != 87 && fmt != 91 && fmt != 28 && fmt != 29 && fmt != 10) return false;

            int L = 0;
            while ((w >> (L + 1)) >= 96 && (h >> (L + 1)) >= 40) L++;
            level = L;

            WriteDesc(w, h, L + 1, fmt, 0 /*DEFAULT*/, 0x8 | 0x20 /*SRV|RT*/, 0, 0x1 /*GENERATE_MIPS*/);
            if (Com.Fn<D_CreateTex2D>(device, 5)(device, desc, IntPtr.Zero, out mipTex) < 0) { mipTex = IntPtr.Zero; return false; }
            if (Com.Fn<D_CreateSRV>(device, 7)(device, mipTex, IntPtr.Zero, out srv) < 0) { FreeTextures(); return false; }

            W = Math.Max(1, w >> L); H = Math.Max(1, h >> L);
            WriteDesc(W, H, 1, fmt, 3 /*STAGING*/, 0, 0x20000 /*CPU_READ*/, 0);
            if (Com.Fn<D_CreateTex2D>(device, 5)(device, desc, IntPtr.Zero, out staging) < 0) { FreeTextures(); return false; }

            texW = w; texH = h; texFmt = fmt;
            return true;
        }

        void WriteDesc(int w, int h, int mips, int fmt, int usage, int bind, int cpu, int misc)
        {
            int[] v = { w, h, mips, 1, fmt, 1, 0, usage, bind, cpu, misc };
            for (int i = 0; i < v.Length; i++) Marshal.WriteInt32(desc, i * 4, v[i]);
        }

        void FreeTextures()
        {
            Com.Release(ref srv); Com.Release(ref mipTex); Com.Release(ref staging);
            texW = texH = texFmt = 0;
        }

        bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            FreeTextures();
            Com.Release(ref dup); Com.Release(ref output1); Com.Release(ref ctx); Com.Release(ref device);
            Marshal.FreeHGlobal(info); Marshal.FreeHGlobal(desc);
        }
    }
}
