using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Text.Json;
using MaaEnd_Log_Retransmitter.Infrastructure;
using MaaEnd_Log_Retransmitter.Ocr;

namespace MaaEnd_Log_Retransmitter.App;

internal static class AppRunner
{
    private const string WebhookBodyTemplateFileName = "webhook-body-template.json";
    private const string WebhookBodyInstructionStart = "----- WEBHOOK BODY INSTRUCTIONS START -----";
    private const string WebhookBodyInstructionEnd = "----- WEBHOOK BODY INSTRUCTIONS END -----";
    private static readonly string[] ConfigFieldOrder =
    [
        nameof(Arguments.WindowTitle),
        nameof(Arguments.SearchText),
        nameof(Arguments.PartialMatch),
        nameof(Arguments.SaveScreenshot),
        nameof(Arguments.Retry),
        nameof(Arguments.RetryInterval),
        nameof(Arguments.RollingIntervalMs),
        nameof(Arguments.CaseSensitive),
        nameof(Arguments.Language),
        nameof(Arguments.WebhookUrl),
        nameof(Arguments.WebhookBody),
        nameof(Arguments.WebhookContentType),
        nameof(Arguments.WebhookTimeoutMs),
        nameof(Arguments.WebhookMode),
        nameof(Arguments.WebhookPushCacheSeconds)
    ];

    public static async Task<int> RunAsync(RuntimeOptions options)
    {
        PrintBanner();

        var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        var argsConfig = LoadOrCreateConfig(configPath, options);
        if (argsConfig == null)
        {
            Logger.Info("用户取消, 程序退出");
            return 0;
        }

        return await RunOcrDetectionAsync(argsConfig, options);
    }

    private static async Task<int> RunOcrDetectionAsync(Arguments args, RuntimeOptions options)
    {
        Logger.Info("══════════════════════════════════════");
        Logger.Info("  OCR 窗口文本检测程序 v1.0");
        Logger.Info("══════════════════════════════════════");
        Logger.Info($"目标窗口标题: \"{args.WindowTitle}\" (匹配模式: {(args.PartialMatch ? "模糊匹配" : "精确匹配")})");
        Logger.Info($"搜索文本: \"{args.SearchText}\" ({(args.CaseSensitive ? "区分大小写" : "不区分大小写")})");
        Logger.Info($"OCR 语言: {args.Language}");
        Logger.Info($"重试次数: {args.Retry}, 重试间隔: {args.RetryInterval}ms");
        Logger.Info($"滚动识别间隔: {args.RollingIntervalMs}ms");
        Logger.Info(IsWebhookEnabled(args, options)
            ? $"Webhook 推送: 已启用 ({args.WebhookUrl}, {args.WebhookContentType}, 模式 {args.WebhookModeDisplay}, 超时 {args.WebhookTimeoutMs}ms)"
            : "Webhook 推送: 已禁用");

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
                    preprocessMode: OcrEngine.OcrPreprocessMode.HighContrastBinary,
                    saveDebugImages: args.SaveScreenshot);
                sw.Stop();
                Logger.Info($"第一次 OCR 完成, 耗时 {sw.ElapsedMilliseconds}ms, 识别到 {firstResult.Text.Length} 个字符");

                if (!string.IsNullOrWhiteSpace(firstResult.Text))
                {
                    Logger.InfoLight($"第一次 OCR 原文:\n--- OCR 开始 ---\n{firstResult.Text}\n--- OCR 结束 ---");
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
            return await RunRollingRecognitionAsync(window, args, options, cropRect, attempt);
        }

        Logger.Error("已达到最大重试次数, 文本未找到");
        return 3;
    }

    private static async Task<int> RunRollingRecognitionAsync(WindowInfo window, Arguments args, RuntimeOptions options, Rectangle cropRect, int attempt)
    {
        var outputFilter = new RollingOcrOutputFilter();
        var bufferedLines = new List<OcrEngine.OcrLineInfo>();
        var webhookDispatcher = new WebhookDispatcher(args);
        var realtimeWebhookCache = new RealtimeWebhookPushCache(webhookDispatcher, args.WebhookPushCacheSeconds);
        var webhookEnabled = IsWebhookEnabled(args, options);
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
                        var optimizedResult = await OcrRecognitionOptimizer.RecognizeRollingAsync(
                            croppedBitmap,
                            args.Language,
                            args.SaveScreenshot);
                        var detailedResult = optimizedResult.Result;
                        Logger.Debug($"滚动 OCR 第 {round} 次完成, 候选 {optimizedResult.Spec.Name}, 分数 {optimizedResult.Score.Value}, 耗时 {optimizedResult.ElapsedMilliseconds}ms, 识别到 {detailedResult.Text.Length} 个字符");
                        Logger.InfoLight($"滚动 OCR 第 {round} 次原文:\n--- OCR 开始 ---\n{FormatOcrTextForLog(detailedResult.Text)}\n--- OCR 结束 ---");

                        var filterResult = outputFilter.Filter(detailedResult.Lines);
                        if (!filterResult.Accepted)
                        {
                            Logger.Warn($"滚动 OCR 第 {round} 次结果已过滤: {filterResult.RejectionReason}。当前共 {filterResult.CurrentLines.Count} 行。");
                            continue;
                        }

                        foreach (var line in filterResult.NewLines)
                        {
                            bufferedLines.Add(line.Source);
                            Logger.Info(FormatRollingOcrEvent(line));
                            if (webhookEnabled && args.ShouldPushRealtime)
                            {
                                var webhookMessage = WebhookMessage.FromOcrLine(line.Source);
                                if (!string.IsNullOrWhiteSpace(webhookMessage.Content))
                                {
                                    await realtimeWebhookCache.SendAsync(webhookMessage, CancellationToken.None);
                                }
                            }
                        }
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
            if (webhookEnabled && args.ShouldPushRealtime)
            {
                await realtimeWebhookCache.FlushAsync(CancellationToken.None);
            }

            PrintBufferedRollingOcrEvents(bufferedLines);
            if (webhookEnabled && args.ShouldPushSummary)
            {
                await DispatchBufferedRollingOcrEventsAsync(bufferedLines, webhookDispatcher);
            }
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

    private static string FormatOcrTextForLog(string? text) => string.IsNullOrWhiteSpace(text) ? "<EMPTY>" : text;

    private static string FormatRollingOcrEvent(RollingOcrOutputFilter.OutputLine line)
        => $"OCR事件[{line.Category}]: TimeText=\"{line.TimeText}\", Content=\"{line.Content}\"";

    private sealed class RealtimeWebhookPushCache
    {
        private const string CriticalLogMarker = "任务";
        private const string ImportantNotificationMarker = "重要通知";
        private readonly WebhookDispatcher _dispatcher;
        private readonly TimeSpan _cacheDuration;
        private readonly List<WebhookMessage> _pendingMessages = [];
        private readonly SemaphoreSlim _sync = new(1, 1);
        private CancellationTokenSource? _flushDelayCancellation;

        public RealtimeWebhookPushCache(WebhookDispatcher dispatcher, int cacheSeconds)
        {
            _dispatcher = dispatcher;
            _cacheDuration = TimeSpan.FromSeconds(Math.Max(0, cacheSeconds));
        }

        public async Task SendAsync(WebhookMessage message, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(message.Content))
            {
                return;
            }

            if (_cacheDuration <= TimeSpan.Zero)
            {
                await _dispatcher.SendAsync(message, cancellationToken);
                return;
            }

            if (IsCriticalLog(message.Content))
            {
                await FlushAsync(cancellationToken);
                await _dispatcher.SendAsync(message, cancellationToken);
                return;
            }

            await _sync.WaitAsync(cancellationToken);
            try
            {
                _pendingMessages.Add(message);
                if (_pendingMessages.Count == 1)
                {
                    StartFlushDelay();
                }
            }
            finally
            {
                _sync.Release();
            }
        }

        public async Task FlushAsync(CancellationToken cancellationToken)
        {
            List<WebhookMessage> messages;
            await _sync.WaitAsync(cancellationToken);
            try
            {
                if (_pendingMessages.Count == 0)
                {
                    return;
                }

                CancelFlushDelay();
                messages = _pendingMessages.ToList();
                _pendingMessages.Clear();
            }
            finally
            {
                _sync.Release();
            }

            var cachedTimeText = BuildCachedTimeText(messages);
            var cachedContent = string.Concat(messages.Select(FormatCachedContentLine));
            await _dispatcher.SendAsync(cachedContent, cachedTimeText, cancellationToken);
        }

        private static string BuildCachedTimeText(IReadOnlyList<WebhookMessage> messages)
            => $"{messages.First().TimeText}-{messages.Last().TimeText}";

        private static string FormatCachedContentLine(WebhookMessage message)
            => $"{Environment.NewLine}- {message.TimeText}: {message.Content.Replace("\r", " ").Replace("\n", " ").Trim()}";

        private void StartFlushDelay()
        {
            CancelFlushDelay();
            _flushDelayCancellation = new CancellationTokenSource();
            _ = FlushAfterDelayAsync(_flushDelayCancellation.Token);
        }

        private void CancelFlushDelay()
        {
            if (_flushDelayCancellation == null)
            {
                return;
            }

            _flushDelayCancellation.Cancel();
            _flushDelayCancellation.Dispose();
            _flushDelayCancellation = null;
        }

        private async Task FlushAfterDelayAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(_cacheDuration, cancellationToken);
                await FlushAsync(CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Logger.Warn($"Webhook 推送缓存自动刷新失败: {ex.Message}");
            }
        }

        private static bool IsCriticalLog(string content)
            => content.Contains(CriticalLogMarker, StringComparison.Ordinal)
                || content.Contains(ImportantNotificationMarker, StringComparison.Ordinal);
    }

    private static void PrintBufferedRollingOcrEvents(List<OcrEngine.OcrLineInfo> bufferedLines)
    {
        foreach (var line in bufferedLines)
        {
            Console.WriteLine(line.GetFinalContent());
        }
    }

    private static async Task DispatchBufferedRollingOcrEventsAsync(
        List<OcrEngine.OcrLineInfo> bufferedLines,
        WebhookDispatcher webhookDispatcher)
    {
        if (bufferedLines.Count == 0)
        {
            Logger.Info("无缓存 OCR 输出, 跳过 Webhook 推送。");
            return;
        }

        Logger.Info($"开始推送 {bufferedLines.Count} 条缓存 OCR 输出到 Webhook...");

        foreach (var line in bufferedLines)
        {
            var message = WebhookMessage.FromOcrLine(line);
            if (string.IsNullOrWhiteSpace(message.Content))
            {
                continue;
            }

            await webhookDispatcher.SendAsync(message, CancellationToken.None);
        }
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

    private static Arguments? LoadOrCreateConfig(string configPath, RuntimeOptions options)
    {
        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath, Encoding.UTF8);
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    Logger.Warn("配置文件格式无效, 进入交互式配置...");
                }
                else
                {
                    var config = ReadConfigFromJson(document.RootElement);
                    var invalidFields = GetConfigFieldsNeedingRepair(document.RootElement, config, options);
                    if (invalidFields.Count > 0)
                    {
                        Logger.Warn("配置文件存在缺失项、空值或非法值, 将依次启动对应配置向导...");
                        foreach (var fieldName in invalidFields)
                        {
                            Logger.Warn($"配置错误: {Arguments.GetValidationMessage(fieldName)}");
                        }

                        if (!RepairConfigFields(config, invalidFields))
                        {
                            return null;
                        }

                        if (!config.TryValidate(AllowEmptyWebhook(options), out var repairedValidationErrors))
                        {
                            foreach (var error in repairedValidationErrors)
                            {
                                Logger.Error($"配置错误: {error}");
                            }

                            return null;
                        }

                        SaveConfig(configPath, config);
                    }

                    if (!config.TryValidate(AllowEmptyWebhook(options), out var validationErrors))
                    {
                        foreach (var error in validationErrors)
                        {
                            Logger.Error($"配置错误: {error}");
                        }

                        return null;
                    }

                    Logger.Info($"已加载配置文件: {configPath}");
                    return config;
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

        SaveConfig(configPath, newConfig);
        return newConfig;
    }

    private static void SaveConfig(string configPath, Arguments config)
    {
        try
        {
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json, Encoding.UTF8);
            Logger.Info($"配置已保存到: {configPath}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"保存配置文件失败: {ex.Message}");
        }
    }

    private static Arguments ReadConfigFromJson(JsonElement root)
    {
        var config = new Arguments();

        ReadString(root, nameof(Arguments.WindowTitle), value => config.WindowTitle = value);
        ReadString(root, nameof(Arguments.SearchText), value => config.SearchText = value);
        ReadBoolean(root, nameof(Arguments.PartialMatch), value => config.PartialMatch = value);
        ReadBoolean(root, nameof(Arguments.SaveScreenshot), value => config.SaveScreenshot = value);
        ReadInteger(root, nameof(Arguments.Retry), value => config.Retry = value);
        ReadInteger(root, nameof(Arguments.RetryInterval), value => config.RetryInterval = value);
        ReadInteger(root, nameof(Arguments.RollingIntervalMs), value => config.RollingIntervalMs = value);
        ReadBoolean(root, nameof(Arguments.CaseSensitive), value => config.CaseSensitive = value);
        ReadString(root, nameof(Arguments.Language), value => config.Language = value);
        ReadString(root, nameof(Arguments.WebhookUrl), value => config.WebhookUrl = value);
        ReadString(root, nameof(Arguments.WebhookBody), value => config.WebhookBody = value);
        ReadString(root, nameof(Arguments.WebhookContentType), value => config.WebhookContentType = value);
        ReadInteger(root, nameof(Arguments.WebhookTimeoutMs), value => config.WebhookTimeoutMs = value);
        ReadString(root, nameof(Arguments.WebhookMode), value => config.WebhookMode = value);
        ReadInteger(root, nameof(Arguments.WebhookPushCacheSeconds), value => config.WebhookPushCacheSeconds = value);

        return config;
    }

    private static IReadOnlyList<string> GetConfigFieldsNeedingRepair(JsonElement root, Arguments config, RuntimeOptions options)
    {
        var invalidFields = new HashSet<string>(StringComparer.Ordinal);

        foreach (var fieldName in ConfigFieldOrder)
        {
            if (IsFieldMissingOrTypeInvalid(root, fieldName, config, options))
            {
                invalidFields.Add(fieldName);
            }
        }

        foreach (var fieldName in config.GetInvalidFields(AllowEmptyWebhook(options)))
        {
            invalidFields.Add(fieldName);
        }

        return ConfigFieldOrder.Where(invalidFields.Contains).ToList();
    }

    private static bool IsWebhookEnabled(Arguments args, RuntimeOptions options)
        => !(options.Debug && string.IsNullOrWhiteSpace(args.WebhookUrl)) && args.HasWebhookUrl;

    private static bool AllowEmptyWebhook(RuntimeOptions options)
        => options.Debug;

    private static bool IsFieldMissingOrTypeInvalid(JsonElement root, string fieldName, Arguments config, RuntimeOptions options)
    {
        if (!root.TryGetProperty(fieldName, out var property))
        {
            if (AllowEmptyWebhook(options) && string.IsNullOrWhiteSpace(config.WebhookUrl) && IsWebhookConfigField(fieldName))
            {
                return false;
            }

            return true;
        }

        return fieldName switch
        {
            nameof(Arguments.WindowTitle)
                or nameof(Arguments.SearchText)
                or nameof(Arguments.Language)
                or nameof(Arguments.WebhookUrl)
                or nameof(Arguments.WebhookBody)
                or nameof(Arguments.WebhookContentType)
                or nameof(Arguments.WebhookMode)
                    => property.ValueKind != JsonValueKind.String,

            nameof(Arguments.PartialMatch)
                or nameof(Arguments.SaveScreenshot)
                or nameof(Arguments.CaseSensitive)
                    => property.ValueKind is not (JsonValueKind.True or JsonValueKind.False),

            nameof(Arguments.Retry)
                or nameof(Arguments.RetryInterval)
                or nameof(Arguments.RollingIntervalMs)
                or nameof(Arguments.WebhookTimeoutMs)
                or nameof(Arguments.WebhookPushCacheSeconds)
                    => property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out _),

            _ => true
        };
    }

    private static bool IsWebhookConfigField(string fieldName)
        => fieldName is nameof(Arguments.WebhookUrl)
            or nameof(Arguments.WebhookBody)
            or nameof(Arguments.WebhookContentType)
            or nameof(Arguments.WebhookTimeoutMs)
            or nameof(Arguments.WebhookMode)
            or nameof(Arguments.WebhookPushCacheSeconds);

    private static void ReadString(JsonElement root, string fieldName, Action<string> assign)
    {
        if (root.TryGetProperty(fieldName, out var property) && property.ValueKind == JsonValueKind.String)
        {
            assign(property.GetString() ?? "");
        }
    }

    private static void ReadBoolean(JsonElement root, string fieldName, Action<bool> assign)
    {
        if (root.TryGetProperty(fieldName, out var property)
            && property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            assign(property.GetBoolean());
        }
    }

    private static void ReadInteger(JsonElement root, string fieldName, Action<int> assign)
    {
        if (root.TryGetProperty(fieldName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out var value))
        {
            assign(value);
        }
    }

    private static bool RepairConfigFields(Arguments config, IReadOnlyList<string> fieldNames)
    {
        foreach (var fieldName in fieldNames)
        {
            Logger.Info($"正在配置: {fieldName}");
            if (!RepairConfigField(config, fieldName))
            {
                Logger.Info("用户取消配置, 程序退出");
                return false;
            }
        }

        return true;
    }

    private static bool RepairConfigField(Arguments config, string fieldName)
    {
        switch (fieldName)
        {
            case nameof(Arguments.WindowTitle):
                if (!TryAskRequiredString("请输入窗口标题 (必填, 输入 q 退出): ", config.WindowTitle, out var windowTitle))
                {
                    return false;
                }

                config.WindowTitle = windowTitle;
                return true;

            case nameof(Arguments.SearchText):
                if (!TryAskRequiredString("请输入要查找的文本 (必填, 输入 q 退出): ", config.SearchText, out var searchText))
                {
                    return false;
                }

                config.SearchText = searchText;
                return true;

            case nameof(Arguments.PartialMatch):
                config.PartialMatch = AskYesNo("是否使用模糊匹配? (y/n, 默认: n 精确匹配): ", config.PartialMatch);
                return true;

            case nameof(Arguments.SaveScreenshot):
                config.SaveScreenshot = AskYesNo("是否保存截图用于调试? (y/n, 默认: n): ", config.SaveScreenshot);
                return true;

            case nameof(Arguments.Retry):
                config.Retry = AskInteger("请输入重试次数 (1-10, 默认: 1): ", GetIntegerDefault(config.Retry, 1, 1, 10), 1, 10);
                return true;

            case nameof(Arguments.RetryInterval):
                config.RetryInterval = AskInteger("请输入重试间隔/毫秒 (100-60000, 默认: 1000): ", GetIntegerDefault(config.RetryInterval, 1000, 100, 60000), 100, 60000);
                return true;

            case nameof(Arguments.RollingIntervalMs):
                config.RollingIntervalMs = AskInteger("请输入滚动识别间隔/毫秒 (500-60000, 默认: 3000): ", GetIntegerDefault(config.RollingIntervalMs, 3000, 500, 60000), 500, 60000);
                return true;

            case nameof(Arguments.CaseSensitive):
                config.CaseSensitive = AskYesNo("是否区分大小写? (y/n, 默认: n 不区分): ", config.CaseSensitive);
                return true;

            case nameof(Arguments.Language):
                var languageDefault = GetRequiredStringDefault(config.Language, "zh-Hans");
                if (!TryAskRequiredString($"请输入 OCR 语言标签 (默认: {languageDefault}): ", languageDefault, out var language))
                {
                    return false;
                }

                config.Language = language;
                return true;

            case nameof(Arguments.WebhookUrl):
                config.WebhookUrl = AskWebhookUrl(config.WebhookUrl);
                return true;

            case nameof(Arguments.WebhookBody):
                config.WebhookBody = AskWebhookBody(config.WebhookBody);
                return true;

            case nameof(Arguments.WebhookContentType):
                config.WebhookContentType = AskRequiredString($"请输入 Webhook Content-Type (默认: {GetRequiredStringDefault(config.WebhookContentType, "application/json")}): ", GetRequiredStringDefault(config.WebhookContentType, "application/json"));
                return true;

            case nameof(Arguments.WebhookTimeoutMs):
                config.WebhookTimeoutMs = AskInteger("请输入 Webhook 超时/毫秒 (1000-60000, 默认: 5000): ", GetIntegerDefault(config.WebhookTimeoutMs, 5000, 1000, 60000), 1000, 60000);
                return true;

            case nameof(Arguments.WebhookMode):
                config.WebhookMode = AskWebhookMode();
                return true;

            case nameof(Arguments.WebhookPushCacheSeconds):
                config.WebhookPushCacheSeconds = AskInteger("请输入 Webhook 推送缓存时间/秒 (0-86400, 0=不启用, 默认: 0): ", GetIntegerDefault(config.WebhookPushCacheSeconds, 0, 0, 86400), 0, 86400);
                return true;

            default:
                Logger.Warn($"未知配置项: {fieldName}");
                return true;
        }
    }

    private static bool TryAskRequiredString(string prompt, string defaultValue, out string value)
    {
        while (true)
        {
            Console.Write(prompt);
            var input = Console.ReadLine()?.Trim() ?? "";
            if (input.Equals("q", StringComparison.OrdinalIgnoreCase))
            {
                value = defaultValue;
                return false;
            }

            value = string.IsNullOrWhiteSpace(input) ? defaultValue : input;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            Logger.Warn("输入无效, 该配置不能为空");
        }
    }

    private static int GetIntegerDefault(int value, int fallback, int min, int max)
        => value >= min && value <= max ? value : fallback;

    private static string GetRequiredStringDefault(string value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;

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
        config.WebhookUrl = AskWebhookUrl(config.WebhookUrl);

        config.WebhookBody = AskWebhookBody(config.WebhookBody);

        config.WebhookContentType = AskRequiredString($"请输入 Webhook Content-Type (默认: {config.WebhookContentType}): ", config.WebhookContentType);

        config.WebhookTimeoutMs = AskInteger("请输入 Webhook 超时/毫秒 (1000-60000, 默认: 5000): ", 5000, 1000, 60000);
        config.WebhookMode = AskWebhookMode();
        config.WebhookPushCacheSeconds = AskInteger("请输入 Webhook 推送缓存时间/秒 (0-86400, 0=不启用, 默认: 0): ", 0, 0, 86400);

        if (!config.TryValidate(out var validationErrors))
        {
            foreach (var error in validationErrors)
            {
                Logger.Error($"配置错误: {error}");
            }

            return null;
        }

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

    private static string AskWebhookUrl(string defaultValue)
    {
        while (true)
        {
            Console.Write($"请输入 Webhook URL (必填, 默认: {defaultValue}): ");
            var input = Console.ReadLine()?.Trim() ?? "";
            var value = string.IsNullOrWhiteSpace(input) ? defaultValue : input;
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                return value;
            }

            Logger.Warn("输入无效, Webhook URL 必须是合法的 http/https URL");
        }
    }

    private static string AskWebhookBody(string defaultValue)
    {
        while (true)
        {
            var path = PrepareWebhookBodyTemplateFile(defaultValue);
            Logger.Info($"即将打开 Webhook Body 模板文件: {path}");
            Logger.Info("请在记事本中编辑模板，保存并关闭记事本后继续。模板必须包含 __CONTENT__，可选包含 __TIME__。");

            if (!OpenWebhookBodyTemplateEditor(path))
            {
                Console.WriteLine($"无法自动打开记事本，请手动编辑文件后按 Enter 继续: {path}");
                Console.ReadLine();
            }

            var value = ReadWebhookBodyTemplateFile(path);

            if (!string.IsNullOrWhiteSpace(value) && value.Contains("__CONTENT__", StringComparison.Ordinal))
            {
                return value;
            }

            Logger.Warn("Webhook Body 模板无效，不能为空且必须包含 __CONTENT__。将重新打开模板文件。");
        }
    }

    private static string PrepareWebhookBodyTemplateFile(string defaultValue)
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, WebhookBodyTemplateFileName);

        try
        {
            if (!File.Exists(path))
            {
                File.WriteAllText(path, BuildWebhookBodyTemplateFileContent(defaultValue), Encoding.UTF8);
                return path;
            }

            var existing = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(existing))
            {
                File.WriteAllText(path, BuildWebhookBodyTemplateFileContent(defaultValue), Encoding.UTF8);
            }
            else if (!existing.Contains(WebhookBodyInstructionStart, StringComparison.Ordinal))
            {
                File.WriteAllText(path, BuildWebhookBodyTemplateFileContent(existing), Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"准备 Webhook Body 模板文件失败: {ex.Message}");
        }

        return path;
    }

    private static string BuildWebhookBodyTemplateFileContent(string body)
    {
        var instructions = string.Join(Environment.NewLine, [
            WebhookBodyInstructionStart,
            "# 请在下方编辑 Webhook Body 模板。",
            "# 必须保留 __CONTENT__ 占位符，程序会用 OCR 内容替换它。",
            "# 可选使用 __TIME__ 占位符，程序会用 OCR 时间替换它。",
            "# 保存并关闭记事本后，CLI 会继续。",
            "# Edit the Webhook Body template below.",
            "# Keep the __CONTENT__ placeholder; the app will replace it with OCR content.",
            "# Optionally use the __TIME__ placeholder; the app will replace it with OCR time.",
            "# Save and close Notepad, then the CLI will continue.",
            WebhookBodyInstructionEnd,
            ""
        ]);

        return instructions + body;
    }

    private static bool OpenWebhookBodyTemplateEditor(string path)
    {
        try
        {
            using var process = new Process();
            process.StartInfo.FileName = "notepad.exe";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = false;
            process.StartInfo.ArgumentList.Add(path);

            if (!process.Start())
            {
                return false;
            }

            process.WaitForExit();
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"打开记事本失败: {ex.Message}");
            return false;
        }
    }

    private static string ReadWebhookBodyTemplateFile(string path)
    {
        try
        {
            var content = File.ReadAllText(path, Encoding.UTF8);
            return StripWebhookBodyTemplateInstructions(content);
        }
        catch (Exception ex)
        {
            Logger.Warn($"读取 Webhook Body 模板文件失败: {ex.Message}");
            return "";
        }
    }

    private static string StripWebhookBodyTemplateInstructions(string content)
    {
        var start = content.IndexOf(WebhookBodyInstructionStart, StringComparison.Ordinal);
        if (start < 0)
        {
            return content;
        }

        var end = content.IndexOf(WebhookBodyInstructionEnd, start, StringComparison.Ordinal);
        if (end < 0)
        {
            return content;
        }

        end += WebhookBodyInstructionEnd.Length;
        while (end < content.Length && (content[end] == '\r' || content[end] == '\n'))
        {
            end++;
        }

        return content.Remove(start, end - start);
    }

    private static string AskRequiredString(string prompt, string defaultValue)
    {
        while (true)
        {
            Console.Write(prompt);
            var input = Console.ReadLine()?.Trim() ?? "";
            var value = string.IsNullOrWhiteSpace(input) ? defaultValue : input;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            Logger.Warn("输入无效, 该配置不能为空");
        }
    }

    private static string AskWebhookMode()
    {
        while (true)
        {
            Console.Write("请选择 Webhook 推送方式 (1=仅实时, 2=仅总结, 3=全部, 默认: 1): ");
            var input = Console.ReadLine()?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(input)) return "Realtime";

            var mode = input switch
            {
                "1" => "Realtime",
                "2" => "Summary",
                "3" => "All",
                _ => input
            };

            if (Arguments.TryNormalizeWebhookMode(mode, out var normalized))
            {
                return normalized;
            }

            Logger.Warn("输入无效, 请输入 1、2、3，或 Realtime/Summary/All");
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
        Console.WriteLine("  Webhook:      启用");
        Console.WriteLine($"  Webhook URL:  {config.WebhookUrl}");
        Console.WriteLine($"  Content-Type: {config.WebhookContentType}");
        Console.WriteLine($"  推送方式:     {config.WebhookModeDisplay}");
        Console.WriteLine($"  推送超时:     {config.WebhookTimeoutMs}ms");
        Console.WriteLine($"  推送缓存时间: {config.WebhookPushCacheSeconds}s");
        Console.WriteLine($"  Body 模板:    {config.WebhookBody}");
        Console.WriteLine("═══════════════════════════════");
        Console.WriteLine();
    }
}
