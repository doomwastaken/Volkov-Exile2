using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

// WORK IN PROGRESS: finds your own shop's window, not yet another player's merchant window (samples/landmine_3, _5).
// Finds the merchant window by its "MERCHANT" title: black letters on a bright gold plate. The window sits
// in different places (centre for another player's shop, left for your own), so the top of the screen is searched.
sealed class MerchantDetector
{
    const int RefHeight = 1440;
    const double MinScore = 0.85;
    const int MaxProbeMisses = 3;

    readonly Bitmap template;
    int scaledFor, tplW, tplH;
    int[] ink, plate;     // template points (y << 16 | x)
    int[] probe;          // a few spread-out points checked first to reject most positions cheaply

    public double BestScore { get; private set; }
    public Point BestAt; public double BestInk, BestPlate;

    public MerchantDetector(Bitmap template) { this.template = template; }

    static bool IsInk(int c) { return ((c >> 16) & 255) < 60 && ((c >> 8) & 255) < 60 && (c & 255) < 60; }
    // The plate's gold varies between merchant windows and has a darker texture; any warm, light pixel counts.
    static bool IsPlate(int c) { int r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255; return r > 140 && g > 110 && r >= b + 30; }

    void Prepare(int gameHeight)
    {
        if (scaledFor == gameHeight) return;
        scaledFor = gameHeight;
        double scale = gameHeight / (double)RefHeight;
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
        var i = new List<int>(); var pl = new List<int>();
        for (int k = 0; k < px.Length; k++)
        {
            int pt = (k / tplW) << 16 | (k % tplW);
            if (IsInk(px[k])) i.Add(pt); else if (IsPlate(px[k])) pl.Add(pt);
        }
        ink = i.ToArray(); plate = pl.ToArray();
        probe = new int[16];
        for (int k = 0; k < 8; k++) { probe[2 * k] = ink[k * ink.Length / 8]; probe[2 * k + 1] = plate[k * plate.Length / 8]; }
    }

    // p is a w*h capture of the game client. Searches the top half.
    public bool Find(int[] p, int w, int h, int gameHeight)
    {
        Prepare(gameHeight);
        int maxY = Math.Min(h / 2, h - tplH), maxX = w - tplW;
        BestScore = 0;
        for (int y = 0; y < maxY; y++)
            for (int x = 0; x < maxX; x++)
            {
                int o = y * w + x, miss = 0;
                for (int k = 0; k < probe.Length && miss < MaxProbeMisses; k++)
                {
                    int pt = probe[k], c = p[o + (pt >> 16) * w + (pt & 0xFFFF)];
                    if ((k & 1) == 0 ? !IsInk(c) : !IsPlate(c)) miss++;
                }
                if (miss >= MaxProbeMisses) continue;
                // Letters and plate are scored apart: a plain gold area matches the plate but not the letters.
                int inkHit = 0, plateHit = 0;
                foreach (var pt in ink) if (IsInk(p[o + (pt >> 16) * w + (pt & 0xFFFF)])) inkHit++;
                foreach (var pt in plate) if (IsPlate(p[o + (pt >> 16) * w + (pt & 0xFFFF)])) plateHit++;
                double score = Math.Min(inkHit / (double)ink.Length, plateHit / (double)plate.Length);
                if (score > BestScore) { BestScore = score; BestAt = new Point(x, y); BestInk = inkHit / (double)ink.Length; BestPlate = plateHit / (double)plate.Length; }
                if (score >= MinScore) return true;
            }
        return false;
    }
}
