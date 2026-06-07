using System.Runtime.InteropServices;

namespace MaaEnd_Log_Retransmitter.Infrastructure;

internal static class DpiAwareness
{
    private static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = new(-4);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("shcore.dll")]
    private static extern int SetProcessDpiAwareness(PROCESS_DPI_AWARENESS value);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    private enum PROCESS_DPI_AWARENESS
    {
        ProcessDpiUnaware = 0,
        ProcessSystemDpiAware = 1,
        ProcessPerMonitorDpiAware = 2
    }

    public static void Enable()
    {
        try
        {
            if (SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2))
            {
                Logger.Debug("DPI 感知: PerMonitorV2");
                return;
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }

        try
        {
            if (SetProcessDpiAwareness(PROCESS_DPI_AWARENESS.ProcessPerMonitorDpiAware) == 0)
            {
                Logger.Debug("DPI 感知: PerMonitor");
                return;
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }

        try
        {
            if (SetProcessDPIAware())
                Logger.Debug("DPI 感知: System aware");
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
}