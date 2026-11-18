using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace Lumen
{
    public class Settings
    {
        public string Mode = "sync";      // sync | static | effect
        public bool Power = true;
        public int Brightness = 100;      // %
        public int Hue = 265, Sat = 75;
        public int Effect = 0, Speed = 50;
        public int Fps = 60;
        public int Smooth = 40;           // 0..100
        public int Vibrance = 125;        // %
        public int Depth = 12;            // % of screen
        public bool Precise = false;
        public int Monitor = 0;
        public int Right = -1, Top = -1, Left = -1, Bottom = 0;
        public bool Mirror = false;
        public bool Startup = false, StartTray = false, CloseToTray = true;

        static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LumenStrip"); } }
        static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var f = typeof(Settings).GetField(line.Substring(0, eq).Trim());
                    if (f == null) continue;
                    string v = line.Substring(eq + 1).Trim();
                    if (f.FieldType == typeof(int)) { int i; if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) f.SetValue(s, i); }
                    else if (f.FieldType == typeof(bool)) f.SetValue(s, v == "1" || v.ToLowerInvariant() == "true");
                    else f.SetValue(s, v);
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var sb = new StringBuilder();
                foreach (var f in typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    object v = f.GetValue(this);
                    sb.Append(f.Name).Append('=').Append(v is bool ? ((bool)v ? "1" : "0") : Convert.ToString(v, CultureInfo.InvariantCulture)).Append("\r\n");
                }
                File.WriteAllText(FilePath, sb.ToString());
            }
            catch { }
        }
    }

    /// <summary>
    /// Physical LED order (matches OpenRGB's zones): Right side bottom→top, Top right→left,
    /// Left side top→bottom, optional Bottom left→right. Mirror flips horizontally.
    /// </summary>
    public class Layout
    {
        public int Count;
        public int[] Side;          // 0 right, 1 top, 2 left, 3 bottom
        public float[] A0, A1;      // span along the side, in 0..1 screen coords
        public int R, T, L, B;

        public static void Defaults(int leds, int inch, out int r, out int t, out int l)
        {
            // Strip covers right + top + left. Split by aspect ratio (34" kits are 21:9).
            double aspect = inch >= 34 ? 21.0 / 9.0 : 16.0 / 9.0;
            double unit = leds / (2.0 + aspect);
            r = l = (int)Math.Round(unit);
            t = leds - r - l;
        }

        public static Layout Build(Settings s, int leds, int inch)
        {
            int dr, dt, dl;
            Defaults(leds, inch, out dr, out dt, out dl);
            var lay = new Layout();
            lay.R = s.Right >= 0 ? s.Right : dr;
            lay.T = s.Top >= 0 ? s.Top : dt;
            lay.L = s.Left >= 0 ? s.Left : dl;
            lay.B = Math.Max(0, s.Bottom);
            lay.Count = leds;
            lay.Side = new int[leds]; lay.A0 = new float[leds]; lay.A1 = new float[leds];
            int[] counts = { lay.R, lay.T, lay.L, lay.B };
            int i = 0;
            for (int side = 0; side < 4 && i < leds; side++)
            {
                int n = counts[side];
                for (int k = 0; k < n && i < leds; k++, i++)
                {
                    float a = (float)k / n, b = (float)(k + 1) / n;
                    float a0, a1;
                    if (side == 0 || side == 1) { a0 = 1 - b; a1 = 1 - a; }   // bottom→top / right→left
                    else { a0 = a; a1 = b; }                                  // top→bottom / left→right
                    int sd = side;
                    if (s.Mirror)
                    {
                        if (sd == 0) sd = 2; else if (sd == 2) sd = 0;
                        if (sd == 1 || sd == 3) { float t0 = 1 - a1; a1 = 1 - a0; a0 = t0; }
                    }
                    lay.Side[i] = sd; lay.A0[i] = a0; lay.A1[i] = a1;
                }
            }
            for (; i < leds; i++) // any LEDs beyond the configured counts reuse the last position
            {
                int p = Math.Max(0, i - 1);
                lay.Side[i] = i > 0 ? lay.Side[p] : 0; lay.A0[i] = i > 0 ? lay.A0[p] : 0; lay.A1[i] = i > 0 ? lay.A1[p] : 1;
            }
            return lay;
        }

        /// <summary>LED center in normalized screen coords (on the screen edge).</summary>
        public void Center(int i, out float x, out float y)
        {
            float m = (A0[i] + A1[i]) / 2;
            switch (Side[i])
            {
                case 0: x = 1; y = m; break;
                case 1: x = m; y = 0; break;
                case 2: x = 0; y = m; break;
                default: x = m; y = 1; break;
            }
        }
    }

    /// <summary>Screen-sync loop on its own thread. Allocation-free per frame.</summary>
    public sealed class Engine
    {
        readonly Func<Strip> getStrip;
        public Settings S;
        volatile bool run;
        volatile bool reinit;
        Thread th;
        volatile Layout layout;

        readonly object pvLock = new object();
        byte[] preview = new byte[0];
        public volatile int SendFps;
        public bool Running { get { return run; } }

        public Engine(Settings s, Func<Strip> getStrip) { S = s; this.getStrip = getStrip; }

        public void SetLayout(Layout l) { layout = l; }
        public void RequestReinit() { reinit = true; }

        public void Start()
        {
            if (run) return;
            run = true;
            th = new Thread(Loop) { IsBackground = true, Name = "screen-sync" };
            th.Start();
        }

        public void Stop()
        {
            if (!run) return;
            run = false;
            if (th != null) th.Join(1500);
            th = null;
            SendFps = 0;
        }

        public bool CopyPreview(ref byte[] dst)
        {
            lock (pvLock)
            {
                if (preview.Length == 0) return false;
                if (dst == null || dst.Length != preview.Length) dst = new byte[preview.Length];
                Buffer.BlockCopy(preview, 0, dst, 0, preview.Length);
                return true;
            }
        }

        void Loop()
        {
            Capture cap = null;
            var sleeper = new PreciseSleeper();
            var sw = Stopwatch.StartNew();
            float[] target = new float[0], cur = new float[0];
            byte[] outRgb = new byte[0], lastSent = new byte[0], tuples = new byte[0];
            int[] rx0 = null, rx1 = null, ry0 = null, ry1 = null;
            Layout rectLayout = null; int rectW = 0, rectH = 0, rectDepth = -1;
            double lastTick = sw.Elapsed.TotalMilliseconds, lastSend = -10000, fpsWindow = lastTick;
            int sends = 0;
            bool converged = false;
            var merger = new RangeMerger();

            try
            {
                while (run)
                {
                    double t0 = sw.Elapsed.TotalMilliseconds;
                    var lay = layout;
                    var strip = getStrip();
                    if (lay == null || strip == null || !strip.Connected) { Thread.Sleep(300); continue; }

                    int n = lay.Count;
                    if (target.Length != n * 3)
                    {
                        target = new float[n * 3]; cur = new float[n * 3];
                        outRgb = new byte[n * 3]; lastSent = new byte[n * 3];
                        tuples = new byte[n * 5];
                        lock (pvLock) preview = new byte[n * 3];
                    }

                    if (reinit && cap != null) { cap.Dispose(); cap = null; }
                    reinit = false;
                    if (cap == null)
                    {
                        cap = new Capture();
                        if (!cap.Init(S.Monitor)) { cap.Dispose(); cap = null; Thread.Sleep(1000); continue; }
                    }

                    int fps = Math.Max(10, Math.Min(240, S.Fps));
                    double interval = 1000.0 / fps;
                    int g = cap.Grab(converged ? 250u : (uint)Math.Max(1, interval));
                    if (g < 0) { cap.Dispose(); cap = null; Thread.Sleep(250); continue; }

                    if (g == 1)
                    {
                        int w = cap.W, h = cap.H;
                        if (rectLayout != lay || rectW != w || rectH != h || rectDepth != S.Depth)
                        {
                            rx0 = new int[n]; rx1 = new int[n]; ry0 = new int[n]; ry1 = new int[n];
                            float d = Math.Max(2, Math.Min(40, S.Depth)) / 100f;
                            for (int i = 0; i < n; i++)
                            {
                                float x0, x1, y0, y1;
                                switch (lay.Side[i])
                                {
                                    case 0: x0 = 1 - d; x1 = 1; y0 = lay.A0[i]; y1 = lay.A1[i]; break;
                                    case 1: x0 = lay.A0[i]; x1 = lay.A1[i]; y0 = 0; y1 = d; break;
                                    case 2: x0 = 0; x1 = d; y0 = lay.A0[i]; y1 = lay.A1[i]; break;
                                    default: x0 = lay.A0[i]; x1 = lay.A1[i]; y0 = 1 - d; y1 = 1; break;
                                }
                                rx0[i] = Clamp((int)(x0 * w), 0, w - 1); rx1[i] = Clamp((int)Math.Ceiling(x1 * w), rx0[i] + 1, w);
                                ry0[i] = Clamp((int)(y0 * h), 0, h - 1); ry1[i] = Clamp((int)Math.Ceiling(y1 * h), ry0[i] + 1, h);
                            }
                            rectLayout = lay; rectW = w; rectH = h; rectDepth = S.Depth;
                        }

                        float vib = S.Vibrance / 100f;
                        var px = cap.Pixels;
                        for (int i = 0; i < n; i++)
                        {
                            double r = 0, gg = 0, b = 0; int cnt = 0;
                            for (int y = ry0[i]; y < ry1[i]; y++)
                            {
                                int o = (y * w + rx0[i]) * 3;
                                for (int x = rx0[i]; x < rx1[i]; x++, o += 3) { r += px[o]; gg += px[o + 1]; b += px[o + 2]; cnt++; }
                            }
                            float fr = (float)(r / cnt), fg = (float)(gg / cnt), fb = (float)(b / cnt);
                            float lum = 0.299f * fr + 0.587f * fg + 0.114f * fb;
                            fr = lum + (fr - lum) * vib; fg = lum + (fg - lum) * vib; fb = lum + (fb - lum) * vib;
                            target[i * 3] = Clamp(fr); target[i * 3 + 1] = Clamp(fg); target[i * 3 + 2] = Clamp(fb);
                        }
                        converged = false;
                    }

                    // temporal smoothing (frame-rate independent)
                    double now = sw.Elapsed.TotalMilliseconds;
                    double dt = Math.Min(200, now - lastTick); lastTick = now;
                    double tau = S.Smooth / 100.0 * 350.0; // ms
                    float a = tau <= 1 ? 1f : (float)(1 - Math.Exp(-dt / tau));
                    float maxDiff = 0;
                    for (int k = 0; k < cur.Length; k++)
                    {
                        float diff = target[k] - cur[k];
                        cur[k] += diff * a;
                        if (Math.Abs(diff) > maxDiff) maxDiff = Math.Abs(diff);
                        outRgb[k] = (byte)(cur[k] + 0.5f);
                    }
                    if (maxDiff < 0.5f) converged = true;

                    bool changed = false;
                    for (int k = 0; k < outRgb.Length; k++) if (outRgb[k] != lastSent[k]) { changed = true; break; }
                    if (changed || now - lastSend > 1000)
                    {
                        int count;
                        if (S.Precise)
                        {
                            for (int i = 0; i < n; i++)
                            {
                                tuples[i * 5] = (byte)(i + 1); tuples[i * 5 + 1] = outRgb[i * 3];
                                tuples[i * 5 + 2] = outRgb[i * 3 + 1]; tuples[i * 5 + 3] = outRgb[i * 3 + 2]; tuples[i * 5 + 4] = (byte)(i + 1);
                            }
                            count = n * 5;
                        }
                        else count = merger.Merge(outRgb, n, 34, tuples);
                        strip.SendRanges(tuples, count);
                        Buffer.BlockCopy(outRgb, 0, lastSent, 0, outRgb.Length);
                        lock (pvLock) Buffer.BlockCopy(outRgb, 0, preview, 0, outRgb.Length);
                        lastSend = now; sends++;
                    }

                    if (now - fpsWindow >= 1000) { SendFps = (int)Math.Round(sends * 1000 / (now - fpsWindow)); sends = 0; fpsWindow = now; }

                    double remain = interval - (sw.Elapsed.TotalMilliseconds - t0);
                    if (remain > 0.5) sleeper.Sleep(remain);
                }
            }
            catch { }
            finally
            {
                if (cap != null) cap.Dispose();
                sleeper.Dispose();
            }
        }

        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
        static float Clamp(float v) { return v < 0 ? 0 : (v > 255 ? 255 : v); }
    }

    /// <summary>
    /// The official app compresses LEDs into 34 ranges. Greedy least-error merge of adjacent
    /// LEDs (same approach as OpenRGB's RobobloqRangeMerger), allocation-free.
    /// </summary>
    sealed class RangeMerger
    {
        int[] st = new int[0], en = new int[0], cnt = new int[0];
        double[] sr = new double[0], sg = new double[0], sb = new double[0], term = new double[0];

        public int Merge(byte[] rgb, int n, int target, byte[] outTuples)
        {
            if (st.Length < n)
            {
                st = new int[n]; en = new int[n]; cnt = new int[n];
                sr = new double[n]; sg = new double[n]; sb = new double[n]; term = new double[n];
            }
            for (int i = 0; i < n; i++)
            {
                st[i] = en[i] = i + 1; cnt[i] = 1;
                sr[i] = rgb[i * 3]; sg[i] = rgb[i * 3 + 1]; sb[i] = rgb[i * 3 + 2];
                term[i] = sr[i] * sr[i] + sg[i] * sg[i] + sb[i] * sb[i];
            }
            int m = n;
            while (m > target)
            {
                double best = double.MaxValue, bestTerm = 0; int bi = -1;
                for (int i = 0; i < m - 1; i++)
                {
                    double r = sr[i] + sr[i + 1], g = sg[i] + sg[i + 1], b = sb[i] + sb[i + 1];
                    int c = cnt[i] + cnt[i + 1];
                    double tm = (r * r + g * g + b * b) / c;
                    double delta = term[i] + term[i + 1] - tm;
                    if (delta < best) { best = delta; bi = i; bestTerm = tm; }
                }
                if (bi < 0) break;
                en[bi] = en[bi + 1]; cnt[bi] += cnt[bi + 1];
                sr[bi] += sr[bi + 1]; sg[bi] += sg[bi + 1]; sb[bi] += sb[bi + 1]; term[bi] = bestTerm;
                for (int j = bi + 1; j < m - 1; j++)
                {
                    st[j] = st[j + 1]; en[j] = en[j + 1]; cnt[j] = cnt[j + 1];
                    sr[j] = sr[j + 1]; sg[j] = sg[j + 1]; sb[j] = sb[j + 1]; term[j] = term[j + 1];
                }
                m--;
            }
            for (int i = 0; i < m; i++)
            {
                outTuples[i * 5] = (byte)st[i];
                outTuples[i * 5 + 1] = (byte)Math.Round(sr[i] / cnt[i]);
                outTuples[i * 5 + 2] = (byte)Math.Round(sg[i] / cnt[i]);
                outTuples[i * 5 + 3] = (byte)Math.Round(sb[i] / cnt[i]);
                outTuples[i * 5 + 4] = (byte)en[i];
            }
            return m * 5;
        }
    }
}
