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
| `src/Universum.Cli` | `net10.0` | `universum-cli` – `analyze` / `apply` a folder without the window (scripts, Task Scheduler); same core, same settings profile. |
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

## Rules, planning and writing

- `Rules/UniversumRules` – the track rules of the specification (section 6) as a pure function of the file,
  a `RuleProfile` (preferred languages, keywords for forced / SDH / commentary) and languages chosen for `und`
  tracks. Result: `TargetState` (tracks in final order, flags, names, languages, issues). Blocking issues
  (undetermined language, several video tracks, no audio) stop automatic processing.
- `Planning/EditPlan` – write method (`None`, `HeaderEdit` with mkvpropedit, `Remux` with mkvmerge when the order
  changes) and the list of changes for previews; `TargetVerifier` compares a written file with its target.
- `MkvToolNix/MkvToolNixArguments` – mkvpropedit / mkvmerge arguments (tracks selected by Matroska track number,
  language set as IETF tag so both language elements are written, statistics tags disabled when tags are removed).
- `Execution/FileProcessor` – writes a plan safely: new files under a temporary name next to their destination,
  read back and verified before the original is touched; a replaced original goes to the Recycle Bin (refused where
  there is none, e.g. network shares); in-place header edits first save an `UndoRecord` (header values and tags)
  and are reverted automatically when verification fails.

## Batch processing

- `Batch/BatchAnalyzer` – finds the Matroska files of a folder (natural order, optionally with subfolders), reads
  them in parallel through `Media/CachingInspector` (results remembered by path, size and modification time) and
  groups them by `StructureSignature` (audio and subtitle languages, forced flags, chapters, tags); the largest group
  comes first. Left-over `*.universum-tmp.*` files from an interrupted run are reported, never analysed.
- `Templates/BatchTemplate` – tolerant template: ordered `TemplateSlot`s (type, language, role, required). The slot
  order becomes the language order of the rules; files with fewer tracks still match unless a required slot is
  missing; tracks outside the template are kept (default) or removed. Flags always come from the rules.
- `Batch/BatchPlanner` – per file: language evidence (file → group decision → track name → suggestion), rules,
  template match and review reasons. `Suggest` adds suggestions that are never used unconfirmed: the language of
  a similar track elsewhere in the folder (codec, channels, sampling rate, name) and forced subtitles detected from
  subtitle counts (statistics tags). `Text/TextLanguageDetector` + `MkvToolNix/SubtitleText` recognise the
  language of text subtitles on request.
- `Batch/BatchRunner` – processes the files that need work: one after another per drive, drives in parallel;
  pause, resume, stop after the current file; a target folder keeps the subfolder structure. `BatchReport` writes
  CSV and HTML reports.

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
