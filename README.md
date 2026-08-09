# GPT Controller

一个面向 Windows 11 的本地优先 Codex 连接管理器：通过官方 OAuth 管理 ChatGPT
账号，也可使用 DeepSeek 或阿里云百炼千问的 Responses API，并在统一界面安全切换提供商。

> 本项目是非官方开源工具，与 OpenAI 无隶属或背书关系。

## 功能

- 一键切换 Chat、Work、Codex 共用的账号登录态。
- Windows DPAPI 加密保存非活动账号的认证档案。
- 通过官方 Codex app-server 和浏览器 OAuth 添加账号，不收集密码。
- 并列显示 5 小时与周限额的剩余比例、进度和各自重置时间。
- 显示 Free、Plus、Pro 5x、Pro 20x、Team、Business。
- Team/Business 显示当前组织名称，其他套餐固定显示“个人账号”。
- 切换前保存最新认证，失败时自动恢复并重启原账号。
- 主窗口可快速切换和查看额度；系统托盘显示当前连接并提供打开、退出入口。
- 使用 `deepseek-v4-flash` / `deepseek-v4-pro`、官方
  `https://api.deepseek.com/` 和 Responses API。
- 显示 DeepSeek CNY 余额，并提供需要明确确认的最小 Responses 测试。
- 支持阿里云百炼北京、新加坡、弗吉尼亚、法兰克福、东京地域，动态读取当前
  账号可用的 `qwen*` 模型，并在应用前验证 Responses Function Call 兼容性。
- DeepSeek 与千问共用可搜索的模型选择窗口；千问自动刷新不会发起网络或推理请求。
- API Key 通过按供应商隔离的 DPAPI 存储加密保存，Codex 通过无界面凭据助手按需读取，
  `config.toml`、日志和备份中不写入明文 Key。

## 系统要求

- Windows 11 x64。
- 从 Microsoft Store/MSIX 安装的当前 ChatGPT Windows 客户端。
- ChatGPT managed OAuth 账号，或一个 DeepSeek / 阿里云百炼按量付费 API Key。
- 模型 API 连接要求 Codex CLI 0.146.0 或更高版本。
- Token Plan、Coding Plan、自定义代理、同供应商多 Key、Chat Completions 不在
  当前支持范围。

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
WindowsApps 或重新分发该二进制。

## 使用

1. 点击“添加连接”，导入当前 ChatGPT 登录状态或通过 OAuth 添加账号。
2. 也可选择“添加 DeepSeek API”或“添加千问 API”。千问需选择地域；除弗吉尼亚外
   还需填写业务空间 ID。
3. 在左侧账号卡片查看 ChatGPT 配额，在右侧 API 卡片查看供应商状态与当前模型。
4. 点击 API 卡片中的模型按钮打开统一选择窗口。千问会动态获取模型，并在应用前发送
   一次明确提示费用的最小 Function Call 验证。
5. 点击“切换”。如果 ChatGPT 正在运行，确认关闭和重启。

切换会中断正在运行的任务。程序默认取消，只有明确确认后才会关闭客户端。不同认证
组的历史记录可能暂时隐藏，但不会被删除；切回原提供商后会重新显示。

协议与配置依据：[DeepSeek Responses API](https://api-docs.deepseek.com/zh-cn/guides/responses_api/)、
[DeepSeek 接入 Codex](https://api-docs.deepseek.com/zh-cn/quick_start/agent_integrations/codex/)
、[千问 Responses API](https://help.aliyun.com/zh/model-studio/qwen-api-via-openai-responses)
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

如果 PATH 中存在 Inno Setup `ISCC.exe`，脚本还会生成安装包。
脚本也会从 Windows 卸载注册表定位自定义目录中的 Inno Setup。

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
| `business` | Business |

未知套餐不会被猜测。网络或协议失败时保留最后一次成功数据并标记为过期。

## 许可证

[MIT](LICENSE)。研究与互操作参考见
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
