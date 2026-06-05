using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Text.Json;
using MaaEnd_Log_Retransmitter.Infrastructure;
using MaaEnd_Log_Retransmitter.Ocr;

namespace MaaEnd_Log_Retransmitter.App;

internal static class AppRunner
{
    public static async Task<int> RunAsync()
    {
        PrintBanner();

        var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        var argsConfig = LoadOrCreateConfig(configPath);
        if (argsConfig == null)
        {
            Logger.Info("用户取消, 程序退出");
            return 0;
        }

        return await RunOcrDetectionAsync(argsConfig);
    }

    private static async Task<int> RunOcrDetectionAsync(Arguments args)
    {
        Logger.Info("══════════════════════════════════════");
        Logger.Info("  OCR 窗口文本检测程序 v1.0");
        Logger.Info("══════════════════════════════════════");
        Logger.Info($"目标窗口标题: \"{args.WindowTitle}\" (匹配模式: {(args.PartialMatch ? "模糊匹配" : "精确匹配")})");
        Logger.Info($"搜索文本: \"{args.SearchText}\" ({(args.CaseSensitive ? "区分大小写" : "不区分大小写")})");
        Logger.Info($"OCR 语言: {args.Language}");
        Logger.Info($"重试次数: {args.Retry}, 重试间隔: {args.RetryInterval}ms");
        Logger.Info($"滚动识别间隔: {args.RollingIntervalMs}ms");

        for (int attempt = 1; attempt <= args.Retry; attempt++)
        {
            if (args.Retry > 1)
            {
                Logger.Info($"--- 第 {attempt}/{args.Retry} 次尝试 ---");
            }

            var window = WindowFinder.FindWindow(args.WindowTitle, args.PartialMatch);
            if (window == null)
            {
                Logger.Error($"未找到匹配的窗口: \"{args.WindowTitle}\"");
                return 1;
            }

            Logger.Info($"找到窗口: \"{window.Title}\" (HWND=0x{window.Handle:X})");
            Logger.Info($"窗口区域: ({window.Rect.Left}, {window.Rect.Top}) - ({window.Rect.Right}, {window.Rect.Bottom}), 尺寸: {window.Width}x{window.Height}");

            Bitmap? screenshot = null;
            try
            {
                screenshot = ScreenCapture.CaptureWindow(window);
                Logger.Info($"截图成功: {screenshot.Width}x{screenshot.Height}");

                if (args.SaveScreenshot)
                {
                    var screenshotPath = ScreenCapture.SaveScreenshot(screenshot, args.WindowTitle, attempt);
                    Logger.Info($"截图已保存: {screenshotPath}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"截图失败: {ex.Message}");
                if (attempt < args.Retry)
                {
                    Logger.Info($"等待 {args.RetryInterval}ms 后重试...");
                    await Task.Delay(args.RetryInterval);
                    continue;
                }

                Logger.Error("已达到最大重试次数, 退出");
                return 2;
            }

            OcrEngine.OcrResultData? firstResult = null;
            try
            {
                Logger.Info($"正在执行第一次 OCR (全窗口, 语言: {args.Language})...");
                var sw = Stopwatch.StartNew();
                firstResult = await OcrEngine.RecognizeWithWordsAsync(
                    screenshot,
                    args.Language,
                    upscale: 4,
                    preprocessMode: OcrEngine.OcrPreprocessMode.HighContrastBinary);
                sw.Stop();
                Logger.Info($"第一次 OCR 完成, 耗时 {sw.ElapsedMilliseconds}ms, 识别到 {firstResult.Text.Length} 个字符");

                if (!string.IsNullOrWhiteSpace(firstResult.Text))
                {
                    Logger.Debug($"第一次 OCR 原文:\n--- OCR 开始 ---\n{firstResult.Text}\n--- OCR 结束 ---");
                }
                else
                {
                    Logger.Warn("OCR 结果为空（可能窗口内容为纯图像或无文字）");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"第一次 OCR 失败: {ex.Message}");
                Logger.Debug($"OCR 异常堆栈: {ex}");
                screenshot.Dispose();
                return 2;
            }

            if (firstResult == null || string.IsNullOrWhiteSpace(firstResult.Text))
            {
                screenshot.Dispose();
                if (attempt < args.Retry)
                {
                    Logger.Info($"等待 {args.RetryInterval}ms 后重试...");
                    await Task.Delay(args.RetryInterval);
                    continue;
                }

                Logger.Error("已达到最大重试次数, 文本未找到");
                return 3;
            }

            var matchRect = OcrEngine.FindTextRect(firstResult.Words, args.SearchText, args.CaseSensitive);
            if (matchRect == null)
            {
                Logger.Warn($"❌ 文本未找到: \"{args.SearchText}\" 不在 OCR 结果中");
                screenshot.Dispose();
                if (attempt < args.Retry)
                {
                    Logger.Info($"等待 {args.RetryInterval}ms 后重试...");
                    await Task.Delay(args.RetryInterval);
                    continue;
                }

                Logger.Error("已达到最大重试次数, 文本未找到");
                return 3;
            }

            Logger.Info($"✅ 文本搜索成功: 找到匹配 \"{args.SearchText}\"");
            Logger.Info($"匹配文本坐标 (原始): ({matchRect.Value.X}, {matchRect.Value.Y}), 尺寸: {matchRect.Value.Width}x{matchRect.Value.Height}");

            var cropRect = new Rectangle(
                matchRect.Value.Left,
                matchRect.Value.Bottom,
                Math.Max(1, screenshot.Width - matchRect.Value.Left),
                Math.Max(1, screenshot.Height - matchRect.Value.Bottom));
            Logger.Info($"滚动 OCR 裁剪矩形: ({cropRect.X}, {cropRect.Y}) → ({cropRect.X + cropRect.Width}, {cropRect.Y + cropRect.Height}), 尺寸: {cropRect.Width}x{cropRect.Height}");

            if (args.SaveScreenshot)
            {
                using var cropCopy = screenshot.Clone(cropRect, screenshot.PixelFormat);
                var path = ScreenCapture.SaveScreenshot(cropCopy, args.WindowTitle + "_crop", attempt);
                Logger.Info($"裁剪截图已保存: {path}");
            }

            screenshot.Dispose();
            return await RunRollingRecognitionAsync(window, args, cropRect, attempt);
        }

        Logger.Error("已达到最大重试次数, 文本未找到");
        return 3;
    }

    private static async Task<int> RunRollingRecognitionAsync(WindowInfo window, Arguments args, Rectangle cropRect, int attempt)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler? handler = (_, e) =>
        {
            e.Cancel = true;
            if (!cancellation.IsCancellationRequested)
            {
                Logger.Info("收到停止信号，正在停止滚动识别...");
                cancellation.Cancel();
            }
        };

        Console.CancelKeyPress += handler;
        Logger.Info("进入滚动识别模式，按 Ctrl+C 停止。");

        List<string>? previousKeys = null;
        int round = 1;

        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                Bitmap? screenshot = null;
                try
                {
                    screenshot = ScreenCapture.CaptureWindow(window);
                    var effectiveCropRect = ClampCropRect(cropRect, screenshot.Size);
                    if (effectiveCropRect == Rectangle.Empty)
                    {
                        Logger.Warn("当前窗口尺寸不足以覆盖目标区域，等待下一轮...");
                    }
                    else
                    {
                        using var croppedBitmap = screenshot.Clone(effectiveCropRect, screenshot.PixelFormat);
                        if (args.SaveScreenshot)
                        {
                            var path = ScreenCapture.SaveScreenshot(croppedBitmap, args.WindowTitle + "_rolling_crop", attempt * 100000 + round);
                            Logger.Debug($"滚动裁剪截图已保存: {path}");
                        }

                        Logger.Debug($"正在执行滚动 OCR (第 {round} 次, 语言: {args.Language})...");
                        var sw = Stopwatch.StartNew();
                        var detailedResult = await OcrEngine.RecognizeWithWordsAsync(
                            croppedBitmap,
                            args.Language,
                            upscale: 8,
                            preprocessMode: OcrEngine.OcrPreprocessMode.DetailPreserving);
                        sw.Stop();
                        Logger.Debug($"滚动 OCR 第 {round} 次完成, 耗时 {sw.ElapsedMilliseconds}ms, 识别到 {detailedResult.Text.Length} 个字符");

                        var currentLines = detailedResult.Lines
                            .Where(line => !string.IsNullOrWhiteSpace(line.RawText))
                            .ToList();
                        if (IsUnstableRollingFrame(currentLines, previousKeys))
                        {
                            Logger.Warn($"滚动 OCR 第 {round} 次结果疑似异常塌缩，已跳过本轮输出。当前共 {currentLines.Count} 行。");
                            continue;
                        }

                        var currentKeys = currentLines.Select(BuildLineKey).ToList();
                        var newLines = ExtractNewLines(currentLines, currentKeys, previousKeys);

                        foreach (var line in newLines)
                        {
                            Logger.Info($"结构化行: TimeText=\"{line.TimeText}\", Content=\"{line.Content}\"");
                        }

                        previousKeys = currentKeys;
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"滚动识别本轮失败: {ex.Message}");
                    Logger.Debug($"滚动识别异常堆栈: {ex}");
                }
                finally
                {
                    screenshot?.Dispose();
                }

                round++;
                try
                {
                    await Task.Delay(args.RollingIntervalMs, cancellation.Token);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    break;
                }
            }

            Logger.Info("滚动识别已停止。");
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private static Rectangle ClampCropRect(Rectangle cropRect, Size size)
    {
        if (size.Width <= 0 || size.Height <= 0) return Rectangle.Empty;
        if (cropRect.X >= size.Width || cropRect.Y >= size.Height) return Rectangle.Empty;

        int x = Math.Max(0, cropRect.X);
        int y = Math.Max(0, cropRect.Y);
        int width = Math.Min(cropRect.Width, size.Width - x);
        int height = Math.Min(cropRect.Height, size.Height - y);
        if (width <= 0 || height <= 0) return Rectangle.Empty;

        return new Rectangle(x, y, width, height);
    }

    private static string BuildLineKey(OcrEngine.OcrLineInfo line)
    {
        var timeText = line.TimeText.Trim();
        var content = line.Content.Trim();
        if (!string.IsNullOrWhiteSpace(timeText))
        {
            return timeText + "|" + content;
        }

        return string.IsNullOrWhiteSpace(content) ? line.RawText.Trim() : content;
    }

    private static List<OcrEngine.OcrLineInfo> ExtractNewLines(
        List<OcrEngine.OcrLineInfo> currentLines,
        List<string> currentKeys,
        List<string>? previousKeys)
    {
        if (previousKeys == null || previousKeys.Count == 0)
        {
            return currentLines;
        }

        int maxOverlap = Math.Min(previousKeys.Count, currentKeys.Count);
        for (int overlap = maxOverlap; overlap >= 1; overlap--)
        {
            bool matched = true;
            for (int i = 0; i < overlap; i++)
            {
                if (!string.Equals(previousKeys[previousKeys.Count - overlap + i], currentKeys[i], StringComparison.Ordinal))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return currentLines.Skip(overlap).ToList();
            }
        }

        var previousSet = new HashSet<string>(previousKeys, StringComparer.Ordinal);
        return currentLines
            .Where((line, index) => !previousSet.Contains(currentKeys[index]))
            .ToList();
    }

    private static bool IsUnstableRollingFrame(List<OcrEngine.OcrLineInfo> currentLines, List<string>? previousKeys)
    {
        if (previousKeys == null || previousKeys.Count < 2) return false;
        if (currentLines.Count != 1) return false;

        var onlyLine = currentLines[0];
        if (!string.IsNullOrWhiteSpace(onlyLine.TimeText)) return false;

        var content = onlyLine.Content.Trim();
        if (content.Length < 80) return false;

        int structuredMarkerCount = CountOccurrences(content, "任务开始")
            + CountOccurrences(content, "任务完成")
            + CountOccurrences(content, "任务失败")
            + CountOccurrences(content, "正在检查画面");

        return structuredMarkerCount >= 2;
    }

    private static int CountOccurrences(string text, string value)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(value)) return 0;

        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static void PrintBanner()
    {
        Console.WriteLine(@"
╔══════════════════════════════════════════╗
║       OCR 窗口文本检测程序 v1.0          ║
║  功能: 截图指定窗口 → OCR → 查找文本     ║
╚══════════════════════════════════════════╝
");
    }

    private static Arguments? LoadOrCreateConfig(string configPath)
    {
        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath, Encoding.UTF8);
                var config = JsonSerializer.Deserialize<Arguments>(json);
                if (config != null && config.Validate())
                {
                    Logger.Info($"已加载配置文件: {configPath}");
                    PrintSummary(config);
                    Console.Write("按 Enter 确认并开始执行, 输入 r 重新配置, 输入 q 退出: ");
                    var key = Console.ReadLine()?.Trim().ToLowerInvariant() ?? "";

                    if (string.IsNullOrWhiteSpace(key))
                    {
                        return config;
                    }

                    if (key == "q")
                    {
                        return null;
                    }

                    Logger.Info("进入重新配置流程...");
                }
                else
                {
                    Logger.Warn("配置文件格式无效, 进入交互式配置...");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"读取配置文件失败: {ex.Message}, 进入交互式配置...");
            }
        }
        else
        {
            Logger.Info("未检测到配置文件, 进入交互式配置...");
        }

        var newConfig = InteractivePrompt();
        if (newConfig == null)
        {
            return null;
        }

        try
        {
            var json = JsonSerializer.Serialize(newConfig, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json, Encoding.UTF8);
            Logger.Info($"配置已保存到: {configPath}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"保存配置文件失败: {ex.Message}");
        }

        return newConfig;
    }

    private static Arguments? InteractivePrompt()
    {
        var config = new Arguments();

        Console.Write("请输入窗口标题 (必填, 输入 q 退出): ");
        var input = Console.ReadLine()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(input)) { Logger.Error("窗口标题不能为空"); return null; }
        if (input.Equals("q", StringComparison.OrdinalIgnoreCase)) return null;
        config.WindowTitle = input;

        Console.Write("请输入要查找的文本 (必填, 输入 q 退出): ");
        input = Console.ReadLine()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(input)) { Logger.Error("搜索文本不能为空"); return null; }
        if (input.Equals("q", StringComparison.OrdinalIgnoreCase)) return null;
        config.SearchText = input;

        config.PartialMatch = AskYesNo("是否使用模糊匹配? (y/n, 默认: n 精确匹配): ", false);
        config.CaseSensitive = AskYesNo("是否区分大小写? (y/n, 默认: n 不区分): ", false);

        Console.Write($"请输入 OCR 语言标签 (默认: {config.Language}): ");
        input = Console.ReadLine()?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(input)) config.Language = input;

        config.Retry = AskInteger("请输入重试次数 (1-10, 默认: 1): ", 1, 1, 10);
        config.RetryInterval = AskInteger("请输入重试间隔/毫秒 (100-60000, 默认: 1000): ", 1000, 100, 60000);
        config.RollingIntervalMs = AskInteger("请输入滚动识别间隔/毫秒 (500-60000, 默认: 3000): ", 3000, 500, 60000);
        config.SaveScreenshot = AskYesNo("是否保存截图用于调试? (y/n, 默认: n): ", false);

        PrintSummary(config);
        if (!AskYesNo("确认以上配置? (y/n, 默认: y): ", true))
        {
            Logger.Info("已取消");
            return null;
        }

        return config;
    }

    private static bool AskYesNo(string prompt, bool defaultYes)
    {
        var suffix = defaultYes ? " [Y/n]" : " [y/N]";
        Console.Write(prompt + suffix);
        var input = Console.ReadLine()?.Trim().ToLowerInvariant() ?? "";
        if (string.IsNullOrWhiteSpace(input)) return defaultYes;
        return input == "y" || input == "yes";
    }

    private static int AskInteger(string prompt, int defaultValue, int min, int max)
    {
        while (true)
        {
            Console.Write(prompt);
            var input = Console.ReadLine()?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(input)) return defaultValue;
            if (int.TryParse(input, out var value) && value >= min && value <= max) return value;
            Logger.Warn($"输入无效, 请输入 {min} 到 {max} 之间的整数");
        }
    }

    private static void PrintSummary(Arguments config)
    {
        Console.WriteLine();
        Console.WriteLine("══════════ 配置摘要 ══════════");
        Console.WriteLine($"  窗口标题:     {config.WindowTitle}");
        Console.WriteLine($"  搜索文本:     {config.SearchText}");
        Console.WriteLine($"  匹配模式:     {(config.PartialMatch ? "模糊匹配" : "精确匹配")}");
        Console.WriteLine($"  区分大小写:   {(config.CaseSensitive ? "是" : "否")}");
        Console.WriteLine($"  OCR 语言:     {config.Language}");
        Console.WriteLine($"  重试次数:     {config.Retry}");
        Console.WriteLine($"  重试间隔:     {config.RetryInterval}ms");
        Console.WriteLine($"  滚动间隔:     {config.RollingIntervalMs}ms");
        Console.WriteLine($"  保存截图:     {(config.SaveScreenshot ? "是" : "否")}");
        Console.WriteLine("═══════════════════════════════");
        Console.WriteLine();
    }
}
