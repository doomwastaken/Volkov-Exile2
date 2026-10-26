using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

// A merchant's asking price as shown in the hovered item's tooltip: "<amount>x <icon> <currency>".
sealed class Price
{
    public long Amount;        // 0 when the currency line was found but the amount couldn't be read
    public string Currency;    // as read, e.g. "CHAOS ORB"
    public override string ToString() { return (Amount > 0 ? Amount.ToString("N0") : "?") + "x " + Currency; }
}

// Finds the price line on screen. OCR reads the currency name well but not the short amount next to the icon,
// so OCR only locates text lines; a line is the price line when an amount ending in "x" stands left of it.
// The amount itself is read by matching glyphs against templates of the game's digit font.
sealed class PriceReader
{
    const int GW = 8, GH = 12;   // glyph template grid

    readonly OcrEngine ocr = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));

    public string LastText { get; private set; }
    public bool Available { get { return ocr != null; } }

    // Light, nearly grey pixels: the white amount and the beige currency name. Coloured mod text and the
    // darker tooltip background are not ink.
    static bool IsInk(int c)
    {
        int r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255;
        int lo = Math.Min(r, Math.Min(g, b)), hi = Math.Max(r, Math.Max(g, b));
        return lo >= 120 && hi - lo < 90;
    }

    // Reads the price in the area (x0, y0, bw, bh) of p (width w). Returns null when no price line is there.
    public Price Read(int[] p, int w, int x0, int y0, int bw, int bh, bool upscale)
    {
        LastText = null;
        if (ocr == null || bw <= 0 || bh <= 0) return null;
        int k = upscale ? 2 : 1, ow = bw * k, oh = bh * k;
        var buf = new byte[ow * oh * 4];
        for (int y = 0; y < oh; y++)
        {
            int src = (y0 + y / k) * w + x0;
            for (int x = 0; x < ow; x++)
            {
                byte v = IsInk(p[src + x / k]) ? (byte)0 : (byte)255;
                int i = (y * ow + x) * 4;
                buf[i] = buf[i + 1] = buf[i + 2] = v; buf[i + 3] = 255;
            }
        }

        var lines = new List<OcrLine>();
        using (var bmp = new SoftwareBitmap(BitmapPixelFormat.Bgra8, ow, oh, BitmapAlphaMode.Premultiplied))
        {
            bmp.CopyFromBuffer(Windows.Security.Cryptography.CryptographicBuffer.CreateFromByteArray(buf));
            var op = ocr.RecognizeAsync(bmp);
            while (op.Status == Windows.Foundation.AsyncStatus.Started) System.Threading.Thread.Sleep(1);
            if (op.Status != Windows.Foundation.AsyncStatus.Completed) return null;
            lines.AddRange(op.GetResults().Lines);
            op.Close();
        }
        LastText = string.Join("\n", lines.Select(l => l.Text));

        foreach (var line in lines)
        {
            // The currency name: the words from the first one made of 3+ letters.
            var words = line.Words.ToList();
            int first = words.FindIndex(wd => wd.Text.Count(char.IsLetter) >= 3);
            if (first < 0) continue;
            var name = words.Skip(first).ToList();
            double left = name[0].BoundingRect.X / k, top = double.MaxValue, bottom = 0;
            foreach (OcrWord wd in name)
            {
                Windows.Foundation.Rect br = wd.BoundingRect;
                top = Math.Min(top, br.Y / k); bottom = Math.Max(bottom, (br.Y + br.Height) / k);
            }
            int h = (int)Math.Round(bottom - top);
            if (h < 6) continue;
            // OCR boxes can be off by most of a line; take the text rows from the pixels of the name.
            int nx = x0 + (int)left, ny0 = y0 + (int)top - h, ny1 = y0 + (int)bottom + h;
            if (!TextRows(p, w, nx, nx + h * 3, Math.Max(y0, ny0), Math.Min(y0 + bh, ny1), out ny0, out ny1)) continue;
            top = ny0 - y0; h = ny1 - ny0;
            if (DumpGlyphs) Console.WriteLine(" line '{0}' left {1} top {2} h {3}", line.Text, x0 + (int)left, y0 + (int)top, h);
            long amount = AmountLeftOf(p, w, x0 + (int)left, y0 + (int)top, h, x0);
            if (amount == 0) continue;
            if (amount < 0) amount = 0;
            var currency = string.Join(" ", name.Select(wd => wd.Text)).ToUpperInvariant();
            return new Price { Amount = amount, Currency = currency };
        }
        return null;
    }

    // The run of rows with ink between x0 and x1 that is closest to the middle of y0..y1.
    static bool TextRows(int[] p, int w, int x0, int x1, int y0, int y1, out int top, out int bottom)
    {
        top = bottom = 0;
        x1 = Math.Min(x1, w);
        int start = -1, best = int.MaxValue, mid = (y0 + y1) / 2;
        for (int y = y0; y <= y1; y++)
        {
            bool ink = false;
            if (y < y1) for (int x = x0; x < x1 && !ink; x++) ink = IsInk(p[y * w + x]);
            if (ink && start < 0) start = y;
            else if (!ink && start >= 0)
            {
                int dist = mid < start ? start - mid : mid >= y ? mid - y + 1 : 0;
                if (y - start >= 6 && dist < best) { best = dist; top = start; bottom = y; }
                start = -1;
            }
        }
        return best != int.MaxValue;
    }

    // Glyphs of the amount sit left of the currency icon, in the band of the currency name's text.
    long AmountLeftOf(int[] p, int w, int nameLeft, int top, int h, int minX)
    {
        int pad = h * 2 / 5;
        int y0 = Math.Max(0, top - pad), y1 = Math.Min(p.Length / w, top + h + pad);
        int x1 = nameLeft - 1, x0 = Math.Max(minX, nameLeft - h * 14);
        var glyphs = Segment(p, w, x0, x1, y0, y1, h);
        int maxGap = h / 2 + 1;

        // Take the last "x" with a contiguous run of digits (and separators) before it. An "x" whose amount
        // can't be read still marks the price line: -1, so the caller blocks rather than passes.
        bool sawX = false;
        for (int i = glyphs.Count - 1; i > 0; i--)
        {
            if (glyphs[i].Char != 'x') continue;
            sawX = true;
            long amount = 0, mul = 1; int digits = 0;
            for (int j = i - 1; j >= 0; j--)
            {
                if (glyphs[j + 1].X0 - glyphs[j].X1 > maxGap) break;
                char c = glyphs[j].Char;
                if (c == ',') continue;
                if (c < '0' || c > '9') { if (digits > 0) break; digits = -1; break; }
                amount += (c - '0') * mul; mul *= 10; digits++;
                if (digits > 9) { digits = -1; break; }
            }
            if (digits > 0) return amount;
        }
        return sawX ? -1 : 0;
    }

    sealed class Glyph { public int X0, X1, Y0, Y1; public char Char; public int Distance; public string Bits; }

    public static bool DumpGlyphs = Environment.GetEnvironmentVariable("WM_GLYPHS") != null;

    List<Glyph> Segment(int[] p, int w, int x0, int x1, int y0, int y1, int lineHeight)
    {
        var list = new List<Glyph>();
        int start = -1;
        for (int x = x0; x <= x1 + 1; x++)
        {
            bool ink = false;
            if (x <= x1) for (int y = y0; y < y1 && !ink; y++) ink = IsInk(p[y * w + x]);
            if (ink && start < 0) start = x;
            else if (!ink && start >= 0) { list.Add(Make(p, w, start, x, y0, y1)); start = -1; }
        }
        foreach (var g in list) Classify(g, lineHeight);
        if (DumpGlyphs)
            foreach (var g in list)
                Console.WriteLine("  glyph x{0}-{1} y{2}-{3} '{4}' d{5} {6}", g.X0, g.X1, g.Y0, g.Y1, g.Char, g.Distance, g.Bits);
        return list;
    }

    static Glyph Make(int[] p, int w, int gx0, int gx1, int y0, int y1)
    {
        int top = y1, bottom = y0;
        for (int y = y0; y < y1; y++)
            for (int x = gx0; x < gx1; x++)
                if (IsInk(p[y * w + x])) { if (y < top) top = y; bottom = y + 1; break; }
        var g = new Glyph { X0 = gx0, X1 = gx1, Y0 = top, Y1 = bottom };
        var sb = new StringBuilder(GW * GH);
        int gw = gx1 - gx0, gh = bottom - top;
        for (int cy = 0; cy < GH; cy++)
            for (int cx = 0; cx < GW; cx++)
            {
                int ax = gx0 + cx * gw / GW, bx = Math.Max(ax + 1, gx0 + (cx + 1) * gw / GW);
                int ay = top + cy * gh / GH, by = Math.Max(ay + 1, top + (cy + 1) * gh / GH);
                int n = 0, total = 0;
                for (int y = ay; y < by; y++) for (int x = ax; x < bx; x++) { total++; if (IsInk(p[y * w + x])) n++; }
                sb.Append(n * 3 >= total ? '#' : '.');
            }
        g.Bits = sb.ToString();
        return g;
    }

    // Thousands separators are small marks; the rest is matched against the font templates.
    static void Classify(Glyph g, int lineHeight)
    {
        g.Char = '?'; g.Distance = int.MaxValue;
        if ((g.X1 - g.X0) * 3 <= lineHeight && (g.Y1 - g.Y0) * 2 <= lineHeight) { g.Char = ','; g.Distance = 0; return; }
        foreach (var t in Templates)
        {
            int d = 0;
            for (int i = 0; i < t.Value.Length; i++) if (t.Value[i] != g.Bits[i]) d++;
            if (d < g.Distance) { g.Distance = d; g.Char = t.Key; }
        }
        if (g.Distance > MaxDistance) g.Char = '?';
    }

    const int MaxDistance = 14;

    // 8x12 cells of the game's price font, from the 1440p screenshots in samples/.
    static readonly KeyValuePair<char, string>[] Templates =
    {
        T('4', ".....##.....###......##....#.##...#..##..##..##..#...##.#....##.########.....##......##......##."),
        T('8', ".##..##..#.....###.....###.......###......####......###.......###......##......##......########."),
        T('9', "..#..##..#....###.....###......##......###.....####....#..##...#......##......##.....##..#####.."),
        T('0', "...####...#...##.##...##.#.....#.#.....###.....###.....###.....#.#.....#.##....#.##...##..#####."),
        T('1', "....############.......#.......#.......#.......#.......#.......#.......#.......#.......#.......#"),
        T('2', ".#..###.#....##.......##......##......#......##......#......#......#......#......#......########"),
        T('3', ".#..###.#....##.......##.....##............####.......##......##......##......##.....##.###.##.."),
        T('5', ".#######.#.......#...............####.......###.......##......##......##.....##......##.###.#..."),
        T('6', "...##..#..#.....###.....##......##......##.####.#......##......###.....###.....###.....#.######."),
        T('7', "########..##...#......##......#......##......#......##......#......##......#......#......##....."),
        T('x', ".##....#.##....#..##..#...##..#....#.#.....##......##......###.....###....#..##..##...####....##"),
    };

    static KeyValuePair<char, string> T(char c, string bits) { return new KeyValuePair<char, string>(c, bits); }
}
