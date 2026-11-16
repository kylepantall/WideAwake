// Renders the WideAwake logo (same geometry as logo.svg, 256x256 design grid) to
// PNGs and a multi-size WideAwake.ico. Run from the repo root via Build.cmd /icon,
// or: csc /out:MakeIcon.exe /r:System.Drawing.dll assets\MakeIcon.cs && MakeIcon.exe assets
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

static class MakeIcon
{
    static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };

    static void Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : ".";
        Directory.CreateDirectory(dir);
        var frames = new byte[Sizes.Length][];
        for (int i = 0; i < Sizes.Length; i++)
        {
            using (var bmp = Render(Sizes[i]))
            {
                // Small sizes as classic 32-bit DIBs (readable by every Windows API), 256 as PNG.
                frames[i] = Sizes[i] >= 256 ? Png(bmp) : Dib(bmp);
                if (Sizes[i] == 256 || Sizes[i] == 64 || Sizes[i] == 32)
                    bmp.Save(Path.Combine(dir, "icon-" + Sizes[i] + ".png"), ImageFormat.Png);
            }
        }
        using (var big = Render(512)) big.Save(Path.Combine(dir, "logo-512.png"), ImageFormat.Png);
        WriteIco(Path.Combine(dir, "WideAwake.ico"), frames);
        Console.WriteLine("Wrote WideAwake.ico and PNGs to " + Path.GetFullPath(dir));
    }

    static Bitmap Render(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            float k = size / 256f;
            g.ScaleTransform(k, k);

            using (var tile = RoundRect(16, 16, 224, 224, 52))
            using (var bg = new LinearGradientBrush(new PointF(16, 16), new PointF(240, 240), Hex("#4C5FE6"), Hex("#1A1D5C")))
                g.FillPath(bg, tile);

            using (var eye = new GraphicsPath())
            {
                eye.AddBezier(40, 128, 80, 66, 176, 66, 216, 128);
                eye.AddBezier(216, 128, 176, 190, 80, 190, 40, 128);
                eye.CloseFigure();
                using (var white = new SolidBrush(Hex("#F4F6FF"))) g.FillPath(white, eye);

                g.SetClip(eye);
                using (var iris = new LinearGradientBrush(new PointF(0, 86), new PointF(0, 170), Hex("#FFD866"), Hex("#FF9A1F")))
                    g.FillEllipse(iris, 128 - 42, 128 - 42, 84, 84);
                using (var pupil = new SolidBrush(Hex("#14174A"))) g.FillEllipse(pupil, 128 - 18, 128 - 18, 36, 36);
                using (var shine = new SolidBrush(Color.White)) g.FillEllipse(shine, 141 - 8, 114 - 8, 16, 16);
                g.ResetClip();
            }
        }
        return bmp;
    }

    static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
    {
        float d = r * 2;
        var p = new GraphicsPath();
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static Color Hex(string h) { return ColorTranslator.FromHtml(h); }

    static byte[] Png(Bitmap bmp)
    {
        using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); return ms.ToArray(); }
    }

    // BITMAPINFOHEADER + bottom-up BGRA pixels + empty AND mask (alpha does the masking).
    static byte[] Dib(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height, maskStride = ((w + 31) / 32) * 4;
        using (var ms = new MemoryStream())
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write(40); bw.Write(w); bw.Write(h * 2); bw.Write((short)1); bw.Write((short)32);
            bw.Write(0); bw.Write(w * h * 4 + maskStride * h); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
            for (int y = h - 1; y >= 0; y--)
                for (int x = 0; x < w; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    bw.Write(c.B); bw.Write(c.G); bw.Write(c.R); bw.Write(c.A);
                }
            bw.Write(new byte[maskStride * h]);
            return ms.ToArray();
        }
    }

    static void WriteIco(string path, byte[][] frames)
    {
        using (var bw = new BinaryWriter(File.Create(path)))
        {
            bw.Write((short)0); bw.Write((short)1); bw.Write((short)frames.Length);
            int offset = 6 + 16 * frames.Length;
            for (int i = 0; i < frames.Length; i++)
            {
                int s = Sizes[i];
                bw.Write((byte)(s >= 256 ? 0 : s)); bw.Write((byte)(s >= 256 ? 0 : s));
                bw.Write((byte)0); bw.Write((byte)0);
                bw.Write((short)1); bw.Write((short)32);
                bw.Write(frames[i].Length); bw.Write(offset);
                offset += frames[i].Length;
            }
            foreach (var f in frames) bw.Write(f);
        }
    }
}
