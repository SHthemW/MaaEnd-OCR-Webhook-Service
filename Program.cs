using System.Text;
using MaaEnd_Log_Retransmitter.App;
using MaaEnd_Log_Retransmitter.Infrastructure;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;
DpiAwareness.Enable();

try
{
    return await AppRunner.RunAsync();
}
catch (Exception ex)
{
    Logger.Error($"程序异常退出: {ex.Message}");
    Logger.Debug($"异常堆栈: {ex}");
    return 2;
}