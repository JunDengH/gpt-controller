# GPT Controller

[English](README.en.md) | 简体中文

一个面向 Windows 11 的本地优先 Codex 连接管理器。GPT Controller 通过官方 OAuth
连接 ChatGPT，使用 Windows DPAPI 保护静态凭据，以可回滚的切换流程管理连接，并通过
Responses API 兼容 DeepSeek 和阿里云百炼千问等提供商。

项目不收集密码，不把 API Key 写入 `config.toml`、日志或备份，也不复制浏览器 Cookie、
浏览器用户目录、项目、插件或本地任务历史。

> 本项目是非官方开源工具，与 OpenAI 无隶属或背书关系。

## 安全凭据管理

- ChatGPT 账号通过官方 Codex app-server 和浏览器 OAuth 添加，应用不接触密码。
- 非活动 ChatGPT 认证档案以及 DeepSeek、千问 API Key 均使用 Windows DPAPI
  `CurrentUser` 加密，仅当前 Windows 用户可以解密。
- 凭据与邮箱、套餐、公司名称、余额、额度、模型缓存等元数据分离存储。
- API Key 按提供商隔离，由随包发布的无界面凭据助手按需提供给 Codex 自定义
  Provider；明文 Key 不会进入 `config.toml`、日志或备份。
- 切换前会保存最新认证状态；切换失败时自动恢复原配置和原连接，避免留下半完成状态。
- 数据迁移会先验证旧数据，再写入新目录；旧目录完整保留为回退副本，迁移失败不会改动
  原目录。

有关明文边界、数据目录和迁移策略的完整说明，请参阅[安全边界](#安全边界)和
[安全策略](SECURITY.md)。

## 维护状态 / 路线图

**持续维护。** 当前重点：Codex Windows 兼容性、提供商互操作、凭据安全和回归覆盖。

> Actively maintained; current focus: Codex Windows compatibility, provider
> interoperability, credential safety and regression coverage.

## 功能

### 连接与提供商互操作

- 在统一界面管理 ChatGPT OAuth、DeepSeek 和阿里云百炼千问连接。
- 在明确确认后切换 Chat、Work、Codex 共用的账号登录态；失败时自动恢复并重启原连接。
- 使用官方 `https://api.deepseek.com/` 和 Responses API，支持
  `deepseek-v4-flash` / `deepseek-v4-pro`。
- 支持阿里云百炼北京、新加坡、弗吉尼亚、法兰克福、东京地域，动态读取当前账号可用的
  `qwen*` 模型，并在应用前验证 Responses Function Call 兼容性。
- DeepSeek 与千问共用可搜索的模型选择窗口；千问自动刷新不会发起推理请求。

### 状态与可观测性

- 并列显示 5 小时与周限额的剩余比例、进度、各自重置时间和数据是否过期。
- 显示 Free、Plus、Pro 5x、Pro 20x、Team、Business、Enterprise、Edu；工作区
  套餐按当前账号精确显示组织名称，无法确认名称时显示“组织名称未知”；非工作区账号
  仅显示邮箱，不追加个人账号标签。
- 显示 DeepSeek CNY 余额，并提供需要明确确认的最小 Responses 测试。
- 主窗口展示当前连接和状态；系统托盘提供当前连接、打开和退出入口。

## 系统要求

- Windows 11 x64。
- 从 Microsoft Store/MSIX 安装的当前 ChatGPT Windows 客户端。
- ChatGPT managed OAuth 账号，或一个 DeepSeek / 阿里云百炼按量付费 API Key。
- 模型 API 连接要求 Codex CLI 0.146.0 或更高版本。
- Token Plan、Coding Plan、自定义代理、同供应商多 Key、Chat Completions 不在当前支持
  范围。

## 安全边界

保存的账号档案位于：

```text
%LOCALAPPDATA%\GptController
```

ChatGPT、DeepSeek 与千问凭据文件都使用 DPAPI `CurrentUser` 加密。元数据（邮箱、
套餐、公司名称、余额、额度和模型缓存）与凭据分离。API Key 只由随包发布的凭据助手
输出给 Codex 自定义 Provider，不会写入 `config.toml`。官方客户端当前使用的：

```text
%USERPROFILE%\.codex\auth.json
```

通过安装器卸载时，程序会先恢复由 GPT Controller 接管前的 Codex 配置。如果检测到
并发切换、外部配置冲突或备份不完整，卸载会中止并保留程序文件，避免留下指向已删除
凭据助手的 Provider 配置。

必须保持官方可读格式，因此不会由本软件额外加密。

从 1.1.x 首次升级时，程序会先验证旧数据并将凭据重新加密到新目录。原目录：

```text
%LOCALAPPDATA%\GptAccountManager
```

会完整保留作为回退副本，本版本不会自动删除。迁移失败时程序会停止启动，并保持旧
目录不变；运行缓存、日志和临时文件不会迁移。

本软件不会复制 ChatGPT Chromium Cookies、浏览器用户目录、项目、插件或本地任务
历史。本地项目数据在账号之间共用，云端数据由切换后的官方账号隔离。

## 官方 Codex 运行时

MSIX `WindowsApps` 内的 `codex.exe` 不允许普通外部程序直接执行。应用会读取用户
已安装的官方客户端版本，将该开源运行时的当前二进制副本复制到当前用户专属的：

```text
%LOCALAPPDATA%\GptController\runtime
```

副本按源文件版本和 SHA-256 标识，仅用于启动官方 app-server；应用不会修改
`WindowsApps` 或重新分发该二进制。

## 使用

1. 点击“添加连接”，导入当前 ChatGPT 登录状态或通过 OAuth 添加账号。
2. 也可选择“添加 DeepSeek API”或“添加千问 API”。千问需选择地域；除弗吉尼亚外
   还需填写业务空间 ID。
3. 在左侧账号卡片查看 ChatGPT 状态，在右侧 API 卡片查看提供商状态与当前模型。
4. 点击 API 卡片中的模型按钮打开统一选择窗口。千问会动态获取模型，并在应用前发送
   一次明确提示费用的最小 Function Call 验证。
5. 点击“切换”。如果 ChatGPT 正在运行，确认关闭和重启。

切换会中断正在运行的任务。程序默认取消，只有明确确认后才会关闭客户端。不同认证
组的历史记录可能暂时隐藏，但不会被删除；切回原提供商后会重新显示。

协议与配置依据：[DeepSeek Responses API](https://api-docs.deepseek.com/zh-cn/guides/responses_api/)、
[DeepSeek 接入 Codex](https://api-docs.deepseek.com/zh-cn/quick_start/agent_integrations/codex/)、
[千问 Responses API](https://help.aliyun.com/zh/model-studio/qwen-api-via-openai-responses)
和 [Codex 自定义模型 Provider](https://learn.chatgpt.com/docs/config-file/config-advanced#custom-model-providers)。

## 从源码构建

需要 .NET 10 SDK：

```powershell
dotnet restore GptController.slnx
dotnet build GptController.slnx -c Release
dotnet run --project src\GptController\GptController.csproj
```

生成便携版和安装包。版本号统一读取仓库根目录的 `Version.props`：

```powershell
.\scripts\package.ps1
```

如果 PATH 中存在 Inno Setup `ISCC.exe`，脚本还会生成安装包。脚本也会从 Windows
卸载注册表定位自定义目录中的 Inno Setup。

版本号、分支和标签的发布规范见 [RELEASING.md](RELEASING.md)，版本变更记录见
[CHANGELOG.md](CHANGELOG.md)。

## 额度与会员数据

额度通过官方 app-server 的 `account/rateLimits/read` 获取。程序优先使用
`rateLimitsByLimitId.codex`，按窗口时长识别约 300 分钟的 5 小时限额和约
10,080 分钟的周限额，并分别计算：

```text
剩余百分比 = 100 - usedPercent
```

会员映射：

| 原始值 | 显示 |
|---|---|
| `free`, `guest` | Free |
| `plus` | Plus |
| `prolite`, `pro_lite`, `pro-lite` | Pro 5x |
| `pro` | Pro 20x |
| `team` | Team |
| `business`, `chatgpt_business`, `self_serve_business*` | Business |
| `enterprise`, `chatgpt_enterprise`, `hc`, `ent26`, `enterprise_cbp_*` | Enterprise |
| `education`, `edu`, `chatgpt_edu` | Edu |

未知套餐不会被猜测。工作区名称优先取自官方 app-server 当前会话，并只按当前账号 ID
精确匹配；旧版 app-server 不支持该能力时回退到令牌声明和同账号缓存。网络或协议失败
时保留最后一次成功数据并标记为过期。

## 许可证

[MIT](LICENSE)。研究与互操作参考见
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
