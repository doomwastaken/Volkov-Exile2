using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

// Finds a waystone tooltip in a frame and counts its mods.
// All pixel thresholds were measured at 1440p and are scaled by frame height.
sealed class Analyzer
{
    public sealed class Result
    {
        public int HeaderRight, HeaderY, HeaderH; // where to place the label
        public int Lines, Pairs;  // Pairs < 0: two-line mods not counted yet
        public bool Counted { get { return Pairs >= 0; } }
        public int Mods { get { return Lines - Pairs; } }
        public string Signature;
        public Rectangle Area; // tooltip region that has to be captured to re-check this result
    }

    struct Band { public int Y0, Y1, X0, X1, Blue, Grey; }

    const int RefHeight = 1440;
    const double MinHeaderScore = 0.6;

    readonly Bitmap template;
    readonly OcrEngine ocr = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
    int scaledFor = -1;
    double scale;
    int tplW, tplH, anchor;
    int[] tplOn, tplOff; // template offsets as (dy, dx) packed: dy * 65536 + dx

    string lastSignature;
    int lastPairs;
    public string LastOcrText { get; private set; }

    public Analyzer(Bitmap template) { this.template = template; }

    static bool IsGold(int c) { int r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255; return r > 200 && g > 160 && b < 130 && r - b > 110; }
    internal static bool IsModBlue(int c) { int r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255; return b > 200 && r > 90 && r < 175 && g > 90 && g < 175 && b - r > 70; }
    static bool IsGreyLabel(int c) { int r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255; return r > 110 && Math.Abs(r - g) < 20 && Math.Abs(r - b) < 25; }

    // Soft mask: keeps anti-aliased edges of small text that a hard threshold would cut off.
    static byte BlueInk(int c)
    {
        int r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255;
        int ink = Math.Min(b - Math.Max(r, g), b - 60) * 3;
        return (byte)(255 - Math.Max(0, Math.Min(255, ink)));
    }

    int S(int px) { return Math.Max(1, (int)Math.Round(px * scale)); }

    void PrepareTemplate(int frameHeight)
    {
        if (scaledFor == frameHeight) return;
        scaledFor = frameHeight;
        scale = frameHeight / (double)RefHeight;
        tplW = Math.Max(8, (int)Math.Round(template.Width * scale));
        tplH = Math.Max(4, (int)Math.Round(template.Height * scale));
        var px = new int[tplW * tplH];
        using (var b = new Bitmap(tplW, tplH, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(b))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(template, 0, 0, tplW, tplH);
            }
            var d = b.LockBits(new Rectangle(0, 0, tplW, tplH), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            Marshal.Copy(d.Scan0, px, 0, px.Length);
            b.UnlockBits(d);
        }
        var on = new List<int>(); var off = new List<int>();
        for (int i = 0; i < px.Length; i++) (IsGold(px[i]) ? on : off).Add((i / tplW) * 65536 + i % tplW);
        tplOn = on.ToArray(); tplOff = off.ToArray();
        anchor = tplOn[tplOn.Length / 2];
    }

    // p is a w*h crop of the game client; gameHeight sets the UI scale.
    // Returns null when no waystone tooltip is visible; coordinates are relative to the crop.
    // countPairs=false skips the OCR step (unless this roll was counted before), for a fast first sighting.
    public Result Analyze(int[] p, int w, int h, int gameHeight, bool countPairs = true)
    {
        PrepareTemplate(gameHeight);
        int hx, hy;
        if (!FindHeader(p, w, h, out hx, out hy)) { lastSignature = null; return null; }

        int cx = hx + tplW / 2 + S(60), half = S(620);
        int x0 = Math.Max(0, cx - half), x1 = Math.Min(w, cx + half);
        int y1 = Math.Min(h, hy + tplH + S(900));
        var mods = ModBands(p, w, x0, x1, hy + tplH, y1);

        var r = new Result { HeaderY = hy, HeaderH = tplH, HeaderRight = HeaderRight(p, w, hx, hy), Lines = mods.Count };
        r.Area = Rectangle.FromLTRB(x0, hy, x1, y1);
        if (mods.Count == 0) return r; // header found, mod lines not readable
        r.Signature = string.Join(";", mods.Select(m => (m.Y0 - hy) + ":" + (m.X1 - m.X0) / 4));
        if (r.Signature == lastSignature) r.Pairs = lastPairs;
        else if (!countPairs) r.Pairs = -1;
        else
        {
            r.Pairs = CountPairs(p, w, mods);
            lastSignature = r.Signature; lastPairs = r.Pairs;
        }
        return r;
    }

    const int Block = 32, Coarse = 4;
    bool[] goldBlocks;

    bool FindHeader(int[] p, int w, int h, out int bx, out int by)
    {
        double best = MinHeaderScore; bx = by = -1;
        int ady = anchor >> 16, adx = anchor & 0xFFFF;

        // Sparse pre-pass: most of the frame has no gold at all, so only blocks with gold get the exact search.
        int bw = (w + Block - 1) / Block, bh = (h + Block - 1) / Block;
        if (goldBlocks == null || goldBlocks.Length != bw * bh) goldBlocks = new bool[bw * bh];
        else Array.Clear(goldBlocks, 0, goldBlocks.Length);
        bool anyGold = false;
        for (int y = 0; y < h; y += Coarse)
            for (int x = 0; x < w; x += Coarse)
                if (IsGold(p[y * w + x])) { goldBlocks[(y / Block) * bw + x / Block] = true; anyGold = true; }
        if (!anyGold) return false;

        for (int y = ady + 1; y + tplH - ady < h - 1; y++)
            for (int x = adx + 1; x + tplW - adx < w - 1; x++)
            {
                // A coarse sample can miss a thin stroke near a block edge, so neighbours count too.
                int gy = y / Block, gx = x / Block;
                if (!goldBlocks[gy * bw + gx]
                    && !(gx > 0 && goldBlocks[gy * bw + gx - 1]) && !(gx + 1 < bw && goldBlocks[gy * bw + gx + 1])
                    && !(gy > 0 && goldBlocks[(gy - 1) * bw + gx]) && !(gy + 1 < bh && goldBlocks[(gy + 1) * bw + gx]))
                {
                    x = (gx + 1) * Block - 1;
                    continue;
                }
                int c = p[y * w + x];
                if (!IsGold(c)) continue;
                // The gold pixel at (x,y) is taken as the template anchor; allow 1px slack.
                for (int sy = -1; sy <= 1; sy++)
                    for (int sx = -1; sx <= 1; sx++)
                    {
                        int ox = x - adx + sx, oy = y - ady + sy;
                        if (ox < 0 || oy < 0 || ox + tplW > w || oy + tplH > h) continue;
                        double s = Score(p, w, ox, oy);
                        if (s > best) { best = s; bx = ox; by = oy; }
                    }
                if (bx >= 0 && best > 0.9) return true;
            }
        return bx >= 0;
    }

    double Score(int[] p, int w, int ox, int oy)
    {
        int o = oy * w + ox, hit = 0, miss = 0;
        // Bail out early: most candidates fail within the first few pixels.
        for (int i = 0; i < tplOn.Length; i++)
        {
            int k = tplOn[i];
            if (IsGold(p[o + (k >> 16) * w + (k & 0xFFFF)])) hit++;
            if (i == 32 && hit < 16) return 0;
        }
        foreach (var k in tplOff) if (IsGold(p[o + (k >> 16) * w + (k & 0xFFFF)])) miss++;
        return (hit - miss) / (double)tplOn.Length;
    }

    // Right edge of the gold header line ("... (Tier 15)"), so the label can sit after it.
    int HeaderRight(int[] p, int w, int hx, int hy)
    {
        int right = hx + tplW, gap = 0, maxGap = S(30);
        for (int x = hx + tplW; x < w && gap <= maxGap; x++)
        {
            bool any = false;
            for (int y = hy; y < hy + tplH && !any; y++) any = IsGold(p[y * w + x]);
            if (any) { right = x; gap = 0; } else gap++;
        }
        return right;
    }

    List<Band> ModBands(int[] p, int w, int x0, int x1, int y0, int y1)
    {
        var bands = new List<Band>(); var cur = new Band(); bool open = false;
        int maxRowGap = S(2) + 1;
        int rows = Math.Max(0, y1 - y0);
        var count = new int[rows];
        for (int y = y0; y < y1; y++)
        {
            int n = 0, row = y * w;
            for (int x = x0; x < x1; x++) if (IsModBlue(p[row + x])) n++;
            count[y - y0] = n;
        }
        // Blue item icons seen through the translucent tooltip leave a few blue pixels in the rows between
        // two mod lines; they must not join the lines into one. A text row has many times more, so rows
        // are judged against the densest rows of this tooltip.
        var sorted = (int[])count.Clone();
        Array.Sort(sorted);
        int dense = rows == 0 ? 0 : sorted[rows * 95 / 100];
        int minRowPixels = Math.Max(3, dense / 6);
        for (int y = y0; y < y1; y++)
        {
            int n = count[y - y0], g = 0, lo = int.MaxValue, hi = -1, row = y * w;
            if (n < minRowPixels) n = 0;
            else
                for (int x = x0; x < x1; x++)
                {
                    int c = p[row + x];
                    if (IsModBlue(c)) { if (x < lo) lo = x; hi = x; }
                    else if (IsGreyLabel(c)) g++;
                }
            if (n > 0)
            {
                if (!open) { cur = new Band { Y0 = y, X0 = lo, X1 = hi }; open = true; }
                cur.Y1 = y; cur.Blue += n; cur.Grey += g;
                cur.X0 = Math.Min(cur.X0, lo); cur.X1 = Math.Max(cur.X1, hi);
            }
            else if (open && y - cur.Y1 > maxRowGap) { bands.Add(cur); open = false; }
        }
        if (open) bands.Add(cur);

        // Mod lines are wide blue text; the stats block above has short blue values next to grey labels.
        int minWidth = S(120), minHeight = S(10), maxLineGap = S(60);
        var mods = new List<Band>();
        foreach (var b in bands)
        {
            if (b.Y1 - b.Y0 < minHeight) continue; // a sliver of icon pixels, not a text line
            bool isMod = b.X1 - b.X0 >= minWidth && b.Grey * 2 < b.Blue;
            if (!isMod) { if (mods.Count > 0) break; continue; }
            if (mods.Count > 0 && b.Y0 - mods[mods.Count - 1].Y1 > maxLineGap) break;
            mods.Add(b);
        }
        return mods;
    }

    // Two mods span two lines each; OCR on a blue-only mask finds them regardless of the translucent background.
    int CountPairs(int[] p, int w, List<Band> mods)
    {
        if (ocr == null) return 0;
        int pad = S(4);
        int x0 = Math.Max(0, mods.Min(m => m.X0) - pad), x1 = Math.Min(w, mods.Max(m => m.X1) + pad);
        int y0 = Math.Max(0, mods[0].Y0 - pad), y1 = mods[mods.Count - 1].Y1 + pad;
        int bw = x1 - x0, bh = Math.Min(y1, p.Length / w) - y0;
        // Windows OCR misreads text below ~1440p size, so small text is upscaled.
        int k = scale < 1 ? 2 : 1, ow = bw * k, oh = bh * k;
        var buf = new byte[ow * oh * 4];
        for (int y = 0; y < oh; y++)
            for (int x = 0; x < ow; x++)
            {
                byte v = BlueInk(p[(y0 + y / k) * w + x0 + x / k]);
                int i = (y * ow + x) * 4;
                buf[i] = buf[i + 1] = buf[i + 2] = v; buf[i + 3] = 255;
            }
        string text;
        using (var bmp = new SoftwareBitmap(BitmapPixelFormat.Bgra8, ow, oh, BitmapAlphaMode.Premultiplied))
        {
            bmp.CopyFromBuffer(Windows.Security.Cryptography.CryptographicBuffer.CreateFromByteArray(buf));
            var op = ocr.RecognizeAsync(bmp);
            while (op.Status == Windows.Foundation.AsyncStatus.Started) System.Threading.Thread.Sleep(1);
            if (op.Status != Windows.Foundation.AsyncStatus.Completed) return 0;
            text = string.Join("\n", op.GetResults().Lines.Select(l => l.Text)).ToUpperInvariant();
            op.Close();
        }
        LastOcrText = text;
        int pairs = 0;
        if (text.Contains("DAMAGE BONUS")) pairs++;
        if (text.Contains("STUN THRESHOLD")) pairs++;
        return pairs;
    }
}
