# GPT Account Manager

一个面向 Windows 11 的本地优先 ChatGPT 账号管理器：通过官方 OAuth 添加账号，
安全切换统一 ChatGPT 客户端的登录态，并显示每个账号的周限额、会员状态和公司归属。

> 本项目是非官方开源工具，与 OpenAI 无隶属或背书关系。

## 功能

- 一键切换 Chat、Work、Codex 共用的账号登录态。
- Windows DPAPI 加密保存非活动账号的认证档案。
- 通过官方 Codex app-server 和浏览器 OAuth 添加账号，不收集密码。
- 显示周限额剩余、重置时间、更新时间和数据新鲜度。
- 显示 Free、Plus、Pro 5x、Pro 20x、Team、Business。
- Team/Business 显示当前组织名称，其他套餐固定显示“个人账号”。
- 切换前保存最新认证，失败时自动恢复并重启原账号。
- 主窗口与系统托盘均可快速切换和查看额度。

## 系统要求

- Windows 11 x64。
- 从 Microsoft Store/MSIX 安装的当前 ChatGPT Windows 客户端。
- ChatGPT managed OAuth 账号；API Key、Bedrock 和外部 Token 模式不在 v1
  支持范围。

## 安全边界

保存的账号档案位于：

```text
%LOCALAPPDATA%\GptAccountManager
```

凭据文件使用 DPAPI `CurrentUser` 加密。元数据（昵称、邮箱、套餐、公司名称和额度
缓存）与凭据分离。官方客户端当前使用的：

```text
%USERPROFILE%\.codex\auth.json
```

必须保持官方可读格式，因此不会由本软件额外加密。

本软件不会复制 ChatGPT Chromium Cookies、浏览器用户目录、项目、插件或本地任务
历史。本地项目数据在账号之间共用，云端数据由切换后的官方账号隔离。

## 官方 Codex 运行时

MSIX `WindowsApps` 内的 `codex.exe` 不允许普通外部程序直接执行。应用会读取用户
已安装的官方客户端版本，将该开源运行时的当前二进制副本复制到当前用户专属的：

```text
%LOCALAPPDATA%\GptAccountManager\runtime
```

副本按源文件版本和 SHA-256 标识，仅用于启动官方 app-server；应用不会修改
WindowsApps 或重新分发该二进制。

## 使用

1. 启动应用并选择“导入当前账号”，保存 ChatGPT 当前登录状态。
2. 选择“添加账号”，在官方浏览器页面登录另一个账号。
3. 在账号卡片查看会员状态、归属和周限额。
4. 点击“切换”。如果 ChatGPT 正在运行，确认关闭和重启。

切换会中断正在运行的任务。程序默认取消，只有明确确认后才会关闭客户端。

## 从源码构建

需要 .NET 10 SDK：

```powershell
dotnet restore GptAccountManager.slnx
dotnet test GptAccountManager.slnx -c Release
dotnet run --project src\GptAccountManager\GptAccountManager.csproj
```

生成便携版：

```powershell
.\scripts\package.ps1 -Version 0.1.0
```

如果 PATH 中存在 Inno Setup `ISCC.exe`，脚本还会生成安装包。
脚本也会从 Windows 卸载注册表定位自定义目录中的 Inno Setup。可使用以下命令
执行一次不会启动应用的静默安装/卸载冒烟：

```powershell
.\scripts\test-installer.ps1
```

## 额度与会员数据

额度通过官方 app-server 的 `account/rateLimits/read` 获取。程序优先使用
`rateLimitsByLimitId.codex`，从约 10,080 分钟的窗口计算：

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

## 测试

自动化测试不使用真实 OpenAI Token，覆盖：

- 套餐和公司名称解析。
- 周窗口识别及百分比边界。
- DPAPI 加密和备份恢复。
- refresh token 降级保护。
- 事务式切换与启动失败回滚。
- 日志敏感信息脱敏。

如果本机已安装官方 ChatGPT 客户端，可在完全隔离、无 Token 的临时目录中执行
app-server 协议握手冒烟测试：

```powershell
$env:GAM_RUN_CODEX_INTEGRATION = "1"
dotnet test tests\GptAccountManager.Tests -c Release `
  --filter "FullyQualifiedName~CodexAppServerIntegrationTests"
```

## 许可证

[MIT](LICENSE)。研究与互操作参考见
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
