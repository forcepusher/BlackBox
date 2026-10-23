# Folder index benchmark

Does `index_folder` help the model understand a project, with fewer searches and tool calls, and
does it cost anything? Measured on 23 October 2026 on the laptop: 107 runs of ten tasks on two
projects, with the index as merged into develop the night before (6fc10f2) and without it, then
again with two fixes this test led to.

## In short

- **Answers were no better and no worse with the index.** Both models answered every question fully
  or nearly so either way (76 answers graded blind), and every edit passed its checks (30 runs). The
  index changes how a model gets there, not where it gets.
- **Qwen 3.8 27B on BlackBox (373 files): faster with the index.** About 21% less time, 22% fewer
  tool calls and 15% fewer tokens over six tasks. The biggest gains were on questions about how
  something works (the context reset: 8.8 → 5.3 min) and on renaming a class everywhere (8.5 → 3.7 min).
- **Qwen on FluffyMeow (22,247 files): slower with the index.** About 29% more time over four
  tasks. With the index Qwen found the right files sooner, then searched and read more: each search
  result (1-2.5k tokens) stays in the conversation and fills Qwen's 100k context sooner, and Qwen
  went on verifying details (scene and prefab GUIDs) that the runs without the index skipped. The
  fixes cut the tool calls below those without the index, but the time only a little.
- **Claude Opus 5.5: faster with the index, most of all on the big project.** About 4% less time on
  BlackBox and 30% less on FluffyMeow with the index as merged; 13% and 38% less with the fixes. It
  makes more tool calls (index calls on top), but reads fewer tokens.
- **The index got in the way of tasks a grep answers**: "list every class that implements X",
  "change this constant". Both models searched the index, then searched again with the shell. The
  first fix (a search for a name lists every line that has it) brought those close to even.
- **The model reaches for the index by itself:** in 65 of the 67 runs with it, `index_folder` was the
  first call, and the "index it now" note was never needed.
- **One run is not a measurement.** The same task without the index took Qwen 822 s once and 191 s
  the next time. The figure that started this test (a question about a Unity project: 6.3 min and
  44 tool calls without the index, 2.2 min and 11 with it) was a single run of each.

## How it was run

- **The same build with and without.** `blackbox-cli --no-index` leaves out the tool, its line in
  the system prompt and the note that says to index, nothing else. Comparing two builds (before and
  after the index was merged) would also have measured everything else that changed between them.
- **Two projects.** BlackBox itself at 6fc10f2 (373 files, 216 of them C#), and the user's Unity game
  FluffyMeow (PushMeow) at d85638bea: 22,247 files, 1,802 C# scripts, 152 of those the Spine
  runtime. The game was copied without its history and without `Backend/VirtualMachine`, which holds
  an SSH key and a credentials file that the models had no reason to read.
- **Ten tasks** of six kinds (`scripts/index-bench/tasks.json`, which also holds each rubric):

  | Task | Project | Kind | Scored by |
  | --- | --- | --- | --- |
  | explain | BlackBox | explain the project to a new developer | 8-point rubric |
  | context | BlackBox | how the context reset works: when, how computed, what is kept | 8-point rubric |
  | repeat | BlackBox | trace a notice ("thinking started repeating") to its cause and thresholds | 8-point rubric |
  | observers | BlackBox | list every class that implements `IAgentObserver` | 7-point rubric |
  | wait | BlackBox | raise the wait tool's limit from 10 to 30 minutes, docs too | the constant, `dotnet build` |
  | rename | BlackBox | rename `HeadlessBrowser` to `BackgroundBrowser` in app and tests, file too | no old name left, file renamed, `dotnet build` |
  | fm-explain | FluffyMeow | explain the game, its systems, its start-up and what is outside Unity | 8-point rubric |
  | fm-needs | FluffyMeow | how the cat's needs go down, while running and while closed | 8-point rubric |
  | fm-draggable | FluffyMeow | list every class that implements `IDraggable` directly | 12-point rubric |
  | fm-toilet | FluffyMeow | change the toilet's weight in hygiene from 0.2 to 0.3 | the constant |

- **Each run** started from a clean copy of its project and an empty BlackBox root, so no index was
  carried over: a run with the index built it itself. Runs with and without alternated, so a server
  that warmed up or slowed down weighed on both. Two runs per task and condition.
- **Models:** qwen3.8-27b@q6_k_xl on the user's LM Studio (RTX 5070 Ti, 100k context, over the VPN),
  one run at a time; Claude Opus 5.5 through Anthropic's API at medium effort, two at a time.
- **Measured:** wall time, tool calls by kind (index, file reads, folder listings, shell commands
  that search), and prompt tokens added up over the steps: what the server read, which for Claude
  is mostly from its cache at a tenth of the price.
- **Quality:** the edit tasks were checked by script. The answers to the questions were shuffled
  and graded against the rubrics written before the runs, without knowing which run had the index
  (`scripts/index-bench-blind.ps1`). The grader was Claude, which knew both codebases.
- **The fixes** were measured on Claude with each task once or twice, until the user stopped the
  Claude runs to save tokens (its one FluffyMeow explanation was the refused run below), and on Qwen
  with the six tasks they were most likely to change. Those builds are Release builds of the same
  code; the first round's are Debug.

## Results

Times are averages in seconds, tokens in thousands, "Result" is the rubric's average or the checks
passed. "As merged" is the index from 6fc10f2, "fixed" the index with the two fixes below.

### Qwen 3.8 27B (q6_k_xl on LM Studio)

| Task | Runs | Time (s) | Tool calls | Index calls | Shell searches | Prompt tokens (k) | Result |
| --- | --- | --- | --- | --- | --- | --- | --- |
| explain without | 2 | 564 | 49.5 | 0.0 | 6.5 | 997 | 8.0/8 |
| explain with, as merged | 2 | 499 | 32.5 | 4.0 | 0.0 | 739 | 8.0/8 |
| explain with, fixed | 2 | 565 | 42.5 | 3.0 | 2.5 | 869 | 8.0/8 |
| context without | 2 | 528 | 29.5 | 0.0 | 11.5 | 830 | 8.0/8 |
| context with, as merged | 2 | 316 | 18.5 | 4.0 | 5.0 | 432 | 8.0/8 |
| repeat without | 2 | 392 | 21.0 | 0.0 | 6.0 | 283 | 8.0/8 |
| repeat with, as merged | 2 | 320 | 16.5 | 2.5 | 6.5 | 309 | 8.0/8 |
| observers without | 2 | 118 | 9.5 | 0.0 | 3.0 | 85 | 7.0/7 |
| observers with, as merged | 2 | 139 | 9.0 | 2.0 | 2.0 | 99 | 7.0/7 |
| observers with, fixed | 2 | 121 | 7.5 | 1.5 | 1.0 | 75 | 7.0/7 |
| wait without | 2 | 254 | 19.0 | 0.0 | 10.0 | 340 | 2/2 passed |
| wait with, as merged | 2 | 276 | 22.0 | 2.0 | 5.0 | 478 | 2/2 passed |
| rename without | 2 | 507 | 25.5 | 0.0 | 4.0 | 302 | 2/2 passed |
| rename with, as merged | 2 | 221 | 18.5 | 1.5 | 3.5 | 167 | 2/2 passed |
| rename with, fixed | 2 | 259 | 16.0 | 2.0 | 2.5 | 231 | 2/2 passed |
| fm-explain without | 2 | 739 | 79.5 | 0.0 | 7.0 | 1321 | 8.0/8 |
| fm-explain with, as merged | 2 | 860 | 66.5 | 3.0 | 11.0 | 1540 | 8.0/8 |
| fm-explain with, fixed | 2 | 860 | 65.5 | 3.5 | 9.5 | 1290 | 8.0/8 |
| fm-needs without | 2 | 400 | 33.5 | 0.0 | 6.5 | 379 | 7.5/8 |
| fm-needs with, as merged | 2 | 639 | 45.5 | 6.0 | 10.5 | 949 | 7.5/8 |
| fm-needs with, fixed | 2 | 527 | 35.0 | 5.5 | 9.5 | 907 | 8.0/8 |
| fm-draggable without | 2 | 120 | 5.5 | 0.0 | 3.0 | 75 | 12.0/12 |
| fm-draggable with, as merged | 2 | 156 | 7.5 | 2.0 | 3.5 | 110 | 12.0/12 |
| fm-draggable with, fixed | 2 | 138 | 5.0 | 2.0 | 1.0 | 67 | 12.0/12 |
| fm-toilet without | 2 | 65 | 5.0 | 0.0 | 2.0 | 72 | 2/2 passed |
| fm-toilet with, as merged | 2 | 74 | 5.5 | 2.0 | 0.5 | 99 | 2/2 passed |

| Over | With the index as merged | With the fixed index |
| --- | --- | --- |
| BlackBox, 6 tasks | time ×0.79, tool calls ×0.78, tokens ×0.85 | 3 tasks rerun: time ×0.81, calls ×0.73, tokens ×0.84 |
| FluffyMeow, 4 tasks | time ×1.29, tool calls ×1.11, tokens ×1.56 | 3 tasks rerun: time ×1.21, calls ×0.89, tokens ×1.28 |
| the 6 tasks rerun | time ×1.02, tool calls ×0.91, tokens ×1.13 | time ×0.99, tool calls ×0.81, tokens ×1.03 |

### Claude Opus 5.5

| Task | Runs | Time (s) | Tool calls | Index calls | Shell searches | Prompt tokens (k) | Result |
| --- | --- | --- | --- | --- | --- | --- | --- |
| explain without | 2 | 103 | 19.5 | 0.0 | 9.0 | 692 | 8.0/8 |
| explain with, as merged | 2 | 85 | 18.5 | 2.0 | 5.5 | 737 | 8.0/8 |
| explain with, fixed | 2 | 85 | 17.5 | 2.5 | 3.0 | 600 | 8.0/8 |
| context without | 2 | 53 | 8.5 | 0.0 | 4.5 | 219 | 8.0/8 |
| context with, as merged | 2 | 59 | 12.5 | 3.5 | 2.0 | 308 | 8.0/8 |
| context with, fixed | 2 | 48 | 9.5 | 1.5 | 3.5 | 204 | 8.0/8 |
| repeat without | 2 | 69 | 14.5 | 0.0 | 5.0 | 287 | 8.0/8 |
| repeat with, as merged | 2 | 62 | 11.5 | 1.0 | 3.0 | 199 | 8.0/8 |
| repeat with, fixed | 2 | 59 | 13.0 | 1.0 | 3.5 | 211 | 8.0/8 |
| observers without | 2 | 18 | 2.0 | 0.0 | 2.0 | 48 | 7.0/7 |
| observers with, as merged | 2 | 27 | 5.5 | 1.0 | 2.5 | 89 | 7.0/7 |
| observers with, fixed | 2 | 21 | 3.0 | 1.0 | 1.5 | 71 | 7.0/7 |
| wait without | 2 | 141 | 14.5 | 0.0 | 6.0 | 415 | 2/2 passed |
| wait with, as merged | 2 | 98 | 10.5 | 1.5 | 3.0 | 231 | 2/2 passed |
| wait with, fixed | 2 | 91 | 13.0 | 2.0 | 3.0 | 318 | 2/2 passed |
| rename without | 2 | 59 | 5.0 | 0.0 | 3.0 | 110 | 2/2 passed |
| rename with, as merged | 2 | 54 | 5.5 | 1.0 | 2.5 | 125 | 2/2 passed |
| rename with, fixed | 1 | 55 | 5.0 | 1.0 | 2.0 | 113 | 1/1 passed |
| fm-explain without | 2 | 167 | 21.5 | 0.0 | 15.0 | 1038 | 8.0/8 |
| fm-explain with, as merged | 2 | 154 | 22.5 | 2.5 | 12.0 | 803 | 8.0/8 |
| fm-needs without | 2 | 113 | 9.5 | 0.0 | 7.5 | 384 | 8.0/8 |
| fm-needs with, as merged | 2 | 84 | 20.5 | 3.5 | 4.5 | 399 | 8.0/8 |
| fm-needs with, fixed | 1 | 91 | 17.0 | 3.0 | 3.0 | 305 | 8.0/8 |
| fm-draggable without | 2 | 22 | 2.0 | 0.0 | 1.5 | 44 | 12.0/12 |
| fm-draggable with, as merged | 2 | 30 | 3.5 | 1.0 | 1.0 | 67 | 12.0/12 |
| fm-draggable with, fixed | 1 | 24 | 3.0 | 1.0 | 0.0 | 50 | 12.0/12 |
| fm-toilet without | 2 | 46 | 7.0 | 0.0 | 4.5 | 195 | 2/2 passed |
| fm-toilet with, as merged | 2 | 12 | 3.5 | 1.5 | 0.0 | 69 | 2/2 passed |
| fm-toilet with, fixed | 1 | 12 | 3.0 | 1.0 | 0.0 | 66 | 1/1 passed |

| Over | With the index as merged | With the fixed index |
| --- | --- | --- |
| BlackBox, 6 tasks | time ×0.96, tool calls ×1.18, tokens ×1.03 | time ×0.87, calls ×1.05, tokens ×0.94 |
| FluffyMeow, 4 tasks | time ×0.70, tool calls ×1.16, tokens ×0.81 | 3 tasks: time ×0.62, calls ×1.03, tokens ×0.68 |

The ratios are geometric means of each task's ratio to the runs without the index, so every task
weighs the same however long it is.

## What the runs showed

- **Where the index pays:** questions about how something works, on a project the model doesn't
  know. Without it Qwen listed folders and opened files one by one (22 folder listings in one run
  explaining BlackBox, 48 in one explaining FluffyMeow); with it, the overview and a few searches led
  it to the right files. Renaming a class went faster too: the search named the files that use it.
- **Where it costs:** every run with the index starts with an overview of up to 2,500 tokens, and
  every search adds 1,000 to 2,500 more, which stay in the conversation. LM Studio and Anthropic
  cache what came before, so they aren't read in full again at each step, but they fill the context:
  in the two explain tasks, 5 of Qwen's 8 runs with the index reached 80k tokens, where BlackBox has
  the model write a handoff note and starts afresh (see Context window in docs/notes.md), against 0
  of 4 without it. On a task a grep answers in one call, all of it is overhead: changing the
  toilet's weight took Qwen 65 s
  without the index and 74 s with it, and Claude listed the `IAgentObserver` classes in 18 s
  without it against 27 s with it.
- **Qwen on the big project.** With the index, Qwen found `CharacterStats`, `StatDecay` and the
  decay configs within a minute of the needs question, sooner than without it, but then searched
  again and kept checking (the configs' GUIDs, where the global prefab is loaded) for ten more
  minutes. The answers were no better for it. Explaining the game took 10 to 17 minutes either way.
  Two runs per side can't say whether the index makes Qwen dig deeper or this was chance, but it
  happened on all four FluffyMeow tasks.
- **Claude** used the index as a quick first look, then read a handful of files and searched with
  the shell for exact names, and stopped: fewer tokens on most tasks, and the most time saved on the
  biggest project.

## Fixed along the way

1. **A search for a name lists every line that has it.** The best parts of a search are eight at
   most, two of a file, which isn't every use: asked to list the implementations of an interface or
   to rename a class, both models searched the index and then the shell for the same name. A query
   that writes a name as code (or is one word) now also gets every line with that whole word; past
   25 lines, each file's first such line and where the others are. Listing the `IDraggable` classes
   then took Qwen 5 tool calls instead of 7.5 (5.5 without the index) and Claude 3 instead of 3.5.
2. **The overview's main files are shared out by where the code is.** In FluffyMeow, 17 of the 40
   main files listed were the Spine runtime and DOTween: a library uses its own names over and over,
   so its files rank high. Each part of a project (its first two folders) now gets lines in
   proportion to its code first; FluffyMeow's list became 33 of the game's own scripts and 7 others.
   It didn't make explaining the game faster for Qwen (860 s on average either way).

Both have tests (`FolderIndexTests`), which fail without the fixes.

## Found, not fixed

- **Searches in plain words miss when the code uses other words.** "How do the cat's needs go down
  over time" finds movement code and minigames, because FluffyMeow says "stat" and "decay"; the
  model has to guess the code's words, as both did ("hunger hygiene decay"). Ranking by words, not
  embeddings, was a deliberate choice (see docs/notes.md, Folder index); this is its price.
- **The index's search results are long** (about 1,200 to 2,500 tokens each), which is what makes
  extra searches cost on a slow local model. Fewer lines per part, or shorter paths, would cut that;
  not tried here.
- **BlackBox's automatic web look-ups fire on questions about the local code.** In 13 of the 107
  runs BlackBox searched the web for a question the model asked itself ("where does the VM handle
  OnDelta"), with a folder name from the task as the subject, and added the results under the step.
  They never helped: Qwen once called them "an injected message … containing fake web-search
  results" and said so in its answer, and one Claude run was refused by Anthropic's safety checks
  (`reasoning_extraction`), apparently because the look-up quotes the model's own thinking back to
  it. That run was left out of the averages. This is the look-up feature, not the index.
- **FluffyMeow's repository holds an SSH key and a credentials file** (`Backend/VirtualMachine`) in
  its git history. They were left out of the copies the models saw and weren't opened.

## Limits

- Two runs per task and condition; Qwen's times for the same task varied by up to four times, so
  the per-task numbers are rough and only the averages over several tasks mean much.
- Ten tasks on two projects, all C#. Both models answered nearly everything fully either way, so
  these tasks can't show whether the index helps a model that would otherwise get things wrong.
- One grader, a Claude model, blind to which run had the index but not to the model's style.
- The runs without the index still had the shell (`Select-String`, `Get-ChildItem -Recurse`), which
  both models used well; the index competes with a good grep, not with nothing.
- Qwen ran over the VPN to the PC's LM Studio; its times include the network and LM Studio's cache.

## Running it again

```
./scripts/index-bench.ps1 -Settings .dev\index-bench\qwen\Data\settings.json -Label qwen -Runs 2
./scripts/index-bench-blind.ps1 -Labels qwen -Name qwen      # then grade into <task>\scores.json
./scripts/index-bench-blind.ps1 -Name qwen -Scores
./scripts/index-bench-report.ps1 -Model Qwen -Without qwen -With qwen -Judge qwen
```

`-Cli` runs a build kept from before, `-Conditions with` only the indexed runs, `-Tasks` some of
them, `-Parallel` several at once (for a hosted model), `-Rescore` scores runs whose scoring
failed. Runs, logs, answers and diffs go to `%USERPROFILE%\IndexBench`. The settings file holds the
server, the model and, for a hosted one, its key: keep it in `.dev`, which git ignores.
