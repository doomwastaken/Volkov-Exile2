using System;
using System.IO;

// Appends timestamped lines to VolkovExile.log next to the exe; flushed per line so it can be tailed live.
static class Log
{
    static readonly object gate = new object();
    static StreamWriter writer;

    public static void Write(string format, params object[] args)
    {
        lock (gate)
        {
            try
            {
                if (writer == null)
                    writer = new StreamWriter(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VolkovExile.log"), true) { AutoFlush = true };
                writer.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + string.Format(format, args));
            }
            catch (IOException) { } // a locked log file must never break the overlay
        }
    }
}
