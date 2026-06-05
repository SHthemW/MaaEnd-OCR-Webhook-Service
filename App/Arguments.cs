namespace MaaEnd_Log_Retransmitter.App;

internal sealed class Arguments
{
    public string WindowTitle { get; set; } = "";
    public string SearchText { get; set; } = "";
    public bool PartialMatch { get; set; }
    public bool SaveScreenshot { get; set; }
    public int Retry { get; set; } = 1;
    public int RetryInterval { get; set; } = 1000;
    public int RollingIntervalMs { get; set; } = 3000;
    public bool CaseSensitive { get; set; }
    public string Language { get; set; } = "zh-Hans";
    public bool WebhookEnabled { get; set; }
    public string WebhookUrl { get; set; } = "";
    public string WebhookBody { get; set; } = "{\"content\":\"__CONTENT__\"}";
    public string WebhookContentType { get; set; } = "application/json";
    public int WebhookTimeoutMs { get; set; } = 5000;

    public bool Validate()
    {
        if (string.IsNullOrWhiteSpace(WindowTitle)) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return false;
        if (string.IsNullOrWhiteSpace(Language)) Language = "zh-Hans";
        if (string.IsNullOrWhiteSpace(WebhookContentType)) WebhookContentType = "application/json";
        Retry = Math.Clamp(Retry, 1, 10);
        RetryInterval = Math.Clamp(RetryInterval, 100, 60000);
        RollingIntervalMs = Math.Clamp(RollingIntervalMs, 500, 60000);
        WebhookTimeoutMs = Math.Clamp(WebhookTimeoutMs, 1000, 60000);
        if (WebhookEnabled)
        {
            if (string.IsNullOrWhiteSpace(WebhookUrl)) return false;
            if (string.IsNullOrWhiteSpace(WebhookBody)) return false;
            if (!WebhookBody.Contains("__CONTENT__", StringComparison.Ordinal)) return false;
        }

        return true;
    }
}