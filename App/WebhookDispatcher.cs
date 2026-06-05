using System.Diagnostics;
using MaaEnd_Log_Retransmitter.Infrastructure;

namespace MaaEnd_Log_Retransmitter.App;

internal sealed class WebhookDispatcher
{
    private readonly string _url;
    private readonly string _bodyTemplate;
    private readonly string _contentType;
    private readonly int _timeoutMs;

    public WebhookDispatcher(Arguments args)
    {
        _url = args.WebhookUrl;
        _bodyTemplate = args.WebhookBody;
        _contentType = args.WebhookContentType;
        _timeoutMs = args.WebhookTimeoutMs;
    }

    public async Task SendFinalContentAsync(string finalContent, CancellationToken cancellationToken)
    {
        var body = _bodyTemplate.Replace("__CONTENT__", finalContent, StringComparison.Ordinal);
        using var process = new Process { StartInfo = CreateStartInfo(body) };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            Logger.Warn($"启动 curl 失败: {ex.Message}");
            return;
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var waitForExitTask = process.WaitForExitAsync(cancellationToken);
        var completedTask = await Task.WhenAny(waitForExitTask, Task.Delay(_timeoutMs, cancellationToken));

        if (completedTask != waitForExitTask)
        {
            TryKill(process);
            Logger.Warn($"Webhook 推送超时 ({_timeoutMs}ms): {TruncateForLog(finalContent)}");
            return;
        }

        await waitForExitTask;
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode == 0)
        {
            Logger.InfoLight($"Webhook 推送成功: {TruncateForLog(finalContent)}");
            return;
        }

        var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        Logger.Warn($"Webhook 推送失败 (exit={process.ExitCode}): {TruncateForLog(detail)}");
    }

    private ProcessStartInfo CreateStartInfo(string body)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "curl",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--silent");
        startInfo.ArgumentList.Add("--show-error");
        startInfo.ArgumentList.Add("--fail-with-body");
        startInfo.ArgumentList.Add("-X");
        startInfo.ArgumentList.Add("POST");
        if (!string.IsNullOrWhiteSpace(_contentType))
        {
            startInfo.ArgumentList.Add("-H");
            startInfo.ArgumentList.Add($"Content-Type: {_contentType}");
        }

        startInfo.ArgumentList.Add("--data-raw");
        startInfo.ArgumentList.Add(body);
        startInfo.ArgumentList.Add(_url);
        return startInfo;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static string TruncateForLog(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "<EMPTY>";
        var normalized = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return normalized.Length <= 200 ? normalized : normalized[..200] + "...";
    }
}
