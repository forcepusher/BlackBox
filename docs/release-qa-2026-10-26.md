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
of a scripted sweep. On the workstation every ComfyUI check passed in the end (below), after six
Video generator fixes found on the way.

## ComfyUI on the workstation

The real app (Debug build of `qa/comfyui`, on release-qa and then develop 3e9a0de) on the
workstation's ComfyUI 0.41.3 (RTX 5070 Ti), driven through UI Automation, posted mouse messages and
the Open dialog's own window messages; no computer use. Qwen 3.8 (q6_k_xl) through a local
pass-through that adds the server's key, so no settings file holds it; Haiku 5.5 with the user's key
in a git-ignored test folder. Each run was checked from the files the app wrote, not from what the
agent said. errors.log stayed empty throughout.

| # | Test | Model | Result | Time | Notes |
|---|---|---|---|---|---|
| 6 | "make me a picture of a red fox in snow", Toolkit None | Qwen | pass | 2 min 58 s | Z-Image Turbo, 5 model calls; the card's Prompt shows the prompt. First run guessed the prompt node (6.text) and was told 57:27 |
| 1 | "Make a 2-clip video: a cat walks across a kitchen, then jumps on the table" | Qwen | pass | 2.5 + 63 min | It only laid it out (the context says make nothing unless asked); **Make the video** made the reference, 3 frames and 2 clips (5 retakes), joined and checked: 57 model calls. Clip 1, Clip 2, the slots and the joined video show and play; References and Generated each have a .json beside every piece |
| 5 | Use this version on clip 2 and on the song; rename and delete a card, a clip, a sound | – | pass | – | The version goes back and the current one becomes v3; renamed files and their .json move together; delete takes the frames only that clip used |
| 7 | File inspector on a clip and on the song | – | pass | – | Frame stepping (Frame 4 / 168, back to 3), an area of frame 49 into the box; the song's waveform, a stretch "10.45-25.37 s" into the box |
| 2 | Clip 3 not made: end slot File… with a picture, start slot Generate, then the clip | Haiku | pass | 2 + 12 min | The picture is cut to 1280x720 at once; the start frame made by Qwen-Image-Edit from f2 and the cat. Four tries of the clip all failed their looks (a morph between two angles), and Haiku stopped and asked which way to go rather than call it done |
| 3a | A square fox picture by File… on a new reference, then "a 4 second clip where this fox trots across the kitchen" | Qwen | pass | 29 min | References/Red Fox.png byte for byte the user's; the frame shows the whole fox in the same kitchen |
| 3b | walk.mp4 (a 4 s cat walk) as a reference video, "moves exactly as the cat does" | Qwen | pass after a fix | 86 min, then 12 min | Card with a poster and ▶, plays in the viewer. The user's LTX-2.5 pose workflow was picked, but its clips came out 2.0 and 2.7 s (fix 3); Qwen then edited the workflow and wrote its own stabilizer for an hour (stopped). Remade after the fix: 4.0 s, the fox walks as the cat (the camera pans with it, which the look failed) |
| 3c | walk.mp4 picked, "a clip that goes on from exactly where this video ends: the cat stops and sits down" | Qwen, Haiku | pass after a fix | 45, 7, 12 min | Both models went on from the last clip, not from walk.mp4 (fix 6). Haiku then made the frame of walk.mp4; its sit clip failed its looks (the camera tilts), and it said so |
| 4 | Sound line 1: + then Foley; + Sound line; Music "a playful jazzy piano tune…"; drag, trim, move to the other line, the two volume sliders, join | Qwen | pass after a fix | 2.6 + 4.4 min | Foley: blackbox/video_to_audio over all 5 clips, 18 s. Music: ACE-Step 1.5 XL Turbo, 60 s, best of 3 (SongEval 3.1, an instrumental). Drag 1.6 s along, left edge 1 s in, to the other line: all saved. Joins at line volumes 1/1, 1.5/0.5, 2/0, 0/0: the foley alone measured -23.7, -20.2 and -17.7 dB and silence, after fix 4 |

**Fixed** (on `qa/comfyui`, each in the changelog and docs/notes.md):
1. New clip on a video just opened was never saved: it stayed only in the panel, was gone at the
   next start, and its start frame could show a picture a deleted clip had left in Generated.
2. After **Use this version**, the panel said "all made" with the joined video holding the old
   clip; it now says "all made, not joined since" (and so does the whole video's card).
3. A workflow's "Length from the prompt (LTX duration head)" switch was taken for a length, given
   the clip's 4 and left on, so LTX's duration predictor chose 2 s. A switch is no length now,
   and such a switch is turned off when the clip's length is set.
4. In the joined video, a sound trimmed at its start played from 0 s: ffmpeg 8.1 dropped its delay
   in the `apad`/`atrim` after it. That also made the sound lines' volumes measure backwards.
5. A song made with Music on a new sound didn't show in the viewer, which still showed the new
   sound's card and asked for its style.
6. With a video of the user's picked, the context told the model that "a video" meant the one
   being made, so "where this video ends" went on from the last clip; it now says "this video"
   means the picked file.

**Left as they are:**
- A frame's File… keeps the picture where it is (`file`, a full path), as the chat does: a video
  moved to another PC finds it missing and marks the frame out of date.
- The dashed + on a sound line already full to the video's end puts the new sound after the end,
  where it isn't heard (a3 at 18.1 s of an 18 s video).
- Clips have no Rename (their names are Clip 1, 2…); the shot's own name ("the jump") shows nowhere
  on the timeline, only its camera words.
- The panel's viewer shows the joined video's first frame, which is black under the fade in.
- Qwen ran off on two requests: an hour hand-editing a workflow and writing a stabilizer for
  "exactly as the cat does", and 45 minutes on the wrong clip. Haiku was faster and stopped to ask.

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
