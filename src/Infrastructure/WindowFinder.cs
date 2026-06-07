using System.Runtime.InteropServices;
using System.Text;

namespace MaaEnd_Log_Retransmitter.Infrastructure;

internal static class WindowFinder
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    private const uint GW_OWNER = 4;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    public static WindowInfo? FindWindow(string title, bool partialMatch)
    {
        var results = new List<(WindowInfo Window, bool ExactMatch)>();

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;

            int length = GetWindowTextLength(hWnd);
            if (length == 0) return true;

            var sb = new StringBuilder(length + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            var windowTitle = sb.ToString();

            if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero) return true;

            var exactMatch = GetTitleMatch(windowTitle, title, partialMatch);

            if (exactMatch != null && GetWindowRect(hWnd, out var rect))
            {
                results.Add((
                    new WindowInfo
                    {
                        Handle = hWnd,
                        Title = windowTitle,
                        Rect = rect
                    },
                    exactMatch.Value));
            }

            return true;
        }, IntPtr.Zero);

        return results
            .OrderBy(match => match.ExactMatch ? 0 : 1)
            .ThenBy(match => IsIconic(match.Window.Handle) ? 1 : 0)
            .ThenByDescending(match => match.Window.Width * match.Window.Height)
            .Select(match => match.Window)
            .FirstOrDefault();
    }

    private static bool? GetTitleMatch(string windowTitle, string title, bool partialMatch)
    {
        if (windowTitle.Equals(title, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if ((partialMatch || title.Length < windowTitle.Length)
            && windowTitle.Contains(title, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
