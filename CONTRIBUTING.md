# Contributing to VidFlow Player

Thanks for helping. Bug reports, testing on unusual footage and pull requests are all welcome.
For a larger change, open an issue first so we can agree on the approach before you spend time
on it.

## What you need

- Windows 10/11 x64
- Visual Studio Build Tools 2022 or newer with the MSBuild component (for the Roslyn C# compiler).
  The app targets .NET Framework 4.8, which is part of Windows, so there is no SDK or NuGet step.
- ffmpeg on `PATH`, only to generate the test clips.

## Build and test

```powershell
.\tools\fetch-engine.ps1     # one time: downloads the pinned libmpv build and checks its SHA-256
.\build.ps1 -NoEmbed         # fast dev build -> dist\dev\VidFlow.exe + libmpv-2.dll
.\build.ps1                  # release build -> dist\VidFlow.exe (engine embedded, single file)
.\tools\run-core-tests.ps1   # unit-level checks, no window (CI runs these on every push)

.\tools\make_testmedia.ps1   # test clips into testmedia\ (also make_vfrmedia.ps1 for frame-accuracy clips)
.\tools\e2e.ps1              # end-to-end checks against the dev build
.\tools\e2e.ps1 -Only cull-apply,cull-undo   # just the tests you're working on
.\tools\e2e.ps1 -TimeScale 4                 # a slow machine: every wait 4x (default: from the load)
.\tools\bench.ps1 -Suite gate   # release gate on the synthetic phone-footage samples
```

`build.ps1 -Check -Files <file>` compile-checks a few files quickly; `-WithContracts` and
`-Overlay` are described at the top of `build.ps1`.

### The test rule: never take over the screen

The end-to-end tests and benchmarks start real VidFlow windows. They must run on the hidden
desktop through `obj\tools\RunHidden.exe` (`e2e.ps1` and `bench.ps1` do this themselves), with
audio sent to mpv's null output. New tests must keep to that:

- no real mouse or keyboard input, SendKeys or UI automation of the visible desktop: drive the
  app through the automation pipe (`--automation`, see docs/SPEC.md);
- no clipboard use unless the test runs only with `-Clipboard`;
- never write the real `HKCU\Software\Classes` (point `FileAssociations.BasePath` at a throwaway key);
- every process a test starts is stopped when it finishes.

## Code style

- Match the surrounding code: naming, layout and comment density. Comments explain why, not what.
- C# with the language version Roslyn supports, targeting .NET Framework 4.8 WinForms. No new
  dependencies or NuGet packages; the single-file exe is part of the product.
- `MainForm` is split into partial classes by area (`MainForm.Cull.cs`, `MainForm.View.cs`, ...).
  Put new code in the part it belongs to, and new user actions in `src/Core/Actions.cs` so they
  get a menu entry, a remappable shortcut and an automation verb.
- Keep the app offline: no network access except the explicit Check for Updates (see PRIVACY.md).

## Pull requests

- One topic per pull request, with the tests you ran listed in the description.
- Add a line to CHANGELOG.md under the upcoming version for anything users will notice.
- CI must be green: it builds the exe and runs the core tests.

## Releases (maintainers)

1. Set `VERSION` (e.g. `1.0.0` or `1.0.0-beta.5`) and date its section in CHANGELOG.md.
2. Run the full `e2e.ps1` and `bench.ps1 -Suite gate` locally against `dist\VidFlow.exe`.
3. Tag and push: `git tag v1.0.0` then `git push origin v1.0.0`.
4. The Release workflow builds that commit and creates a **draft** release with the exe, the zip,
   the engine's corresponding source and SHA256SUMS.txt. Check it, then press Publish. (Re-running
   the workflow makes a second draft; delete the older one.) The engine source is copied from the
   engine pre-release, so after an engine change run the Engine source workflow first
   (docs/ENGINE-SOURCE.md).

## Licence

VidFlow Player is GPLv3. By contributing you agree that your contribution is licensed under the
same terms.
