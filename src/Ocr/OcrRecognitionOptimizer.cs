using System.Drawing;
using MaaEnd_Log_Retransmitter.Infrastructure;

namespace MaaEnd_Log_Retransmitter.Ocr;

internal static class OcrRecognitionOptimizer
{
    private static readonly OcrCandidateSpec[] RollingSpecs =
    [
        new("detail", 8, OcrEngine.OcrPreprocessMode.DetailPreserving),
        new("high-contrast", 8, OcrEngine.OcrPreprocessMode.HighContrastBinary)
    ];

    public static async Task<OcrCandidateResult> RecognizeRollingAsync(
        Bitmap bitmap,
        string languageTag,
        bool saveDebugImages)
    {
        var candidates = new List<OcrCandidateResult>(RollingSpecs.Length);
        foreach (var spec in RollingSpecs)
        {
            var candidate = await OcrCandidateRunner.RunAsync(bitmap, languageTag, spec, saveDebugImages);
            candidates.Add(candidate);
            Logger.Debug(
                $"滚动 OCR 候选 {candidate.Spec.Name}: score={candidate.Score.Value}, {candidate.Score.Reason}, elapsed={candidate.ElapsedMilliseconds}ms, chars={candidate.Result.Text.Length}");
            foreach (var detail in candidate.Score.Details)
            {
                Logger.Debug($"滚动 OCR 候选 {candidate.Spec.Name} 评分: {detail}");
            }
        }

        var accepted = candidates
            .OrderByDescending(candidate => candidate.Score.Value)
            .ThenBy(candidate => candidate.ElapsedMilliseconds)
            .First();
        var rejected = candidates
            .Where(candidate => !ReferenceEquals(candidate, accepted))
            .Select(candidate => $"{candidate.Spec.Name}=score {candidate.Score.Value}, elapsed {candidate.ElapsedMilliseconds}ms");

        Logger.Debug($"滚动 OCR 采纳 {accepted.Spec.Name}: score={accepted.Score.Value}, elapsed={accepted.ElapsedMilliseconds}ms; 未采纳: {string.Join("; ", rejected)}");
        Logger.Debug("滚动 OCR 采纳标准: 分数最高优先，分数相同选择耗时更短的候选");
        return accepted;
    }
}
