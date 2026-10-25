using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

// Generates src/volkov.ico: a Volkov Commander style screen (two blue panels with cyan
// double borders, a teal selection bar and the black function-key bar), drawn as pixel art.
// Usage: makeicon.exe <out.ico>
static class MakeIcon
{
    static readonly Color Blue = Color.FromArgb(0, 0, 170);
    static readonly Color Cyan = Color.FromArgb(85, 255, 255);
    static readonly Color Teal = Color.FromArgb(0, 170, 170);
    static readonly Color Black = Color.FromArgb(0, 0, 0);
    static readonly Color White = Color.FromArgb(255, 255, 255);

    // (icon size, design grid, pixels per grid cell)
    static readonly int[][] Sizes = { new[] { 16, 16, 1 }, new[] { 24, 24, 1 }, new[] { 32, 32, 1 }, new[] { 48, 24, 2 }, new[] { 256, 32, 8 } };

    static void Main(string[] args)
    {
        var images = new byte[Sizes.Length][];
        for (int i = 0; i < Sizes.Length; i++)
            using (var b = Draw(Sizes[i][1], Sizes[i][2]))
                images[i] = Sizes[i][0] == 256 ? Png(b) : Dib(b);

        using (var f = new BinaryWriter(File.Create(args[0])))
        {
            f.Write((short)0); f.Write((short)1); f.Write((short)Sizes.Length);
            int offset = 6 + 16 * Sizes.Length;
            for (int i = 0; i < Sizes.Length; i++)
            {
                int s = Sizes[i][0];
                f.Write((byte)(s == 256 ? 0 : s)); f.Write((byte)(s == 256 ? 0 : s));
                f.Write((byte)0); f.Write((byte)0);
                f.Write((short)1); f.Write((short)32);
                f.Write(images[i].Length); f.Write(offset);
                offset += images[i].Length;
            }
            foreach (var img in images) f.Write(img);
        }
    }

    static Bitmap Draw(int g, int k)
    {
        var b = new Bitmap(g * k, g * k, PixelFormat.Format32bppArgb);
        using (var gr = Graphics.FromImage(b))
        {
            gr.SmoothingMode = SmoothingMode.None;
            gr.PixelOffsetMode = PixelOffsetMode.Half;
            Action<Color, int, int, int, int> cell = (c, x, y, w, h) => { using (var br = new SolidBrush(c)) gr.FillRectangle(br, x * k, y * k, w * k, h * k); };

            bool big = g >= 24;
            int bar = big ? 3 : 2;               // function-key bar rows
            int panelH = g - bar;
            cell(Blue, 0, 0, g, panelH);

            int half = g / 2;
            for (int p = 0; p < 2; p++)
            {
                int x0 = p * half, w = p == 0 ? half : g - half;
                Frame(cell, Cyan, x0, 0, w, panelH);
                if (big) Frame(cell, Cyan, x0 + 2, 2, w - 4, panelH - 4);
                int inX = x0 + (big ? 4 : 2), inW = w - (big ? 8 : 4);
                int row0 = big ? 4 : 2;
                // File names of varying length, one row under the selection bar.
                int[] lens = { inW, inW - 2, inW - 1, inW - 3, inW, inW - 2 };
                int selected = p == 0 ? 1 : 3;
                for (int r = 0, y = row0; y < panelH - (big ? 4 : 2); r++, y += 2)
                {
                    if (r == selected) cell(Teal, inX - 1, y, inW + 2, 1);
                    else cell(Cyan, inX, y, Math.Max(1, lens[r % lens.Length]), 1);
                }
            }
            // Function-key bar: white numbers on black, cyan labels.
            cell(Black, 0, panelH, g, bar);
            int by = panelH + (big ? 1 : 0);
            for (int x = 0; x < g; x += 4)
            {
                cell(White, x, by, 1, 1);
                cell(Teal, x + 1, by, Math.Min(2, g - x - 1), 1);
            }
        }
        return b;
    }

    static void Frame(Action<Color, int, int, int, int> cell, Color c, int x, int y, int w, int h)
    {
        cell(c, x, y, w, 1); cell(c, x, y + h - 1, w, 1);
        cell(c, x, y, 1, h); cell(c, x + w - 1, y, 1, h);
    }

    static byte[] Png(Bitmap b) { using (var m = new MemoryStream()) { b.Save(m, ImageFormat.Png); return m.ToArray(); } }

    // Classic icon entry: BITMAPINFOHEADER, bottom-up BGRA pixels, then an all-opaque AND mask.
    static byte[] Dib(Bitmap b)
    {
        int s = b.Width, maskStride = ((s + 31) / 32) * 4;
        using (var m = new MemoryStream())
        using (var w = new BinaryWriter(m))
        {
            w.Write(40); w.Write(s); w.Write(s * 2); w.Write((short)1); w.Write((short)32);
            w.Write(0); w.Write(s * s * 4 + maskStride * s); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            for (int y = s - 1; y >= 0; y--)
                for (int x = 0; x < s; x++) w.Write(b.GetPixel(x, y).ToArgb());
            w.Write(new byte[maskStride * s]);
            return m.ToArray();
        }
    }
}
