# Security policy

## Supported versions

Only the newest GitHub release receives security fixes.

## Reporting a vulnerability

Please report credential exposure, unsafe process handling, path traversal, or
account-isolation issues through a private GitHub security advisory. Do not put
tokens, `auth.json`, email addresses, or diagnostic archives containing account
data in a public issue.

## Credential model

- Saved profiles are encrypted with Windows DPAPI in `CurrentUser` scope.
- The currently active `%USERPROFILE%\.codex\auth.json` remains in the format
  required by the official ChatGPT/Codex client.
- Decrypted probe files live only in the current user's local application-data
  directory and are removed after use and again at next startup.
- Logs redact JWT-shaped values, bearer credentials, refresh-token fields, and
  email addresses.
- The application never asks for an OpenAI password and never calls logout
  while switching profiles.

DPAPI protects credentials from other Windows users and offline copying. It
does not protect them from malware already running as the same Windows user.
