# 官方信息核对记录

核对日期：2026-10-07（Asia/Shanghai）。

| 范围 | 核对结果 | 实现 |
|---|---|---|
| ChatGPT 套餐 | 当前为 Free、Go、Plus、Pro 5x、Pro 10x、Pro 25x、Business、Enterprise、Edu；Pro 按月价格档位区分，当前无五小时限额 | 以 `prolite` / `pro` / `promax` 区分三档；旧枚举数值不变，旧 20x 等待刷新；Team 显示 Business；未知套餐不推断 |
| 账号协议 | 本机官方 CLI 0.155.1 schema 仍包含 `prolite`、`team`，并包含 `edu_plus` / `edu_pro` | 区分协议兼容值与当前展示名称；识别 Edu 变体 |
| 额度 | Work 与 Codex 共享；窗口以 `windowDurationMins` 为准；窗口和 credits 可为空 | 实际时长标签；缺少窗口隐藏进度条；空数据不转成 0% / 100% |
| Credits | `balance` 可为空，`hasCredits` 是可用性标志 | Credits 与 CNY/USD 余额分开；可用性标志不能制造数字余额 |
| DeepSeek 模型 | `deepseek-flash` 对应当前 Flash；旧 V4 Flash 名称是兼容别名；最新 pricing/update 页继续列出 V4 Pro | 默认 ID 更新；用户保存的模型 ID 可读取；列表与名称取自官方 API |
| DeepSeek 能力 | `/models` 返回 context/output/modalities/effort；Responses 内置搜索被忽略，verbosity 无效果，summary 不生成 | 解析元数据并写入 Codex 目录；禁用被忽略的能力；未知元数据不猜测推理档位 |
| 百炼查询 | 文档示例采用 `providers=qwen` / `capabilities=TG`，分页读取 `output.models` / `output.total` | 修正查询序列化并保留分页校验；读取 model_info、features 与 inference_metadata |
| 百炼地域 | 共享域名和业务空间专属域名并存；香港也在当前地域表内 | 六个地域；可选专属域名；东京和法兰克福保留必填业务空间 |

## 来源

- [当前英文定价](https://learn.chatgpt.com/docs/pricing)
- [app-server](https://learn.chatgpt.com/docs/app-server)
- 本机 `codex app-server generate-json-schema`：CLI 0.155.1 的 `GetAccountRateLimitsResponse`。
- [DeepSeek 模型列表](https://api-docs.deepseek.com/api/list-models/)
- [DeepSeek 更新说明](https://api-docs.deepseek.com/updates/)
- [DeepSeek 当前价格](https://api-docs.deepseek.com/quick_start/pricing/)
- [DeepSeek Responses](https://api-docs.deepseek.com/guides/responses_api/)
- [百炼查询模型](https://help.aliyun.com/zh/model-studio/list-models)
- [百炼 Base URL](https://help.aliyun.com/zh/model-studio/base-url)
- [百炼模型参数](https://help.aliyun.com/en/model-studio/text-generation-model/)

## 信息优先级与限制

当前英文定价与较旧的本地化表格有差异，套餐显示采用当前英文文档。DeepSeek 初次发布新闻与后续价格/更新页也有差异，当前模型清单使用最新的价格/更新页及实时 `/models`，不依赖早期新闻。

公开定价不能证明某个账号的具体月度档位，也不能证明某个 API Key 能访问全部示例模型。界面以账号/Key/地域的返回值为准；预览是示例数据。历史发布日志、备份、旧设计图和用户自定义模型选择不作为当前官方信息来源。

核对与更改未读取用户真实 Key 或执行付费推理。模型目录在隔离的 CODEX_HOME 下由本机 CLI 的 model/list 读取成功。已添加套餐兼容、无窗口额度、动态时长、能力元数据和地域地址测试。

Pro 档位识别和独立账号详情读取补全团队名称的依据、测试与限制，见 [修复记录](pro-tiers-and-workspaces-2026-10-07.md)。
