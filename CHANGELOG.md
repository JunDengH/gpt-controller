# Changelog

All notable changes to this project are documented in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
project uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/JunDengH/gpt-controller/compare/v1.2.0...HEAD
[1.2.0]: https://github.com/JunDengH/gpt-controller/compare/v1.1.5...v1.2.0
[1.1.5]: https://github.com/JunDengH/gpt-controller/compare/v1.1.4...v1.1.5
[1.1.4]: https://github.com/JunDengH/gpt-controller/compare/v1.1.3...v1.1.4
[1.1.3]: https://github.com/JunDengH/gpt-controller/compare/v1.1.1...v1.1.3
[1.1.1]: https://github.com/JunDengH/gpt-controller/compare/v1.0.1...v1.1.1
[1.0.1]: https://github.com/JunDengH/gpt-controller/releases/tag/v1.0.1
