# Releasing

How to put out a new Sloperator. GitHub Actions builds the exe; you never attach it yourself.

1. In `Directory.Build.props`, raise `Version` (for example to `1.2.0`).
2. In `CHANGELOG.md`, rename `[Unreleased]` to `[1.2.0] - <date>` (like `2026-10-29`) and add a new
   empty `[Unreleased]` above it. At the bottom, point `[unreleased]` at `compare/1.2.0...HEAD` and
   add `[1.2.0]: https://github.com/forcepusher/Sloperator/compare/1.1.0...1.2.0`.
3. Push `develop`, merge it into `master` and push `master`.
4. On GitHub: **Releases → Draft a new release**.
   - Tag: the same number, `1.2.0`, on `master`.
   - Notes: paste that version's CHANGELOG section.
   - Leave **Set as a pre-release** off and **Set as the latest release** on: installed copies only
     look at the latest release.
   - **Publish**. Don't attach `Sloperator.exe`.
5. Wait a few minutes. The **Release** workflow (Actions tab) builds `Sloperator.exe` and attaches it to the release.
   - Once SignPath is set up: SignPath emails you a signing request. Approve it on signpath.io within an hour, then the signed exe gets attached.
6. Done. Installed copies see the new version within an hour of the exe being attached and offer
   **Restart** (in Program Files, **Download** instead).

Both the tag and `Version` must be higher than the last release's, or installed copies ignore it.

## If the build fails
Actions tab → **Release** → the failed run. Fix the cause on `develop`, merge into `master`, then delete the release and its tag and make it again, or press **Re-run jobs** if nothing in the code needed fixing.

## SignPath, once
1. Apply at https://signpath.org/apply (the README already has the code signing policy they ask for).
2. At SignPath: project `Sloperator`, signing policy `release-signing`, artifact configuration = a zip holding one PE file, `Sloperator.exe`.
3. GitHub → repository **Settings → Secrets and variables → Actions**: secret `SIGNPATH_API_TOKEN` (a SignPath API token), variable `SIGNPATH_ORGANIZATION_ID`.
   Other project or policy names: variables `SIGNPATH_PROJECT_SLUG`, `SIGNPATH_SIGNING_POLICY_SLUG`.

Until then every release is attached unsigned, and updates still work.
