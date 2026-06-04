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

        // 3. OCR 识别
        try
        {
            Logger.Info($"正在执行 OCR 识别 (语言: {args.Language})...");
            var sw = Stopwatch.StartNew();
            var ocrText = await OcrEngine.RecognizeAsync(screenshot, args.Language);
            sw.Stop();
            Logger.Info($"OCR 完成, 耗时 {sw.ElapsedMilliseconds}ms, 识别到 {ocrText.Length} 个字符");

            if (string.IsNullOrWhiteSpace(ocrText))
            {
                Logger.Warn("OCR 结果为空（可能窗口内容为纯图像或无文字）");
            }
            else
            {
                Logger.Debug($"OCR 原文:\n--- OCR 开始 ---\n{ocrText}\n--- OCR 结束 ---");
            }

            // 4. 搜索文本
            var found = SearchText(ocrText, args.SearchText, args.CaseSensitive);
            if (found)
            {
                Logger.Info($"✅ 文本搜索成功: 找到匹配 \"{args.SearchText}\"");
                return 0;
            }
            else
            {
                Logger.Warn($"❌ 文本未找到: \"{args.SearchText}\" 不在 OCR 结果中");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"OCR 识别失败: {ex.Message}");
            Logger.Debug($"OCR 异常堆栈: {ex}");
            return 2;
        }
        finally
        {
            screenshot.Dispose();
        }

        // 未找到, 等待后重试
        if (attempt < args.Retry)
        {
            Logger.Info($"等待 {args.RetryInterval}ms 后重试...");
            await Task.Delay(args.RetryInterval);
        }
    }

    Logger.Error("已达到最大重试次数, 文本未找到");
    return 3;
}

// ═══════════════════════════════════════════════════════════
// 文本搜索
// ═══════════════════════════════════════════════════════════

/// <summary>
/// 在 OCR 结果中搜索目标文本。
/// 自动归一化 CJK 字符之间的空格（OCR 常把"运行日志"识别为"运 行 日 志"）。
/// </summary>
static bool SearchText(string source, string search, bool caseSensitive)
{
    var comparison = caseSensitive
        ? StringComparison.Ordinal
        : StringComparison.OrdinalIgnoreCase;

    // 归一化：去掉 CJK 字符之间的多余空格
    var normalizedSource = NormalizeCjkSpaces(source);
    var normalizedSearch = NormalizeCjkSpaces(search);

    return normalizedSource.Contains(normalizedSearch, comparison);
}

/// <summary>
/// 移除 CJK 字符之间的空格，但保留拉丁字母/数字之间的空格。
/// "运 行 日 志" → "运行日志", "Hello World" → "Hello World"
/// </summary>
static string NormalizeCjkSpaces(string text)
{
    if (string.IsNullOrEmpty(text)) return text;

    var sb = new StringBuilder(text.Length);
    for (int i = 0; i < text.Length; i++)
    {
        char c = text[i];
        if (c == ' ' || c == ' ')
        {
            // 只有当空格两侧都是 CJK/标点时才跳过
            bool prevCjk = i > 0 && IsCjkOrPunct(text[i - 1]);
            bool nextCjk = i < text.Length - 1 && IsCjkOrPunct(text[i + 1]);
            if (prevCjk || nextCjk)
                continue;
        }
        sb.Append(c);
    }
    return sb.ToString();
}

/// <summary>判断字符是否为 CJK 字符或中文标点</summary>
static bool IsCjkOrPunct(char c)
{
    return (c >= 0x4E00 && c <= 0x9FFF)   // CJK 统一汉字
        || (c >= 0x3400 && c <= 0x4DBF)   // CJK 扩展 A
        || (c >= 0x20000 && c <= 0x2A6DF) // CJK 扩展 B
        || (c >= 0x3000 && c <= 0x303F)   // CJK 标点
        || (c >= 0xFF00 && c <= 0xFFEF)   // 全角字符
        || (c >= 0x2F00 && c <= 0x2FDF)   // 康熙部首
        || (c >= 0x31C0 && c <= 0x31EF)   // CJK 笔画
        || (c >= 0xFE30 && c <= 0xFE4F)   // CJK 兼容形式
        || (c >= 0x3000 && c <= 0x301F)   // CJK 标点
        || c == '．' || c == '：' || c == '，' || c == '。'  // 全角标点
        || c == '／' || c == '（' || c == '）' || c == '［'
        || c == '］' || c == '｛' || c == '｝';
}

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
    public static void Info(string message)  => Log("INFO",  message, ConsoleColor.Gray);
    public static void Warn(string message)  => Log("WARN",  message, ConsoleColor.Yellow);
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
                rcDestination = new RECT { Left = 0, Top = 0, Right = width, Bottom = height }
            };
            DwmUpdateThumbnailProperties(hThumbnail, ref props);

            // 4. 等待 DWM 合成完成
            DwmFlush();
            Thread.Sleep(50); // 额外缓冲确保合成完毕

            // 5. PrintWindow 抓取宿主窗口的 DWM 合成内容
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
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
    /// <summary>
    /// 对 Bitmap 执行 OCR 识别，返回识别的文本
    /// </summary>
    public static async Task<string> RecognizeAsync(Bitmap bitmap, string languageTag)
    {
        // Step 1: 图像预处理 —— 放大 + 增强，提升 OCR 识别率（对中文尤其重要）
        using var processed = PreprocessForOcr(bitmap);
        Logger.Debug($"图像预处理: {bitmap.Width}x{bitmap.Height} → {processed.Width}x{processed.Height}");

        // Step 2: 将 Bitmap 转换为 SoftwareBitmap
        using var softwareBitmap = await ConvertToSoftwareBitmapAsync(processed);

        // Step 3: 获取指定语言的 OCR 引擎
        Windows.Media.Ocr.OcrEngine? engine;
        var requestedLanguage = new Language(languageTag);
        if (Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(requestedLanguage) is { } matchedEngine)
        {
            engine = matchedEngine;
            Logger.Debug($"使用 OCR 语言: {languageTag}");
        }
        else
        {
            var availableLanguages = Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages;
            if (availableLanguages.Count == 0)
                throw new NotSupportedException("系统没有可用的 OCR 语言包");

            var fallbackLang = availableLanguages[0];
            engine = Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(fallbackLang);
            if (engine == null)
                throw new NotSupportedException($"无法创建 OCR 引擎（语言: {fallbackLang.DisplayName}）");

            Logger.Warn($"不支持语言 \"{languageTag}\", 已回退到: {fallbackLang.DisplayName} ({fallbackLang.LanguageTag})");
        }

        // Step 4: 执行 OCR 识别
        var result = await engine.RecognizeAsync(softwareBitmap);

        // Step 5: 拼接识别文本（按行）
        var sb = new StringBuilder();
        foreach (var line in result.Lines)
        {
            sb.AppendLine(line.Text);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// OCR 预处理：3x 放大
    /// 中文等 CJK 字符结构复杂，需要充足像素才能被准确识别
    /// </summary>
    private static Bitmap PreprocessForOcr(Bitmap source)
    {
        int newW = source.Width * 3;
        int newH = source.Height * 3;

        var scaled = new Bitmap(newW, newH, PixelFormat.Format32bppArgb);

        // 高质量双三次插值放大
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            g.DrawImage(source, new Rectangle(0, 0, newW, newH));
        }

        return scaled;
    }

    /// <summary>
    /// 将 System.Drawing.Bitmap 转换为 SoftwareBitmap（通过 BMP 流中转）
    /// </summary>
    private static async Task<SoftwareBitmap> ConvertToSoftwareBitmapAsync(Bitmap bitmap)
    {
        // 保存 Bitmap 到内存流 (BMP 格式)
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Bmp);
        ms.Position = 0;

        // 将 .NET Stream 转换为 WinRT IRandomAccessStream
        var randomAccessStream = ms.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
        var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

        // OCR 引擎要求像素格式为 Rgba8 或 Gray8
        if (softwareBitmap.BitmapPixelFormat != BitmapPixelFormat.Rgba8 &&
            softwareBitmap.BitmapPixelFormat != BitmapPixelFormat.Gray8)
        {
            softwareBitmap = SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Rgba8);
        }

        return softwareBitmap;
    }
}
