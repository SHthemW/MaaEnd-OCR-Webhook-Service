using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

// ═══════════════════════════════════════════════════════════
// OCR 窗口文本检测程序
// 功能：查找指定标题的窗口 → 截图 → OCR → 检查文本是否存在
// ═══════════════════════════════════════════════════════════

#region 配置加载 / 交互式输入

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;
DpiAwareness.Enable();

PrintBanner();

var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
var argsConfig = LoadOrCreateConfig(configPath);
if (argsConfig == null)
{
    Logger.Info("用户取消, 程序退出");
    return 0;
}

#endregion

#region 主流程

try
{
    return await RunOcrDetectionAsync(argsConfig);
}
catch (Exception ex)
{
    Logger.Error($"程序异常退出: {ex.Message}");
    Logger.Debug($"异常堆栈: {ex}");
    return 2;
}

#endregion

// ═══════════════════════════════════════════════════════════
// 主逻辑
// ═══════════════════════════════════════════════════════════

static async Task<int> RunOcrDetectionAsync(Arguments args)
{
    Logger.Info("══════════════════════════════════════");
    Logger.Info("  OCR 窗口文本检测程序 v1.0");
    Logger.Info("══════════════════════════════════════");
    Logger.Info($"目标窗口标题: \"{args.WindowTitle}\" (匹配模式: {(args.PartialMatch ? "模糊匹配" : "精确匹配")})");
    Logger.Info($"搜索文本: \"{args.SearchText}\" ({(args.CaseSensitive ? "区分大小写" : "不区分大小写")})");
    Logger.Info($"OCR 语言: {args.Language}");
    Logger.Info($"重试次数: {args.Retry}, 重试间隔: {args.RetryInterval}ms");

    // 重试循环
    for (int attempt = 1; attempt <= args.Retry; attempt++)
    {
        if (args.Retry > 1)
            Logger.Info($"--- 第 {attempt}/{args.Retry} 次尝试 ---");

        // 1. 查找窗口
        var window = WindowFinder.FindWindow(args.WindowTitle, args.PartialMatch);
        if (window == null)
        {
            Logger.Error($"未找到匹配的窗口: \"{args.WindowTitle}\"");
            return 1;
        }
        Logger.Info($"找到窗口: \"{window.Title}\" (HWND=0x{window.Handle:X})");
        Logger.Info($"窗口区域: ({window.Rect.Left}, {window.Rect.Top}) - ({window.Rect.Right}, {window.Rect.Bottom}), 尺寸: {window.Width}x{window.Height}");

        // 2. 截图
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

        // 3. 第一次 OCR: 全窗口, 标准精度
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
                Logger.Debug($"第一次 OCR 原文:\n--- OCR 开始 ---\n{firstResult.Text}\n--- OCR 结束 ---");
            else
                Logger.Warn("OCR 结果为空（可能窗口内容为纯图像或无文字）");
        }
        catch (Exception ex)
        {
            Logger.Error($"第一次 OCR 失败: {ex.Message}");
            Logger.Debug($"OCR 异常堆栈: {ex}");
            screenshot.Dispose();
            return 2;
        }

        // 4. 搜索文本并定位坐标
        if (firstResult == null || string.IsNullOrWhiteSpace(firstResult.Text))
        {
            screenshot.Dispose();
            if (attempt < args.Retry) { Logger.Info($"等待 {args.RetryInterval}ms 后重试..."); await Task.Delay(args.RetryInterval); continue; }
            Logger.Error("已达到最大重试次数, 文本未找到");
            return 3;
        }

        var matchRect = OcrEngine.FindTextRect(firstResult.Words, args.SearchText, args.CaseSensitive);
        if (matchRect == null)
        {
            Logger.Warn($"❌ 文本未找到: \"{args.SearchText}\" 不在 OCR 结果中");
            screenshot.Dispose();
            if (attempt < args.Retry) { Logger.Info($"等待 {args.RetryInterval}ms 后重试..."); await Task.Delay(args.RetryInterval); continue; }
            Logger.Error("已达到最大重试次数, 文本未找到");
            return 3;
        }

        Logger.Info($"✅ 文本搜索成功: 找到匹配 \"{args.SearchText}\"");
        Logger.Info($"匹配文本坐标 (原始): ({matchRect.Value.X}, {matchRect.Value.Y}), 尺寸: {matchRect.Value.Width}x{matchRect.Value.Height}");

        // 5. 裁剪区域: 从命中文本右下角延伸到窗口右下角
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

        // 6. 第二次 OCR: 裁剪区域, 更高精度
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
                Logger.Info($"第二次 OCR 结果:\n--- 二次 OCR 开始 ---\n{detailedResult.Text}\n--- 二次 OCR 结束 ---");
            else
                Logger.Warn("第二次 OCR 结果为空");

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

// ═══════════════════════════════════════════════════════════
// 文本搜索
// ═══════════════════════════════════════════════════════════


// ═══════════════════════════════════════════════════════════
// 配置加载 / 交互式输入
// ═══════════════════════════════════════════════════════════

static void PrintBanner()
{
    Console.WriteLine(@"
╔══════════════════════════════════════════╗
║       OCR 窗口文本检测程序 v1.0          ║
║  功能: 截图指定窗口 → OCR → 查找文本     ║
╚══════════════════════════════════════════╝
");
}

/// <summary>
/// 加载配置文件或引导用户交互式输入
/// </summary>
static Arguments? LoadOrCreateConfig(string configPath)
{
    // 尝试加载已有配置
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
                    return config;  // Enter → 直接执行

                if (key == "q")
                    return null;

                // r 或其他 → 进入交互式配置流程（会覆盖原文件）
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

    // 交互式配置
    var newConfig = InteractivePrompt();
    if (newConfig == null) return null;

    // 保存配置
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

/// <summary>交互式输入各项配置</summary>
static Arguments? InteractivePrompt()
{
    var config = new Arguments();

    // ── 窗口标题 ──
    Console.Write("请输入窗口标题 (必填, 输入 q 退出): ");
    var input = Console.ReadLine()?.Trim() ?? "";
    if (string.IsNullOrWhiteSpace(input)) { Logger.Error("窗口标题不能为空"); return null; }
    if (input.Equals("q", StringComparison.OrdinalIgnoreCase)) return null;
    config.WindowTitle = input;

    // ── 搜索文本 ──
    Console.Write("请输入要查找的文本 (必填, 输入 q 退出): ");
    input = Console.ReadLine()?.Trim() ?? "";
    if (string.IsNullOrWhiteSpace(input)) { Logger.Error("搜索文本不能为空"); return null; }
    if (input.Equals("q", StringComparison.OrdinalIgnoreCase)) return null;
    config.SearchText = input;

    // ── 匹配模式 ──
    config.PartialMatch = AskYesNo("是否使用模糊匹配? (y/n, 默认: n 精确匹配): ", false);

    // ── 大小写 ──
    config.CaseSensitive = AskYesNo("是否区分大小写? (y/n, 默认: n 不区分): ", false);

    // ── OCR 语言 ──
    Console.Write($"请输入 OCR 语言标签 (默认: {config.Language}): ");
    input = Console.ReadLine()?.Trim() ?? "";
    if (!string.IsNullOrWhiteSpace(input)) config.Language = input;

    // ── 重试次数 ──
    config.Retry = AskInteger("请输入重试次数 (1-10, 默认: 1): ", 1, 1, 10);

    // ── 重试间隔 ──
    config.RetryInterval = AskInteger("请输入重试间隔/毫秒 (100-60000, 默认: 1000): ", 1000, 100, 60000);

    // ── 保存截图 ──
    config.SaveScreenshot = AskYesNo("是否保存截图用于调试? (y/n, 默认: n): ", false);

    // ── 确认 ──
    PrintSummary(config);
    if (!AskYesNo("确认以上配置? (y/n, 默认: y): ", true))
    {
        Logger.Info("已取消");
        return null;
    }

    return config;
}

/// <summary>询问是/否问题</summary>
static bool AskYesNo(string prompt, bool defaultYes)
{
    var suffix = defaultYes ? " [Y/n]" : " [y/N]";
    Console.Write(prompt + suffix);
    var input = Console.ReadLine()?.Trim().ToLowerInvariant() ?? "";
    if (string.IsNullOrWhiteSpace(input)) return defaultYes;
    return input == "y" || input == "yes";
}

/// <summary>询问整数, 带范围校验</summary>
static int AskInteger(string prompt, int defaultValue, int min, int max)
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

/// <summary>打印配置摘要</summary>
static void PrintSummary(Arguments config)
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

// ═══════════════════════════════════════════════════════════
// 参数配置类
// ═══════════════════════════════════════════════════════════

class Arguments
{
    public string WindowTitle { get; set; } = "";
    public string SearchText { get; set; } = "";
    public bool PartialMatch { get; set; } = false;
    public bool SaveScreenshot { get; set; } = false;
    public int Retry { get; set; } = 1;
    public int RetryInterval { get; set; } = 1000;
    public bool CaseSensitive { get; set; } = false;
    public string Language { get; set; } = "zh-Hans";

    /// <summary>校验配置必填字段和范围约束</summary>
    public bool Validate()
    {
        if (string.IsNullOrWhiteSpace(WindowTitle)) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return false;
        if (string.IsNullOrWhiteSpace(Language)) Language = "zh-Hans";
        Retry = Math.Clamp(Retry, 1, 10);
        RetryInterval = Math.Clamp(RetryInterval, 100, 60000);
        return true;
    }
}

// ═══════════════════════════════════════════════════════════
// 日志工具类
// ═══════════════════════════════════════════════════════════

static class Logger
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

// ═══════════════════════════════════════════════════════════
// 窗口查找工具 (P/Invoke user32.dll)
// ═══════════════════════════════════════════════════════════

static class WindowFinder
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    private const uint GW_OWNER = 4;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>
    /// 根据窗口标题查找窗口
    /// </summary>
    public static WindowInfo? FindWindow(string title, bool partialMatch)
    {
        var results = new List<WindowInfo>();

        EnumWindows((hWnd, lParam) =>
        {
            // 跳过不可见窗口
            if (!IsWindowVisible(hWnd))
                return true;

            // 跳过无标题窗口(某些系统窗口)
            int length = GetWindowTextLength(hWnd);
            if (length == 0)
                return true;

            var sb = new StringBuilder(length + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            var windowTitle = sb.ToString();

            // 跳过所有者窗口(避免重复匹配)
            if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero)
                return true;

            bool matched = partialMatch
                ? windowTitle.Contains(title, StringComparison.OrdinalIgnoreCase)
                : windowTitle.Equals(title, StringComparison.OrdinalIgnoreCase);

            if (matched)
            {
                if (GetWindowRect(hWnd, out var rect))
                {
                    results.Add(new WindowInfo
                    {
                        Handle = hWnd,
                        Title = windowTitle,
                        Rect = rect
                    });
                }
            }

            return true;
        }, IntPtr.Zero);

        // 优先选择非最小化的可视窗口
        var best = results
            .OrderBy(w => IsIconic(w.Handle) ? 1 : 0)  // 非最小化优先
            .ThenByDescending(w => w.Width * w.Height)   // 面积大的优先(更可能是主窗口)
            .FirstOrDefault();

        return best;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

class WindowInfo
{
    public IntPtr Handle { get; set; }
    public string Title { get; set; } = "";
    public WindowFinder.RECT Rect { get; set; }
    public int Width => Rect.Right - Rect.Left;
    public int Height => Rect.Bottom - Rect.Top;
}

// ═══════════════════════════════════════════════════════════
// DPI 感知
// ═══════════════════════════════════════════════════════════

static class DpiAwareness
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

// ═══════════════════════════════════════════════════════════
// 屏幕截图工具
// ═══════════════════════════════════════════════════════════

static class ScreenCapture
{
    // ── PrintWindow ──
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint nFlags);

    // ── 窗口创建（用于承载 DWM 缩略图） ──
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern uint GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern uint DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    // ── DWM 缩略图 ──
    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmRegisterThumbnail(IntPtr hwndDestination, IntPtr hwndSource, out IntPtr phThumbnailId);

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmUnregisterThumbnail(IntPtr hThumbnailId);

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmUpdateThumbnailProperties(IntPtr hThumbnailId, ref DWM_THUMBNAIL_PROPERTIES ptnProperties);

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmQueryThumbnailSourceSize(IntPtr hThumbnailId, out SIZE pSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    // ── 分层窗口（实现全透明宿主） ──
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const uint PW_CLIENTONLY = 0x00000001;
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const uint LWA_ALPHA = 0x00000002;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint WM_DESTROY = 0x0002;

    private const int DWM_TNP_VISIBLE = 0x8;
    private const int DWM_TNP_OPACITY = 0x4;
    private const int DWM_TNP_RECTDESTINATION = 0x1;
    private const int DWM_TNP_RECTSOURCE = 0x2;

    private const uint PM_REMOVE = 0x0001;
    private static bool _windowClassRegistered;

    [StructLayout(LayoutKind.Sequential)]
    struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        [MarshalAs(UnmanagedType.FunctionPtr)] public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DWM_THUMBNAIL_PROPERTIES
    {
        public int dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        public bool fVisible;
        public bool fSourceClientAreaOnly;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct SIZE { public int cx, cy; }

    delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 截取指定窗口的内容
    /// 方案 A: PrintWindow (GDI 窗口)
    /// 方案 B: DWM 缩略图 (D3D 窗口, 后台捕获, 不改任何窗口状态)
    /// </summary>
    public static Bitmap CaptureWindow(WindowInfo window)
    {
        var rect = window.Rect;
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;

        if (width <= 0 || height <= 0)
            throw new InvalidOperationException($"窗口尺寸无效: {width}x{height}（窗口可能已最小化或不可见）");

        // 方案 A: PrintWindow —— 仅对 GDI 窗口有效
        var bitmap = CaptureViaPrintWindow(window.Handle, width, height);
        if (bitmap != null)
        {
            Logger.Debug("截图方式: PrintWindow (直接窗口渲染)");
            return bitmap;
        }

        // 方案 B: DWM 缩略图 —— 对 D3D 窗口后台捕获, 不改窗口显隐和层级
        Logger.Debug("PrintWindow 失败, 尝试 DWM 缩略图 (后台捕获)...");
        bitmap = CaptureViaDwmThumbnail(window.Handle, width, height);
        if (bitmap != null)
        {
            Logger.Debug("截图方式: DWM 缩略图 (后台合成)");
            return bitmap;
        }

        throw new InvalidOperationException("所有截图方式均失败（PrintWindow + DWM 缩略图）");
    }

    /// <summary>
    /// 方案 A: PrintWindow 直接渲染
    /// </summary>
    private static Bitmap? CaptureViaPrintWindow(IntPtr hWnd, int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            var hdc = g.GetHdc();
            try
            {
                if (PrintWindow(hWnd, hdc, PW_RENDERFULLCONTENT) ||
                    PrintWindow(hWnd, hdc, PW_CLIENTONLY))
                {
                    return bitmap;
                }
                return null;
            }
            finally { g.ReleaseHdc(hdc); }
        }
    }

    /// <summary>
    /// 方案 B: 创建透明分层窗口承载 DWM 缩略图，后台合成 → 抓取
    /// 宿主窗口在屏幕内全透明不可见，不接收任何输入，不改目标窗口显隐/Z序
    /// </summary>
    private static Bitmap? CaptureViaDwmThumbnail(IntPtr targetHwnd, int width, int height)
    {
        IntPtr hThumbnail = IntPtr.Zero;
        IntPtr hHostWnd = IntPtr.Zero;

        try
        {
            RegisterHostWindowClass();

            // 1. 创建全透明分层窗口（在屏幕内，DWM 才能合成，但对用户完全不可见）
            hHostWnd = CreateWindowEx(
                WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                "OcrHostWindow", "",
                WS_POPUP,
                0, 0, width, height,
                IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null!), IntPtr.Zero);

            if (hHostWnd == IntPtr.Zero) return null;

            // 设置全透明（alpha=1 即几乎完全不可见，但要 > 0 才能让 DWM 合成缩略图）
            SetLayeredWindowAttributes(hHostWnd, 0, 1, LWA_ALPHA);

            // 显示窗口（DWM 只对已显示的窗口合成缩略图）
            ShowWindow(hHostWnd, SW_SHOWNOACTIVATE);

            // 2. 注册 DWM 缩略图: 目标窗口 → 宿主窗口
            int hr = DwmRegisterThumbnail(hHostWnd, targetHwnd, out hThumbnail);
            if (hr != 0 || hThumbnail == IntPtr.Zero) return null;

            // 3. 查询源尺寸并设置缩略图属性
            DwmQueryThumbnailSourceSize(hThumbnail, out var srcSize);
            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = DWM_TNP_VISIBLE | DWM_TNP_RECTSOURCE | DWM_TNP_RECTDESTINATION | DWM_TNP_OPACITY,
                fVisible = true,
                fSourceClientAreaOnly = false,
                opacity = 255,
                rcSource = new RECT { Left = 0, Top = 0, Right = srcSize.cx, Bottom = srcSize.cy },
                rcDestination = new RECT { Left = 0, Top = 0, Right = srcSize.cx, Bottom = srcSize.cy }
            };
            DwmUpdateThumbnailProperties(hThumbnail, ref props);

            // 4. 等待 DWM 合成完成
            DwmFlush();
            Thread.Sleep(50); // 额外缓冲确保合成完毕

            // 5. PrintWindow 抓取宿主窗口的 DWM 合成内容
            var bitmap = new Bitmap(srcSize.cx, srcSize.cy, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                var hdc = g.GetHdc();
                try
                {
                    if (!PrintWindow(hHostWnd, hdc, PW_RENDERFULLCONTENT))
                        return null;
                }
                finally { g.ReleaseHdc(hdc); }
            }

            return bitmap;
        }
        catch (Exception ex)
        {
            Logger.Debug($"DWM 缩略图捕获异常: {ex.Message}");
            return null;
        }
        finally
        {
            if (hThumbnail != IntPtr.Zero) DwmUnregisterThumbnail(hThumbnail);
            if (hHostWnd != IntPtr.Zero) DestroyWindow(hHostWnd);
        }
    }

    private static void RegisterHostWindowClass()
    {
        if (_windowClassRegistered) return;

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = HostWndProc,
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = GetModuleHandle(null!),
            hIcon = IntPtr.Zero,
            hCursor = IntPtr.Zero,
            hbrBackground = IntPtr.Zero,
            lpszMenuName = "",
            lpszClassName = "OcrHostWindow",
            hIconSm = IntPtr.Zero
        };

        if (RegisterClassEx(ref wc) == 0)
            throw new InvalidOperationException("注册宿主窗口类失败");

        _windowClassRegistered = true;
    }

    private static IntPtr HostWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_DESTROY) return IntPtr.Zero;
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// 保存截图到文件（用于调试）
    /// </summary>
    public static string SaveScreenshot(Bitmap bitmap, string windowTitle, int attempt)
    {
        var safeName = string.Join("_", windowTitle.Split(Path.GetInvalidFileNameChars()));
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        var filename = $"screenshot_{safeName}_{timestamp}_attempt{attempt}.png";
        var path = Path.Combine(dir, filename);
        bitmap.Save(path, ImageFormat.Png);
        return path;
    }
}

// ═══════════════════════════════════════════════════════════
// OCR 识别引擎 (Windows.Media.OCR)
// ═══════════════════════════════════════════════════════════

static class OcrEngine
{
    // ── 数据类型 ──

    /// <summary>OCR 单词信息（坐标已映射回原始 bitmap 空间）</summary>
    public class OcrWordInfo
    {
        public string Text { get; set; } = "";
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    /// <summary>OCR 识别结果：文本 + 单词坐标</summary>
    public class OcrResultData
    {
        public string Text { get; set; } = "";
        public List<OcrWordInfo> Words { get; set; } = new();
    }

    // ── 公开方法 ──

    public enum OcrPreprocessMode
    {
        HighContrastBinary,
        DetailPreserving
    }

    /// <summary>
    /// 执行 OCR 识别，返回文本 + 单词坐标（坐标已映射到原始图像空间）
    /// </summary>
    public static async Task<OcrResultData> RecognizeWithWordsAsync(
        Bitmap bitmap,
        string languageTag,
        int upscale,
        OcrPreprocessMode preprocessMode = OcrPreprocessMode.HighContrastBinary)
    {
        // Step 1: 图像预处理 —— 放大提升识别率
        using var processed = PreprocessForOcr(bitmap, upscale, preprocessMode);
        Logger.Debug($"图像预处理 ({preprocessMode}, {upscale}x): {bitmap.Width}x{bitmap.Height} → {processed.Width}x{processed.Height}");

        // Step 2: 转换为 SoftwareBitmap
        using var softwareBitmap = await ConvertToSoftwareBitmapAsync(processed);

        // Step 3: 获取 OCR 引擎
        var engine = GetEngine(languageTag);

        // Step 4: 执行 OCR
        var result = await engine.RecognizeAsync(softwareBitmap);

        // Step 5: 提取文本 + 单词坐标（映射回原始空间）
        var sb = new StringBuilder();
        var words = new List<OcrWordInfo>();

        foreach (var line in result.Lines)
        {
            sb.AppendLine(line.Text);
            foreach (var word in line.Words)
            {
                var rect = word.BoundingRect;
                words.Add(new OcrWordInfo
                {
                    Text = word.Text,
                    X = (int)rect.X / upscale,
                    Y = (int)rect.Y / upscale,
                    Width = (int)rect.Width / upscale,
                    Height = (int)rect.Height / upscale
                });
            }
        }

        return new OcrResultData { Text = sb.ToString().TrimEnd(), Words = words };
    }

    /// <summary>
    /// 在 OCR 单词列表中查找目标文本，返回匹配区域（原始图像坐标）
    /// </summary>
    public static Rectangle? FindTextRect(List<OcrWordInfo> words, string searchText, bool caseSensitive)
    {
        if (words.Count == 0 || string.IsNullOrWhiteSpace(searchText))
            return null;

        var comparison = caseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        var normalizedSearch = NormalizeCjkSpaces(searchText);

        // 将所有单词的归一化文本拼接，记录每个字符对应的原始单词索引
        var flatWords = new List<(int wordIdx, string text)>();
        for (int i = 0; i < words.Count; i++)
        {
            var norm = NormalizeCjkSpaces(words[i].Text);
            if (!string.IsNullOrEmpty(norm))
                flatWords.Add((i, norm));
        }

        // 构建完整归一化文本用于搜索
        var fullNorm = string.Concat(flatWords.Select(w => w.text));
        var matchIdx = fullNorm.IndexOf(normalizedSearch, comparison);
        if (matchIdx < 0) return null;

        // 定位匹配涉及哪些单词
        int matchEnd = matchIdx + normalizedSearch.Length;
        int runningPos = 0;
        int? firstWordIdx = null;
        int? lastWordIdx = null;

        foreach (var (wordIdx, wText) in flatWords)
        {
            int wordStart = runningPos;
            int wordEnd = runningPos + wText.Length;
            runningPos = wordEnd;

            if (wordStart < matchEnd && wordEnd > matchIdx)
            {
                firstWordIdx ??= wordIdx;
                lastWordIdx = wordIdx;
            }
        }

        if (firstWordIdx == null || lastWordIdx == null) return null;

        // 计算联合矩形: 左上角 = 第一个匹配单词的左上角
        var first = words[firstWordIdx.Value];
        var last = words[lastWordIdx.Value];
        int x = first.X;
        int y = first.Y;
        int right = Math.Max(first.X + first.Width, last.X + last.Width);
        int bottom = Math.Max(first.Y + first.Height, last.Y + last.Height);

        return new Rectangle(x, y, right - x, bottom - y);
    }

    // ── 内部方法 ──

    private static Windows.Media.Ocr.OcrEngine GetEngine(string languageTag)
    {
        var requestedLanguage = new Language(languageTag);
        if (Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(requestedLanguage) is { } engine)
        {
            Logger.Debug($"使用 OCR 语言: {languageTag}");
            return engine;
        }

        var available = Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages;
        if (available.Count == 0)
            throw new NotSupportedException("系统没有可用的 OCR 语言包");

        var fallback = available[0];
        var fallbackEngine = Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(fallback);
        if (fallbackEngine == null)
            throw new NotSupportedException($"无法创建 OCR 引擎（语言: {fallback.DisplayName}）");

        Logger.Warn($"不支持语言 \"{languageTag}\", 已回退到: {fallback.DisplayName} ({fallback.LanguageTag})");
        return fallbackEngine;
    }

    /// <summary>
    /// 移除 CJK 字符之间的空格。
    /// "运 行 日 志" → "运行日志", "Hello World" → "Hello World"
    /// </summary>
    public static string NormalizeCjkSpaces(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == ' ' || c == ' ')
            {
                bool prevCjk = i > 0 && IsCjkOrPunct(text[i - 1]);
                bool nextCjk = i < text.Length - 1 && IsCjkOrPunct(text[i + 1]);
                if (prevCjk || nextCjk) continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>判断字符是否为 CJK 字符或中文标点</summary>
    public static bool IsCjkOrPunct(char c)
    {
        return (c >= 0x4E00 && c <= 0x9FFF)
            || (c >= 0x3400 && c <= 0x4DBF)
            || (c >= 0x20000 && c <= 0x2A6DF)
            || (c >= 0x3000 && c <= 0x303F)
            || (c >= 0xFF00 && c <= 0xFFEF)
            || (c >= 0x2F00 && c <= 0x2FDF)
            || (c >= 0x31C0 && c <= 0x31EF)
            || (c >= 0xFE30 && c <= 0xFE4F)
            || (c >= 0x3000 && c <= 0x301F)
            || c == '．' || c == '：' || c == '，' || c == '。'
            || c == '／' || c == '（' || c == '）' || c == '［'
            || c == '］' || c == '｛' || c == '｝';
    }

    // ── 调试用计数器 ──
    private static int _debugSeq;

    /// <summary>
    /// OCR 预处理：根据场景选择高对比二值化或细节保留模式
    /// 每步输出调试截图到 screenshots/
    /// </summary>
    private static Bitmap PreprocessForOcr(Bitmap source, int upscale, OcrPreprocessMode preprocessMode)
    {
        int newW = source.Width * upscale;
        int newH = source.Height * upscale;
        var debugDir = EnsureDebugDir();
        var modePrefix = preprocessMode == OcrPreprocessMode.HighContrastBinary ? "hc" : "detail";

        // 保存原始截图
        SaveDebugImage(source, debugDir, $"{modePrefix}_01_original");

        // 1. 高质量放大
        var scaled = new Bitmap(newW, newH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            g.DrawImage(source, new Rectangle(0, 0, newW, newH));
        }
        SaveDebugImage(scaled, debugDir, $"{modePrefix}_02_scaled");

        if (preprocessMode == OcrPreprocessMode.DetailPreserving)
        {
            var enhanced = ApplyColorContrast(scaled, 1.35f);
            scaled.Dispose();
            SaveDebugImage(enhanced, debugDir, $"{modePrefix}_03_color_contrast");

            var sharpened = ApplySharpen(enhanced);
            enhanced.Dispose();
            SaveDebugImage(sharpened, debugDir, $"{modePrefix}_04_sharpen");
            return sharpened;
        }

        // 2. 灰度化 (ColorMatrix)
        var gray = new Bitmap(newW, newH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(gray))
        {
            var grayMatrix = new ColorMatrix(new float[][] {
                new float[] { 0.299f, 0.299f, 0.299f, 0, 0 },
                new float[] { 0.587f, 0.587f, 0.587f, 0, 0 },
                new float[] { 0.114f, 0.114f, 0.114f, 0, 0 },
                new float[] { 0,      0,      0,      1, 0 },
                new float[] { 0,      0,      0,      0, 1 }
            });
            using var attr = new ImageAttributes();
            attr.SetColorMatrix(grayMatrix);
            g.DrawImage(scaled, new Rectangle(0, 0, newW, newH),
                0, 0, newW, newH, GraphicsUnit.Pixel, attr);
        }
        scaled.Dispose();
        SaveDebugImage(gray, debugDir, $"{modePrefix}_03_grayscale");

        // 3. 对比度增强 (ColorMatrix: contrast 3.5x, 强拉对比)
        var contrasted = ApplyColorContrast(gray, 3.5f);
        gray.Dispose();
        SaveDebugImage(contrasted, debugDir, $"{modePrefix}_04_contrast");

        // 4. Otsu 自适应阈值二值化：纯黑文字 + 纯白背景
        var binary = ApplyOtsuBinarization(contrasted);
        contrasted.Dispose();
        SaveDebugImage(binary, debugDir, $"{modePrefix}_05_binary");

        return binary;
    }

    private static Bitmap ApplyColorContrast(Bitmap source, float contrast)
    {
        int w = source.Width;
        int h = source.Height;
        var translate = (1.0f - contrast) * 0.5f;
        var result = new Bitmap(w, h, PixelFormat.Format32bppArgb);

        using var g = Graphics.FromImage(result);
        using var attr = new ImageAttributes();
        var contrastMatrix = new ColorMatrix(new float[][] {
            new float[] { contrast, 0,        0,        0, 0 },
            new float[] { 0,        contrast, 0,        0, 0 },
            new float[] { 0,        0,        contrast, 0, 0 },
            new float[] { 0,        0,        0,        1, 0 },
            new float[] { translate, translate, translate, 0, 1 }
        });
        attr.SetColorMatrix(contrastMatrix);
        g.DrawImage(source, new Rectangle(0, 0, w, h), 0, 0, w, h, GraphicsUnit.Pixel, attr);

        return result;
    }

    private static Bitmap ApplySharpen(Bitmap source)
    {
        int w = source.Width;
        int h = source.Height;
        var result = new Bitmap(w, h, PixelFormat.Format32bppArgb);

        var srcData = source.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var dstData = result.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        try
        {
            int srcStride = srcData.Stride;
            int dstStride = dstData.Stride;
            var srcPixels = new byte[Math.Abs(srcStride) * h];
            var dstPixels = new byte[Math.Abs(dstStride) * h];

            Marshal.Copy(srcData.Scan0, srcPixels, 0, srcPixels.Length);

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int dstOffset = y * dstStride + x * 4;
                    int srcOffset = y * srcStride + x * 4;

                    if (x == 0 || y == 0 || x == w - 1 || y == h - 1)
                    {
                        Buffer.BlockCopy(srcPixels, srcOffset, dstPixels, dstOffset, 4);
                        continue;
                    }

                    for (int channel = 0; channel < 3; channel++)
                    {
                        int center = srcPixels[srcOffset + channel] * 5;
                        int left = srcPixels[y * srcStride + (x - 1) * 4 + channel];
                        int right = srcPixels[y * srcStride + (x + 1) * 4 + channel];
                        int top = srcPixels[(y - 1) * srcStride + x * 4 + channel];
                        int bottom = srcPixels[(y + 1) * srcStride + x * 4 + channel];
                        int value = center - left - right - top - bottom;
                        dstPixels[dstOffset + channel] = (byte)Math.Clamp(value, 0, 255);
                    }

                    dstPixels[dstOffset + 3] = srcPixels[srcOffset + 3];
                }
            }

            Marshal.Copy(dstPixels, 0, dstData.Scan0, dstPixels.Length);
        }
        finally
        {
            source.UnlockBits(srcData);
            result.UnlockBits(dstData);
        }

        return result;
    }

    /// <summary>
    /// Otsu 自适应阈值二值化：纯黑(0)文字 + 纯白(255)背景
    /// 低对比度文字经过此处理后 OCR 识别率大幅提升
    /// </summary>
    private static Bitmap ApplyOtsuBinarization(Bitmap source)
    {
        int w = source.Width, h = source.Height;
        var result = new Bitmap(w, h, PixelFormat.Format32bppArgb);

        // 1. 收集灰度直方图（用安全托管方式）
        var srcData = source.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int stride = srcData.Stride;
        int bytes = Math.Abs(stride) * h;
        var pixels = new byte[bytes];
        Marshal.Copy(srcData.Scan0, pixels, 0, bytes);
        source.UnlockBits(srcData);

        int[] hist = new int[256];
        var grayValues = new byte[w * h]; // 每像素的灰度值

        for (int y = 0; y < h; y++)
        {
            int rowOff = y * stride;
            for (int x = 0; x < w; x++)
            {
                int px = rowOff + x * 4;
                // BGRA: pixels[px]=B, pixels[px+1]=G, pixels[px+2]=R
                byte gray = (byte)(pixels[px + 2] * 0.299 + pixels[px + 1] * 0.587 + pixels[px] * 0.114);
                grayValues[y * w + x] = gray;
                hist[gray]++;
            }
        }

        // 2. Otsu 算法: 找最优阈值
        int total = w * h;
        int sumAll = 0;
        for (int i = 0; i < 256; i++) sumAll += i * hist[i];

        int threshold = 128;
        float maxVariance = 0;
        int weightBg = 0;
        long sumBg = 0;

        for (int t = 0; t < 256; t++)
        {
            weightBg += hist[t];
            if (weightBg == 0 || weightBg == total) continue;

            sumBg += (long)t * hist[t];
            int weightFg = total - weightBg;
            long sumFg = sumAll - sumBg;

            float meanBg = (float)sumBg / weightBg;
            float meanFg = (float)sumFg / weightFg;
            float variance = weightBg * weightFg * (meanBg - meanFg) * (meanBg - meanFg);

            if (variance > maxVariance)
            {
                maxVariance = variance;
                threshold = t;
            }
        }

        Logger.Debug($"Otsu 阈值: {threshold} (最大类间方差: {maxVariance:F0})");

        // 3. 应用阈值: 低于阈值 → 黑色(文字), 高于阈值 → 白色(背景)
        var dstData = result.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        var dstPixels = new byte[Math.Abs(dstData.Stride) * h];

        for (int y = 0; y < h; y++)
        {
            int rowOff = y * dstData.Stride;
            for (int x = 0; x < w; x++)
            {
                byte gv = grayValues[y * w + x];
                byte val = gv < threshold ? (byte)0 : (byte)255;
                int px = rowOff + x * 4;
                dstPixels[px] = val;       // B
                dstPixels[px + 1] = val;   // G
                dstPixels[px + 2] = val;   // R
                dstPixels[px + 3] = 255;   // A
            }
        }

        Marshal.Copy(dstPixels, 0, dstData.Scan0, dstPixels.Length);
        result.UnlockBits(dstData);

        return result;
    }

    private static string EnsureDebugDir()
    {
        var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void SaveDebugImage(Bitmap bmp, string dir, string label)
    {
        try
        {
            int seq = Interlocked.Increment(ref _debugSeq);
            var path = Path.Combine(dir, $"{seq:000}_{label}.png");
            bmp.Save(path, ImageFormat.Png);
        }
        catch { /* 调试保存失败不影响主流程 */ }
    }

    /// <summary>
    /// 将 System.Drawing.Bitmap 转换为 SoftwareBitmap（通过 BMP 流中转）
    /// </summary>
    private static async Task<SoftwareBitmap> ConvertToSoftwareBitmapAsync(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Bmp);
        ms.Position = 0;

        var randomAccessStream = ms.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
        var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

        if (softwareBitmap.BitmapPixelFormat != BitmapPixelFormat.Rgba8 &&
            softwareBitmap.BitmapPixelFormat != BitmapPixelFormat.Gray8)
        {
            softwareBitmap = SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Rgba8);
        }

        return softwareBitmap;
    }
}
