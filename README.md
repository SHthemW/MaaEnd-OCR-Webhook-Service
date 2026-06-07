<p align="center">
  <img src="res/title.jpg" alt="MaaEnd OCR Webhook Service" width="60%">
</p>

<p align="center">
  简体中文 | <a href="README.en.md">English</a>
</p>

# MaaEnd OCR Webhook Service

MaaEnd OCR Webhook Service 是一个 Windows 命令行工具，用于监听目标程序窗口，通过 OCR 读取 MaaEnd 风格的运行日志，并把识别到的日志推送到 Webhook。它适用于日志只显示在 GUI 窗口中，但需要被采集、缓存并转发到外部服务的场景。

由于 MaaEnd 的官方仓库对 Webhook 推送支持不佳，仅支持少数平台可用的 HTTP POST，且短期内没有此内容的开发计划，见 [MaaEnd issue #1421](https://github.com/MaaEnd/MaaEnd/issues/1421)，因此开发了本程序。

相比 [MaaEnd-Webhook-Retransmitter](https://github.com/SHthemW/MaaEnd-Webhook-Retransmitter)，基于 OCR 识别的推送器能捕捉更多信息，也提供了更多自由度，因此后续主要维护本仓库。

## 功能

- 按窗口标题查找目标窗口，支持精确匹配和模糊匹配。
- 截取目标窗口，并通过 OCR 定位配置的锚点文本。
- 进入滚动 OCR 模式，提取锚点文本下方的新日志行。
- 支持实时推送、结束后汇总推送，或两者同时启用。
- 支持带 `__CONTENT__` 占位符的 Webhook Body 模板。
- 交互式配置 Webhook Body 时会自动打开记事本，便于编辑多行内容。
- 支持按秒缓存实时 Webhook 推送。
- 关键日志必定单独推送。包含 `任务` 或 `重要通知` 的日志会先触发已有缓存立即推送并清空，随后该关键日志单独推送。
- 可保存调试截图和 OCR 预处理图片，便于排查识别问题。

## 运行要求

- Windows 10 19041 或更高版本。
- 开发构建需要 .NET 8 SDK；也可以直接运行已发布的可执行文件。
- 系统需要安装配置语言对应的 Windows OCR 语言支持，例如 `zh-Hans`。
- 一个可接收当前 Body 和 Content-Type 的 Webhook 地址。

## 构建

在仓库目录中运行：

```powershell
dotnet build -p:UseAppHost=false
```

如果已有 `MaaEnd-Log-Retransmitter.exe` 实例正在运行，它可能会锁住 `bin` 目录中的文件。普通重建前请先关闭正在运行的程序。

## 运行

开发时可以运行：

```powershell
dotnet run
```

也可以运行构建后的可执行文件：

```powershell
.\bin\Debug\net8.0-windows10.0.19041.0\MaaEnd-Log-Retransmitter.exe
```

首次启动时，程序会通过交互式向导在可执行文件旁创建 `config.json`。后续启动时会自检配置文件：如果所有必需 Key 都存在且值合法，会直接开始运行；如果出现缺 Key、空值或非法值，只会依次修复受影响的配置项。

## 配置说明

`config.json` 是标准 JSON。主要字段如下：

- `WindowTitle`：目标窗口标题。
- `SearchText`：锚点文本，程序会从该文本下方开始进行滚动日志 OCR。
- `PartialMatch`：是否使用窗口标题模糊匹配。
- `SaveScreenshot`：是否保存截图和 OCR 预处理图片。
- `Retry`：初始 OCR 重试次数，范围 `1` 到 `10`。
- `RetryInterval`：重试间隔，单位毫秒，范围 `100` 到 `60000`。
- `RollingIntervalMs`：滚动 OCR 间隔，单位毫秒，范围 `500` 到 `60000`。
- `CaseSensitive`：锚点文本匹配是否区分大小写。
- `Language`：Windows OCR 语言标签，例如 `zh-Hans`。
- `WebhookUrl`：Webhook 地址，必须是 `http` 或 `https`。
- `WebhookBody`：Webhook 请求体模板，必须包含 `__CONTENT__`。
- `WebhookContentType`：Webhook 请求的 Content-Type，通常是 `application/json`。
- `WebhookTimeoutMs`：Webhook 超时时间，单位毫秒，范围 `1000` 到 `60000`。
- `WebhookMode`：Webhook 推送模式，可选 `Realtime`、`Summary`、`All`。
- `WebhookPushCacheSeconds`：实时推送缓存时间，单位秒。`0` 表示不启用缓存。

### WebhookMode 可选值

- `Realtime`：滚动识别时实时推送每条被接受的 OCR 日志。
- `Summary`：程序停止时汇总推送已收集的 OCR 日志。
- `All`：同时启用实时推送和汇总推送。

### Webhook Body 模板

Body 模板必须包含 `__CONTENT__`。程序发送前会把它替换为最终 OCR 日志内容。

示例：

```json
{
  "content": "__CONTENT__"
}
```

由于 Webhook Body 通常是多行内容，交互式向导会打开 `webhook-body-template.json`。请在记事本中编辑模板，保存并关闭记事本后，CLI 会继续执行。

## 运行流程

1. 加载或创建 `config.json`。
2. 校验配置，如果存在非法项则只修复对应字段。
3. 查找配置的目标窗口。
4. 截取窗口并执行第一次 OCR。
5. 定位 `SearchText`。
6. 裁剪匹配文本下方区域，进入滚动 OCR 模式。
7. 输出被接受的日志，并根据 `WebhookMode` 推送 Webhook。
8. 按 `Ctrl+C` 停止滚动 OCR，并刷新尚未发送的缓存推送。

## 排查问题

- 如果 OCR 找不到 `SearchText`，可以启用 `SaveScreenshot` 并检查保存的图片。
- 如果截图内容异常，请确认目标窗口可见且没有最小化。
- 如果 Webhook 推送失败，请检查 `WebhookUrl`、`WebhookContentType`、`WebhookBody` 和超时时间。
- 如果 OCR 语言不可用，请安装对应的 Windows OCR 语言包。
