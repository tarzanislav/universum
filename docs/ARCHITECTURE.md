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
- `Execution/FileProcessor` – writes a plan into the library; an original is never lost:
  - the file is read again first; if it changed since the analysis, nothing is written;
  - header-only change of an original that is to be removed, on the same drive: an `UndoRecord` (header values,
    tags, time, track UIDs, where the file came from) is written first, then the original is moved into the library as
    `*.universum-moving` (ignored by media servers), edited without interruption, verified and renamed; any failure,
    also an exception, restores the header and moves it back;
  - otherwise the result is written as `*.universum-tmp`, flushed, read back, verified and renamed, and only then is
    the original sent to the Recycle Bin;
  - a file already in its library folder is edited with undo, or replaced: the old one is renamed to
    `*.universum-old`, the new one takes its place, then the old one goes to the Recycle Bin;
  - `Library/LibraryIndex` remembers which original (fingerprint of its content) each library file came from, so
    the same original processed again is not written twice; unknown files are never skipped, at worst they become a
    new version.
- `Platform.Windows/WindowsRecycleBin` – only fixed drives whose Recycle Bin is on and large enough for the file
  (per-volume settings in the registry); Windows must ask before any permanent delete (`FOF_WANTNUKEWARNING`).

## Library (specification, section 13)

- `Library/LibraryLayout` – the output folder with `MOVIES` and `TVSHOWS` (created, never deleted).
  Films: `MOVIES\Title (Year)\Title (Year) - {stream}.mkv`; series:
  `TVSHOWS\Title (Year)\Season 01\Title S01E04 - {stream}.mkv` (Jellyfin naming; a film file starts with its
  folder name so versions are grouped).
- `Library/TitleInfo` – the name used: the original title when it is written in Latin script, otherwise the English
  title plus `[tmdbid-ID]`; `LibraryNames` removes characters Windows forbids and detects the script.
- `Library/StreamLabel` – `{languages} {quality} {codec} {size}`, e.g. `CZ EN FHD H264 SMALL`: audio languages
  without commentaries (or `CZ titulky` / `SK titulky` when no audio is in a preferred language), SD/HD/FHD/4K by
  resolution with tolerance for cropped formats, H264/HEVC/AV1/…, SMALL/BIG/BluRay/BluRay BIG by the final size
  (thresholds in the profile). `Placement` chooses the final name after writing: another stream with the same name
  gets a number (`BIG2`, `BIG3`).
- `Library/EpisodeParser`, `TitleGuess`, `LibraryJob` – episode numbers and titles from file and folder names (only
  suggestions until confirmed, except names in library form with a TMDB ID), kind film/series, per-batch decisions.
- `Library/Histra/HistraApi` – HISTRA (histra.net, tracking of watched titles; API at `https://histra.net`): the
  user's token is accepted only when it fits a Bearer header (RFC 6750 token68) and is stored with DPAPI like the
  TMDB key; `Request` sends it only to histra.net over HTTPS. No HISTRA requests are made yet (endpoints to come).
- TMDB and HISTRA each have a "connect" switch in the settings (`TmdbEnabled`, `HistraEnabled`, on by default):
  switched off, nothing is sent there and the stored key or token stays (the CLI honours the TMDB switch for the
  stored key). With both on, the two are to be synchronised (`SyncActive`; what exactly follows the HISTRA API).

## Batch processing

- `Batch/BatchAnalyzer` – finds the Matroska files of a folder (natural order, optionally with subfolders), reads
  them in parallel through `Media/CachingInspector` (results remembered by path, size and modification time) and
  groups them by `StructureSignature` (audio and subtitle languages, forced flags, chapters, tags); the largest group
  comes first. Left-over `*.universum-tmp` files from an interrupted run are reported, never analysed.
- `Templates/BatchTemplate` – tolerant template: ordered `TemplateSlot`s (type, language, role, required). The slot
  order becomes the language order of the rules; files with fewer tracks still match unless a required slot is
  missing; tracks outside the template are kept (default) or removed. Flags always come from the rules.
- `Batch/BatchPlanner` – per file: language evidence (file → group decision → track name → suggestion), rules,
  template match, title, episode and place in the library, and review reasons. `Suggest` adds suggestions that are never used unconfirmed: the language of
  a similar track elsewhere in the folder (codec, channels, sampling rate, name) and forced subtitles detected from
  subtitle counts (statistics tags). `Text/TextLanguageDetector` + `MkvToolNix/SubtitleText` recognise the
  language of text subtitles on request.
- `Batch/BatchRunner` – processes the files that need work: files whose source or target drives meet one after
  another, others in parallel (with one output drive: one at a time); pause, resume, stop after the current file.
  `BatchReport` writes CSV and HTML reports with the target paths.

## Updates (specification, section 4)

- `Updates/UpdateChecker` asks only public addresses: the MKVToolNix release feed and `NEWS.md` (change log, shown
  in English, security fixes highlighted), `compatibility.json` from the branch `compatibility` (weekly CI run with
  the newest MKVToolNix) and the latest GitHub release of UNIVERSUM. `UpdatePolicy`: at start, at most once a day,
  "remind later" (3 days, security fixes excepted) and "skip this version".
- Order: MKVToolNix first (official installer, SHA-256 + Authenticode as at installation), then UNIVERSUM (its
  installer restarts the program).
- `Updates/ReleaseSignature`: a UNIVERSUM release is installed only when `SHA256SUMS.txt` is signed with the release
  key (ECDSA P-256; public key in the program, private key only as the secret of `release.yml`), its signed first
  line is `# UNIVERSUM X.Y.Z` of exactly the offered (newer) version – an older signed release cannot be replayed –
  and the installer `UNIVERSUM_Setup_X.Y.Z.exe` has the SHA-256 listed there. Release data and downloads must come
  from `github.com/tarzanislav/universum/releases/`. Without a configured key UNIVERSUM opens the release page.
- Installers (MKVToolNix and UNIVERSUM) are downloaded into a new folder with a random name, locked against writes
  while they are checked again and started; MKVToolNix additionally needs its Authenticode signature with a
  successful revocation check (offline revocation servers = no installation).
- `release.yml` publishes a release only from a tag `vX.Y.Z` (validated before use, passed to scripts only through
  the environment) with notes in `docs/releases/`. Job `build` compiles, tests and packs with read-only access, no
  secrets and only preinstalled tools; job `publish` (environment `release`) only signs the checksum list with the
  key matching the program and creates the release.

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
