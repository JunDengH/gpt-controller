# GPT Controller

English | [简体中文](README.md)

A local-first Codex connection manager for Windows 11. GPT Controller connects
ChatGPT through the official OAuth flow, protects credentials at rest with Windows
DPAPI, manages connection changes with rollback, and interoperates with providers
such as DeepSeek and Alibaba Cloud Model Studio Qwen through the Responses API.

The application does not collect passwords, write API keys to `config.toml`, logs,
or backups, or copy browser cookies, browser profiles, projects, plugins, or local
task history.

> This is an unofficial open-source project and is not affiliated with or endorsed
> by OpenAI.

## Secure credential management

- ChatGPT accounts are added through the official Codex app-server and browser OAuth
  flow. The application never handles account passwords.
- Inactive ChatGPT authentication profiles and DeepSeek or Qwen API keys are encrypted
  with Windows DPAPI `CurrentUser`, so only the current Windows user can decrypt them.
- Credentials are stored separately from metadata such as email addresses, plans,
  organization names, balances, rate limits, and model caches.
- API keys are isolated by provider and supplied to Codex custom providers on demand by
  a bundled non-interactive credential helper. Plaintext keys do not enter
  `config.toml`, logs, or backups.
- The latest authentication state is saved before a connection change. If the change
  fails, the application restores the original configuration and connection instead
  of leaving a partially applied state.
- Data migrations validate the old data before writing to the new directory. The old
  directory remains intact as a rollback copy, and migration failures do not modify it.

See [Security boundaries](#security-boundaries) and the [security policy](SECURITY.md)
for the complete plaintext boundary, data-directory, and migration details.

## Maintained / Roadmap

**Actively maintained; current focus: Codex Windows compatibility, provider
interoperability, credential safety and regression coverage.**

## Features

### Connections and provider interoperability

- Manage ChatGPT OAuth, DeepSeek, and Alibaba Cloud Model Studio Qwen connections in
  one interface.
- Change the shared Chat, Work, and Codex authentication state after explicit
  confirmation, with automatic restoration and restart of the original connection on
  failure.
- Discover available DeepSeek models from the official `GET /models` endpoint and
  use `https://api.deepseek.com/` with the Responses API.
- Support the Beijing, Singapore, Virginia, Frankfurt, Tokyo, and Hong Kong Model Studio regions;
  discover the current account's available `qwen*` models dynamically; and validate
  Responses Function Call compatibility before applying a model.
- Search DeepSeek and Qwen models from a shared picker. Automatic refreshes do not
  issue inference requests.

### Status and observability

- Show Work / Codex shared quota using the actual returned window durations, progress and reset times,
  and stale-data status side by side.
- Display Free, Go, Plus, Pro 5x, Pro 10x, Pro 25x, Business, Enterprise, and Edu plans.
  Workspace plans show the organization for the exact current account, fall back to
  "Organization name unavailable" when it cannot be confirmed, while non-workspace
  accounts show only their email address without a personal-account label.
- Show DeepSeek CNY / USD balances and provide an explicitly confirmed minimal Responses
  test.
- Show the current connection and status in the main window; expose the current
  connection, Open, and Exit actions from the system tray.

## Requirements

- Windows 11 x64.
- A current Microsoft Store/MSIX installation of the ChatGPT Windows app.
- A ChatGPT managed OAuth account, or a pay-as-you-go DeepSeek or Alibaba Cloud Model
  Studio API key.
- Codex CLI 0.146.0 or later for model API connections.
- Token Plan, Coding Plan, custom proxies, multiple keys for one provider, and Chat
  Completions are not currently supported.

## Security boundaries

Saved account profiles are stored in:

```text
%LOCALAPPDATA%\GptController
```

When uninstalling through the installer, the application first restores the Codex
configuration that existed before GPT Controller took ownership. If a concurrent
switch, external configuration conflict, or incomplete backup makes restoration
unsafe, uninstall stops and preserves the application files instead of leaving a
provider that points to a deleted credential helper.

ChatGPT, DeepSeek, and Qwen credential files are encrypted with DPAPI `CurrentUser`.
Metadata (email, plan, organization name, balance, rate limits, and model cache) is
stored separately from credentials. API keys are exposed only by the bundled credential
helper to Codex custom providers and are not written to `config.toml`. The official
client's active file:

```text
%USERPROFILE%\.codex\auth.json
```

must remain in the official readable format, so GPT Controller does not add another
encryption layer to that file.

On the first upgrade from 1.1.x, the application validates the old data before
re-encrypting credentials into the new directory. The original directory:

```text
%LOCALAPPDATA%\GptAccountManager
```

is preserved in full as a rollback copy and is not automatically deleted by this
version. If migration fails, the application stops startup and leaves the old directory
unchanged. Runtime caches, logs, and temporary files are not migrated.

GPT Controller does not copy ChatGPT Chromium cookies, browser profiles, projects,
plugins, or local task history. Local project data remains shared between accounts;
cloud data is isolated by the active official account.

## Official Codex runtime

The `codex.exe` inside the MSIX `WindowsApps` directory cannot be executed directly by
a normal external process. The application reads the user's installed official client
version and copies the current binary from that open-source runtime into the current
user's private directory:

```text
%LOCALAPPDATA%\GptController\runtime
```

The copy is identified by its source-file version and SHA-256 hash and is used only to
start the official app-server. The application neither modifies `WindowsApps` nor
redistributes the binary.

## Usage

1. Select "Add connection" to import the current ChatGPT authentication state or add an
   account through OAuth.
2. Alternatively, select "Add DeepSeek API" or "Add Qwen API." Qwen requires a region;
   all regions except Virginia also require a workspace ID.
3. View ChatGPT status on the account cards and provider status plus the active model on
   the API cards.
4. Select the model button on an API card to open the shared model picker. Qwen discovers
   models dynamically and, before applying one, sends a minimal Function Call validation
   request with an explicit cost notice.
5. Select "Switch." If ChatGPT is running, confirm that it may be closed and restarted.

Changing connections interrupts running tasks. Cancellation is the default; the client
is closed only after explicit confirmation. History belonging to a different
authentication group may be temporarily hidden, but it is not deleted and reappears
when the original provider is restored.

Protocol and configuration references:
[DeepSeek Responses API](https://api-docs.deepseek.com/guides/responses_api/),
[DeepSeek with Codex](https://api-docs.deepseek.com/quick_start/agent_integrations/codex/),
[Qwen Responses API](https://www.alibabacloud.com/help/en/model-studio/qwen-api-via-openai-responses),
and [Codex custom model providers](https://learn.chatgpt.com/docs/config-file/config-advanced#custom-model-providers).

## Build from source

Install the .NET 10 SDK, then run:

```powershell
dotnet restore GptController.slnx
dotnet build GptController.slnx -c Release
dotnet run --project src\GptController\GptController.csproj
```

To build the portable distribution and installer, with the version read from the
repository-root `Version.props`:

```powershell
.\scripts\package.ps1
```

If `ISCC.exe` from Inno Setup is available on `PATH`, the script also builds the
installer. It can locate a custom Inno Setup installation through the Windows uninstall
registry.

See [RELEASING.md](RELEASING.md) for version, branch, and tag conventions, and
[CHANGELOG.md](CHANGELOG.md) for release history.

## Rate-limit and plan data

Rate limits are read from the official app-server's `account/rateLimits/read` method.
The application prefers `rateLimitsByLimitId.codex`, identifies the returned quota
windows by duration without assuming every plan has a five-hour limit, and calculates:

```text
remaining percentage = 100 - usedPercent
```

Plan mapping:

| Raw value | Display |
|---|---|
| `free`, `guest` | Free |
| `plus` | Plus |
| `go`, `chatgpt_go` | Go |
| `prolite`, `pro_lite`, `pro-lite` | Pro 5x |
| `pro` | Pro 10x |
| `promax`, `pro_max`, `pro_25x` | Pro 25x |
| `team` (legacy protocol name) | Business |
| `business`, `chatgpt_business`, `self_serve_business*` | Business |
| `enterprise`, `chatgpt_enterprise`, `hc`, `ent26`, `enterprise_cbp_*` | Enterprise |
| `education`, `edu`, `edu_plus`, `edu_pro`, `chatgpt_edu` | Edu |

Unknown plans are not guessed. Official account details supply workspace names and raw
plan codes, matched only to the exact current account ID. App-server sessions, token
claims, and the same account's cached metadata provide fallbacks. Failed lookups retain
the last successful data and show a sanitized status.

## License

[MIT](LICENSE). See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for research and
interoperability references.

Connection data now syncs immediately after startup and every 2 minutes by default (configurable from 1 to 120 minutes). The previous default of 15 minutes is upgraded; customized intervals are preserved. DeepSeek models are discovered from its official `/models` endpoint, and Qwen uses the paginated Model Studio catalog with a 404 fallback for older compatible endpoints. Official workspace credit balances are displayed when returned; missing credits remain unavailable. Automatic sync never sends inference requests.

The dark home screen uses one unified connection ledger with a contextual inspector. Selecting a row does not switch the active account; the explicit switch action does. Selection is retained after refresh.

## Official information check — 2026-10-07

Current display names are Free, Go, Plus, Pro, Business, Enterprise and Edu. Current Pro tiers are distinguished as 5x / 10x / 25x; historical numeric values remain readable and Team displays as Business. The current pricing page lists Pro at $100/$200/$500 per month and says Pro has no five-hour limit. Tier recognition uses the official client plan codes prolite / pro / promax. The raw account-details endpoint preserves promax even when an older CLI reports it as unknown. Missing windows are unavailable rather than zero or unlimited. Credits are distinct from currency balances.

Provider metadata now determines names, context/output limits, input modalities and reasoning efforts. New DeepSeek connections default to `deepseek-flash`; ignored Responses features are disabled. Qwen uses the documented native catalog query and supports workspace/shared endpoints across the supported six regions. See [the dated source audit](docs/official-information-2026-10-07.md).

Account import, OAuth save and quota refresh now query official account details, matching exact account IDs in flat or mapped responses. Missing workspace names can fall back to the versioned account endpoint used by the official desktop client. Failures retain same-account names and expose sanitized diagnostics. Pro tiers remain distinct.
