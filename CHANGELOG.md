# Changelog

All notable changes to this project are documented in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
project uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.3.1] - 2026-08-14

### Fixed

- Restored GPT Controller-managed Codex provider settings before uninstalling so
  DeepSeek and Qwen connections cannot leave a deleted credential-helper path in
  `config.toml`; unsafe or conflicting restores now stop with an explicit warning.
- Displayed the effective ChatGPT workspace beside each account email, retained a
  previously known workspace name when refreshed tokens contain sparse metadata,
  and provided explicit personal and unknown-workspace fallbacks.
- Recognized current workspace plan variants and matched multi-workspace accounts by
  their active workspace identifier instead of guessing from the available names.

## [1.3.0] - 2026-08-09

### Added

- Added Alibaba Cloud Model Studio Qwen as the second Responses API provider, with
  official endpoint mapping for Beijing, Singapore, Virginia, Frankfurt, and Tokyo.
- Added dynamic Qwen model discovery, per-region caching, and an explicit low-cost
  Responses Function Call compatibility check before a model can be applied.
- Added a shared searchable model selection dialog for DeepSeek and Qwen.
- Added an in-card model switcher for DeepSeek V4 Flash and V4 Pro.
- Added crash-recoverable Codex managed-field transactions for active model changes.

### Changed

- Generalized API provider switching so DeepSeek and Qwen share the same encrypted
  original-config backup, crash recovery state, API-to-API rollback, and credential helper.
- Upgraded the connection index to schema v2 with automatic schema v1 migration while
  retaining existing DeepSeek metadata and credentials.
- Redesigned the connection workspace into two balanced lanes separated by a central
  divider: ChatGPT OAuth accounts on the left and model API connections on the right.
- Reworked account and API cards into equal-width, equal-height compact rows. Account
  cards now lead with email, while API cards lead with provider; redundant avatars,
  nicknames, protocol subcopy, and model descriptions were removed.
- Expanded the local Codex model catalog to include both supported DeepSeek V4 models.

### Fixed

- Matched the animated active-card outline to each card's plan or provider accent color
  while preserving the edge-aligned trace path.

## [1.2.1] - 2026-08-04

### Changed

- Standardized dialogs, menus, inputs, focus states, and destructive actions around a
  consistent minimal visual system.
- Replaced account-specific global copy with provider-neutral connection and model API
  terminology.
- Split OAuth accounts and API providers into independent card templates, with a reusable
  API presentation model for future providers.
- Redesigned API connection cards as a responsive status ledger for provider, model,
  protocol, endpoint, CNY balance, validation state, and API actions.

### Fixed

- Removed API-key identifiers and USD balances from API card presentation data and UI.
- Improved keyboard focus visibility, accessible control names, minimum action targets,
  long-value tooltips, and compact-window behavior across the connection experience.

## [1.2.0] - 2026-08-02

### Added

- Added a single DeepSeek V4 Flash connection using the official Responses API.
- Added a DPAPI-protected API-key store and command-backed Codex credential helper.
- Added balance refresh, explicit low-cost Responses testing, and transactional provider switching.
- Added a versioned unified connection index for ChatGPT and DeepSeek providers.

### Changed

- Centralized application versioning and standardized the tag-based release process.
- Reworked the main page into unified ChatGPT OAuth and DeepSeek connection management.
- Require Codex 0.146.0 or newer before enabling the DeepSeek provider.
- Renamed the application, assemblies, installer, and release artifacts to GPT Controller.
- Migrated 1.1.x data to `%LOCALAPPDATA%\GptController` with new DPAPI entropy while
  retaining the previous data directory as an untouched fallback.

## [1.1.5] - 2026-07-30

### Changed

- Added separate five-hour and weekly quota cards.

## [1.1.4] - 2026-07-27

### Fixed

- Improved account switching and quota refresh reliability.

## [1.1.3] - 2026-07-25

### Fixed

- Fixed issues found after the UI redesign release.

## [1.1.1] - 2026-07-24

### Changed

- Redesigned the application UI and added the current application icon.

## [1.0.1] - 2026-07-24

### Added

- Published the initial release under the previous application name.

[Unreleased]: https://github.com/JunDengH/gpt-controller/compare/v1.3.1...HEAD
[1.3.1]: https://github.com/JunDengH/gpt-controller/compare/v1.3.0...v1.3.1
[1.3.0]: https://github.com/JunDengH/gpt-controller/compare/v1.2.1...v1.3.0
[1.2.1]: https://github.com/JunDengH/gpt-controller/compare/v1.2.0...v1.2.1
[1.2.0]: https://github.com/JunDengH/gpt-controller/compare/v1.1.5...v1.2.0
[1.1.5]: https://github.com/JunDengH/gpt-controller/compare/v1.1.4...v1.1.5
[1.1.4]: https://github.com/JunDengH/gpt-controller/compare/v1.1.3...v1.1.4
[1.1.3]: https://github.com/JunDengH/gpt-controller/compare/v1.1.1...v1.1.3
[1.1.1]: https://github.com/JunDengH/gpt-controller/compare/v1.0.1...v1.1.1
[1.0.1]: https://github.com/JunDengH/gpt-controller/releases/tag/v1.0.1
