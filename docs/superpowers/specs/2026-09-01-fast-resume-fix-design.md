# Fast Resume Fix Design

## Goal

Make interrupted E-Hentai and ExHentai downloads resume from the first missing
image without re-extracting every preceding image, while keeping progress,
speed, and remaining-time reporting accurate.

## Confirmed Root Cause

The application currently scans local files correctly and passes a range such
as `--range 218-` to `gallery-dl`. The ExHentai extractor still produces the
first 217 image URLs before the generic range predicate filters them out. Its
3-6 second request interval therefore turns a resume into a long apparent
stall.

## Resume Strategy

For a non-CBZ gallery with a contiguous local prefix, the application will:

1. Find the first missing image with the existing local scan.
2. Build a continuation URL by adding `#pageN` to the normalized gallery URL,
   for example `https://exhentai.org/g/123/token/#page218`.
3. Pass that continuation URL to `gallery-dl` without a `--range` argument.

`gallery-dl` parses the fragment locally and initializes the ExHentai extractor
at image `N`, so preceding image pages are not requested. The URL fragment is
not sent to the website.

CBZ jobs will keep their existing behavior because the application cannot infer
missing entries inside an archive from external files. A job with no contiguous
local prefix will continue to use its original gallery URL.

If a continuation URL cannot be constructed from an otherwise valid job, the
application will fall back to the original URL and emit a visible diagnostic
message rather than silently claiming that fast resume is active.

## Progress State

Starting another attempt will reset only attempt-specific counters:

- newly completed files;
- files skipped during this attempt;
- failures;
- current file;
- speed and estimated remaining time.

The last known gallery total will be retained until fresh metadata replaces it.
After the local prefix scan, that prefix will be reflected as already present,
so a resumed task can immediately show progress such as `217 / 837`.

The user-facing status will distinguish the phases:

- local prefix found;
- continuation page selected;
- gallery information being requested;
- actual file transfer started.

## Speed And ETA

Transfer timing will begin with the first real prepare/download event, not when
the child process is launched. This excludes local scanning and extractor setup
from transfer speed.

ETA will use the number of files processed in the current attempt:

`average seconds per processed file * remaining global files`

It will not divide the current attempt duration by the gallery's global image
number. ETA remains unavailable until at least one file has completed, skipped,
or failed during the current attempt.

## Components

### GalleryResumePlanner

Extend the existing resume plan to carry the continuation URL in addition to
the start index and existing prefix count. URL construction stays next to the
scan because both values describe one resume decision.

### GalleryDlService

Use the continuation URL when a resume plan exists and remove the generic
`--range` argument for that path. Emit phase messages through the existing
output event. Track transfer time and per-attempt processed files for progress
calculation.

### DownloadJob

Preserve `TotalFiles` in `BeginAttempt`. Fresh gallery metadata remains
authoritative and may update the retained value.

No unrelated `MainWindow` refactor is included.

## Error Handling

- Invalid or unsupported input URLs continue through the existing validator.
- A failed continuation URL construction falls back visibly to the original
  behavior instead of preventing the download.
- Network, authentication, engine compatibility, stop, and automatic node
  failover behavior remain unchanged.
- Automatic node failover starts a new transfer timing window while retaining
  the known gallery total.

## Tests

Add offline regression coverage for:

1. A 217-image prefix produces a `#page218` continuation URL.
2. The continuation invocation does not also contain `--range 218-`.
3. No-prefix and CBZ jobs retain their existing behavior.
4. `BeginAttempt` resets attempt counters but preserves the known total.
5. Transfer timing excludes pre-transfer setup time.
6. ETA uses files processed this attempt, not the global image number.
7. Existing gap detection still stops at the first missing image.

Verification requires:

- `dotnet build .\EhGalleryDownloader.sln -c Release`
- the offline integration probe with `--unit-only`
- a clean `git diff --check`

## Delivery

After implementation and verification, commit the source and tests directly to
`main` and push to `origin/main`. Do not create a Pull Request, release, or
Windows distribution package in this change.
