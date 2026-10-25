# UNIVERSUM architecture

UNIVERSUM is a Windows GUI on top of the MKVToolNix command-line tools. It never writes Matroska files with its own
code: every change is made by `mkvmerge` or `mkvpropedit`. The specification (in Slovak) lives in the project
documents; this file describes the code.

## Projects

| Project | Target | Purpose |
| --- | --- | --- |
| `src/Universum.Core` | `net10.0` | Tool-independent core: media model, language handling, rules, planning, MKVToolNix backend. Only the .NET base library — builds and is tested on Windows and Linux. |
| `src/Universum.Platform.Windows` | `net10.0` (Windows APIs marked with `[SupportedOSPlatform("windows")]`) | Registry lookup of MKVToolNix, Authenticode verification, later Recycle Bin. |
| `src/Universum.App` | `net10.0-windows`, WPF | User interface only: windows, pages, view models. No business logic. |
| `tests/Universum.Tests` | `net10.0` | Own small test runner (no third-party framework). Runs against a real `mkvmerge` and the files in `testdata/`. |

Third-party packages: only `CommunityToolkit.Mvvm` (MIT) in the WPF project.

## Backends and extensibility

The rest of the program works with `MediaFile` / `MediaTrack` and the `IMediaInspector` interface, never with
tool output directly. MKVToolNix is the first backend (`Universum.Core.MkvToolNix`):

- `MkvMergeInspector` – structure of a file from `mkvmerge --identify` in JSON (arguments passed through a UTF-8
  option file, so unusual file names are safe),
- `MkvToolNixLocator` – user folder → `UNIVERSUM_MKVTOOLNIX_DIR` → platform sources (registry) → default folders → PATH;
  the Microsoft Store edition is rejected because its command-line tools cannot be run,
- `MkvToolNixReleaseFeed`, `MkvToolNixDownloads` – latest version and official download addresses.

A future ffmpeg backend (ffprobe inspection, audio/subtitle conversion, audio samples for language detection) plugs
in next to it without changing the rules, planning or UI.

## Processes

`ProcessRunner` starts tools without a shell, passes arguments one by one, reads output as UTF-8, kills the whole
process tree on cancellation and forces a UTF-8 locale on Linux. Exit codes of MKVToolNix: 0 success, 1 warnings,
2 error.

## Safety

- Downloads only over HTTPS from `mkvtoolnix.download`; the installer must match the official SHA-256 list **and**
  carry a valid Authenticode signature of LINET Services GmbH, otherwise it is deleted and never started.
- The CI uses a pinned MKVToolNix version, verified the same way, and GitHub Actions pinned to commit SHAs.

## Tests

- `dotnet run --project tests/Universum.Tests` – unit and integration tests (Linux and Windows).
- `Universum.exe --samotest` – self-test of the real program on Windows in CI: MKVToolNix detection, signatures,
  the full download and verification path of the MKVToolNix installer, windows and screenshots.

## Conventions

Code, identifiers, commit messages and technical documentation are in English; the user interface, the user guide
and the specification are in Slovak.
