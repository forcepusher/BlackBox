# Sloperator
Sloperator works on your PC to get the job done. Say what you want and watch it go. It uses the
actual desktop via keyboard and mouse when needed.

It is a single portable `Sloperator.exe`. Nothing gets installed: its settings, chats and whatever it
downloads stay in a `Data` folder next to the exe.

[![Sloperator answering its first message (click to play)](docs/demo-poster.png)](docs/demo.mp4)

## Getting started
1. Download `Sloperator.exe` from [Releases](https://github.com/forcepusher/Sloperator/releases) and
   put it in a folder of your own (Documents, the Desktop, a USB stick), not in Program Files.
2. Double-click it. If Windows says "Windows protected your PC", click **More info**, then
   **Run anyway**.
3. Pick a model in the bar at the top (below), type what you want done and press Enter.

**Pause/Break** stops it at any time. It doesn't ask before it acts; to approve each step, turn on
"Ask me first" in Settings.

## Picking a model
- **Free, no account:** out of the box it runs on Kilo's free online models. They allow about 200
  requests an hour, so a long job may stop to wait, and your screenshots go to their servers.
- **Claude:** type `api.anthropic.com` as the server and your Anthropic API key next to it.
- **Your own PC:** in [LM Studio](https://lmstudio.ai), turn its server on in the Developer tab and type
  `http://localhost:1234/v1` as the server. Pick a model that sees images (Qwen3.8 27B is the one
  tested most), give it at least 32k of context, and turn off its "Image Input: Never exceed 2048 px"
  option. llama.cpp, Ollama and vLLM work the same way.
- **Other online APIs:** any OpenAI-compatible server, such as OpenRouter: its address and your key.

Sloperator picks the strongest model the server has, or you pick one from the list.

## Building
You need Windows and the .NET 10 SDK.

```powershell
dotnet build             # debug build, keeps its Data in .dev/
dotnet test              # unit tests
./scripts/publish.ps1    # makes publish/Sloperator.exe
```

How each part works and why, the test scripts and the benchmark results are in
[docs/notes.md](docs/notes.md).
