# BlackBox
Local AIO AI for doing any kind of work on your desktop.

BlackBox is a portable desktop agent: you describe a task in the chat, and an AI model looks at the
screen, moves the mouse, types, runs shell commands and edits files step by step until the task is
done. The model runs on any OpenAI-compatible server: LM Studio (on this PC or another machine on
your network), llama.cpp's llama-server, vLLM, or a hosted API.

## Portable layout
No installer, no registry, nothing in AppData or %TEMP%. Ship just `BlackBox.exe`: on first run it
unpacks its native UI libraries into `Data/runtime` next to itself.

```
BlackBox/
  BlackBox.exe
  Data/        settings.json, logs/, runtime/ (unpacked by the single-file exe)
  Workspace/   default working folder for file and shell tools
```

Set `BLACKBOX_HOME` to use a different root. Debug builds use `.dev/` in the repository.

## Connecting to a model
Settings → General → AI model server. Enter the server address (LM Studio's default is
`http://localhost:1234/v1`), press **Test connection** and pick a model. For screen control the
model must accept images (for example Qwen3.5+ VL models or Gemma 4 with their vision projector).

Sampling defaults: temperature 0.1, top-k 40, top-p 0.95, min-p 0.05, no repetition penalty.
top-k, min-p, repetition penalty and the chat-template thinking switches are extras that LM Studio
and llama.cpp understand; if a stricter server rejects them, BlackBox drops them automatically.

Thinking models get their earlier reasoning sent back each step (`reasoning_content`,
`preserve_thinking`), as the Qwen3.5+ and Gemma 4 templates expect.

## Context window
The OpenAI API has no field for the context length, so BlackBox asks each server its own way:
LM Studio (`/api/v0/models/{id}`, `loaded_context_length`, after loading the model if needed),
llama-server (`/props`, `n_ctx`), vLLM (`max_model_len`) and OpenRouter (`context_length`).
Otherwise the size in Settings is used. Each request is also capped at 64k tokens by default
(Settings → Advanced) so steps stay fast.

When a run outgrows that budget, BlackBox drops old screenshots and old thinking, then has the
model **summarize the middle of the run** (the original task and the latest steps stay verbatim).
Summaries are written in ~8k-token chunks, so each request stays short. If summarizing fails, old
tool output and then whole middle steps are trimmed instead, and a server's "context length
exceeded" error triggers the same trimming and a retry.

### Unstable connections
VPNs and proxies often cut a connection that has been silent for about a minute, which happens
while a server processes a long prompt. BlackBox resends the identical request when a connection
drops or a reply stream stops early; the server still has the already-processed prompt in its
cache, so each retry continues where the last one stopped (up to 12 times per step).
## Tools the agent has
| Tool | What it does |
| --- | --- |
| `screenshot` | Captures the primary screen, downscaled to ~1.3 MP on the 32px vision grid |
| `zoom` | Magnified view of a region with rulers labelled in screenshot coordinates |
| `list_ui_elements`, `click_element` | Windows UI Automation: find controls by name and press them (no pixel aiming) |
| `mouse_click`, `mouse_move`, `mouse_drag`, `scroll` | Mouse control in screenshot coordinates |
| `type_text`, `press_keys` | Unicode typing and key chords (`ctrl+c`, `win+r`, …) |
| `wait` | Waits, then screenshots |
| `list_windows`, `focus_window` | Window discovery and activation |
| `read_file`, `write_file`, `edit_file`, `list_directory` | File work |
| `run_shell` | PowerShell on Windows, `sh` elsewhere |

Models often fumble tool-call syntax, so BlackBox repairs calls on the fly: Qwen XML, Gemma 4 and
JSON calls printed as text, broken JSON, aliased tool names (`click`, `bash`…) and misnamed
arguments (`coordinate: [x, y]`) are all accepted, and a model that announces a step without taking
it gets nudged to continue.

Actions run without asking by default (approvals can be switched on in Settings → General).
Moving the mouse to the top-left corner of the screen, or pressing Stop, halts the agent. BlackBox
stays on screen while it works but is hidden from the agent's screenshots, and steps behind other
windows for the moment a click lands on it.

## Build
Requires the .NET 10 SDK.

```powershell
dotnet build                      # debug build; keeps its Data/ and Workspace/ in .dev/
dotnet test                       # unit tests
./scripts/publish.ps1             # single-file publish/BlackBox.exe
./scripts/publish.ps1 -Folder     # classic self-contained folder in publish/BlackBox-win-x64
dotnet run --project src/BlackBox.Cli -- --yes "open notepad and type hello"   # headless run
./scripts/agent-tasks.ps1         # real desktop task suite (drives your mouse and keyboard)
```

Integration tests against a real server (they send prompts larger than the context):

```powershell
$env:BB_SERVER_URL = "http://localhost:1234/v1"; $env:BB_SERVER_MODEL = "qwen3.8-27b@iq2_s"
dotnet test --filter ServerIntegrationTests
```

## Project structure
- `src/BlackBox.Core`: OpenAI-compatible streaming client, server probe, agent loop, tools,
  settings. Platform-neutral.
- `src/BlackBox.Platform.Windows` (net10.0-windows): screen capture (GDI), input (SendInput), windows
  (EnumWindows), UI Automation (FlaUI), host-window handling. Only referenced when building on Windows.
- `src/BlackBox.Cli`: headless runner for trying tasks and models.
- `src/BlackBox.App`: Avalonia 11 UI with CommunityToolkit.Mvvm.
- `tests/BlackBox.Core.Tests`: xUnit tests (unit tests plus opt-in server integration tests).
- `scripts/`: `publish.ps1` (portable build) and `agent-tasks.ps1` (real desktop task suite).

Linux and macOS ports need an `IPlatformServices` implementation (screen, input, windows); the rest
of the code is already cross-platform.