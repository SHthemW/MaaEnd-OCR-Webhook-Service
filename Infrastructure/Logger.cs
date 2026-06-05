namespace MaaEnd_Log_Retransmitter.Infrastructure;

internal static class Logger
{
    public static void Info(string message) => Log("INFO", message, ConsoleColor.Gray);
    public static void Warn(string message) => Log("WARN", message, ConsoleColor.Yellow);
    public static void Error(string message) => Log("ERROR", message, ConsoleColor.Red);
    public static void Debug(string message) => Log("DEBUG", message, ConsoleColor.DarkGray);

    private static void Log(string level, string message, ConsoleColor color)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var line = $"[{timestamp}] [{level}] {message}";
        Console.ForegroundColor = color;
        Console.WriteLine(line);
        Console.ResetColor();
    }
}