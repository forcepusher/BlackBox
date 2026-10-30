# Release check, 30 October 2026

A last pass before 1.2.0, on the laptop, branch `release-1.3.0` off develop: a clean-up, real agent
tasks on three models with the clicks in real programs rather than the click benchmark's fake screens,
and the long tests of `scripts/`. The workstation's tests (ComfyUI and video, second-monitor tasks,
the opt-in tests that need its programs) are in their own section.

## In short

| Model | Laptop tasks passed | Second-monitor tasks (workstation) | Long runs |
|---|---|---|---|
| Qwen 3.8 27B | 12 of 12 | 3 of 4 | 2048 in the browser: pass, 10 of 10 checks; 2048 in Unity: pass, 1 h 55 min; ComfyUI 8 of 9; chat-test 64 of 65 checks so far; sysadmin 2 of 2 tickets run |
| Claude Haiku 5.5 | 12 of 12 | 4 of 4 | chat-test: every check of 4 scenarios |
| Nemotron 3.5 Super VL | 10 of 12 | 4 of 4 (Kilo) | – |

Every real task that needed clicks passed on Qwen and Haiku: Notepad, Paint (filled and saved),
a web form with a dropdown, checkboxes and radio buttons, Explorer by mouse, Calculator, Edge.
Nemotron failed Paint and Explorer on clicks it gave on its 0-1000 grid. Nothing found needed a
change to Sloperator; two test scripts got fixes. The clean-up changed no behaviour, and every
unit test passes on it.

## Agent runs

Each task ran in `sloperator-cli` with `--yes` from a scratch root of its own (its settings only),
one after another on the laptop's one screen (1920x1200 at 125%), and was checked by a script
afterwards, not by what the agent said.

| Task | What is asked | Checked |
|---|---|---|
| files | Revenue per region from a CSV into summary.md, highest first; fix three typos in README.txt | totals 106/70/61/8 in order; the rest of README.txt unchanged |
| code | test.ps1 fails: fix calc.ps1 (median of unsorted input, wrong odd and even index) without touching the test | test.ps1 unchanged and passes |
| web | Latest 7-Zip version and its date into 7zip.txt | against 7-zip.org at check time |
| notepad | A haiku typed in Notepad, saved with Save As, Notepad closed | file of three lines, Notepad gone |
| paint | A filled red rectangle drawn in Paint, saved as PNG, Paint closed | red pixels over 80% of their box (filled, not an outline), Paint gone |
| excel | budget.xlsx with a SUM total and a bold header | read back through Excel |
| form | A pizza order on a web page already open in Edge: name, size dropdown, two checkboxes, a radio button, a note, Place order | the page's title after the order holds exactly the values asked for |
| explorer | In File Explorer, by mouse: a new folder Reports, two of three files moved into it | the files where asked; shell commands counted |

Times are the whole task, from start to the agent's last word; steps are model calls. The four
tasks of `scripts/agent-tasks.ps1` ran after them (its second-monitor tasks on the workstation).

### Qwen 3.8 27B (q6_k_xl, the user's LM Studio over the VPN; develop f80fef2)

| Task | Result | Steps | Time |
|---|---|---|---|
| files | pass | 5 | 2 min 23 s |
| code | pass | 10 | 4 min 52 s |
| web | pass (26.05, 2026-10-21) | 7 | 2 min 14 s |
| notepad | pass | 14 | 4 min 34 s |
| paint | pass (filled, 336x278 px) | 42 | 14 min 16 s |
| excel | pass (`=SUM(B2:B5)`) | 6 | 2 min 6 s |
| form | pass (order 2547) | 12 | 2 min 46 s |
| explorer | pass, no shell command | 20 | 6 min 57 s |
| agent-tasks files / calc / hn / search | 4 of 4 (6016; three Hacker News titles; 31.7k stars) | 3 / 7 / 7 / 9 | 42 s / 1 min 56 s / 1 min 48 s / 2 min 53 s |

- Paint, which Qwen failed in 20 minutes at 1.1.0, passed: Edit colors set to 255, 0, 0, the
  rectangle tool, the bucket inside it, File → Save as with the path typed and Save clicked, then
  the file checked with `Test-Path` and looked at before Paint was closed (with `Stop-Process`).
- explorer: Explorer from the taskbar, the path typed in its address bar, right-click → New →
  Folder, "Reports" typed, q1 clicked and q2 Ctrl+clicked, both dragged onto the folder.
- form: eight clicks and two typed texts, each on its field, the first time.
- code: the same trap as at 1.1.0 (`[int]($n / 2)` rounds 1.5 to 2), found with a debug script and
  fixed with `($n - 1) / 2`.
- excel: `write_file`'s xlsx writer, then a look inside the file's XML to check it (Python isn't
  on the laptop, which it tried first).

### Nemotron 3.5 Super VL (OpenRouter, paid; release-1.3.0 a343e17)

Through OpenRouter rather than Kilo's free tier, so the workstation's Kilo runs kept the free
allowance to themselves.

| Task | Result | Steps | Time |
|---|---|---|---|
| files | pass | 6 | 13 s |
| code | pass | 13 | 28 s |
| web | pass | 4 | 11 s |
| notepad | pass (left Notepad open) | 15 | 43 s |
| paint | **fail**: said it was saved; the Save As dialog was still open, the rectangle an outline | 44 | 15 min 4 s |
| excel | pass | 5 | 2 min 47 s |
| form | pass (order 2547) | 9 | 1 min 8 s |
| explorer | **fail**: timed out at 15 min, nothing moved | 44 | – |
| agent-tasks files / calc / hn / search | 4 of 4 | 3 / 32 / 26 / 9 | 7 s / 3 min 27 s / 1 min 55 s / 51 s |

- Fast where it doesn't look at the screen (files, code and web in seconds), and right at the
  form, which it clicked in fractions of the screenshot (0.365, 0.355), as Sloperator reads them.
- Paint: it typed the right path in Save As, then clicked (690, 666) twice: on the 0-1000 grid it
  still slips into, that is Save (1192, 719 of the 1728x1080 screenshot); in pixels, it is beside
  the dialog's type box. It then said the file was saved. The click benchmark found the same
  pointing (notes, "Tools the agent has": Nemotron still answers on its grid); no change for it, as
  one way of pointing holds for every model.
- explorer: it kept opening the taskbar's search box and closing windows with Alt+F4.
- OpenRouter's provider read some prompts slowly: 100 to 180 s each at 77k to 89k tokens, where
  the first ones took 1 to 8 s.

### Claude Haiku 5.5 (OpenRouter, anthropic/claude-haiku-5.5, medium effort; release-1.3.0 a343e17)

The user's new key is OpenRouter's, so Haiku went through the OpenAI-compatible path here; the
workstation's second-monitor runs went through Anthropic's Messages API.

| Task | Result | Steps | Time |
|---|---|---|---|
| files | pass | 4 | 14 s |
| code | pass | 4 | 17 s |
| web | pass | 5 | 18 s |
| notepad | pass | 7 | 44 s |
| paint | pass (filled, 448x278 px) | 11 | 1 min 2 s |
| excel | pass | 3 | 15 s |
| form | pass (order 2547) | 5 | 43 s |
| explorer | pass (opened with one shell command, the rest by mouse) | 9 | 1 min 36 s |
| agent-tasks files / calc / hn / search | 4 of 4 | 3 / 5 / 4 / 5 | 9 s / 35 s / 48 s / 28 s |

- Everything first time, several calls a step: Paint's 25 tool calls took 11 steps.
- calc: its first pass gave 6144 (8 pressed for 7); it saw that, cleared, entered it again and
  checked the zoomed display before closing Calculator.
- hn: it said it might have closed the wrong tab. The tab in front when it pressed Ctrl+W was an
  Avalonia page an earlier search run had left; the user's own tabs were still there.
- excel, paint and explorer: with the task's folder inside the Sloperator checkout, it also called
  `index_folder` on the checkout, alongside its first step, so it cost no time.

## Long runs

- **2048 in the browser** (`game2048-test.ps1`, Qwen, release build): written, played and checked in
  58 min 31 s, 40 model calls, 0 tool errors, 3 context resets each with its note. Qwen played to 160,
  filled the board with long key sequences until Game Over, and restarted with R; it found nothing
  to fix. `game2048-check.mjs` (run on the workstation: the laptop has no Node) passed all 10 checks:
  start, readable board, moves by the rules, merges, score, game over exactly when no move is left,
  R, tiles inside the board, no script errors.
- **2048 in Unity** (`unity2048-test.ps1`, Qwen, release build): on the workstation, which has Unity
  6000.3.23f1 and a second monitor for the log, after the browser run so the two didn't share Qwen.
  Passed in 1 h 55 min, 178 model calls, 0 tool errors, 8 context resets: the game made in Unity 6.3,
  played in Play mode with real key presses to a score of 2420, Game Over and R checked, then a
  Unity2048.exe built and played too. It fixed four bugs of its own on the way (the arrow keys, the
  font, merging, the build folder).
- **Folder index** (`index-bench.ps1 -Tasks wait -Runs 1`, Nemotron through OpenRouter): the script
  runs and scores as before. With the index 24 tool calls and 441k prompt tokens against 46 and
  936k without; both runs failed the task's check (they edited more files than asked), a model
  result, not the script's.

## On the workstation

The workstation thread's runs, on develop f80fef2 and then on release-1.3.0 (from 59cde27).

| Test | Result |
|---|---|
| Real mouse and screens across two monitors (`BB_DESKTOP_TESTS=1`) | 3 of 3, before and after the clean-up |
| SSH lab (a 3 GB copy, a reboot) and real MCP servers through npx (`BB_MCP_NPX`) | 26 of 26 after the clean-up |
| ComfyUI online tests (`BB_COMFY_ONLINE`) | 2 of 2 |
| Second-monitor tasks (`agent-tasks.ps1` mm-*) | Haiku 4 of 4 (10-22 s each), Nemotron on Kilo 4 of 4 (19-89 s), Qwen 3 of 4 |
| `chat-test.ps1` on Haiku | self-test 6 of 6; inbox 19/19, support 12/12, haggle 13/13, social 11/11, 0 tool errors |
| `comfy-test.ps1` on Qwen (RTX 5070 Ti) | 8 of 9 |
| `chat-test.ps1` on Qwen | inbox 19/19, support 12/12, haggle 13/13, away 20/21; social stopped, evening not run |
| `sysadmin-test.ps1` on Qwen | game-server 10/10 (61.7 min), fix-website 9/9 (4.5 min); ssh-hardening stopped, three tickets not run |

- comfy-test: picture (3 min 50 s), img2img (layout kept, 0.77), named (DreamShaper), sound
  (3 min 12 s, 10 s), music (3 min 59 s, 30 s), speech (4 min 45 s, 3.8 s), a 3D model (10 min 13 s,
  an 8.8 MB textured .glb, 0 errors) and the combo (picture, video, sound: 15 min 55 s) passed. The
  video task made a usable 3 s 1280x768 clip with sound first, but the video check held it to Qwen's
  own, very exact prompt (the cat walks, the ball leaves the frame; one of its two looks also
  misdescribed the cat's pose), and the two retakes failed on camera drift, so the script's 45 minutes
  ran out. The clip call took 525 s, 80 of them generating and the rest the looks on the shared Qwen.
- Two test-script fixes, taken onto the release branch: comfy-test now reads a clip the video tool
  made (it names it "Shot s1 (s1.mp4)", not by path), and asks ffprobe for a length only for tasks
  that want one (a .glb stopped the script).
- Qwen's mm-move timed out: Win+Up and then Win+Right snapped Notepad to the right half of screen 1,
  Snap Assist filled the left half, and Qwen thought for minutes over each step.
- Nemotron's first click on screen 2 landed at about 0.55 of the target's x; its zoom corrected it.
- Qwen's chat-test and sysadmin runs went at the same time on the shared Qwen server, so their
  times are slow, and the user had both wrapped up before they finished. Its one chat miss (away):
  it posted "I'm stuck in meetings until 04:25" in the hiking group before anyone there asked.
  The finished runs had 0 tool errors in chat and one each in the two sysadmin tickets.
- chat-test social: a click into a message box changed nothing visible, so the agent got the
  "nothing changed" note and the end-of-task check cost one step; Haiku confirmed from the
  screenshot and finished.

## Changes in this pass

- `ComAbi`: one copy of the COM and WinRT calls (vtable slots, HSTRINGs, async waits, vectors)
  that Media Foundation, the PDF renderer, text recognition, the notifications and the shell
  previewer each had their own of.
- Toolkit is Side panel in the code too (`ISidePanel`, `SidePanelOption`, `SidePanelColumns`,
  `SidePanelClose`), as the UI has said since 29 October; no saved setting carried the name.
- Helpers shared that two to six files repeated: the players' icons, the binary-file check, a
  substring count, the sound's channels in words, a table row's cells, the chat client's sending
  and error reading for OpenAI's and Anthropic's API, the full-screen viewer's placing.
- The build has no analyzer findings left (unused usings and parameter, ambiguous crefs).
- New tests: sound read through Media Foundation (no test covered it), and an opt-in real
  notification (`BB_TOAST_TESTS=1`).
- The changelog no longer lists steps between unreleased versions (the right-click Copy split, a
  second entry on the box's hint, History and Servers "fixed" to the left edge and then moved back).
- `Directory.Build.props` says 1.2.0 and the changelog's section is dated.

Also checked on the release build: the real-screen unit tests and the real-mouse click test on the
laptop, the published single-file exe (24 MB) starting on an empty folder with nothing in
errors.log, and every Side panel tool opened from its dropdown in the real app and closed with None.

## Left as they are

- Nemotron 3.5 Super VL still points on its 0-1000 grid at times, and said Paint's picture was
  saved with Save As still open. Not changed: one way of pointing for every model.
- Qwen can hold a video clip to its own over-exact prompt and retake a usable clip until the time
  runs out (comfy-test's video).
- A click into an empty text box changes nothing the screenshot shows, so it gets the "nothing
  changed" note (chat-test social): one extra step, harmless.
- The laptop has no Node and an older Unity (6000.3.10f1): the 2048 checker and Unity 2048 ran on
  the workstation.
