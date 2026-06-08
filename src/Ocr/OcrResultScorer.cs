using System.Text.RegularExpressions;

namespace MaaEnd_Log_Retransmitter.Ocr;

internal static class OcrResultScorer
{
    private static readonly Regex ValidTimePattern = new(
        @"^\d{2}:\d{2}(?::\d{2}(?:\.\d{1,3})?)?$",
        RegexOptions.Compiled);

    private static readonly string[] StrongMarkers =
    [
        "任务开始",
        "任务完成",
        "任务失败",
        "重要通知"
    ];

    private static readonly string[] WeakMarkers =
    [
        "正在",
        "开始",
        "进入",
        "检查",
        "领取",
        "运行日志"
    ];

    public static OcrResultScore Score(OcrEngine.OcrResultData result)
    {
        var details = new List<string>();
        if (result.Lines.Count == 0 || string.IsNullOrWhiteSpace(result.Text))
        {
            details.Add("空结果: -1000");
            return new OcrResultScore(-1000, "empty", details);
        }

        int score = 0;
        int validTimes = 0;
        int strongMarkers = 0;
        int weakMarkers = 0;
        int noisyLines = 0;
        int nonEmptyContentLines = 0;

        foreach (var line in result.Lines)
        {
            var content = line.Content.Trim();
            if (string.IsNullOrWhiteSpace(content)) continue;

            nonEmptyContentLines++;
            score += 8;
            if (IsValidTime(line.TimeText))
            {
                validTimes++;
                score += 32;
            }

            strongMarkers += CountMarkers(content, StrongMarkers);
            weakMarkers += CountMarkers(content, WeakMarkers);
            if (IsNoisyLine(line.RawText, content))
            {
                noisyLines++;
                score -= 18;
            }
        }

        AddDetail(details, $"非空内容行 {nonEmptyContentLines} * 8", nonEmptyContentLines * 8);
        AddDetail(details, $"合法时间 {validTimes} * 32", validTimes * 32);
        AddDetail(details, $"噪声行 {noisyLines} * -18", noisyLines * -18);

        var strongMarkerScore = Math.Min(strongMarkers, 6) * 28;
        var weakMarkerScore = Math.Min(weakMarkers, 8) * 10;
        var lineCountScore = ScoreLineCount(result.Lines.Count);
        var textNoisePenalty = ScoreTextNoise(result.Text);

        score += strongMarkerScore;
        score += weakMarkerScore;
        score += lineCountScore;
        score -= textNoisePenalty;

        AddDetail(details, $"强关键词 {strongMarkers} 个，最多计 6 个 * 28", strongMarkerScore);
        AddDetail(details, $"弱关键词 {weakMarkers} 个，最多计 8 个 * 10", weakMarkerScore);
        AddDetail(details, $"行数 {result.Lines.Count} 合理性", lineCountScore);
        AddDetail(details, "全文噪声惩罚", -textNoisePenalty);

        var reason = $"lines={result.Lines.Count}, time={validTimes}, strong={strongMarkers}, weak={weakMarkers}, noisy={noisyLines}";
        return new OcrResultScore(score, reason, details);
    }

    private static void AddDetail(List<string> details, string label, int delta)
        => details.Add($"{label}: {FormatDelta(delta)}");

    private static string FormatDelta(int delta)
        => delta >= 0 ? $"+{delta}" : delta.ToString();

    private static bool IsValidTime(string text)
        => !string.IsNullOrWhiteSpace(text) && ValidTimePattern.IsMatch(text.Trim());

    private static int CountMarkers(string content, string[] markers)
        => markers.Count(marker => content.Contains(marker, StringComparison.Ordinal));

    private static int ScoreLineCount(int lineCount)
    {
        if (lineCount is >= 2 and <= 12) return 24;
        if (lineCount is >= 13 and <= 24) return 12;
        if (lineCount == 1) return -12;
        return -24;
    }

    private static bool IsNoisyLine(string rawText, string content)
    {
        if (content.Length > 90) return true;
        if (content.Length <= 1) return true;
        return GetSignalRatio(rawText) < 0.35;
    }

    private static int ScoreTextNoise(string text)
    {
        int penalty = 0;
        var lines = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.Length > 120) penalty += 20;
            if (GetSignalRatio(line) < 0.30) penalty += 16;
        }

        return Math.Min(penalty, 120);
    }

    private static double GetSignalRatio(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;

        int total = text.Count(c => !char.IsWhiteSpace(c));
        if (total == 0) return 0;

        int signal = text.Count(c =>
            c >= 0x4E00 && c <= 0x9FFF
            || char.IsLetterOrDigit(c)
            || c is ':' or '.' or '-' or '_' or '[' or ']' or '(' or ')' or '：' or '，' or '。');
        return (double)signal / total;
    }
}
