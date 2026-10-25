using System;
using System.Runtime.InteropServices;

// Screen capture into a reused int[] via BitBlt + GetDIBits; no per-call allocations.
// One instance per thread.
sealed class ScreenGrab : IDisposable
{
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, int[] bits, byte[] bmi, uint usage);

    IntPtr screenDc, memDc, bitmap, standIn;
    readonly byte[] bmi = new byte[40];
    public int Width, Height;
    public int[] Pixels;

    public void Capture(int x, int y, int w, int h)
    {
        if (w != Width || h != Height) Allocate(w, h);
        BitBlt(memDc, 0, 0, w, h, screenDc, x, y, 0xCC0020 /* SRCCOPY */);
        // GetDIBits needs the bitmap deselected from every DC.
        SelectObject(memDc, standIn);
        GetDIBits(memDc, bitmap, 0, (uint)h, Pixels, bmi, 0);
        SelectObject(memDc, bitmap);
    }

    void Allocate(int w, int h)
    {
        Release();
        screenDc = GetDC(IntPtr.Zero);
        memDc = CreateCompatibleDC(screenDc);
        bitmap = CreateCompatibleBitmap(screenDc, w, h);
        standIn = CreateCompatibleBitmap(screenDc, 1, 1);
        SelectObject(memDc, bitmap);
        Width = w; Height = h;
        Pixels = new int[w * h];
        BitConverter.GetBytes(40).CopyTo(bmi, 0);
        BitConverter.GetBytes(w).CopyTo(bmi, 4);
        BitConverter.GetBytes(-h).CopyTo(bmi, 8);      // top-down rows
        BitConverter.GetBytes((short)1).CopyTo(bmi, 12);
        BitConverter.GetBytes((short)32).CopyTo(bmi, 14);
    }

    void Release()
    {
        if (memDc != IntPtr.Zero) DeleteDC(memDc);
        if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
        if (standIn != IntPtr.Zero) DeleteObject(standIn);
        if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        memDc = bitmap = standIn = screenDc = IntPtr.Zero;
        Pixels = null; Width = Height = 0;
    }

    public void Dispose() { Release(); }
}
