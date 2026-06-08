using System.Text;
using MaaEnd_Log_Retransmitter.App;
using MaaEnd_Log_Retransmitter.Infrastructure;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;
DpiAwareness.Enable();

try
{
    var options = RuntimeOptions.Parse(args);
    Logger.SetDebugEnabled(options.Debug);
    return await AppRunner.RunAsync(options);
}
catch (Exception ex)
{
    Logger.Error($"程序异常退出: {ex.Message}");
    Logger.Debug($"异常堆栈: {ex}");
    return 2;
}
