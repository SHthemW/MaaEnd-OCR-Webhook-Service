using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MaaEnd_Log_Retransmitter.Infrastructure;

internal static class ScreenCapture
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint nFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmRegisterThumbnail(IntPtr hwndDestination, IntPtr hwndSource, out IntPtr phThumbnailId);

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmUnregisterThumbnail(IntPtr hThumbnailId);

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmUpdateThumbnailProperties(IntPtr hThumbnailId, ref DWM_THUMBNAIL_PROPERTIES ptnProperties);

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmQueryThumbnailSourceSize(IntPtr hThumbnailId, out SIZE pSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const uint PW_CLIENTONLY = 0x00000001;
    private const uint PW_RENDERFULLCONTENT = 0x00000002;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const uint LWA_ALPHA = 0x00000002;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint WM_DESTROY = 0x0002;
    private const int DWM_TNP_VISIBLE = 0x8;
    private const int DWM_TNP_OPACITY = 0x4;
    private const int DWM_TNP_RECTDESTINATION = 0x1;
    private const int DWM_TNP_RECTSOURCE = 0x2;

    private static bool _windowClassRegistered;

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        [MarshalAs(UnmanagedType.FunctionPtr)] public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_THUMBNAIL_PROPERTIES
    {
        public int dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        public bool fVisible;
        public bool fSourceClientAreaOnly;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public static Bitmap CaptureWindow(WindowInfo window)
    {
        var rect = window.Rect;
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;

        if (width <= 0 || height <= 0)
            throw new InvalidOperationException($"窗口尺寸无效: {width}x{height}（窗口可能已最小化或不可见）");

        var bitmap = CaptureViaPrintWindow(window.Handle, width, height);
        if (bitmap != null)
        {
            Logger.Debug("截图方式: PrintWindow (直接窗口渲染)");
            return bitmap;
        }

        Logger.Debug("PrintWindow 失败, 尝试 DWM 缩略图 (后台捕获)...");
        bitmap = CaptureViaDwmThumbnail(window.Handle, width, height);
        if (bitmap != null)
        {
            Logger.Debug("截图方式: DWM 缩略图 (后台合成)");
            return bitmap;
        }

        throw new InvalidOperationException("所有截图方式均失败（PrintWindow + DWM 缩略图）");
    }

    public static string SaveScreenshot(Bitmap bitmap, string windowTitle, int attempt)
    {
        var safeName = string.Join("_", windowTitle.Split(Path.GetInvalidFileNameChars()));
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        var filename = $"screenshot_{safeName}_{timestamp}_attempt{attempt}.png";
        var path = Path.Combine(dir, filename);
        bitmap.Save(path, ImageFormat.Png);
        return path;
    }

    private static Bitmap? CaptureViaPrintWindow(IntPtr hWnd, int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        var hdc = g.GetHdc();
        try
        {
            if (PrintWindow(hWnd, hdc, PW_RENDERFULLCONTENT) || PrintWindow(hWnd, hdc, PW_CLIENTONLY))
            {
                return bitmap;
            }

            bitmap.Dispose();
            return null;
        }
        finally
        {
            g.ReleaseHdc(hdc);
        }
    }

    private static Bitmap? CaptureViaDwmThumbnail(IntPtr targetHwnd, int width, int height)
    {
        IntPtr hThumbnail = IntPtr.Zero;
        IntPtr hHostWnd = IntPtr.Zero;

        try
        {
            RegisterHostWindowClass();

            hHostWnd = CreateWindowEx(
                WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                "OcrHostWindow",
                "",
                WS_POPUP,
                0,
                0,
                width,
                height,
                IntPtr.Zero,
                IntPtr.Zero,
                GetModuleHandle(null!),
                IntPtr.Zero);

            if (hHostWnd == IntPtr.Zero) return null;

            SetLayeredWindowAttributes(hHostWnd, 0, 1, LWA_ALPHA);
            ShowWindow(hHostWnd, SW_SHOWNOACTIVATE);

            int hr = DwmRegisterThumbnail(hHostWnd, targetHwnd, out hThumbnail);
            if (hr != 0 || hThumbnail == IntPtr.Zero) return null;

            DwmQueryThumbnailSourceSize(hThumbnail, out var srcSize);
            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = DWM_TNP_VISIBLE | DWM_TNP_RECTSOURCE | DWM_TNP_RECTDESTINATION | DWM_TNP_OPACITY,
                fVisible = true,
                fSourceClientAreaOnly = false,
                opacity = 255,
                rcSource = new RECT { Left = 0, Top = 0, Right = srcSize.cx, Bottom = srcSize.cy },
                rcDestination = new RECT { Left = 0, Top = 0, Right = srcSize.cx, Bottom = srcSize.cy }
            };
            DwmUpdateThumbnailProperties(hThumbnail, ref props);

            DwmFlush();
            Thread.Sleep(50);

            var bitmap = new Bitmap(srcSize.cx, srcSize.cy, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bitmap);
            var hdc = g.GetHdc();
            try
            {
                if (!PrintWindow(hHostWnd, hdc, PW_RENDERFULLCONTENT))
                {
                    bitmap.Dispose();
                    return null;
                }
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }

            return bitmap;
        }
        catch (Exception ex)
        {
            Logger.Debug($"DWM 缩略图捕获异常: {ex.Message}");
            return null;
        }
        finally
        {
            if (hThumbnail != IntPtr.Zero) DwmUnregisterThumbnail(hThumbnail);
            if (hHostWnd != IntPtr.Zero) DestroyWindow(hHostWnd);
        }
    }

    private static void RegisterHostWindowClass()
    {
        if (_windowClassRegistered)
        {
            return;
        }

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = HostWndProc,
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = GetModuleHandle(null!),
            hIcon = IntPtr.Zero,
            hCursor = IntPtr.Zero,
            hbrBackground = IntPtr.Zero,
            lpszMenuName = "",
            lpszClassName = "OcrHostWindow",
            hIconSm = IntPtr.Zero
        };

        if (RegisterClassEx(ref wc) == 0)
            throw new InvalidOperationException("注册宿主窗口类失败");

        _windowClassRegistered = true;
    }

    private static IntPtr HostWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_DESTROY) return IntPtr.Zero;
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }
}