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
  Data/        settings.json, runtime/ (unpacked by the single-file exe)
```

Set `BLACKBOX_HOME` to use a different root. Debug builds use `.dev/` in the repository.
The agent itself works across the whole PC; relative paths and shell commands start in your home folder.

## Connecting to a model
Enter the server address in the bar at the top of the main window (LM Studio's default is
`http://localhost:1234/v1`); the model dropdown fills in from the models the server reports. Any
form of the address works: `host:1234`, with or without `/v1`, or a full endpoint link such as
`.../v1/chat/completions`. Without `http://` or `https://`, BlackBox tries both and keeps the one
that answers. An API
key, if your server needs one, goes in Settings. For screen control the
model must accept images (for example Qwen3.5+ VL models or Gemma 4 with their vision projector);
LM Studio reports this and BlackBox sets it automatically.

BlackBox sends no sampling parameters: temperature, top-k/p, reply length and thinking come from
the server's own model configuration (in LM Studio, per model).

Thinking models get their earlier reasoning sent back each step (`reasoning_content`,
`preserve_thinking`), as the Qwen3.5+ and Gemma 4 templates expect.

## Context window
The OpenAI API has no field for the context length, so BlackBox asks each server its own way:
LM Studio (`/api/v0/models/{id}`, `loaded_context_length`, after loading the model if needed),
llama-server (`/props`, `n_ctx`), Ollama (`/api/ps`, or `num_ctx` from `/api/show`), vLLM
(`max_model_len`) and OpenRouter (`context_length`); the first that answers wins. Servers that
don't say are assumed to have 100k. If that is too much, the server's "too long" error usually
names the real size, and BlackBox switches to it. BlackBox uses the whole context.

Until the context is nearly full (a fifth left, at least 16k), the conversation only grows at the
end, so the server reuses its cache and each step processes only the new messages. Then BlackBox
does a **context reset**: the model writes a handoff note for itself on the cached history (it
streams at once), and the conversation is replaced by your messages, that note and the current
screenshot, a few thousand tokens. When to reset is decided from the token counts the server
reports, not guessed. If the model can't write the note, a record of its last steps is used
instead; a reply cut off by the context limit, or a server's "context length exceeded" error,
triggers the same reset and a retry.

### Unstable connections
VPNs and proxies often cut a connection that has been silent for about a minute, which happens
while a server processes a long prompt. BlackBox resends the identical request when a connection
drops or a reply stream stops early; the server still has the already-processed prompt in its
cache, so each retry continues where the last one stopped (up to 12 times per step).
## Tools the agent has
| Tool | What it does |
| --- | --- |
| `screenshot` | Captures a screen, scaled to fit the chosen resolution (1920×1080 by default) on the 32px vision grid; `screen=N` looks at another monitor |
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

With several monitors the agent sees one at a time: screenshots show the screen it looked at last
(the primary at first), `screen=N` switches, and `focus_window` follows a window to its screen. All
coordinates refer to the screen in the latest screenshot.

Actions run without asking by default (approvals can be switched on in Settings → General).
Pressing Stop, pressing the Break (Pause) key anywhere, or moving the mouse to the top-left corner of
the primary screen halts the agent. The Break key works whatever window has focus; the agent's own
key presses can't trigger it. BlackBox
stays on screen while it works but is hidden from the agent's screenshots, and steps behind other
windows for the moment a click lands on it. Every click and key press the agent makes shows up in a
log in the bottom-right corner that fades after 15 seconds (adjustable), with a ring where it clicked. The agent
can't see it and it never takes focus (turn it off in Settings → Advanced).

## Build
Requires the .NET 10 SDK.

```powershell
dotnet build                      # debug build; keeps its Data/ in .dev/
dotnet test                       # unit tests
./scripts/publish.ps1             # single-file publish/BlackBox.exe
./scripts/publish.ps1 -Folder     # classic self-contained folder in publish/BlackBox-win-x64
dotnet run --project src/BlackBox.Cli -- --yes "open notepad and type hello"   # headless run
./scripts/agent-tasks.ps1         # real desktop task suite (drives your mouse and keyboard)
./scripts/game2048-test.ps1       # long run: BlackBox writes, play-tests and fixes its own 2048
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
- `scripts/`: `publish.ps1` (portable build), `agent-tasks.ps1` (real desktop task suite) and `game2048-test.ps1` (long self-testing run, judged by `game2048-check.mjs`).

Linux and macOS ports need an `IPlatformServices` implementation (screen, input, windows); the rest
of the code is already cross-platform.