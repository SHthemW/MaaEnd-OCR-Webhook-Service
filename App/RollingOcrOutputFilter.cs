using System.Text;
using System.Text.RegularExpressions;
using MaaEnd_Log_Retransmitter.Ocr;

namespace MaaEnd_Log_Retransmitter.App;

internal sealed class RollingOcrOutputFilter
{
    private static readonly string[] StructuredCollapseMarkers =
    [
        "任务开始",
        "任务完成",
        "任务失败",
        "正在检查画面"
    ];

    private static readonly Regex LeadingTimePattern = new(
        @"^\s*(?<time>[0-9０-９:：﹕︰．.·•,，]{6,16})\s*(?<content>\S.+)$",
        RegexOptions.Compiled);

    private static readonly Regex StructuredEventPattern = new(
        @"^(?<prefix>任务开始|任务完成|任务失败)\s*(?:[:：．.]|\s)?\s*(?<payload>.+)$",
        RegexOptions.Compiled);

    private const int MaxHistorySize = 160;
    private readonly List<OutputLine> _acceptedHistory = [];
    private List<string>? _previousFrameSignatures;
    private TimeSpan? _latestAcceptedTime;

    public Result Filter(List<OcrEngine.OcrLineInfo> lines)
    {
        var currentLines = lines
            .Where(line => !string.IsNullOrWhiteSpace(line.RawText))
            .ToList();

        if (IsUnstableFrame(currentLines, out var rejectionReason))
        {
            return Result.Reject(currentLines, rejectionReason);
        }

        var normalizedLines = currentLines
            .Select(TryBuildOutputLine)
            .Where(line => line != null)
            .Cast<OutputLine>()
            .DistinctBy(line => line.Signature)
            .ToList();

        var newLines = ExtractNewLines(normalizedLines);
        _previousFrameSignatures = normalizedLines.Select(line => line.Signature).ToList();
        return Result.Accept(currentLines, newLines);
    }

    private bool IsUnstableFrame(List<OcrEngine.OcrLineInfo> currentLines, out string reason)
    {
        reason = string.Empty;

        if (_previousFrameSignatures == null || _previousFrameSignatures.Count < 2) return false;
        if (currentLines.Count != 1) return false;

        var onlyLine = currentLines[0];
        if (!string.IsNullOrWhiteSpace(onlyLine.TimeText)) return false;

        var content = NormalizeText(onlyLine.Content);
        if (content.Length < 80) return false;

        int structuredMarkerCount = StructuredCollapseMarkers.Sum(marker => CountOccurrences(content, marker));
        if (structuredMarkerCount < 2) return false;

        reason = $"单行内容疑似塌缩，且命中 {structuredMarkerCount} 个结构化标记";
        return true;
    }

    private List<OutputLine> ExtractNewLines(List<OutputLine> currentLines)
    {
        if (currentLines.Count == 0)
        {
            return [];
        }

        var visibleSlice = TrimFrameOverlap(currentLines);
        if (visibleSlice.Count == 0)
        {
            return [];
        }

        var accepted = new List<OutputLine>();
        foreach (var line in visibleSlice)
        {
            if (IsTimeRegression(line)) continue;
            if (IsDuplicateFromHistory(line)) continue;

            accepted.Add(line);
            RegisterAcceptedLine(line);
        }

        return accepted;
    }

    private List<OutputLine> TrimFrameOverlap(List<OutputLine> currentLines)
    {
        if (_previousFrameSignatures == null || _previousFrameSignatures.Count == 0)
        {
            return currentLines;
        }

        var currentSignatures = currentLines.Select(line => line.Signature).ToList();
        int maxOverlap = Math.Min(_previousFrameSignatures.Count, currentSignatures.Count);
        for (int overlap = maxOverlap; overlap >= 1; overlap--)
        {
            bool matched = true;
            for (int i = 0; i < overlap; i++)
            {
                if (!string.Equals(_previousFrameSignatures[_previousFrameSignatures.Count - overlap + i], currentSignatures[i], StringComparison.Ordinal))
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

        var previousSet = new HashSet<string>(_previousFrameSignatures, StringComparer.Ordinal);
        return currentLines
            .Where(line => !previousSet.Contains(line.Signature))
            .ToList();
    }

    private OutputLine? TryBuildOutputLine(OcrEngine.OcrLineInfo line)
    {
        var rawText = NormalizeText(line.RawText);
        var content = NormalizeContent(line.Content);
        var timeText = NormalizeTimeText(line.TimeText);

        if (string.IsNullOrWhiteSpace(content))
        {
            content = NormalizeContent(rawText);
        }

        if (string.IsNullOrWhiteSpace(timeText))
        {
            if (TryExtractLeadingTime(content, out var extractedTime, out var remainingContent)
                || TryExtractLeadingTime(rawText, out extractedTime, out remainingContent))
            {
                timeText = extractedTime;
                content = NormalizeContent(remainingContent);
            }
        }

        if (string.IsNullOrWhiteSpace(timeText)) return null;
        if (!TryParsePreciseTime(timeText, out var timeValue)) return null;

        content = NormalizeContent(content);
        if (!IsUsableContent(content)) return null;

        var category = ClassifyContent(content);
        var canonicalContent = BuildCanonicalContent(content, category);
        if (string.IsNullOrWhiteSpace(canonicalContent)) return null;

        return new OutputLine(
            timeText,
            content,
            category,
            canonicalContent,
            $"{timeText}|{category}|{canonicalContent}",
            timeValue);
    }

    private bool IsTimeRegression(OutputLine line)
    {
        if (_latestAcceptedTime == null) return false;
        return line.TimeValue < _latestAcceptedTime.Value - TimeSpan.FromSeconds(1);
    }

    private bool IsDuplicateFromHistory(OutputLine line)
    {
        foreach (var previous in _acceptedHistory)
        {
            if (string.Equals(previous.Signature, line.Signature, StringComparison.Ordinal))
            {
                return true;
            }

            if (line.Category is LineCategory.TaskStarted or LineCategory.TaskCompleted or LineCategory.TaskFailed
                && previous.Category == line.Category
                && previous.TimeValue == line.TimeValue
                && AreSimilarCanonicalContents(previous.CanonicalContent, line.CanonicalContent))
            {
                return true;
            }
        }

        return false;
    }

    private void RegisterAcceptedLine(OutputLine line)
    {
        _acceptedHistory.Add(line);
        if (_acceptedHistory.Count > MaxHistorySize)
        {
            _acceptedHistory.RemoveAt(0);
        }

        if (_latestAcceptedTime == null || line.TimeValue > _latestAcceptedTime.Value)
        {
            _latestAcceptedTime = line.TimeValue;
        }
    }

    private static bool TryExtractLeadingTime(string text, out string timeText, out string content)
    {
        timeText = string.Empty;
        content = string.Empty;

        if (string.IsNullOrWhiteSpace(text)) return false;

        var match = LeadingTimePattern.Match(text);
        if (!match.Success) return false;

        var normalizedTime = NormalizeTimeText(match.Groups["time"].Value);
        if (string.IsNullOrWhiteSpace(normalizedTime)) return false;

        timeText = normalizedTime;
        content = match.Groups["content"].Value.Trim();
        return !string.IsNullOrWhiteSpace(content);
    }

    private static string NormalizeTimeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var digits = new StringBuilder(8);
        foreach (var c in text)
        {
            if (TryNormalizeDigit(c, out var digit))
            {
                digits.Append(digit);
                continue;
            }

            if (char.IsWhiteSpace(c) || IsTimeSeparator(c))
            {
                continue;
            }

            return string.Empty;
        }

        if (digits.Length < 6) return string.Empty;

        string hourText = digits.ToString(0, 2);
        string minuteText = digits.ToString(2, 2);
        string secondText = digits.ToString(4, 2);
        if (!IsValidTimeComponents(hourText, minuteText, secondText)) return string.Empty;

        return $"{hourText}:{minuteText}:{secondText}";
    }

    private static bool TryParsePreciseTime(string timeText, out TimeSpan timeValue)
    {
        timeValue = default;
        return TimeSpan.TryParseExact(timeText, @"hh\:mm\:ss", null, out timeValue);
    }

    private static string NormalizeContent(string content)
    {
        var normalized = NormalizeText(content);
        if (string.IsNullOrWhiteSpace(normalized)) return string.Empty;

        var structuredMatch = StructuredEventPattern.Match(normalized);
        if (structuredMatch.Success)
        {
            var prefix = structuredMatch.Groups["prefix"].Value;
            var payload = SanitizePayload(structuredMatch.Groups["payload"].Value);
            if (string.IsNullOrWhiteSpace(payload)) return string.Empty;
            return $"{prefix}：{payload}";
        }

        return SanitizeGeneralContent(normalized);
    }

    private static string SanitizePayload(string payload)
    {
        var sanitized = NormalizeText(payload);
        sanitized = Regex.Replace(sanitized, @"^[：:．.，,、;；\-\s]+", string.Empty);
        sanitized = TrimNoisePrefix(sanitized, aggressive: true);
        sanitized = Regex.Replace(sanitized, @"\s+[：:．.，,、;；]+", " ");
        sanitized = sanitized.Trim();
        return sanitized;
    }

    private static string SanitizeGeneralContent(string content)
    {
        var sanitized = TrimNoisePrefix(content, aggressive: false);
        sanitized = Regex.Replace(sanitized, @"\s+", " ").Trim();
        return sanitized;
    }

    private static string TrimNoisePrefix(string text, bool aggressive)
    {
        var trimmed = text.TrimStart();
        int removed = 0;

        while (trimmed.Length > 1 && removed < 2)
        {
            char first = trimmed[0];
            if (IsTrimmableNoise(first, aggressive))
            {
                trimmed = trimmed[1..].TrimStart();
                removed++;
                continue;
            }

            break;
        }

        return trimmed;
    }

    private static bool IsTrimmableNoise(char c, bool aggressive)
    {
        if (char.IsWhiteSpace(c)) return true;
        if (c is '0' or 'O' or 'o' or '○' or '●' or '·' or '•' or '．' or '.' or ',' or '，' or '、' or '：' or ':') return true;
        if (aggressive && c is '头' or '虐') return true;
        return false;
    }

    private static bool IsUsableContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;

        var category = ClassifyContent(content);
        return category is LineCategory.TaskStarted or LineCategory.TaskCompleted or LineCategory.TaskFailed
            ? IsUsableStructuredContent(content)
            : IsUsableGeneralContent(content);
    }

    private static bool IsUsableStructuredContent(string content)
    {
        if (content.Length < 2 || content.Length > 48) return false;
        if (CountStructuredMarkers(content) > 1) return false;
        if (Regex.Matches(content, @"\d{1,2}[:：]\d{2}").Count > 0) return false;

        int signalChars = content.Count(c => IsReadableSignalChar(c));
        if (signalChars < 2) return false;

        var payload = content[(content.IndexOf('：') + 1)..].Trim();
        payload = TrimNoisePrefix(payload, aggressive: true);
        return payload.Length >= 2;
    }

    private static bool IsUsableGeneralContent(string content)
    {
        if (content.Length < 2 || content.Length > 72) return false;

        int signalChars = content.Count(c => IsReadableSignalChar(c));
        if (signalChars < 2) return false;

        bool hasChinese = content.Any(c => c >= 0x4E00 && c <= 0x9FFF);
        bool hasLetterOrDigit = content.Any(char.IsLetterOrDigit);
        if (!hasChinese && !hasLetterOrDigit) return false;

        if (CountStructuredMarkers(content) >= 3) return false;
        return true;
    }

    private static int CountStructuredMarkers(string content)
    {
        return StructuredCollapseMarkers.Sum(marker => CountOccurrences(content, marker));
    }

    private static bool IsReadableSignalChar(char c)
    {
        return c >= 0x4E00 && c <= 0x9FFF
            || char.IsLetterOrDigit(c);
    }

    private static LineCategory ClassifyContent(string content)
    {
        if (content.StartsWith("任务开始：", StringComparison.Ordinal)) return LineCategory.TaskStarted;
        if (content.StartsWith("任务完成：", StringComparison.Ordinal)) return LineCategory.TaskCompleted;
        if (content.StartsWith("任务失败：", StringComparison.Ordinal)) return LineCategory.TaskFailed;
        if (content.StartsWith("开始", StringComparison.Ordinal)
            || content.StartsWith("正在", StringComparison.Ordinal)
            || content.StartsWith("没有", StringComparison.Ordinal)
            || content.StartsWith("领取", StringComparison.Ordinal))
        {
            return LineCategory.Activity;
        }

        return LineCategory.Other;
    }

    private static string BuildCanonicalContent(string content, LineCategory category)
    {
        var normalized = content;
        if (category is LineCategory.TaskStarted or LineCategory.TaskCompleted or LineCategory.TaskFailed)
        {
            var match = StructuredEventPattern.Match(content);
            if (match.Success)
            {
                var prefix = match.Groups["prefix"].Value;
                var payload = TrimNoisePrefix(match.Groups["payload"].Value, aggressive: true);
                normalized = prefix + payload;
            }
        }

        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (char.IsWhiteSpace(c)) continue;
            if (c is '：' or ':' or '．' or '.' or ',' or '，' or '、' or '·' or '•') continue;
            sb.Append(NormalizeDigitChar(c));
        }

        return sb.ToString();
    }

    private static bool AreSimilarCanonicalContents(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal)) return true;
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;

        var shorter = left.Length <= right.Length ? left : right;
        var longer = left.Length > right.Length ? left : right;
        if (longer.Contains(shorter, StringComparison.Ordinal) && longer.Length - shorter.Length <= 2)
        {
            return true;
        }

        int threshold = Math.Max(1, Math.Min(2, shorter.Length / 6));
        return GetLevenshteinDistance(left, right) <= threshold;
    }

    private static int GetLevenshteinDistance(string left, string right)
    {
        var costs = new int[right.Length + 1];
        for (int j = 0; j <= right.Length; j++)
        {
            costs[j] = j;
        }

        for (int i = 1; i <= left.Length; i++)
        {
            int previousDiagonal = costs[0];
            costs[0] = i;

            for (int j = 1; j <= right.Length; j++)
            {
                int previousTop = costs[j];
                int substitutionCost = left[i - 1] == right[j - 1] ? 0 : 1;
                costs[j] = Math.Min(
                    Math.Min(costs[j] + 1, costs[j - 1] + 1),
                    previousDiagonal + substitutionCost);
                previousDiagonal = previousTop;
            }
        }

        return costs[right.Length];
    }

    private static string NormalizeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var normalized = Regex.Replace(text, @"\s+", " ").Trim();
        normalized = normalized.Replace('﹕', '：').Replace('︰', '：');
        normalized = Regex.Replace(normalized, @"\s+([，。；：！？、])", "$1");
        normalized = Regex.Replace(normalized, @"([（\(\[［\{｛])\s+", "$1");
        normalized = Regex.Replace(normalized, @"\s+([）\)\]］\}｝])", "$1");
        return normalized;
    }

    private static bool TryNormalizeDigit(char c, out char digit)
    {
        if (c >= '0' && c <= '9')
        {
            digit = c;
            return true;
        }

        if (c >= '０' && c <= '９')
        {
            digit = (char)('0' + (c - '０'));
            return true;
        }

        digit = default;
        return false;
    }

    private static char NormalizeDigitChar(char c)
    {
        return TryNormalizeDigit(c, out var digit) ? digit : c;
    }

    private static bool IsTimeSeparator(char c)
    {
        return c is ':' or '：' or '﹕' or '︰' or '．' or '.' or '·' or '•' or ',' or '，';
    }

    private static bool IsValidTimeComponents(string hourText, string minuteText, string secondText)
    {
        if (!int.TryParse(hourText, out var hour) || hour is < 0 or > 23) return false;
        if (!int.TryParse(minuteText, out var minute) || minute is < 0 or > 59) return false;
        return int.TryParse(secondText, out var second) && second is >= 0 and <= 59;
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

    internal sealed class Result
    {
        private Result(
            bool accepted,
            IReadOnlyList<OcrEngine.OcrLineInfo> currentLines,
            IReadOnlyList<OutputLine> newLines,
            string? rejectionReason)
        {
            Accepted = accepted;
            CurrentLines = currentLines;
            NewLines = newLines;
            RejectionReason = rejectionReason;
        }

        public bool Accepted { get; }
        public IReadOnlyList<OcrEngine.OcrLineInfo> CurrentLines { get; }
        public IReadOnlyList<OutputLine> NewLines { get; }
        public string? RejectionReason { get; }

        public static Result Accept(
            IReadOnlyList<OcrEngine.OcrLineInfo> currentLines,
            IReadOnlyList<OutputLine> newLines)
        {
            return new Result(true, currentLines, newLines, null);
        }

        public static Result Reject(IReadOnlyList<OcrEngine.OcrLineInfo> currentLines, string rejectionReason)
        {
            return new Result(false, currentLines, Array.Empty<OutputLine>(), rejectionReason);
        }
    }

    internal sealed class OutputLine(
        string timeText,
        string content,
        LineCategory category,
        string canonicalContent,
        string signature,
        TimeSpan timeValue)
    {
        public string TimeText { get; } = timeText;
        public string Content { get; } = content;
        public LineCategory Category { get; } = category;
        public string CanonicalContent { get; } = canonicalContent;
        public string Signature { get; } = signature;
        public TimeSpan TimeValue { get; } = timeValue;
    }

    internal enum LineCategory
    {
        TaskStarted,
        TaskCompleted,
        TaskFailed,
        Activity,
        Other
    }
}
