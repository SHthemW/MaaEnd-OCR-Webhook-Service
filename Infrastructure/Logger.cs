namespace MaaEnd_Log_Retransmitter.Infrastructure;

internal static class Logger
{
    private enum LogLevel
    {
        Debug = 0,
        Info = 1,
        Warn = 2,
        Error = 3
    }

    private static readonly LogLevel MinimumLevel = LogLevel.Info;

    public static void Info(string message) => Log("INFO", message, ConsoleColor.Gray);
    public static void Warn(string message) => Log("WARN", message, ConsoleColor.Yellow);
    public static void Error(string message) => Log("ERROR", message, ConsoleColor.Red);
    public static void Debug(string message) => Log("DEBUG", message, ConsoleColor.DarkGray);

    private static void Log(string level, string message, ConsoleColor color)
    {
        if (!ShouldLog(level))
        {
            return;
        }

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var line = $"[{timestamp}] [{level}] {message}";
        Console.ForegroundColor = color;
        Console.WriteLine(line);
        Console.ResetColor();
    }

    private static bool ShouldLog(string level) => ParseLevel(level) >= MinimumLevel;

    private static LogLevel ParseLevel(string level) => level switch
    {
        "DEBUG" => LogLevel.Debug,
        "INFO" => LogLevel.Info,
        "WARN" => LogLevel.Warn,
        "ERROR" => LogLevel.Error,
        _ => LogLevel.Info
    };
}