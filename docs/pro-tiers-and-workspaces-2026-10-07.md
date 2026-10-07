# Pro 档位与团队名称修复

日期：2026-10-07。

## Pro 档位

按用户要求，区分 Pro 5x、10x、25x。当前官方定价列出 $100/$200/$500 三档；本机官方客户端代码使用 `prolite`、`pro`、`promax` 区分这三档。程序保留原始套餐代码并分别映射为 5x、10x、25x。旧 CLI 0.155.1 的 PlanType 未包含 promax，因此普通 account/read 不能作为唯一来源。

旧枚举数值 3/4 等不重排；新增档位有独立数值。没有确认依据的旧 20x 记录显示待刷新，避免凭旧记录猜成新档位。旧用户可能享有保留额度或优惠，使用额度仍以返回窗口为准，不能用公开套餐倍数推算绝对额度。

## 团队名称根因与实现

本机 CLI 的正常及 experimental schema 不包含此前依赖的 account/sessions/list。旧流程的 fallback 只有 Token 和缓存；Token 往往不带工作区名称。

新增读取官方账号详情：

- 优先 GET `https://chatgpt.com/backend-api/wham/accounts/check`，与 OpenAI 的官方 backend-client 路径相同。
- 名称缺失时读取官方桌面客户端使用的 `https://chatgpt.com/backend-api/accounts/check/v4-2023-04-27`。
- 支持 accounts 数组和 accounts 字典包裹 account 对象的形状。
- 只接受当前账号 ID 对应条目；拒绝字典键与内部 account_id 冲突，不按 default_account_id 或 account_ordering 借用名字。
- 名称和完整 plan_type 在导入、OAuth 登录保存、额度刷新中都会补全，并保存到元数据。
- HTTP 客户端不使用 Cookie、不跟随重定向；Token 只放在 HTTPS 请求头，响应正文不进入错误日志。
- 名称请求失败不丢弃已获取的有效套餐信息；保留同账号缓存名称并显示可读的诊断信息。

## 验证与限制

335 项测试通过。新增用例覆盖三档识别、旧 CLI 对 Pro 25x 的降级、多个工作区精确选择、名称变更、版本化 fallback、个人账号、错配 ID、重定向、取消和失败保留。

只读实测：本机保存的一条个人 Plus 账号，官方账号接口返回成功，StoredAccountId 和 Token 账号 ID 一致。报告只保存成功状态和名称是否存在，不包含名称、邮箱、账号 ID 或 Token。当前未保存 Team 账号，因此真实 Team 名称未进行实测。

这些属于官方客户端/开源客户端内部账号接口，并非稳定的公共 OpenAI 平台 API；已加入安全的失败回退。不能承诺凭过期或无权限的凭据读取名称。

## 来源

- [当前 Pro 定价](https://learn.chatgpt.com/docs/pricing)
- [Pro 三档及旧用户额度说明](https://help.openai.com/en/articles/9793128-about-chatgpt-pro-tiers)
- [OpenAI backend-client 路径和请求头](https://github.com/openai/codex/blob/main/codex-rs/backend-client/src/client.rs)
- [OpenAI accounts/check 数据形状](https://github.com/openai/codex/blob/main/codex-rs/backend-client/src/types.rs)
- 本机 OpenAI.Codex 26.1002.6548.0 的定价和账号读取代码：套餐标识 prolite/pro/promax，账号详情 account.plan_type 与 account.name。

公开套餐名称/价格不能证明每个账号的实际额度，界面展示实际元数据和接口窗口。
