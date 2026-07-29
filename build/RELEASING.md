# Releasing FlowType

Maintainer notes for cutting a release. Users don't need any of this — see the
[README](../README.md) to install, or [SECURITY.md](../SECURITY.md) for what
the app does on a machine.

## Cut a build

```powershell
pwsh build\publish.ps1
```

Produces, in `dist\`:

- `FlowType-<version>-win-x64\` — self-contained folder build
- `FlowType-<version>-win-x64.zip` — the same folder, zipped for the release

The version comes from `<Version>` in `src/FlowType/FlowType.csproj`, which is
the single source of truth. Bump it there and in `app.manifest` together.

### Publishing choices worth preserving

- **No single-file bundle.** Whisper.net resolves its native libraries from
  `runtimes\win-x64\native` beside the executable; single-file bundling
  flattens that layout and the app fails at model load with *"Native Library
  not found in default paths."* Verified — don't re-enable it without testing
  `--selftest` against the bundled exe.
- **No compression or trimming.** Both rewrite the payload into a
  self-extracting blob, the same shape packers use, and are a leading cause of
  false-positive antivirus detections.

## Verify before publishing

```powershell
dist\FlowType-<version>-win-x64\FlowType.exe --selftest
```

Check `%APPDATA%\FlowType\selftest.txt` reports `SELFTEST OK` — that covers
model loading, a live capture, transcription, and the formatter and
English-variant test vectors.

## Code signing (optional)

Signing removes the SmartScreen "unknown publisher" prompt. An OV certificate
builds publisher reputation over time; an EV certificate is trusted essentially
immediately. Self-signed certificates don't help — Windows doesn't trust them.

With a certificate installed in the current user's store:

```powershell
pwsh build\publish.ps1 -Sign -Thumbprint <cert-thumbprint>
```

The script signs with SHA-256 and adds a trusted timestamp, so signatures stay
valid after the certificate expires.

## Publish

```powershell
gh release create v<version> "dist\FlowType-<version>-win-x64.zip" `
    --title "FlowType <version>" --notes-file <notes.md>
```

Update [CHANGELOG.md](../CHANGELOG.md) in the same commit as the version bump.
