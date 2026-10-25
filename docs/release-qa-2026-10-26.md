# Release check, 26 October 2026

A last pass before release, on the laptop, branch `release-qa` off develop: names brought in line
with the UI, a clean-up, the app driven through its features, and real agent tasks on three models.
ComfyUI work (Video generator clips, references, foley and music, image generation) ran on the
workstation, in its own thread.

## In short

| Model | Passed | Time for the passes | Fails |
|---|---|---|---|
| Claude Haiku 5.5 | 7 of 7 | 2 min 21 s | – |
| Qwen 3.8 27B | 6 of 7 | 11 min 8 s | Paint: couldn't fill the rectangle in 20 min |
| Nemotron 3.5 Super VL (free) | 6 of 7 | 3 min 57 s | Paint: couldn't fill it, then Kilo's hourly limit |

Every model managed files, code, web look-ups, Excel, Notepad and the toolkit panels; only Haiku
drew a filled rectangle in Paint (Shapes, Fill, Solid color, in 56 s). The app passed all 25 checks
of a scripted sweep. On the workstation, Video generator's two-clip video (63 min, 57 model calls)
and a picture in the chat passed; its other ComfyUI checks are in that thread.

## Agent runs

Each task ran in `blackbox-cli` with `--yes` from a scratch root of its own (its settings only),
one after another on the laptop's one screen (1920x1080 at 125%), and was checked by a script
afterwards, not by what the agent said. The toolkit task ran in the app's window, driven through
UI Automation.

| Task | What is asked | Checked |
|---|---|---|
| files | Revenue per region from a CSV into summary.md, highest first; fix three typos in README.txt | totals 106/70/61/8 in order; typos gone |
| code | test.ps1 fails: fix calc.ps1 (median of unsorted input, wrong even index) without touching the test | test.ps1 unchanged and passes |
| web | Latest 7-Zip version and its date into 7zip.txt | 26.05, 2026-10-21 (7-zip.org) |
| notepad | A haiku typed in Notepad, saved with Save As, Notepad closed | file of three lines, Notepad gone |
| paint | A filled red rectangle drawn in Paint, saved as PNG, Paint closed | red pixels in the PNG, Paint gone |
| excel | budget.xlsx with a SUM total and a bold header | read back through Excel |
| toolkit | In the app: add a Todo list to notes.md; pick its lines in **File changes** and ask for a numbered list; then ask about the file open in **File inspector** | numbered list in the file; the answer names 3 items and the last |

Times are the whole task, from start to the agent's last word; steps are model calls.

### Claude Haiku 5.5 (Anthropic API, the user's key, 100k context on Auto, medium effort)

| Task | Result | Steps | Time |
|---|---|---|---|
| files | pass | 3 | 9 s |
| code | pass | 3 | 10 s |
| web | pass | 4 | 10 s |
| notepad | pass | 6 | 36 s |
| paint | pass | 9 | 56 s |
| excel | pass | 2 | 7 s |
| toolkit | pass | 3 turns | 13 s |

- Everything first time, fast. It batches: Paint was 19 tool calls in 9 steps (several clicks per
  step, then F12, the file name and Enter typed at once) and it still checked the file with
  `Test-Path` before closing Paint.
- Excel: one `write_file` of a Markdown table to budget.xlsx, `=SUM(B2:B5)` and `**Item**` turned
  into a formula and bold by BlackBox's writer, then a `read_file` to check. It never opened Excel.
- In File inspector it answered from what it had read after its own edit ("no need to check the
  file again"), which was right.

### Nemotron 3.5 Super VL (Kilo, free, nvidia/nemotron-3.5-super-vl:free)

| Task | Result | Steps | Time |
|---|---|---|---|
| files | pass | 5 | 16 s |
| code | pass | 7 | 48 s |
| web | pass | 4 | 19 s |
| notepad | pass | 11 | 72 s |
| paint | **fail** twice (timed out at 25 and 20 min) | 37, 39 | – |
| excel | pass (second run, after Kilo's hourly limit) | 6 | 34 s |
| toolkit | pass | 3 turns | 48 s |

- Kilo answered fast tonight (0.5-4 s a reply), unlike the 1-3 min of 17 October.
- excel: it looked for Python and openpyxl three times (neither is on the PC) before using
  `write_file`'s own xlsx writer, as Haiku did at once.
- code: it tried `pwsh` first (not on the PC), then `powershell`; its first fix still failed, it
  reasoned out the index and fixed it on the second try.
- paint: it drew the rectangle's outline but couldn't fill it: the bucket used Color 2 (white),
  then it set Color 2 to red, redrew, selected all, clicked inside the shape for minutes. After 37
  steps (about 80 requests in the hour with the tasks before) Kilo answered 429 for every request:
  200 requests an hour per IP, and other threads' runs share the laptop's IP. BlackBox waited and
  retried as it should ("Trying again in 5 min (Stop ends the wait)"), so the task ran out its
  25 minutes waiting; Excel, next, got 429 from its first step. A second Paint run (a third, broken
  off when my own script closed Paint under it, isn't counted) set the bucket's Fill to Solid
  color, wandered into Save As and cancelled it, and ran into 429 again after 39 steps.

### Qwen 3.8 27B (q6_k_xl, the user's LM Studio over the VPN, 100k context)

| Task | Result | Steps | Time |
|---|---|---|---|
| files | pass | 4 | 51 s |
| code | pass | 17 | 4 min 56 s |
| web | pass | 5 | 28 s |
| excel | pass | 4 | 46 s |
| notepad | pass | 12 | 2 min 57 s |
| paint | **fail** (timed out at 20 min) | 19 | – |
| toolkit | pass | 3 turns | 70 s |

- The server had the laptop to itself (the workstation's ComfyUI runs waited), so these times are
  clean. Text steps took 2-5 s to read and 3-25 s to write; each screenshot added about 2.5k tokens
  and 8-10 s of reading.
- code: the slowest and the most thorough. Its first fix kept `[int]($n / 2)` for the odd case,
  which PowerShell rounds to even (3/2 gives 2), so the test still failed; it wrote four little
  debug scripts into BlackBox's scratch folder, searched the web for how `[int]` rounds, and fixed
  it with `($n - 1) / 2`.
- paint: the same wall as Nemotron, filling the rectangle: it drew it, undid it six times, drew
  it again, went to the bucket, then to Color 1. Two steps took long: one reply of 3.5k tokens
  (2 min) and one of 8k (4.6 min) of thinking, and at 64k tokens one prompt took 152 s to read,
  the 50-60k stall noted before.
- web: the only model that opened 7-zip.org to confirm what the search snippets said; the others
  wrote the snippets' answer (right here, as the snippet was the site's own).
- In File inspector it wondered whether the user had changed the file since it read it, then
  answered right.

## The app, driven

A stand-in model server (scripted replies, tool calls and a 30 s slow step) and a script driving
the window through UI Automation, on a fresh `BLACKBOX_HOME`: 25 of 25 checks passed.

- Starts, connects, a reply shows, the title names the chat.
- A slow step shows Stop; a message sent meanwhile waits above the box; Pause/Break stops the run
  ("Stopped with Pause/Break." with Continue) and the waiting message goes back into the box;
  Continue sends "Continue.".
- Files the agent writes (Markdown, CSV, a picture) get cards with Inspect; each opens in File
  inspector, and the file shows over the chat's box ("whole document", "whole file").
- Toolkit: Video generator opens on "Untitled video" with References, Video clips and Sound line 1
  (nothing written to Data\videos until something is made); File changes lists the two text files
  (the copied picture is left out, as binary files are); None closes the panel.
- New chat empties the chat, History brings it back; Settings opens and Cancel closes it; New
  window opens a second window; Data\errors.log stays empty; closing the window ends the app.
- No server at the address: the banner says so with Use Kilo's free models and Check again; a
  message sent then waits for the server, and the first "Could not reach … Trying again in 10 s"
  line shows about 16 s after sending (the first short retries show nothing), then every wait;
  Stop ends it. The offline status goes into errors.log.

## Changes in this pass

- Names: `VideoGeneratorViewModel`, `VideoGeneratorPanel`, `VideoGeneratorTool` (were Video…);
  clips, tries and animations where comments and notes said takes or film; "Version 2 of clip 1"
  lower-cased in a sentence again (the helper still looked for "Shot "); the changelog no longer
  lists renames and removals of things 1.0.0 never had.
- Claude on Auto uses 100k of its 1M context, as OpenRouter's and Kilo's models do (the user's
  call during this pass).
- Unused code out, helpers shared that two or three files repeated (`MarkdownSyntax`,
  `Strings.Capital`); the test build has no warnings.

## Left as they are

- In File inspector and in cards, a Markdown table is drawn in a monospace font noticeably smaller
  than the text around it.
- A free Kilo model shares 200 requests an hour with every BlackBox on the same IP; a long desktop
  task can use most of it.
