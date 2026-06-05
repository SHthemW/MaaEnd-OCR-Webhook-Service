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

    private List<string>? _previousKeys;

    public Result Filter(List<OcrEngine.OcrLineInfo> lines)
    {
        var currentLines = lines
            .Where(line => !string.IsNullOrWhiteSpace(line.RawText))
            .ToList();

        if (IsUnstableFrame(currentLines, out var rejectionReason))
        {
            return Result.Reject(currentLines, rejectionReason);
        }

        var currentKeys = currentLines.Select(BuildLineKey).ToList();
        var newLines = ExtractNewLines(currentLines, currentKeys, _previousKeys);
        _previousKeys = currentKeys;
        return Result.Accept(currentLines, newLines);
    }

    private bool IsUnstableFrame(List<OcrEngine.OcrLineInfo> currentLines, out string reason)
    {
        reason = string.Empty;

        if (_previousKeys == null || _previousKeys.Count < 2) return false;
        if (currentLines.Count != 1) return false;

        var onlyLine = currentLines[0];
        if (!string.IsNullOrWhiteSpace(onlyLine.TimeText)) return false;

        var content = onlyLine.Content.Trim();
        if (content.Length < 80) return false;

        int structuredMarkerCount = StructuredCollapseMarkers.Sum(marker => CountOccurrences(content, marker));
        if (structuredMarkerCount < 2) return false;

        reason = $"单行内容疑似塌缩，且命中 {structuredMarkerCount} 个结构化标记";
        return true;
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
            IReadOnlyList<OcrEngine.OcrLineInfo> newLines,
            string? rejectionReason)
        {
            Accepted = accepted;
            CurrentLines = currentLines;
            NewLines = newLines;
            RejectionReason = rejectionReason;
        }

        public bool Accepted { get; }
        public IReadOnlyList<OcrEngine.OcrLineInfo> CurrentLines { get; }
        public IReadOnlyList<OcrEngine.OcrLineInfo> NewLines { get; }
        public string? RejectionReason { get; }

        public static Result Accept(
            IReadOnlyList<OcrEngine.OcrLineInfo> currentLines,
            IReadOnlyList<OcrEngine.OcrLineInfo> newLines)
        {
            return new Result(true, currentLines, newLines, null);
        }

        public static Result Reject(IReadOnlyList<OcrEngine.OcrLineInfo> currentLines, string rejectionReason)
        {
            return new Result(false, currentLines, Array.Empty<OcrEngine.OcrLineInfo>(), rejectionReason);
        }
    }
}
