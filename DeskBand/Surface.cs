using System;
using System.Drawing;
using System.Drawing.Imaging;

namespace NetSummaryBar
{
        internal sealed class Surface : IDisposable
        {
            public readonly int Width;
            public readonly int Height;
            public readonly Graphics G;
            private readonly Bitmap _bmp;
            private readonly IntPtr _memDc;
            private readonly IntPtr _hBitmap;
            private readonly IntPtr _oldObj;
    
            public Surface(int w, int h)
            {
                Width = w;
                Height = h;
    
                IntPtr screenDc = Native.GetDC(IntPtr.Zero);
                try
                {
                    _memDc = Native.CreateCompatibleDC(screenDc);
                    Native.BITMAPINFOHEADER bi = new Native.BITMAPINFOHEADER();
                    bi.biSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER));
                    bi.biWidth = w;
                    bi.biHeight = -h; // top-down
                    bi.biPlanes = 1;
                    bi.biBitCount = 32;
                    bi.biCompression = 0; // BI_RGB
                    IntPtr bits;
                    _hBitmap = Native.CreateDIBSection(screenDc, ref bi, 0, out bits, IntPtr.Zero, 0);
                    if (_hBitmap == IntPtr.Zero || bits == IntPtr.Zero)
                        throw new InvalidOperationException("CreateDIBSection failed");
                    _oldObj = Native.SelectObject(_memDc, _hBitmap);
                    _bmp = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits);
                    G = Graphics.FromImage(_bmp);
                }
                finally
                {
                    Native.ReleaseDC(IntPtr.Zero, screenDc);
                }
            }
    
            public bool Present(IntPtr hwnd)
            {
                Native.GdiFlush();
                Native.SIZE size = new Native.SIZE();
                size.cx = Width;
                size.cy = Height;
                Native.POINT src = new Native.POINT();
                Native.BLENDFUNCTION blend = new Native.BLENDFUNCTION();
                blend.BlendOp = Native.AC_SRC_OVER;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = Native.AC_SRC_ALPHA;
                bool ok = Native.UpdateLayeredWindow(hwnd, IntPtr.Zero, IntPtr.Zero, ref size, _memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
            if (!ok) Log.Write("UpdateLayeredWindow failed, Win32 error " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            return ok;
            }
    
            public void Dispose()
            {
                if (G != null) G.Dispose();
                if (_bmp != null) _bmp.Dispose();
                if (_memDc != IntPtr.Zero)
                {
                    if (_oldObj != IntPtr.Zero) Native.SelectObject(_memDc, _oldObj);
                    Native.DeleteDC(_memDc);
                }
                if (_hBitmap != IntPtr.Zero) Native.DeleteObject(_hBitmap);
            }
        }
}
