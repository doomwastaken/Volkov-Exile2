using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

// VolkovExile.ini next to the exe; written with defaults on first run, edited by hand.
sealed class Config
{
    const string DefaultText =
@"[AntiLandmine]
; Price of each currency in Exalted Orbs. Prices in currencies not listed here are always blocked.
Exalted Orb = 1
Chaos Orb = 65
Divine Orb = 650
; Block a purchase that costs more than this many times the reference item.
MaxRatio = 3
; 1 = save screenshots where no price was found (debug folder next to the exe), to report misses.
SaveMisses = 0
; 1 = act only while a merchant window is detected (experimental: other players' merchant windows are not detected yet).
MerchantOnly = 0
";

    public readonly Dictionary<string, double> Rates = new Dictionary<string, double>();
    public double MaxRatio = 3;
    public bool SaveMisses;
    public bool MerchantOnly;

    public static string Path { get { return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VolkovExile.ini"); } }

    public static Config Load()
    {
        string text = DefaultText;
        try
        {
            if (File.Exists(Path)) text = File.ReadAllText(Path);
            else File.WriteAllText(Path, DefaultText);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        var c = new Config();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '[') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line.Substring(0, eq).Trim();
            double value;
            if (!double.TryParse(line.Substring(eq + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) continue;
            if (key.Equals("SaveMisses", StringComparison.OrdinalIgnoreCase)) c.SaveMisses = value != 0;
            else if (key.Equals("MerchantOnly", StringComparison.OrdinalIgnoreCase)) c.MerchantOnly = value != 0;
            else if (value <= 0) continue;
            else if (key.Equals("MaxRatio", StringComparison.OrdinalIgnoreCase)) c.MaxRatio = value;
            else c.Rates[Normalize(key)] = value;
        }
        Log.Write("config: {0}, max ratio {1}, save misses {2}", string.Join(", ", c.Rates.Select(r => r.Key + "=" + r.Value)), c.MaxRatio, c.SaveMisses);
        return c;
    }

    static string Normalize(string s) { return new string(s.Where(char.IsLetter).Select(char.ToUpperInvariant).ToArray()); }

    // Price in Exalted Orbs of one unit of the currency as read by OCR (tolerates a couple of misread letters);
    // 0 when the currency is not configured.
    public double RateOf(string currency)
    {
        var n = Normalize(currency);
        double best = 0; int bestDistance = 3;
        foreach (var r in Rates)
        {
            int d = Distance(n, r.Key);
            if (d < bestDistance) { bestDistance = d; best = r.Value; }
        }
        return best;
    }

    static int Distance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            int diag = row[0]; row[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int up = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diag + (a[i - 1] == b[j - 1] ? 0 : 1));
                diag = up;
            }
        }
        return row[b.Length];
    }
}
