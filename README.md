# Sloperator
Sloperator works on your PC to get the job done. Say what you want and watch it go.\
It uses the actual desktop via keyboard and mouse when needed.

It is a single portable `Sloperator.exe`. Nothing gets installed.\
Its settings, chats and whatever it downloads stay in a `Data` folder next to the exe.

https://github.com/user-attachments/assets/8a6d325e-0564-483e-95d7-42900efc6903

## Getting started
1. Download `Sloperator.exe` from [Releases](https://github.com/forcepusher/Sloperator/releases) and
   put it in whatever folder you like (not Program Files).
2. Double-click it. If Windows says "Windows protected your PC", click **More info**, then
   **Run anyway**.
3. Type what you want and press Enter.

**Pause/Break** stops it at any time. It doesn't ask before it acts; to change that see Settings.
Turn on **Notify me when done** there to get a Windows notification when a task needs you.

## Connecting a model
- **Free, no account:** out of the box it runs on Kilo's free online models. They allow about 200
  requests an hour.
- **Claude:** type `api.anthropic.com` as the server and your Anthropic API key next to it.
- **Your own PC:** in [LM Studio](https://lmstudio.ai), turn its server on in the Developer tab and type
  `http://localhost:1234/v1` as the server. Pick a model that sees images (Qwen3.8 27B is the one
  tested most), give it at least 64k of context, and turn off its "Image Input: Never exceed 2048 px"
  option. llama.cpp, Ollama and vLLM work the same way.
- **Other online APIs:** any OpenAI-compatible server, such as [OpenRouter](https://openrouter.ai): its address and your key.

Sloperator picks the strongest model the server has, or you pick one from the list. Every server
that answered is under **Servers**, with its key and model, to switch back to.

If you still have no idea what to do, you can ask the free model to connect another model for you.\
Same with MCPs, tell Sloperator what you want to be connected and it'll be done.

## Building (you don't have to, download from [Releases](https://github.com/forcepusher/Sloperator/releases))
You need Windows and the .NET 10 SDK.

```powershell
dotnet build             # debug build, keeps its Data in .dev/
dotnet test              # unit tests
./scripts/publish.ps1    # makes publish/Sloperator.exe
```

Details are in [docs/notes.md](docs/notes.md), which is meant to be read by LLMs, not people.
