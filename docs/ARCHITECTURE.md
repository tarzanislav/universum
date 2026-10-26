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
- `Rules/ManualTarget` – the header editor's target: every track with the name, language and flags the user set, in
  the chosen order, left-out audio/subtitles, tags and the segment title (`TargetState.NewTitle`). Never blocking.
- `Rules/TargetChecks` – the Check panel: undetermined languages, several defaults of one kind, no enabled track,
  forced subtitles nobody sees, tags left, order not CZ → SK → others (commentary last). Advice only.
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
  - originals are always kept: `FileProcessor.DeleteOriginalsAllowed` is off, so "delete" means "keep" (owner's
    decision of 26 October 2026, until the program is proven in practice); the apps offer no delete;
  - every tool writes its result as a new file into the library, named by the rules (`LibraryTargetPicker` in the
    app: film/series, title, episode, TMDB): rules, batch, header editor, chapter editor (`TargetState.NewChapters`)
    and multiplexer (`MuxSpec.Placement`, name chosen after writing by the size class); `Placement.InPlace` /
    `Placement.Beside` remain for later use;
  - only a library result UNIVERSUM made is reworked where it is: in the title folder, named "{Prefix} - … {size}{n}.mkv"
    (`Placement.IsLibraryName`) **and** recorded in the library index as a result with the same size and modification
    time (`LibraryIndex.IsResult`); the index follows the result through edits, renames and undo (`Replace`, `Undone`);
    a result changed outside UNIVERSUM counts as an original, and its undo is refused; an undone result is replaced by
    the next run of its original (K8).
    Any other file in the folder – an existing collection, originals put there by hand, also ones with library-like
    names ("Creep (2004) - 1080p BluRay.mkv") – is only read and the result is a new file; without an index nothing is
    reworked; the batch analyses again when the library comes to contain analysed files;
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
  TMDB key; `Request` sends it only to histra.net over HTTPS. `HistraClient` is ready for the endpoints: requests
  through `RequestPacer.Histra` (2 per second), 429/5xx waited out like TMDB, 401/403 explained, answers limited to
  4 MiB, redirects elsewhere refused. No HISTRA requests are made yet (endpoints and the TMDB synchronisation to come).
- TMDB attribution: the notice and the official logo `Assets/tmdb-logo.svg` (blue_short from
  https://www.themoviedb.org/about/logos-attribution, SHA-256 = the fingerprint in its official file name
  `8e7b30f7…5f6c`, kept byte-identical) shown as `tmdb-logo.png` rendered from it (WPF draws no SVG), smaller than the
  UNIVERSUM name, in the TMDB card of the settings.
- `Library/RequestPacer` – shared by TMDB and HISTRA: one request at a time per service, a fixed rate, pauses after
  "too many requests" and `RetryDelay` (Retry-After, else 2–16 s, at most 60 s).
- TMDB and HISTRA each have a "connect" switch in the settings (`TmdbEnabled`, `HistraEnabled`, on by default):
  switched off, nothing is sent there and the stored key or token stays (the CLI honours the TMDB switch for the
  stored key). With both on, the two are to be synchronised (`SyncActive`; what exactly follows the HISTRA API).

## Multiplexer (specification, section 5)

- `Muxing/MuxSpec` – input files (`MuxSource`: keep chapters / attachments / tags), the tracks taken in output order
  with names, languages and flags (`MuxTrack`), title, chapter file, new attachments, output file; `Validate` stops
  writing over an existing file or an input, unknown or duplicate tracks, wrong extensions, missing files.
- `Muxing/MuxArguments` – mkvmerge arguments per input (track selection, per-track values) and `--track-order`
  across inputs; always through an options file.
- `Muxing/Multiplexer` – writes `*.universum-tmp` next to the output, flushes, reads it back and compares it
  (`MuxVerifier`: tracks, names, languages, flags, title, chapters, attachments), then gives it its name; never
  overwrites. `MuxRules` runs the UNIVERSUM rules and the Check panel over the chosen tracks of all inputs.
- `Jobs/MuxJob` – the multiplexer as a queue job.

## Chapters (specification, section 5)

- `Chapters/ChapterFormats` – Matroska chapter XML (read with mkvextract, written for mkvpropedit, declared and saved
  as UTF-8) and OGM text; anything but one edition of flat chapters with one name each is "complex" and left to
  MKVToolNix GUI. `ChapterTime` parses and formats "HH:MM:SS.mmm"; `ChapterChecks` (after the end, same start) and
  `Every` (a chapter every N minutes).
- `Chapters/ChapterEditor` – saves the old chapters to the journal, writes the new ones in place, reads them back and
  compares (any difference puts the old ones back), keeps the modification time (+2 s); undo restores chapters and
  time.

## Job queue (specification, section 5)

- `Jobs/Job` – description, status (waiting, running, done, done with warnings, failed, cancelled), progress, output
  (info / warning / error lines), outcome, times. An exception fails only that job.
- `Jobs/JobQueue` – one job at a time in queue order while started; stop after the running job, cancel the running
  one (the processor leaves the original unchanged) or take waiting ones out, move waiting jobs, retry, clear
  finished. Events come on the thread that runs the queue; the app marshals progress from tool threads.
- `Jobs/FileJob` – a plan written by the `FileProcessor` (same safe route, undo record, Recycle Bin); the tools'
  warnings and errors become the job output. Header editor and rules page add jobs ("Do fronty"); the queue starts
  at once unless switched off in the queue page.

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
  from `github.com/tarzanislav/universum/releases/`. The public key is built in (created by the owner on
  26 Oct 2026); a release without a valid signature is never installed.
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
