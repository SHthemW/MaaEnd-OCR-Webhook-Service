namespace MaaEnd_Log_Retransmitter.Infrastructure;

internal sealed class WindowInfo
{
    public IntPtr Handle { get; set; }
    public string Title { get; set; } = "";
    public WindowFinder.RECT Rect { get; set; }
    public int Width => Rect.Right - Rect.Left;
    public int Height => Rect.Bottom - Rect.Top;
}