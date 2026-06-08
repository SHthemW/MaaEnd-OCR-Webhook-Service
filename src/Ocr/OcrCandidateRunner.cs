using System.Diagnostics;
using System.Drawing;

namespace MaaEnd_Log_Retransmitter.Ocr;

internal static class OcrCandidateRunner
{
    public static async Task<OcrCandidateResult> RunAsync(
        Bitmap bitmap,
        string languageTag,
        OcrCandidateSpec spec,
        bool saveDebugImages)
    {
        var sw = Stopwatch.StartNew();
        var result = await OcrEngine.RecognizeWithWordsAsync(
            bitmap,
            languageTag,
            spec.Upscale,
            spec.PreprocessMode,
            saveDebugImages);
        sw.Stop();

        return new OcrCandidateResult(
            spec,
            result,
            OcrResultScorer.Score(result),
            sw.ElapsedMilliseconds);
    }
}
