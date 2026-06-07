<p align="center">
  <img src="res/title.jpg" alt="MaaEnd OCR Webhook Service" width="60%">
</p>

<p align="center">
  <a href="README.md">简体中文</a> | English
</p>

# MaaEnd OCR Webhook Service

MaaEnd OCR Webhook Service is a Windows command line tool that watches a target application window, reads MaaEnd-style runtime logs with OCR, and forwards detected log lines to a Webhook endpoint. It is designed for workflows where MaaEnd logs are visible in a GUI window but need to be collected, buffered, and pushed to external services.

This program was created because the official MaaEnd repository has limited Webhook push support, currently relying on HTTP POST that is only usable on a small number of platforms, and there is no short-term development plan for broader notification support. See [MaaEnd issue #1421](https://github.com/MaaEnd/MaaEnd/issues/1421).

Compared with [MaaEnd-Webhook-Retransmitter](https://github.com/SHthemW/MaaEnd-Webhook-Retransmitter), this OCR-based pusher can capture more information and offers more flexibility, so this repository will be the primary maintained implementation going forward.

## Features

- Finds a target window by title, with exact or partial title matching.
- Captures the window and locates a configured anchor text with OCR.
- Enters rolling OCR mode and extracts new log lines below the anchor text.
- Sends realtime log lines, a summary after exit, or both.
- Supports Webhook body templates with the `__CONTENT__` placeholder.
- Opens Notepad for multi-line Webhook body editing during interactive setup.
- Can cache realtime Webhook pushes for a configured number of seconds.
- Keeps critical logs as standalone pushes. Logs containing `任务` or `重要通知` flush pending cached logs first, then push the critical log separately.
- Can save debug screenshots and OCR preprocessing images for troubleshooting.

## Requirements

- Windows 10 19041 or later.
- .NET 8 SDK for development builds, or a published executable.
- Windows OCR language support for the configured language, such as `zh-Hans`.
- A Webhook endpoint that accepts the configured body and content type.

## Build

From this repository:

```powershell
dotnet build -p:UseAppHost=false
```

If an existing `MaaEnd-Log-Retransmitter.exe` instance is running, it may lock files in `bin`. Stop the running instance before a normal rebuild.

## Run

During development:

```powershell
dotnet run
```

Or run the built executable:

```powershell
.\bin\Debug\net8.0-windows10.0.19041.0\MaaEnd-Log-Retransmitter.exe
```

On first launch, the program creates `config.json` next to the executable through an interactive wizard. On later launches, it self-checks the configuration. If all required keys are present and valid, it starts immediately. If a key is missing, empty, or invalid, only the affected fields are repaired through the wizard.

## Configuration

`config.json` is standard JSON. Important fields:

- `WindowTitle`: target window title.
- `SearchText`: anchor text that marks where rolling log OCR should start.
- `PartialMatch`: `true` to match a partial window title.
- `SaveScreenshot`: `true` to save screenshots and OCR preprocessing images.
- `Retry`: number of initial OCR retries, from `1` to `10`.
- `RetryInterval`: retry interval in milliseconds, from `100` to `60000`.
- `RollingIntervalMs`: rolling OCR interval in milliseconds, from `500` to `60000`.
- `CaseSensitive`: `true` for case-sensitive anchor text matching.
- `Language`: Windows OCR language tag, such as `zh-Hans`.
- `WebhookUrl`: target Webhook URL. Must be `http` or `https`.
- `WebhookBody`: request body template. It must contain `__CONTENT__`.
- `WebhookContentType`: Webhook request content type, usually `application/json`.
- `WebhookTimeoutMs`: Webhook timeout in milliseconds, from `1000` to `60000`.
- `WebhookMode`: `Realtime`, `Summary`, or `All`.
- `WebhookPushCacheSeconds`: realtime push cache time in seconds. `0` disables caching.

### Webhook Modes

- `Realtime`: push each accepted OCR log line during rolling recognition.
- `Summary`: push collected OCR log lines when the program stops.
- `All`: use both realtime and summary push modes.

### Webhook Body Template

The body template must include `__CONTENT__`. The program replaces it with the final OCR log content before sending.

Example:

```json
{
  "content": "__CONTENT__"
}
```

Because Webhook bodies are usually multi-line, the interactive wizard opens `webhook-body-template.json` in Notepad. Edit the template, save it, close Notepad, and the CLI will continue.

## Runtime Flow

1. Load or create `config.json`.
2. Validate the configuration and repair only invalid fields if needed.
3. Find the configured target window.
4. Capture the window and run initial OCR.
5. Locate `SearchText`.
6. Crop the area below the matched text and enter rolling OCR mode.
7. Print accepted log lines and push Webhook messages according to `WebhookMode`.
8. Press `Ctrl+C` to stop rolling OCR and flush pending cached pushes.

## Troubleshooting

- If OCR cannot find `SearchText`, enable `SaveScreenshot` and inspect the saved images.
- If screenshots look incorrect, make sure the target window is visible and not minimized.
- If Webhook pushes fail, check `WebhookUrl`, `WebhookContentType`, `WebhookBody`, and the timeout value.
- If the configured OCR language is unavailable, install the corresponding Windows OCR language package.
