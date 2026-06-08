namespace MaaEnd_Log_Retransmitter.App;

internal sealed class Arguments
{
    public string WindowTitle { get; set; } = "";
    public string SearchText { get; set; } = "";
    public bool PartialMatch { get; set; }
    public int Retry { get; set; } = 1;
    public int RetryInterval { get; set; } = 1000;
    public int RollingIntervalMs { get; set; } = 3000;
    public bool CaseSensitive { get; set; }
    public string Language { get; set; } = "zh-Hans";
    public string WebhookUrl { get; set; } = "";
    public string WebhookBody { get; set; } = "{\"time\":\"__TIME__\",\"content\":\"__CONTENT__\"}";
    public string WebhookContentType { get; set; } = "application/json";
    public int WebhookTimeoutMs { get; set; } = 5000;
    public string WebhookMode { get; set; } = RealtimeWebhookMode;
    public int WebhookPushCacheSeconds { get; set; }

    private const string RealtimeWebhookMode = "Realtime";
    private const string SummaryWebhookMode = "Summary";
    private const string AllWebhookMode = "All";

    public bool ShouldPushRealtime => WebhookMode == RealtimeWebhookMode || WebhookMode == AllWebhookMode;
    public bool ShouldPushSummary => WebhookMode == SummaryWebhookMode || WebhookMode == AllWebhookMode;
    public bool HasWebhookUrl => IsValidHttpUrl(WebhookUrl);

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
        => TryValidate(allowEmptyWebhook: false, out errors);

    public bool TryValidate(bool allowEmptyWebhook, out List<string> errors)
    {
        var invalidFields = GetInvalidFields(allowEmptyWebhook);
        errors = invalidFields.Select(GetValidationMessage).ToList();

        if (errors.Count == 0 && TryNormalizeWebhookMode(WebhookMode, out var webhookMode))
        {
            WebhookMode = webhookMode;
        }

        return errors.Count == 0;
    }

    public IReadOnlyList<string> GetInvalidFields()
        => GetInvalidFields(allowEmptyWebhook: false);

    public IReadOnlyList<string> GetInvalidFields(bool allowEmptyWebhook)
    {
        var fields = new List<string>();

        if (string.IsNullOrWhiteSpace(WindowTitle)) fields.Add(nameof(WindowTitle));
        if (string.IsNullOrWhiteSpace(SearchText)) fields.Add(nameof(SearchText));
        if (string.IsNullOrWhiteSpace(Language)) fields.Add(nameof(Language));

        if (Retry is < 1 or > 10) fields.Add(nameof(Retry));
        if (RetryInterval is < 100 or > 60000) fields.Add(nameof(RetryInterval));
        if (RollingIntervalMs is < 500 or > 60000) fields.Add(nameof(RollingIntervalMs));

        var webhookUrlIsEmpty = string.IsNullOrWhiteSpace(WebhookUrl);
        var webhookDisabled = allowEmptyWebhook && webhookUrlIsEmpty;
        if (!webhookDisabled)
        {
            if (webhookUrlIsEmpty || !IsValidHttpUrl(WebhookUrl)) fields.Add(nameof(WebhookUrl));
            if (string.IsNullOrWhiteSpace(WebhookBody) || !WebhookBody.Contains("__CONTENT__", StringComparison.Ordinal)) fields.Add(nameof(WebhookBody));
            if (string.IsNullOrWhiteSpace(WebhookContentType)) fields.Add(nameof(WebhookContentType));
            if (WebhookTimeoutMs is < 1000 or > 60000) fields.Add(nameof(WebhookTimeoutMs));
            if (WebhookPushCacheSeconds is < 0 or > 86400) fields.Add(nameof(WebhookPushCacheSeconds));
            if (string.IsNullOrWhiteSpace(WebhookMode) || !TryNormalizeWebhookMode(WebhookMode, out _)) fields.Add(nameof(WebhookMode));
        }

        return fields;
    }

    public static string GetValidationMessage(string fieldName)
    {
        return fieldName switch
        {
            nameof(WindowTitle) => "WindowTitle 不能为空",
            nameof(SearchText) => "SearchText 不能为空",
            nameof(Language) => "Language 不能为空",
            nameof(Retry) => "Retry 必须在 1 到 10 之间",
            nameof(RetryInterval) => "RetryInterval 必须在 100 到 60000 毫秒之间",
            nameof(RollingIntervalMs) => "RollingIntervalMs 必须在 500 到 60000 毫秒之间",
            nameof(WebhookUrl) => "WebhookUrl 必须是合法且非空的 http/https URL",
            nameof(WebhookBody) => "WebhookBody 不能为空且必须包含 __CONTENT__ 占位符",
            nameof(WebhookContentType) => "WebhookContentType 不能为空",
            nameof(WebhookTimeoutMs) => "WebhookTimeoutMs 必须在 1000 到 60000 毫秒之间",
            nameof(WebhookPushCacheSeconds) => "WebhookPushCacheSeconds 必须在 0 到 86400 秒之间，0 表示不启用",
            nameof(WebhookMode) => "WebhookMode 必须是 Realtime、Summary 或 All",
            nameof(PartialMatch) => "PartialMatch 必须是布尔值",
            nameof(CaseSensitive) => "CaseSensitive 必须是布尔值",
            _ => $"{fieldName} 配置无效"
        };
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
