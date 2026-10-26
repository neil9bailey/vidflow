# winget

[winget](https://learn.microsoft.com/windows/package-manager/) installs the portable `VidFlow.exe`
straight from the GitHub release: `winget install NeilBailey.VidFlowPlayer`. Listing is free and
needs no code signing. It does not get round Smart App Control; the Store edition does
(docs/STORE.md).

Only stable releases go to winget: it has no pre-release channel, so a beta in the repository would
be offered to every winget user as an upgrade.

## For each stable release

1. Publish the GitHub release first: the manifest points at its `VidFlow.exe`.
2. Write the manifests (the SHA-256 is read from the release, nothing is downloaded):

   ```powershell
   .\tools\winget-manifest.ps1 -Version 1.0.0
   winget validate --manifest obj\winget\manifests\n\NeilBailey\VidFlowPlayer\1.0.0
   ```

3. Submit them to [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs): fork it, copy
   the `manifests\n\NeilBailey\VidFlowPlayer\<version>` folder into the fork at the same path, and
   open a pull request. (Or `wingetcreate submit <folder>` with a GitHub token.) The pull request is
   public and made from your GitHub account.

Automated validation runs on the pull request, then a moderator reviews it. If it is labelled
*Needs-Author-Feedback*, reply promptly: after 5 days without activity it is marked stale, and it is
closed 3 days after that.
