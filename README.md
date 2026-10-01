# Windows Debloater

A single-file Windows 11 debloater with a WPF interface. Double-click
`Debloater.exe`, tick what you want, read the preview, then apply.

## Running it

```
dist\Debloater.exe
```

The executable is self-contained, so it needs no .NET runtime installed. It is
also unsigned, so Windows SmartScreen will warn on first run; choose
*More info* then *Run anyway*.

UAC will prompt for administrator rights. The tool needs them: AppX removal,
service changes and `HKLM` policy writes are all admin-only.

## How it works

The tool never changes anything while you are choosing. The flow is:

1. **Scan** reads the current state of every package, service, task and registry
   value in the catalog. This touches nothing.
2. **Select** you tick the changes you want. Protected entries are listed but
   locked.
3. **Preview** builds a plan and shows a table of category, target, current
   state, and the exact outcome. Nothing has run yet. You can copy the plan to
   the clipboard as text.
4. **Apply** creates a system restore point, then runs the plan. Every change is
   recorded to an undo manifest.
5. **Undo** on the main window reverts the last run from that manifest.

## Safety model

- A restore point is created before anything is applied.
- `SysMain` and Windows Update are never touched.
- A hardcoded blocklist protects `Microsoft.WindowsStore`,
  `DesktopAppInstaller`, the `VCLibs`/`NET Native` frameworks, the `UI.Xaml`
  frameworks, and `Store.Engagement`. These cannot be selected.
- Removed AppX packages are **not** auto-restored by undo. Undoing them needs
  the original installer, so the result window lists each one for manual
  reinstall. Registry, service and task changes do revert automatically.
- Moderate and high-risk items require ticking an acknowledgement box before
  Apply becomes clickable.

## Data files

`src\WindowsDebloater\Data\Apps.json` and `Tweaks.json` hold the catalogs and
are embedded in the executable. Drop same-named files in a `Data` folder next
to `Debloater.exe` to override them without rebuilding.

`Apps.json` entries match installed and provisioned packages by exact name.
Wildcards are only used where an entry sets `"allowWildcard": true`.

## Building

```
build.ps1
```

or directly:

```
dotnet publish src\WindowsDebloater\WindowsDebloater.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

The icon is generated, not checked in as an opaque binary. After a clean clone:

```
powershell -File tools\Make-Icon.ps1
```

## Tests

```
dotnet test tests\WindowsDebloater.Tests
```

Covers registry value round-tripping (including `REG_MULTI_SZ` entries containing
newlines, which is what an undo manifest has to preserve), catalog safety
invariants, plan building against a synthetic machine, and service start-mode
normalisation. The registry tests write only to a scratch key under
`HKCU\Software\WindowsDebloaterTests` and clean up after themselves.

## Logs and manifests

Written under `%LocalAppData%\WindowsDebloater`:

- `manifest-latest.json` - the undo record for the most recent run
- `manifest-<stamp>.json` - a per-run copy
- `run-<stamp>.log` - per-item apply and undo output