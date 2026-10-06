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
  Data/        settings.json, runtime/ (unpacked by the single-file exe), browser/ (see Web search)
```

Set `BLACKBOX_HOME` to use a different root. Debug builds use `.dev/` in the repository.
The agent itself works across the whole PC; relative paths and shell commands start in your home folder.

## Connecting to a model
Enter the server address in the bar at the top of the main window (LM Studio's default is
`http://localhost:1234/v1`); the model dropdown fills in from the models the server reports. Any
form of the address works: `host:1234`, with or without `/v1`, or a full endpoint link such as
`.../v1/chat/completions`. Without `http://` or `https://`, BlackBox tries both and keeps the one
that answers. An API key, if your server needs one, goes next to the address (the eye button
shows it). For screen control the
model must accept images (for example Qwen3.5+ VL models or Gemma 4 with their vision projector);
LM Studio reports this and BlackBox follows it; models on other servers are assumed to see images.

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

The **Context** slider in the top bar shows that size while **Auto** is ticked ("not loaded" until LM Studio has loaded
the model, which your first message does; a `?` means the server doesn't say and it is the 100k guess). Untick Auto
to set the size yourself: the slider stops at 4k to 1M in coarser steps as it grows (4k, 8k, 12k,
16k, 24k, 32k, 40k, 48k, 64k, 80k, 96k, 128k, 160k, 192k, 256k, 320k, 384k, 512k, 640k, 768k,
1M) and ends at the server's own size when the server reports one. The box next to it takes any
size typed in (`48k`, `100000`, `1M`; Enter or leaving the box applies it), past the slider's end
too; the server refuses requests longer than its own size, and a run that hits that drops to the
size the server names. The choice is saved; it only changes BlackBox's budgeting and when it
resets, and does not reload the model on the server.

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
| `screenshot` | Captures a screen at full resolution, or scaled to fit the chosen size, on the 32px vision grid; `screen=N` looks at another monitor |
| `mouse_click` | Clicks a named target where a fresh look at the screen finds it (see below) |
| `mouse_move`, `mouse_drag`, `scroll` | Mouse control in screenshot coordinates |
| `type_text`, `press_keys` | Unicode typing and key chords (`ctrl+c`, `win+r`, …) |
| `wait` | Waits, then screenshots |
| `read_file`, `write_file`, `edit_file`, `list_directory` | File work |
| `run_shell` | PowerShell on Windows, `sh` elsewhere |
| `web_search` | Searches the web: titles, links and snippets (see below) |
| `fetch_page` | Reads a web page as plain text; `find` returns only the passages that mention some words, `start` reads on in a long page |
| `<server>__<tool>` | Any tool of the MCP servers you add in Settings → Advanced (see below) |

Models often fumble tool-call syntax, so BlackBox repairs calls on the fly: Qwen XML, Gemma 4 and
JSON calls printed as text, broken JSON, aliased tool names (`click`, `bash`…) and misnamed
arguments (`coordinate: [x, y]`) are all accepted, and a model that announces a step without taking
it gets nudged to continue.

With several monitors the agent sees one at a time: screenshots show the screen it looked at last
(the primary at first) and `screen=N` switches. All coordinates refer to the screen in the latest
screenshot.

Coordinates follow what the model was trained on. Qwen3 and later point on a 0-1000 grid over the
image, so for them x/y are on that grid; asked for pixels, qwen3.8 slipped back to the grid in about
a third of its clicks and missed by hundreds of pixels (11/16 hits against 16/16 on the grid in
`ClickAccuracyTests`). Every other model gets screenshot pixels.

Screenshots go out at full resolution by default. In LM Studio, turn off the model's "Image Input:
Never exceed 2048 px" option, or it shrinks them back to about 2048×864 (qwen3.8 hit text links
15/16 at full resolution, 8/16 at 1920×1080). For models that point in pixels, keep that option and the screenshot size in
step, since a server that resizes the image moves every click. A `settings.json` saved before full
resolution became the default keeps 1920×1080; choose "Full screen resolution" in Settings.

Every click is aimed by a fresh look. `mouse_click` names its `target` ("File menu", "Save button
in the Export dialog"); BlackBox takes a new screenshot and asks the model, in a separate request
with only that screenshot, where the target is, and clicks there. The x/y the model gives are used
only when the look can't find the target, or when the model leaves `target` out to click a spot
that has no name. Deep in a long run the model settles on wrong ideas of where things are (in a
Unity run: "the menu bar is at y≈3", three times, 28 px above the menus) and copies its previous
call's coordinates after writing the right ones, while the same model looking at the screen afresh
gets them right. In `ClickAccuracyTests` (qwen3.8, 79 trials in 16 scenes) first clicks hit 69 times
against 57 with the model's own coordinates, and 19 of 19 menus, tabs and buttons on a real Unity
screenshot (`ClickScenes/unity-editor.png`) against 10; with the first screenshot shifted 30 px, as
a misjudged screen would be, 30 of 31 against 0. The look costs about 15 s per click on that server.

After each click BlackBox also compares the screen before and after it. When nothing changed near
the click and nothing big changed anywhere (a dialog opening), the click almost certainly missed,
and the result says so. A fresh look that misses misses the same way again, so the note tells the
model to click again with `target` left out and its own x/y. On the scenes where looks miss (a dense
table, half-covered buttons, an ultrawide screen) 4 of 9 misses were fixed within two more clicks
with the note, 0 of 8 without it, and 4 of 10 when every click used the model's own coordinates.
Before the fresh look, a red ring drawn at the click added nothing to the note, nor did a prompt
rule about aiming carefully or temperature 0.2 instead of 0.6.

### Web search
A local model's memory of APIs is where it slips most (qwen3.8 writes Unity's `KeyCode.Left`, which
doesn't exist, for `KeyCode.LeftArrow`), so the agent can look things up, and the system prompt
tells it to whenever it isn't sure of an exact name or meets an error it doesn't understand. Given
Unity's CS0117 error for `KeyCode.Left`, qwen3.8 searched, read the `KeyCode` page with
`find=Arrow` and fixed the file in 55 s.

No API key and nothing to install. `web_search` asks DuckDuckGo's HTML page through the system's
`curl` (built into Windows 10 and later; a fraction of a second): DuckDuckGo recognizes .NET's own
HTTP client by its TLS handshake and answers it with a captcha, even on the first search.
DuckDuckGo also refuses a computer that searched about ten times in a few minutes; then, for the
next 10 minutes, or when it finds nothing, Bing is searched in the PC's own Edge or Chrome run
headless (one to three seconds). Bing can't be asked directly: to a plain request it answers with
results for the first word only; Brave, Mojeek and Startpage answer with a captcha.

`fetch_page` keeps only a page's main content (no menus, sidebars, cookie banners or footers) and
writes it as text: `#` headings, `-` lists, fenced code blocks, `| a | b |` table rows; links keep only
their text. A page comes in parts of 6,000 characters (about 1.5k tokens), so a long page can't
flood the context, and `find` returns just the matching lines with a little context and the heading
they sit under. A page that turns away plain requests, or that its scripts build, is loaded in the
headless browser instead. PDFs aren't read. The last eight pages are kept in memory, so reading on
doesn't download a page again. The browser's profile is `Data/browser`. Turn web access off in
Settings → General.

### MCP servers
BlackBox is an MCP client: any [Model Context Protocol](https://modelcontextprotocol.io) server adds its
tools to the list above. Paste the `mcpServers` block from the server's README into Settings → Advanced;
it is the same format Claude Desktop, LM Studio and Cursor use, and it is kept in `Data/mcp.json`:

```json
{
  "mcpServers": {
    "filesystem": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "C:\\Users\\me\\Documents"] },
    "remote":     { "url": "https://example.com/mcp", "headers": { "Authorization": "Bearer …" } }
  }
}
```

Servers with a `command` are started with BlackBox (and stopped with it); `env` and `cwd` are passed
on. Servers with a `url` are connected to over Streamable HTTP, or the older SSE protocol when the
server only speaks that (`"type": "sse"` forces it). `"disabled": true` keeps an entry without using
it. The model sees each tool as `server__tool` (`filesystem__read_file`), so names never clash with
BlackBox's own tools; a call to the bare name still works when only one server has it. Tools a
server marks read-only are treated like screenshots in the approval settings, all others like shell
commands. Text results go back to the model as they are; an image result is shown to it like a
screenshot. Comments and trailing commas in the file are fine. The settings dialog shows what each
server reported when it started, and a server that fails to start is reported in the chat; the
others keep working.

Actions run without asking by default (approvals can be switched on in Settings → General).
Pressing Stop, or the Break (Pause) key anywhere, halts the agent. The Break key works whatever
window has focus; the agent's own key presses can't trigger it. BlackBox
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
dotnet test --filter ClickAccuracyTests   # click hit rate in 15 hard scenes; BB_CLICK_SCENES, BB_CLICK_TRIALS, BB_CLICK_COORDS=pixels|grid
```

## Project structure
- `src/BlackBox.Core`: OpenAI-compatible streaming client, server probe, agent loop, tools,
  settings. Platform-neutral.
- `src/BlackBox.Platform.Windows` (net10.0-windows): screen capture (GDI), input (SendInput),
  host-window handling. Only referenced when building on Windows.
- `src/BlackBox.Cli`: headless runner for trying tasks and models.
- `src/BlackBox.App`: Avalonia 11 UI with CommunityToolkit.Mvvm.
- `tests/BlackBox.Core.Tests`: xUnit tests (unit tests plus opt-in server integration tests).
- `tests/BlackBox.McpTestServer`: a small MCP server the tests run in-process and as a child process.
- `scripts/`: `publish.ps1` (portable build), `agent-tasks.ps1` (real desktop task suite) and `game2048-test.ps1` (long self-testing run, judged by `game2048-check.mjs`).

Linux and macOS ports need an `IPlatformServices` implementation (screen, input, windows); the rest
of the code is already cross-platform.