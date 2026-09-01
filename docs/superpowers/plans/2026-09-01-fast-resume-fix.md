# Fast Resume Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Resume E-Hentai and ExHentai jobs directly at the first missing image and report retained progress, transfer speed, and ETA accurately.

**Architecture:** Extend `GalleryResumePlan` into the single source of truth for the engine input URL and optional legacy range fallback. Keep transfer math in a small deterministic calculator so the service orchestration and offline probe can share and verify it.

**Tech Stack:** C# 12, .NET 8, WPF, the existing console integration probe, `gallery-dl`.

## Global Constraints

- Direct continuation uses `#pageN` and must not also pass `--range N-`.
- CBZ and no-prefix jobs keep their existing behavior.
- Preserve the last known gallery total until fresh metadata replaces it.
- Do not change Cookie, proxy, original-image, stop, or node-failover behavior.
- Do not create a release or Windows distribution package.

---

### Task 1: Direct continuation planning

**Files:**
- Modify: `src/EhGalleryDownloader/GalleryResumePlanner.cs`
- Modify: `src/EhGalleryDownloader/GalleryDlService.cs`
- Test: `tests/EhGalleryDownloader.IntegrationProbe/Program.cs`

**Interfaces:**
- Produces: `GalleryResumePlan(int StartIndex, int ExistingPrefixCount, string InputUrl, string? Range)`.
- Consumes: `DownloadJob.Url`, `DownloadJob.OutputDirectory`, and local numbered files.

- [ ] **Step 1: Add failing continuation assertions**

Extend the existing resume-planner probe so a prefix ending at 217 requires:

```csharp
gapPlan.InputUrl == "https://exhentai.org/g/4089450/token/#page3"
gapPlan.Range is null
```

Also retain the existing first-gap and CBZ assertions.

- [ ] **Step 2: Run the offline probe and confirm failure**

Run:

```powershell
dotnet run --project .\tests\EhGalleryDownloader.IntegrationProbe\EhGalleryDownloader.IntegrationProbe.csproj -c Release -- --unit-only
```

Expected: compilation fails because `GalleryResumePlan` does not expose `InputUrl` or `Range`.

- [ ] **Step 3: Implement continuation planning**

Change the plan record to carry the selected engine input and fallback range. Build a URL fragment with `UriBuilder.Fragment = $"page{firstMissing}"`. For a valid continuation, set `Range` to `null`; only retain the old range when URL construction unexpectedly fails.

In `GalleryDlService`, add `resumePlan.Range` only when non-null and launch:

```csharp
info.ArgumentList.Add(resumePlan?.InputUrl ?? job.Url);
```

Emit a visible message stating that image `StartIndex` is being opened directly.

- [ ] **Step 4: Run the offline probe**

Run the command from Step 2. Expected: continuation and existing gap tests pass.

### Task 2: Retained task progress

**Files:**
- Modify: `src/EhGalleryDownloader/Models.cs`
- Test: `tests/EhGalleryDownloader.IntegrationProbe/Program.cs`

**Interfaces:**
- Changes: `DownloadJob.BeginAttempt()` retains `TotalFiles` and resets only attempt-local fields.

- [ ] **Step 1: Add a failing retained-total assertion**

Set `stoppedJob.TotalFiles = 837`, call `BeginAttempt()`, and assert:

```csharp
stoppedJob.TotalFiles == 837
```

Keep the existing assertions that completed, skipped, and failed counts reset.

- [ ] **Step 2: Run the offline probe and confirm failure**

Expected: the retained-total assertion fails because `BeginAttempt()` currently sets the total to zero.

- [ ] **Step 3: Remove the total reset**

Delete only `TotalFiles = 0` from `BeginAttempt()`. Metadata events remain authoritative and can replace the retained value.

- [ ] **Step 4: Run the offline probe**

Expected: retained-total and task-continuation assertions pass.

### Task 3: Transfer speed and ETA

**Files:**
- Create: `src/EhGalleryDownloader/DownloadProgressCalculator.cs`
- Modify: `src/EhGalleryDownloader/GalleryDlService.cs`
- Test: `tests/EhGalleryDownloader.IntegrationProbe/Program.cs`

**Interfaces:**
- Produces: `DownloadProgressCalculator.Calculate(int current, int total, long downloadedBytes, double elapsedSeconds, int processedFiles)` returning `DownloadProgress`.
- Consumes: per-attempt byte count, elapsed transfer seconds, and processed-file count.

- [ ] **Step 1: Add failing deterministic math assertions**

For current image 218 of 837, 1 processed file, 1 MiB, and 10 transfer seconds, assert speed is 0.1 MiB/s and ETA is 6,190 seconds. Assert ETA is null when processed files is zero.

- [ ] **Step 2: Run the offline probe and confirm failure**

Expected: compilation fails because `DownloadProgressCalculator` does not exist.

- [ ] **Step 3: Implement the calculator and service tracking**

Calculate:

```csharp
speed = downloadedBytes / 1024d / 1024d / elapsedSeconds;
remaining = TimeSpan.FromSeconds(
    elapsedSeconds / processedFiles * Math.Max(total - current, 0));
```

Reset the service stopwatch without starting it during setup. Start it on the first structured prepare/success/skip/failure event. Increment the processed-file count only for unique success, skip, or failure events, then call the calculator.

- [ ] **Step 4: Run the offline probe**

Expected: all new math assertions and existing structured-progress tests pass.

### Task 4: Full verification and delivery

**Files:**
- Modify if needed: `CHANGELOG.md`

**Interfaces:**
- Produces: verified source commit on `main`, pushed to `origin/main`.

- [ ] **Step 1: Document the fix**

Add an Unreleased section describing direct `#pageN` resume, retained totals, and corrected transfer timing.

- [ ] **Step 2: Run full local verification**

```powershell
dotnet build .\EhGalleryDownloader.sln -c Release --nologo
dotnet run --project .\tests\EhGalleryDownloader.IntegrationProbe\EhGalleryDownloader.IntegrationProbe.csproj -c Release --no-build -- --unit-only
git diff --check
```

Expected: build exits 0 with zero warnings and errors; all offline probe assertions pass; `git diff --check` is clean.

- [ ] **Step 3: Review the final diff**

Confirm there are no Cookies, runtime state, logs, binaries, or unrelated refactors in the diff.

- [ ] **Step 4: Commit and push**

```powershell
git add src tests CHANGELOG.md docs/superpowers/plans/2026-09-01-fast-resume-fix.md
git commit -m "fix: resume galleries from first missing image"
git push origin main
```

Expected: `origin/main` advances to the verified fix commit.
