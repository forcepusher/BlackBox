# BlackBox
Local AIO AI for doing any kind of work on your desktop.

BlackBox is a portable desktop agent: you describe a task in the chat, and a local model looks at the
screen, moves the mouse, types, runs shell commands and edits files step by step until the task is done.
It runs fully offline on a local GGUF model through llama.cpp, or against any OpenAI-compatible API.

## Portable layout
No installer, no registry, nothing in AppData. Everything lives in one folder you can move or copy:

```
BlackBox/
  BlackBox.exe
  Models/      Qwen3.8-27B-UD-IQ2_S.gguf, mmproj-F16.gguf
  llama/       llama-server and its DLLs (optional if llama-server is on PATH)
  Data/        settings.json, logs/
  Workspace/   default working folder for file and shell tools
```

Paths inside the folder are stored relative, so the folder keeps working from another drive or PC.
Set `BLACKBOX_HOME` to use a different root.

## Model
Default target: `unsloth/Qwen3.8-27B-GGUF`, file `Qwen3.8-27B-UD-IQ2_S.gguf` (7.8 GB) plus
`mmproj-F16.gguf` (0.9 GB) so the model can see screenshots. Put both in `Models/`.

Defaults are tuned for a 16 GB GPU: all layers on the GPU (`-ngl 99`), 32k context, flash attention,
q8_0 KV cache, `--jinja` for native tool calling. Change them under Settings → Model.
Without the mmproj file the model runs text-only and the screen/mouse tools are turned off.

## Tools the agent has
| Tool | What it does |
| --- | --- |
| `screenshot` | Captures the primary screen, downscaled to fit 1280x800 |
| `mouse_click`, `mouse_move`, `mouse_drag`, `scroll` | Mouse control in screenshot coordinates |
| `type_text`, `press_keys` | Unicode typing and key chords (`ctrl+c`, `win+r`, …) |
| `wait` | Waits, then screenshots |
| `list_windows`, `focus_window` | Window discovery and activation |
| `read_file`, `write_file`, `edit_file`, `list_directory` | File work |
| `run_shell` | PowerShell on Windows, `sh` elsewhere |

Safety: by default BlackBox asks before shell commands and file changes. Moving the mouse to the
top-left corner of the screen, or pressing Stop, halts the agent.

## Build
Requires the .NET 10 SDK.

```powershell
dotnet build                      # debug build; uses the repo root as the portable folder
dotnet test                       # unit tests
./publish.ps1 -BundleLlama        # self-contained portable folder in publish/BlackBox-win-x64
```

## Project structure
- `src/BlackBox.Core`: OpenAI-compatible streaming client, llama-server host, agent loop, tools,
  settings. Platform-neutral.
- `src/BlackBox.Platform.Windows`: screen capture (GDI), input (SendInput), windows (EnumWindows),
  job object that kills llama-server with the app.
- `src/BlackBox.App`: Avalonia 11 UI with CommunityToolkit.Mvvm.
- `tests/BlackBox.Core.Tests`: xUnit tests.

Linux and macOS ports need an `IPlatformServices` implementation (screen, input, windows); the rest
of the code is already cross-platform.
