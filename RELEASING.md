# Release process

This repository uses Semantic Versioning and keeps the release version in one source:
[`Version.props`](Version.props).

## Branch policy

- `main` is the stable branch and should stay releasable.
- Changes are developed on short-lived branches such as `codex/deepseek-provider`.
- Changes reach `main` through a pull request after CI succeeds.
- Release branches are optional; do not keep a long-lived `develop` or release branch for this project.

## Prepare a release

1. Update `Version.props` to the intended SemVer version.
2. Move the release notes from the `Unreleased` section of `CHANGELOG.md` into a dated version section.
3. Run the local checks:

   ```powershell
   .\scripts\verify-version.ps1
   dotnet restore GptController.slnx
   dotnet build GptController.slnx -c Release --no-restore
   dotnet test GptController.slnx -c Release --no-build
   .\scripts\package.ps1
   .\scripts\verify-hashes.ps1
   ```

4. Merge the release pull request into `main`.
5. Create and push an annotated tag from the updated `main` commit:

   ```powershell
   git switch main
   git pull --ff-only origin main
   git tag -a v1.2.3 -m "GPT Controller 1.2.3"
   git push origin v1.2.3
   ```

The tag must exactly match `Version.props` and must point to a commit contained in `main`.
Pushing the tag runs the release workflow, which builds, tests, packages, verifies hashes,
and publishes a GitHub Release with generated notes.

Do not move or overwrite a published tag. If a release is incorrect, fix it in a new patch
version.
