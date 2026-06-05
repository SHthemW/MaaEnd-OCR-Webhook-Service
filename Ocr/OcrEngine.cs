using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.RegularExpressions;
using MaaEnd_Log_Retransmitter.Infrastructure;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace MaaEnd_Log_Retransmitter.Ocr;

internal static class OcrEngine
{
    public class OcrWordInfo
    {
        public string Text { get; set; } = "";
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    public class OcrResultData
    {
        public string Text { get; set; } = "";
        public List<OcrWordInfo> Words { get; set; } = new();
        public List<OcrLineInfo> Lines { get; set; } = new();
    }

    public class OcrLineInfo
    {
        public string RawText { get; set; } = "";
        public string TimeText { get; set; } = "";
        public string Content { get; set; } = "";
        public List<OcrWordInfo> Words { get; set; } = new();
    }

    public enum OcrPreprocessMode
    {
        HighContrastBinary,
        DetailPreserving
    }

    private static int _debugSeq;

    public static async Task<OcrResultData> RecognizeWithWordsAsync(
        Bitmap bitmap,
        string languageTag,
        int upscale,
        OcrPreprocessMode preprocessMode = OcrPreprocessMode.HighContrastBinary)
    {
        using var processed = PreprocessForOcr(bitmap, upscale, preprocessMode);
        Logger.Debug($"图像预处理 ({preprocessMode}, {upscale}x): {bitmap.Width}x{bitmap.Height} → {processed.Width}x{processed.Height}");

        using var softwareBitmap = await ConvertToSoftwareBitmapAsync(processed);
        var engine = GetEngine(languageTag);
        var result = await engine.RecognizeAsync(softwareBitmap);

        var words = new List<OcrWordInfo>();
        foreach (var line in result.Lines)
        {
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

        var visualLines = GroupWordsByVisualLines(words);
        var orderedWords = visualLines
            .SelectMany(line => line.Words.OrderBy(word => word.X))
            .ToList();
        var parsedLines = BuildLineInfos(visualLines);
        var text = string.Join(Environment.NewLine, parsedLines
            .Select(line => line.RawText)
            .Where(line => !string.IsNullOrWhiteSpace(line)));

        return new OcrResultData
        {
            Text = text,
            Words = orderedWords,
            Lines = parsedLines
        };
    }

    public static Rectangle? FindTextRect(List<OcrWordInfo> words, string searchText, bool caseSensitive)
    {
        if (words.Count == 0 || string.IsNullOrWhiteSpace(searchText)) return null;

        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var normalizedSearch = NormalizeCjkSpaces(searchText);

        var flatWords = new List<(int wordIdx, string text)>();
        for (int i = 0; i < words.Count; i++)
        {
            var norm = NormalizeCjkSpaces(words[i].Text);
            if (!string.IsNullOrEmpty(norm)) flatWords.Add((i, norm));
        }

        var fullNorm = string.Concat(flatWords.Select(w => w.text));
        var matchIdx = fullNorm.IndexOf(normalizedSearch, comparison);
        if (matchIdx < 0) return null;

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

        var first = words[firstWordIdx.Value];
        var last = words[lastWordIdx.Value];
        int x = first.X;
        int y = first.Y;
        int right = Math.Max(first.X + first.Width, last.X + last.Width);
        int bottom = Math.Max(first.Y + first.Height, last.Y + last.Height);
        return new Rectangle(x, y, right - x, bottom - y);
    }

    private sealed class VisualLine
    {
        public List<OcrWordInfo> Words { get; } = new();
        public double CenterY { get; private set; }
        public double AverageHeight { get; private set; }

        public void Add(OcrWordInfo word)
        {
            Words.Add(word);
            double wordCenterY = word.Y + word.Height / 2.0;
            if (Words.Count == 1)
            {
                CenterY = wordCenterY;
                AverageHeight = word.Height;
                return;
            }

            int count = Words.Count;
            CenterY = ((CenterY * (count - 1)) + wordCenterY) / count;
            AverageHeight = ((AverageHeight * (count - 1)) + word.Height) / count;
        }
    }

    private static List<VisualLine> GroupWordsByVisualLines(List<OcrWordInfo> words)
    {
        if (words.Count == 0) return new List<VisualLine>();

        var sorted = words
            .OrderBy(word => word.Y + word.Height / 2.0)
            .ThenBy(word => word.X)
            .ToList();

        var lines = new List<VisualLine>();
        foreach (var word in sorted)
        {
            var line = lines.FirstOrDefault(candidate => IsSameVisualLine(candidate, word));
            if (line == null)
            {
                line = new VisualLine();
                lines.Add(line);
            }

            line.Add(word);
        }

        return lines.OrderBy(line => line.CenterY).ToList();
    }

    private static bool IsSameVisualLine(VisualLine line, OcrWordInfo word)
    {
        double wordCenterY = word.Y + word.Height / 2.0;
        double tolerance = Math.Max(line.AverageHeight, word.Height) * 0.6;
        return Math.Abs(line.CenterY - wordCenterY) <= tolerance;
    }

    private static List<OcrLineInfo> BuildLineInfos(List<VisualLine> visualLines)
    {
        var result = new List<OcrLineInfo>();
        foreach (var visualLine in visualLines)
        {
            var orderedWords = visualLine.Words.OrderBy(word => word.X).ToList();
            if (orderedWords.Count == 0) continue;

            var rawText = NormalizeOcrSpacing(BuildLineText(orderedWords));
            var split = TrySplitTimeAndContent(orderedWords);
            var content = split?.Content ?? rawText;

            result.Add(new OcrLineInfo
            {
                RawText = rawText,
                TimeText = split?.TimeText ?? string.Empty,
                Content = NormalizeOcrSpacing(content),
                Words = orderedWords
            });
        }

        return MergeContinuationLines(result);
    }

    private static List<OcrLineInfo> MergeContinuationLines(List<OcrLineInfo> lines)
    {
        var merged = new List<OcrLineInfo>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line.TimeText) && merged.Count > 0)
            {
                var previous = merged[^1];
                previous.RawText = NormalizeOcrSpacing(CombineContinuationText(previous.RawText, line.RawText));
                previous.Content = NormalizeOcrSpacing(CombineContinuationText(previous.Content, line.Content));
                previous.Words.AddRange(line.Words);
                continue;
            }

            merged.Add(line);
        }

        return merged;
    }

    private static string CombineContinuationText(string left, string right)
    {
        left = left.TrimEnd();
        right = right.TrimStart();

        if (string.IsNullOrEmpty(left)) return right;
        if (string.IsNullOrEmpty(right)) return left;

        char leftLast = left[^1];
        char rightFirst = right[0];
        if (ShouldOmitSeparator(leftLast, rightFirst))
        {
            return left + right;
        }

        return left + " " + right;
    }

    private static bool ShouldOmitSeparator(char leftLast, char rightFirst)
    {
        return IsOpeningPunctuation(leftLast)
            || IsClosingPunctuation(rightFirst)
            || leftLast == ':'
            || leftLast == '：'
            || rightFirst == ','
            || rightFirst == '，';
    }

    private static bool IsOpeningPunctuation(char c)
    {
        return c == '('
            || c == '（'
            || c == '['
            || c == '［'
            || c == '{'
            || c == '｛';
    }

    private static bool IsClosingPunctuation(char c)
    {
        return c == ')'
            || c == '）'
            || c == ']'
            || c == '］'
            || c == '}'
            || c == '｝';
    }

    private static (string TimeText, string Content)? TrySplitTimeAndContent(List<OcrWordInfo> orderedWords)
    {
        if (orderedWords.Count < 2) return null;

        int? bestSplitIndex = null;
        double bestGapScore = 0;
        string? bestTimeText = null;

        for (int i = 0; i < orderedWords.Count - 1; i++)
        {
            var previous = orderedWords[i];
            var current = orderedWords[i + 1];
            int gap = current.X - (previous.X + previous.Width);
            if (gap <= 0) continue;

            var leftText = BuildLineText(orderedWords.Take(i + 1).ToList());
            var normalizedTimeText = TryNormalizeTimeText(leftText);
            if (normalizedTimeText == null) continue;

            double previousCharWidth = previous.Text.Length > 0 ? (double)previous.Width / previous.Text.Length : previous.Width;
            double currentCharWidth = current.Text.Length > 0 ? (double)current.Width / current.Text.Length : current.Width;
            double typicalCharWidth = Math.Max(4.0, Math.Min(previousCharWidth, currentCharWidth));
            double gapScore = gap / typicalCharWidth;

            if (gapScore > bestGapScore)
            {
                bestGapScore = gapScore;
                bestSplitIndex = i;
                bestTimeText = normalizedTimeText;
            }
        }

        if (bestSplitIndex == null || bestGapScore < 1.8 || string.IsNullOrWhiteSpace(bestTimeText)) return null;

        string content = BuildLineText(orderedWords.Skip(bestSplitIndex.Value + 1).ToList());
        if (string.IsNullOrWhiteSpace(content)) return null;

        return (bestTimeText, content);
    }

    private static string BuildLineText(List<OcrWordInfo> lineWords)
    {
        var ordered = lineWords.OrderBy(word => word.X).ToList();
        var sb = new StringBuilder();

        for (int i = 0; i < ordered.Count; i++)
        {
            var word = ordered[i];
            if (i > 0)
            {
                var previous = ordered[i - 1];
                if (ShouldInsertSpace(previous, word)) sb.Append(' ');
            }

            sb.Append(word.Text);
        }

        return sb.ToString().Trim();
    }

    private static bool ShouldInsertSpace(OcrWordInfo previous, OcrWordInfo current)
    {
        int gap = current.X - (previous.X + previous.Width);
        if (gap <= 0) return false;

        double previousCharWidth = previous.Text.Length > 0 ? (double)previous.Width / previous.Text.Length : previous.Width;
        double currentCharWidth = current.Text.Length > 0 ? (double)current.Width / current.Text.Length : current.Width;
        double typicalCharWidth = Math.Max(4.0, Math.Min(previousCharWidth, currentCharWidth));
        return gap >= typicalCharWidth * 1.2;
    }

    private static string NormalizeOcrSpacing(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var normalized = Regex.Replace(text, @"\s+", " ").Trim();
        normalized = Regex.Replace(normalized, @"(?<=[\p{IsCJKUnifiedIdeographs}\p{IsCJKSymbolsandPunctuation}\p{IsHalfwidthandFullwidthForms}])\s+(?=[\p{IsCJKUnifiedIdeographs}\p{IsCJKSymbolsandPunctuation}\p{IsHalfwidthandFullwidthForms}])", "");
        normalized = Regex.Replace(normalized, @"([（\(\[［\{｛])\s+", "$1");
        normalized = Regex.Replace(normalized, @"\s+([）\)\]］\}｝])", "$1");
        normalized = Regex.Replace(normalized, @"\s+([，。；：！？、])", "$1");
        normalized = Regex.Replace(normalized, @"([，。；：！？、])\s+", "$1");
        return normalized;
    }

    private static string? TryNormalizeTimeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var normalized = NormalizeTimeCandidate(text);
        if (normalized == null) return null;

        var digits = new string(normalized.Where(char.IsDigit).ToArray());
        if (digits.Length == 4)
            return $"{digits[..2]}:{digits[2..4]}";

        if (digits.Length == 6)
            return $"{digits[..2]}:{digits[2..4]}:{digits[4..6]}";

        if (digits.Length is >= 7 and <= 9)
        {
            var fraction = digits[6..];
            return $"{digits[..2]}:{digits[2..4]}:{digits[4..6]}.{fraction}";
        }

        return null;
    }

    private static string? NormalizeTimeCandidate(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || c == ' ' || c == '　')
                continue;

            if (char.IsDigit(c))
            {
                sb.Append(c);
                continue;
            }

            if (c == '：' || c == '﹕' || c == '︰' || c == ':' || c == '·' || c == '•' || c == '．' || c == '.')
            {
                sb.Append(':');
                continue;
            }

            if (c == ',' || c == '，')
            {
                sb.Append('.');
                continue;
            }

            return null;
        }

        while (sb.Length > 0 && (sb[^1] == ':' || sb[^1] == '.'))
            sb.Length--;

        return sb.Length == 0 ? null : sb.ToString();
    }

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

    private static Bitmap PreprocessForOcr(Bitmap source, int upscale, OcrPreprocessMode preprocessMode)
    {
        int newW = source.Width * upscale;
        int newH = source.Height * upscale;
        var debugDir = EnsureDebugDir();
        var modePrefix = preprocessMode == OcrPreprocessMode.HighContrastBinary ? "hc" : "detail";

        SaveDebugImage(source, debugDir, $"{modePrefix}_01_original");

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

        var gray = new Bitmap(newW, newH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(gray))
        {
            var grayMatrix = new ColorMatrix(new float[][]
            {
                new float[] { 0.299f, 0.299f, 0.299f, 0, 0 },
                new float[] { 0.587f, 0.587f, 0.587f, 0, 0 },
                new float[] { 0.114f, 0.114f, 0.114f, 0, 0 },
                new float[] { 0, 0, 0, 1, 0 },
                new float[] { 0, 0, 0, 0, 1 }
            });
            using var attr = new ImageAttributes();
            attr.SetColorMatrix(grayMatrix);
            g.DrawImage(scaled, new Rectangle(0, 0, newW, newH), 0, 0, newW, newH, GraphicsUnit.Pixel, attr);
        }
        scaled.Dispose();
        SaveDebugImage(gray, debugDir, $"{modePrefix}_03_grayscale");

        var contrasted = ApplyColorContrast(gray, 3.5f);
        gray.Dispose();
        SaveDebugImage(contrasted, debugDir, $"{modePrefix}_04_contrast");

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
        var contrastMatrix = new ColorMatrix(new float[][]
        {
            new float[] { contrast, 0, 0, 0, 0 },
            new float[] { 0, contrast, 0, 0, 0 },
            new float[] { 0, 0, contrast, 0, 0 },
            new float[] { 0, 0, 0, 1, 0 },
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

    private static Bitmap ApplyOtsuBinarization(Bitmap source)
    {
        int w = source.Width, h = source.Height;
        var result = new Bitmap(w, h, PixelFormat.Format32bppArgb);

        var srcData = source.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int stride = srcData.Stride;
        int bytes = Math.Abs(stride) * h;
        var pixels = new byte[bytes];
        Marshal.Copy(srcData.Scan0, pixels, 0, bytes);
        source.UnlockBits(srcData);

        int[] hist = new int[256];
        var grayValues = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            int rowOff = y * stride;
            for (int x = 0; x < w; x++)
            {
                int px = rowOff + x * 4;
                byte gray = (byte)(pixels[px + 2] * 0.299 + pixels[px + 1] * 0.587 + pixels[px] * 0.114);
                grayValues[y * w + x] = gray;
                hist[gray]++;
            }
        }

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
                dstPixels[px] = val;
                dstPixels[px + 1] = val;
                dstPixels[px + 2] = val;
                dstPixels[px + 3] = 255;
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
        catch
        {
        }
    }

    private static async Task<SoftwareBitmap> ConvertToSoftwareBitmapAsync(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Bmp);
        ms.Position = 0;

        var randomAccessStream = ms.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
        var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

        if (softwareBitmap.BitmapPixelFormat != BitmapPixelFormat.Rgba8 && softwareBitmap.BitmapPixelFormat != BitmapPixelFormat.Gray8)
        {
            softwareBitmap = SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Rgba8);
        }

        return softwareBitmap;
    }
}
