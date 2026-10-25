using System;
using System.Drawing;

// Checks the currency-tab craft slot, which sits at a fixed place in the fullscreen game.
// A search match draws a 1px gold line just inside the slot's black outline. The cursor and the
// item tooltip can't cover all four corners at once, so one gold corner is enough.
static class SlotDetector
{
    // Black outline of the slot in a 2560x1440 client, measured on the samples (Right/Bottom exclusive).
    static readonly Rectangle RefSlot = Rectangle.FromLTRB(390, 487, 500, 705);
    const int RefHeight = 1440;
    const int RefArm = 14;            // how far along a side from the corner the gold must run
    const int Slack = 2;              // rounding after scaling can shift a line by a pixel or two
    const double ArmFraction = 0.7;

    // Where the slot item's tooltip can be: above the slot, or beside it when it is too tall (1440p reference).
    static readonly Rectangle RefTooltipZone = Rectangle.FromLTRB(0, 0, 1800, 705);

    // One capture area covering both the slot and its tooltip, in client coordinates.
    public static Rectangle WatchBounds(int gameHeight)
    {
        var r = Scale(RefTooltipZone, gameHeight);
        r.Height += Slack + 2;
        return r;
    }

    public static Rectangle SlotBounds(int gameHeight) { return Scale(RefSlot, gameHeight); }

    static Rectangle Scale(Rectangle r, int gameHeight)
    {
        double s = gameHeight / (double)RefHeight;
        return Rectangle.FromLTRB((int)Math.Round(r.Left * s), (int)Math.Round(r.Top * s),
                                  (int)Math.Round(r.Right * s), (int)Math.Round(r.Bottom * s));
    }

    // Identifies the current roll by the layout of the tooltip's blue mod text. A reroll changes the mods and so
    // the hash, also when neither roll matches the search. Only rows dense with blue count, so stray blue
    // particles in the game scene don't change it. 0 means no mod text is visible.
    public static long RollFingerprint(int[] p, int w, int h)
    {
        const long FnvPrime = 1099511628211;
        const int MinRowPixels = 6;
        long hash = unchecked((long)14695981039346656037);
        int rows = 0;
        for (int y = 0; y < h; y += 2)
        {
            int n = 0, first = -1, row = y * w;
            for (int x = 0; x < w; x += 2)
                if (Analyzer.IsModBlue(p[row + x])) { n++; if (first < 0) first = x; }
            if (n < MinRowPixels) continue;
            hash = unchecked((hash ^ y) * FnvPrime);
            hash = unchecked((hash ^ first) * FnvPrime);
            hash = unchecked((hash ^ n) * FnvPrime);
            rows++;
        }
        return rows < 2 ? 0 : hash;
    }

    // Measured on the highlight frame: RGB(231,180,119).
    static bool IsMatchGold(int c)
    {
        int r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255;
        return Math.Abs(r - 231) < 35 && Math.Abs(g - 180) < 35 && Math.Abs(b - 119) < 35;
    }

    static bool IsOutline(int c) { return ((c >> 16) & 255) <= 6 && ((c >> 8) & 255) <= 6 && (c & 255) <= 6; }

    // Whether the currency tab's craft slot is on screen: a thin black outline at the expected place with no
    // grid lines inside (other stash tabs draw a cell grid there). The tooltip and the cursor cover at most
    // two sides, so two visible sides are enough; a highlighted slot counts as present regardless.
    public static bool Present(int[] p, int w, int h, Rectangle slot)
    {
        int sides = 0;
        if (ThinLine(p, w, h, slot.Left, slot.Top, slot.Bottom, true)) sides++;
        if (ThinLine(p, w, h, slot.Right - 1, slot.Top, slot.Bottom, true)) sides++;
        if (ThinLine(p, w, h, slot.Top, slot.Left, slot.Right, false)) sides++;
        if (ThinLine(p, w, h, slot.Bottom - 1, slot.Left, slot.Right, false)) sides++;
        if (sides < 2) return false;
        int margin = Math.Max(4, slot.Width / 10);
        for (int x = slot.Left + margin; x < slot.Right - margin; x++)
            if (ThinLine(p, w, h, x, slot.Top, slot.Bottom, true)) return false;
        for (int y = slot.Top + margin; y < slot.Bottom - margin; y++)
            if (ThinLine(p, w, h, y, slot.Left, slot.Right, false)) return false;
        return true;
    }

    // A 1-2px black line at pos (±1 px) over most of the span, with non-black on both sides of it,
    // so a plain black area doesn't count as a line.
    static bool ThinLine(int[] p, int w, int h, int pos, int from, int to, bool vertical)
    {
        int len = to - from - 4;
        for (int o = pos - 1; o <= pos + 1; o++)
        {
            int hit = 0;
            for (int k = from + 2; k < to - 2; k++)
            {
                int c, before, after;
                if (vertical) { c = At(p, w, h, o, k); before = At(p, w, h, o - 2, k); after = At(p, w, h, o + 2, k); }
                else { c = At(p, w, h, k, o); before = At(p, w, h, k, o - 2); after = At(p, w, h, k, o + 2); }
                if (IsOutline(c) && !(IsOutline(before) && IsOutline(after))) hit++;
            }
            if (hit >= len * 0.6) return true;
        }
        return false;
    }

    // p is a w*h buffer; slot is the slot outline in buffer coordinates.
    public static bool Highlighted(int[] p, int w, int h, Rectangle slot, int gameHeight)
    {
        int arm = Math.Max(5, RefArm * gameHeight / RefHeight);
        // Expected gold corners: one pixel inside the outline.
        int x0 = slot.Left + 1, y0 = slot.Top + 1, x1 = slot.Right - 2, y1 = slot.Bottom - 2;
        return Corner(p, w, h, x0, y0, 1, 1, arm) || Corner(p, w, h, x1, y0, -1, 1, arm)
            || Corner(p, w, h, x0, y1, 1, -1, arm) || Corner(p, w, h, x1, y1, -1, -1, arm);
    }

    // A gold corner pixel near (cx, cy) plus a gold run along either side of it; sides go in (dx, dy).
    static bool Corner(int[] p, int w, int h, int cx, int cy, int dx, int dy, int arm)
    {
        for (int oy = -Slack; oy <= Slack; oy++)
            for (int ox = -Slack; ox <= Slack; ox++)
            {
                int x = cx + ox, y = cy + oy, horiz = 0, vert = 0;
                if (!IsMatchGold(At(p, w, h, x, y))) continue;
                for (int k = 1; k <= arm; k++)
                {
                    if (IsMatchGold(At(p, w, h, x + dx * k, y))) horiz++;
                    if (IsMatchGold(At(p, w, h, x, y + dy * k))) vert++;
                }
                if (horiz >= arm * ArmFraction || vert >= arm * ArmFraction) return true;
            }
        return false;
    }

    static int At(int[] p, int w, int h, int x, int y) { return x < 0 || y < 0 || x >= w || y >= h ? 0x7F7F7F : p[y * w + x]; }
}
