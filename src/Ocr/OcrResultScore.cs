namespace MaaEnd_Log_Retransmitter.Ocr;

internal sealed record OcrResultScore(
    int Value,
    string Reason,
    IReadOnlyList<string> Details);
