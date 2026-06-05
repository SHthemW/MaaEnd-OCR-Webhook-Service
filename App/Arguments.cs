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
    public string WebhookMode { get; set; } = SummaryWebhookMode;

    private const string RealtimeWebhookMode = "Realtime";
    private const string SummaryWebhookMode = "Summary";
    private const string AllWebhookMode = "All";

    public bool ShouldPushRealtime => WebhookEnabled && (WebhookMode == RealtimeWebhookMode || WebhookMode == AllWebhookMode);
    public bool ShouldPushSummary => WebhookEnabled && (WebhookMode == SummaryWebhookMode || WebhookMode == AllWebhookMode);

    public string WebhookModeDisplay => WebhookMode switch
    {
        RealtimeWebhookMode => "仅实时",
        SummaryWebhookMode => "仅总结",
        AllWebhookMode => "全部",
        _ => WebhookMode
    };

    public bool Validate()
    {
        if (string.IsNullOrWhiteSpace(WindowTitle)) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return false;
        if (string.IsNullOrWhiteSpace(Language)) Language = "zh-Hans";
        if (string.IsNullOrWhiteSpace(WebhookContentType)) WebhookContentType = "application/json";
        if (!TryNormalizeWebhookMode(WebhookMode, out var webhookMode))
        {
            if (WebhookEnabled) return false;
            webhookMode = SummaryWebhookMode;
        }

        WebhookMode = webhookMode;
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

    public static bool TryNormalizeWebhookMode(string? value, out string normalized)
    {
        normalized = SummaryWebhookMode;
        if (string.IsNullOrWhiteSpace(value)) return true;

        var text = value.Trim();
        var key = text.Replace(" ", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        normalized = key switch
        {
            "realtime" or "live" or "onlyrealtime" or "realtimeonly" or "仅实时" or "实时" => RealtimeWebhookMode,
            "summary" or "final" or "onlysummary" or "summaryonly" or "仅总结" or "总结" => SummaryWebhookMode,
            "all" or "both" or "全部" or "全量" => AllWebhookMode,
            _ => ""
        };

        return !string.IsNullOrWhiteSpace(normalized);
    }
}
