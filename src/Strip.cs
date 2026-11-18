using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Lumen
{
    /// <summary>
    /// Robobloq USB monitor light strip (VID 1A86 / PID FE07, vendor HID interface 0xFF00).
    /// Protocol per OpenRGB's RobobloqLightStripController (GPL-2.0-or-later).
    /// </summary>
    public sealed class Strip : IDisposable
    {
        public const ushort VID = 0x1A86, PID = 0xFE07;

        FileStream fs;
        readonly int outLen, inLen;
        byte idx = 2;
        readonly object wlock = new object();
        readonly BlockingCollection<byte[]> replies = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>());
        Thread reader;
        volatile bool alive = true;

        public int LedCount = 0;
        public int SizeInch = 0;
        public string Firmware = "?";
        public bool Connected { get { return alive; } }
        public event Action Lost;

        Strip(SafeFileHandle h, int outLen, int inLen)
        {
            this.outLen = Math.Max(65, outLen);
            this.inLen = Math.Max(65, inLen);
            fs = new FileStream(h, FileAccess.ReadWrite, 1, true);
            reader = new Thread(ReadLoop) { IsBackground = true, Name = "strip-rx" };
            reader.Start();
        }

        public static Strip Open()
        {
            Guid g;
            Native.HidD_GetHidGuid(out g);
            IntPtr set = Native.SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return null;
            try
            {
                for (int i = 0; ; i++)
                {
                    var d = new Native.SP_DEVICE_INTERFACE_DATA();
                    d.cbSize = Marshal.SizeOf(d);
                    if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d)) break;
                    int req;
                    Native.SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out req, IntPtr.Zero);
                    if (req <= 0) continue;
                    IntPtr buf = Marshal.AllocHGlobal(req);
                    string path;
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                        if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref d, buf, req, out req, IntPtr.Zero)) continue;
                        path = Marshal.PtrToStringUni(buf + 4);
                    }
                    finally { Marshal.FreeHGlobal(buf); }

                    if (path == null || path.ToLowerInvariant().IndexOf("vid_1a86&pid_fe07") < 0) continue;

                    var h = Native.CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
                    if (h.IsInvalid) continue;
                    IntPtr pp;
                    if (!Native.HidD_GetPreparsedData(h, out pp)) { h.Dispose(); continue; }
                    Native.HIDP_CAPS caps;
                    Native.HidP_GetCaps(pp, out caps);
                    Native.HidD_FreePreparsedData(pp);
                    if (caps.UsagePage != 0xFF00) { h.Dispose(); continue; }
                    var s = new Strip(h, caps.OutputReportByteLength, caps.InputReportByteLength);
                    if (!s.ReadInfo()) { s.LedCount = 63; }
                    // Stop the controller from "typing" a driver-download URL (OpenRGB does the same).
                    s.Send(0x93, 0x00);
                    return s;
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }
            return null;
        }

        void ReadLoop()
        {
            try
            {
                while (alive)
                {
                    var b = new byte[inLen];
                    int n = fs.Read(b, 0, inLen);
                    if (n <= 0) break;
                    if (replies.Count < 16) replies.Add(b);
                }
            }
            catch { }
            Fail();
        }

        void Fail()
        {
            if (!alive) return;
            alive = false;
            var l = Lost;
            if (l != null) l();
        }

        void WriteReport(byte[] data, int offset)
        {
            var rep = new byte[outLen];
            Buffer.BlockCopy(data, offset, rep, 1, 64);
            try { fs.Write(rep, 0, outLen); }
            catch { Fail(); }
        }

        /// <summary>Single-report command: 52 42 len idx cmd... checksum.</summary>
        public byte Send(params byte[] cmd)
        {
            if (!alive) return 0;
            lock (wlock)
            {
                var p = new byte[64];
                p[0] = 0x52; p[1] = 0x42; p[2] = (byte)(cmd.Length + 5); p[3] = idx;
                Buffer.BlockCopy(cmd, 0, p, 4, cmd.Length);
                int sum = 0;
                for (int i = 0; i < 4 + cmd.Length; i++) sum += p[i];
                p[4 + cmd.Length] = (byte)sum;
                byte id = idx;
                WriteReport(p, 0);
                idx++;
                return id;
            }
        }

        /// <summary>Multi-report command: 53 43 lenHi lenLo idx cmd payload checksum, padded to 64.</summary>
        public void SendMulti(byte cmd, byte[] payload, int count)
        {
            if (!alive) return;
            lock (wlock)
            {
                int len = count + 7;
                int total = (len + 63) / 64 * 64;
                var d = new byte[total];
                d[0] = 0x53; d[1] = 0x43; d[2] = (byte)(len >> 8); d[3] = (byte)len; d[4] = idx; d[5] = cmd;
                Buffer.BlockCopy(payload, 0, d, 6, count);
                int sum = 0;
                for (int i = 0; i < 6 + count; i++) sum += d[i];
                d[6 + count] = (byte)sum;
                for (int o = 0; o < total; o += 64) WriteReport(d, o);
                idx++;
            }
        }

        bool ReadInfo()
        {
            for (int t = 0; t < 3; t++)
            {
                byte[] junk;
                while (replies.TryTake(out junk)) { }
                byte id = Send(0x82);
                var until = DateTime.UtcNow.AddMilliseconds(700);
                byte[] b;
                while (DateTime.UtcNow < until && replies.TryTake(out b, 300))
                {
                    // b[0] is the HID report id, protocol bytes start at b[1]
                    if (b.Length >= 25 && b[1] == 0x52 && b[2] == 0x42 && b[4] == id && b[5] == 0x82)
                    {
                        SizeInch = b[9];
                        LedCount = b[12];
                        Firmware = b[22] + "." + b[23] + "." + b[24];
                        return LedCount > 0;
                    }
                }
                Send(0x93, 0x00);
            }
            return false;
        }

        public void SetColor(byte r, byte g, byte b)
        {
            int n = LedCount;
            Send(0x86, 0x01, r, g, b, (byte)n, (byte)(n + 1), 0, 0, 0, 0xFE);
        }

        public void SetBrightnessPercent(int pct)
        {
            int v = (int)Math.Round(Math.Max(1, Math.Min(100, pct)) * 2.55);
            Send(0x87, (byte)Math.Max(1, Math.Min(255, v)));
        }

        public void SetEffect(int effect) { Send(0x85, 0x02, (byte)effect); }

        /// <summary>0 = slow, 100 = fast.</summary>
        public void SetSpeed(int speed) { Send(0x8A, (byte)(100 - Math.Max(0, Math.Min(100, speed)))); }

        public void StopAnimation() { Send(0x97); }

        public void TurnOff() { Send(0x97); SetColor(0, 0, 0); }

        /// <summary>Tuples of (start, r, g, b, end), 1-indexed LEDs.</summary>
        public void SendRanges(byte[] tuples, int count) { SendMulti(0x80, tuples, count); }

        public void Dispose()
        {
            alive = false;
            try { fs.Dispose(); } catch { }
        }
    }
}
