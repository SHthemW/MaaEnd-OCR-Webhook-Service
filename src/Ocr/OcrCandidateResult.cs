namespace MaaEnd_Log_Retransmitter.Ocr;

internal sealed record OcrCandidateResult(
    OcrCandidateSpec Spec,
    OcrEngine.OcrResultData Result,
    OcrResultScore Score,
    long ElapsedMilliseconds);
