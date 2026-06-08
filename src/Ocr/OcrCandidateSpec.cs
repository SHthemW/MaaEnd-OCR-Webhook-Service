namespace MaaEnd_Log_Retransmitter.Ocr;

internal sealed record OcrCandidateSpec(
    string Name,
    int Upscale,
    OcrEngine.OcrPreprocessMode PreprocessMode);
