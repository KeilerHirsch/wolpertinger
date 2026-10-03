# WOLPERTINGER R0 packaging

This directory contains the R0 Windows package boundary.

`Build-R0Package.ps1` creates a deterministic **local unsigned** x64 MSIX using the repository-pinned .NET SDK, the trusted Ada kernel, and the installed Windows SDK `MakeAppx.exe`. It does not install tooling, create certificates, trust publishers, install the package, or upload anything.

`Verify-R0Package.ps1` unpacks the MSIX and verifies the manifest and exact payload boundary. It rejects engineering/test binaries, PDBs, repository source, credential containers, and missing self-contained runtime files.

The checked-in manifest uses the explicit local development identity `KeilerHirsch.WOLPERTINGER.R0.Local`. **Do not upload it to Partner Center.** Before any Store/private-audience upload, replace/bind `Identity Name` and `Publisher` with the values issued by the actual Partner Center reservation and use the Store signing/servicing path.

The WAP project is intentionally kept outside `WOLPERTINGER.slnx` while the local machine lacks Visual Studio Desktop Bridge/MSIX packaging targets. The direct Windows SDK path remains locally testable through `MakeAppx`.

The manifest declares:
- x64 full-trust packaged classic AppHost at mediumIL;
- `internetClient` and `runFullTrust`;
- `wolpertinger://` protocol activation with `--protocol "%1"`;
- disabled-by-default `WolpertingerStartup` startup task.

Programmatic opt-in for the startup task remains gated until the Windows SDK .NET projection is available in the pinned build environment. No Registry or Startup-folder fallback is permitted.
