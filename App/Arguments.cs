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
    public string WebhookUrl { get; set; } = "";
    public string WebhookBody { get; set; } = "{\"content\":\"__CONTENT__\"}";
    public string WebhookContentType { get; set; } = "application/json";
    public int WebhookTimeoutMs { get; set; } = 5000;
    public string WebhookMode { get; set; } = RealtimeWebhookMode;
    public int WebhookPushCacheSeconds { get; set; }

    private const string RealtimeWebhookMode = "Realtime";
    private const string SummaryWebhookMode = "Summary";
    private const string AllWebhookMode = "All";

    public bool ShouldPushRealtime => WebhookMode == RealtimeWebhookMode || WebhookMode == AllWebhookMode;
    public bool ShouldPushSummary => WebhookMode == SummaryWebhookMode || WebhookMode == AllWebhookMode;

    public string WebhookModeDisplay => WebhookMode switch
    {
        RealtimeWebhookMode => "仅实时",
        SummaryWebhookMode => "仅总结",
        AllWebhookMode => "全部",
        _ => WebhookMode
    };

    public bool Validate()
    {
        return TryValidate(out _);
    }

    public bool TryValidate(out List<string> errors)
    {
        errors = [];

        if (string.IsNullOrWhiteSpace(WindowTitle)) errors.Add("WindowTitle 不能为空");
        if (string.IsNullOrWhiteSpace(SearchText)) errors.Add("SearchText 不能为空");
        if (string.IsNullOrWhiteSpace(Language)) errors.Add("Language 不能为空");

        if (Retry is < 1 or > 10) errors.Add("Retry 必须在 1 到 10 之间");
        if (RetryInterval is < 100 or > 60000) errors.Add("RetryInterval 必须在 100 到 60000 毫秒之间");
        if (RollingIntervalMs is < 500 or > 60000) errors.Add("RollingIntervalMs 必须在 500 到 60000 毫秒之间");

        if (string.IsNullOrWhiteSpace(WebhookUrl))
        {
            errors.Add("WebhookUrl 不能为空");
        }
        else if (!IsValidHttpUrl(WebhookUrl))
        {
            errors.Add("WebhookUrl 必须是合法的 http/https URL");
        }

        if (string.IsNullOrWhiteSpace(WebhookBody))
        {
            errors.Add("WebhookBody 不能为空");
        }
        else if (!WebhookBody.Contains("__CONTENT__", StringComparison.Ordinal))
        {
            errors.Add("WebhookBody 必须包含 __CONTENT__ 占位符");
        }

        if (string.IsNullOrWhiteSpace(WebhookContentType)) errors.Add("WebhookContentType 不能为空");
        if (WebhookTimeoutMs is < 1000 or > 60000) errors.Add("WebhookTimeoutMs 必须在 1000 到 60000 毫秒之间");
        if (WebhookPushCacheSeconds is < 0 or > 86400) errors.Add("WebhookPushCacheSeconds 必须在 0 到 86400 秒之间，0 表示不启用");

        if (string.IsNullOrWhiteSpace(WebhookMode))
        {
            errors.Add("WebhookMode 不能为空");
        }
        else if (TryNormalizeWebhookMode(WebhookMode, out var webhookMode))
        {
            WebhookMode = webhookMode;
        }
        else
        {
            errors.Add("WebhookMode 必须是 Realtime、Summary 或 All");
        }

        return errors.Count == 0;
    }

    public static bool TryNormalizeWebhookMode(string? value, out string normalized)
    {
        normalized = RealtimeWebhookMode;
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

    private static bool IsValidHttpUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}
