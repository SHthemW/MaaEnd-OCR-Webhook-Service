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
            Logger.Info($"二次 OCR 裁剪矩形: ({cropRect.X}, {cropRect.Y}) → ({cropRect.X + cropRect.Width}, {cropRect.Y + cropRect.Height}), 尺寸: {cropRect.Width}x{cropRect.Height}");

            if (args.SaveScreenshot)
            {
                using var cropCopy = screenshot.Clone(cropRect, screenshot.PixelFormat);
                var path = ScreenCapture.SaveScreenshot(cropCopy, args.WindowTitle + "_crop", attempt);
                Logger.Info($"裁剪截图已保存: {path}");
            }

            try
            {
                using var croppedBitmap = screenshot.Clone(cropRect, screenshot.PixelFormat);
                screenshot.Dispose();

                Logger.Info($"正在执行第二次 OCR (裁剪区域, 高精度, 语言: {args.Language})...");
                var sw = Stopwatch.StartNew();
                var detailedResult = await OcrEngine.RecognizeWithWordsAsync(
                    croppedBitmap,
                    args.Language,
                    upscale: 8,
                    preprocessMode: OcrEngine.OcrPreprocessMode.DetailPreserving);
                sw.Stop();
                Logger.Info($"第二次 OCR 完成, 耗时 {sw.ElapsedMilliseconds}ms, 识别到 {detailedResult.Text.Length} 个字符");

                if (!string.IsNullOrWhiteSpace(detailedResult.Text))
                {
                    Logger.Info($"第二次 OCR 结果:\n--- 二次 OCR 开始 ---\n{detailedResult.Text}\n--- 二次 OCR 结束 ---");
                }
                else
                {
                    Logger.Warn("第二次 OCR 结果为空");
                }

                foreach (var line in detailedResult.Lines.Where(line => !string.IsNullOrWhiteSpace(line.RawText)))
                {
                    Logger.Info($"结构化行: TimeText=\"{line.TimeText}\", Content=\"{line.Content}\"");
                }

                return 0;
            }
            catch (Exception ex)
            {
                Logger.Error($"第二次 OCR 失败: {ex.Message}");
                Logger.Debug($"OCR 异常堆栈: {ex}");
                return 2;
            }
        }

        Logger.Error("已达到最大重试次数, 文本未找到");
        return 3;
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
        Console.WriteLine($"  保存截图:     {(config.SaveScreenshot ? "是" : "否")}");
        Console.WriteLine("═══════════════════════════════");
        Console.WriteLine();
    }
}
