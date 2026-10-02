# BlackBox
Local AIO AI for doing any kind of work on your desktop.

BlackBox is a portable desktop agent: you describe a task in the chat, and a local model looks at the
screen, moves the mouse, types, runs shell commands and edits files step by step until the task is done.
It runs fully offline on a local GGUF model through llama.cpp, or against any OpenAI-compatible API.

## Portable layout
No installer, no registry, nothing in AppData or %TEMP%. Ship just `BlackBox.exe`: on first run it
unpacks its native libraries and llama.cpp into `Data/runtime` next to itself and offers to download
the model into `Models/`. Everything lives in that one folder you can move or copy:

```
BlackBox/
  BlackBox.exe
  Models/      Qwen3.8-27B-UD-IQ2_S.gguf, mmproj-F16.gguf
  llama/       llama-server and its DLLs (optional if llama-server is on PATH)
  Data/        settings.json, logs/, runtime/ (unpacked by the single-file exe)
  Workspace/   default working folder for file and shell tools
```

Paths inside the folder are stored relative, so the folder keeps working from another drive or PC.
Set `BLACKBOX_HOME` to use a different root.

## Model
Default target: `unsloth/Qwen3.8-27B-GGUF`, file `Qwen3.8-27B-UD-IQ2_S.gguf` (7.8 GB) plus
`mmproj-F16.gguf` (0.9 GB) so the model can see screenshots. On first run BlackBox offers to
download both into `Models/` (resumable); you can also drop them there yourself.

Defaults are tuned for a 16 GB GPU and 32 GB RAM: all layers on the GPU (`-ngl 99`), 80k context,
flash attention, q8_0 KV cache, `--jinja` for native tool calling. If the context does not fit,
BlackBox steps it down automatically (64k, 48k, 32k…) and says so.
Sampling: temperature 0.1, top-k 40, top-p 0.95, min-p 0.05, no repetition penalty.
Everything is adjustable under Settings → Advanced. Without the mmproj file the model runs
text-only and the screen/mouse tools are turned off.

Small quantized models often fumble tool-call syntax, so BlackBox repairs calls on the fly:
Qwen XML or JSON calls printed as text, broken JSON, aliased tool names (`click`, `bash`…) and
misnamed arguments (`coordinate: [x, y]`) are all accepted, and a model that announces a step
without taking it gets nudged to continue.

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

Actions run without asking by default (approvals can be switched on in Settings → General).
Moving the mouse to the top-left corner of the screen, or pressing Stop, halts the agent. BlackBox
minimizes itself while it works and comes back when it is done.

## Build
Requires the .NET 10 SDK.

```powershell
dotnet build                      # debug build; uses the repo root as the portable folder
dotnet test                       # unit tests
./publish.ps1                     # single-file publish/BlackBox.exe (llama.cpp embedded)
./publish.ps1 -Folder             # classic self-contained folder in publish/BlackBox-win-x64
dotnet run --project src/BlackBox.Cli -- --yes "open notepad and type hello"   # headless run
```

## Project structure
- `src/BlackBox.Core`: OpenAI-compatible streaming client, llama-server host, agent loop, tools,
  settings. Platform-neutral.
- `src/BlackBox.Platform.Windows` (net10.0-windows): screen capture (GDI), input (SendInput), windows
  (EnumWindows), UI Automation (FlaUI), job object that kills llama-server with the app. Only
  referenced when building on Windows.
- `src/BlackBox.Cli`: headless runner for trying tasks and models.
- `src/BlackBox.App`: Avalonia 11 UI with CommunityToolkit.Mvvm.
- `tests/BlackBox.Core.Tests`: xUnit tests.

Linux and macOS ports need an `IPlatformServices` implementation (screen, input, windows); the rest
of the code is already cross-platform.
