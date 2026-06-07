<p align="center">
  <img src="res/title.jpg" alt="MaaEnd OCR Webhook Service" width="100%">
</p>

<p align="center">
  简体中文 | <a href="README.en.md">English</a>
</p>

# MaaEnd OCR Webhook Service

MaaEnd OCR Webhook Service 是一个 Windows 命令行工具，用于监听目标程序窗口，通过 OCR 读取 MaaEnd 风格的运行日志，并把识别到的日志推送到 Webhook。它适用于日志只显示在 GUI 窗口中，但需要被采集、缓存并转发到外部服务的场景。

由于 MaaEnd 的官方仓库对 Webhook 推送支持不佳，仅支持少数平台可用的 HTTP POST，且短期内没有此内容的开发计划，见 [MaaEnd issue #1421](https://github.com/MaaEnd/MaaEnd/issues/1421)，因此开发了本程序。

相比 [MaaEnd-Webhook-Retransmitter](https://github.com/SHthemW/MaaEnd-Webhook-Retransmitter)，基于 OCR 识别的推送器能捕捉更多信息，也提供了更多自由度，因此后续主要维护本仓库。

## 主要功能

- 通过窗口标题查找目标窗口，支持精确匹配和模糊匹配。
- 通过 OCR 定位指定锚点文本，并从锚点下方开始滚动识别日志。
- 支持实时推送、结束后汇总推送，或两者同时启用。
- 支持 Webhook Body 模板，使用 `__CONTENT__` 作为日志内容占位符。
- 交互式配置 Webhook Body 时会自动打开记事本，便于编辑多行 JSON。
- 支持实时推送缓存，减少频繁 Webhook 请求。
- 关键日志必定单独推送。包含 `任务` 或 `重要通知` 的日志会先触发已有缓存立即推送并清空，随后该关键日志单独推送。
- 可保存截图和 OCR 预处理图片，用于排查识别问题。

## 快速使用

1. 下载并解压发布包。
2. 运行 `MaaEnd-Log-Retransmitter.exe`。
3. 首次运行通常只需要填写两个 Webhook 项：
   - `WebhookUrl`
   - `WebhookBody`
4. 保存后程序会直接开始识别 MaaEnd 日志并推送。
5. 按 `Ctrl+C` 停止滚动识别；如果存在未发送的缓存推送，程序会在退出前发送。

发布包已包含默认 `config.json`，默认会查找标题中包含 `MaaEnd` 的窗口，并从 `运行日志` 下方开始识别。后续启动时，程序会自动检查配置；如果配置完整且合法，会直接开始运行，不再要求确认。

## 首次配置

默认配置已经覆盖大多数 MaaEnd 使用场景。用户第一次使用时重点配置下面两项即可：

- `WebhookUrl`：接收日志推送的 HTTP/HTTPS 地址。
- `WebhookBody`：推送请求体模板，必须包含 `__CONTENT__`。

Webhook Body 通常是多行 JSON。配置到此项时，程序会打开 `webhook-body-template.json`，请在记事本中编辑模板，保存并关闭记事本后，CLI 会继续执行。

示例 Body：

```json
{
  "content": "__CONTENT__"
}
```

如果你只是使用企业微信、Discord、Slack 或其他 Webhook 服务，通常只需要按对方平台要求调整这两个字段。

## 默认行为

- 目标窗口：标题中包含 `MaaEnd` 的窗口，例如 `MaaEnd v2.13.0`。
- 锚点文本：`运行日志`。
- 推送方式：`Realtime`，识别到新日志后实时推送。
- OCR 语言：`zh-Hans`。
- 推送缓存：默认关闭。
- 截图保存：默认关闭。

## 常用可改项

- 窗口标题：目标程序窗口标题，例如 MaaEnd 的主窗口标题。
- 查找文本：用于定位日志区域的锚点文本，例如界面中稳定出现的标题或字段。
- 匹配方式：程序会优先精确匹配窗口标题；如果输入较短，例如 `MaaEnd`，也可以匹配到 `MaaEnd v2.13.0` 这类完整标题。
- Webhook 推送方式：选择实时推送、汇总推送或全部推送。
- Webhook 推送缓存时间：单位秒，`0` 表示不启用缓存。
- 保存截图：只有排查 OCR 或截图问题时才建议打开。

## 完整配置参考

`config.json` 位于可执行文件旁边，是标准 JSON。主要字段如下：

| 字段 | 含义 | 合法值 |
| --- | --- | --- |
| `WindowTitle` | 目标窗口标题 | 非空字符串 |
| `SearchText` | 锚点文本，程序从它下方开始滚动 OCR | 非空字符串 |
| `PartialMatch` | 是否强制允许窗口标题模糊匹配；即使为 `false`，短标题也会作为完整标题的包含匹配兜底 | `true` / `false` |
| `SaveScreenshot` | 是否保存截图和 OCR 预处理图片 | `true` / `false` |
| `Retry` | 初始 OCR 重试次数 | `1` 到 `10` |
| `RetryInterval` | 初始 OCR 重试间隔，单位毫秒 | `100` 到 `60000` |
| `RollingIntervalMs` | 滚动 OCR 间隔，单位毫秒 | `500` 到 `60000` |
| `CaseSensitive` | 查找 `SearchText` 时是否区分大小写 | `true` / `false` |
| `Language` | Windows OCR 语言标签 | 例如 `zh-Hans` |
| `WebhookUrl` | Webhook 地址 | 非空 `http` / `https` URL |
| `WebhookBody` | Webhook 请求体模板 | 非空，且必须包含 `__CONTENT__` |
| `WebhookContentType` | Webhook 请求 Content-Type | 非空字符串，常用 `application/json` |
| `WebhookTimeoutMs` | Webhook 请求超时，单位毫秒 | `1000` 到 `60000` |
| `WebhookMode` | Webhook 推送模式 | `Realtime` / `Summary` / `All` |
| `WebhookPushCacheSeconds` | 实时推送缓存时间，单位秒 | `0` 到 `86400`，`0` 表示不启用 |

## Webhook 推送模式

- `Realtime`：滚动识别过程中实时推送每条新日志。
- `Summary`：程序停止时汇总推送本次收集到的日志。
- `All`：同时启用实时推送和汇总推送。

当 `WebhookPushCacheSeconds` 大于 `0` 时，普通实时日志会先进入缓存。缓存开始后到达指定秒数时，程序会把缓存内容合并成一条 Webhook 推送。

关键日志不进入缓存合并。包含 `任务` 或 `重要通知` 的日志出现时，程序会先发送并清空已有缓存，然后单独发送该关键日志。

## 使用建议

- `SearchText` 应选择在目标窗口中稳定出现、且位于日志区域上方的文本。
- `WindowTitle` 可以填写完整标题，也可以填写稳定短标题，例如 `MaaEnd`。
- 如果 OCR 找不到锚点文本，可以启用 `SaveScreenshot` 后检查保存的截图和预处理图片。
- 如果 Webhook 平台要求 JSON，请确保 `WebhookContentType` 为 `application/json`，且 `WebhookBody` 是合法 JSON。
- 如果推送过于频繁，可以设置 `WebhookPushCacheSeconds`，例如 `10` 或 `30`。

## 排查问题

- 找不到窗口：检查 `WindowTitle` 和 `PartialMatch`。
- 找不到 `SearchText`：检查截图、OCR 语言和大小写配置。
- 截图内容异常：确认目标窗口可见且没有最小化。
- Webhook 推送失败：检查 `WebhookUrl`、`WebhookBody`、`WebhookContentType` 和 `WebhookTimeoutMs`。
- OCR 语言不可用：安装对应的 Windows OCR 语言包。

## 开发与打包

普通开发构建：

```powershell
dotnet build
```

Release 打包：

```powershell
dotnet publish -c Release
```

项目已配置为 Windows x64、自包含、单文件发布。发布产物位于 `publish\`：

```text
publish\MaaEnd-Log-Retransmitter.exe
publish\MaaEnd-Log-Retransmitter.pdb
```

<p align="center">
  <img src="res/icon.png" alt="MaaEnd OCR Webhook Service Icon" width="33%">
</p>

<p align="center">
  <img src="https://img.shields.io/github/downloads/SHthemW/MaaEnd-OCR-Webhook-Service/total" alt="downloads" />
</p>
