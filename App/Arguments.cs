namespace MaaEnd_Log_Retransmitter.App;

internal sealed class Arguments
{
    public string WindowTitle { get; set; } = "";
    public string SearchText { get; set; } = "";
    public bool PartialMatch { get; set; }
    public bool SaveScreenshot { get; set; }
    public int Retry { get; set; } = 1;
    public int RetryInterval { get; set; } = 1000;
    public bool CaseSensitive { get; set; }
    public string Language { get; set; } = "zh-Hans";

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