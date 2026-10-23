# Changelog
All notable changes to BlackBox are written down in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]
### Added
- Folder index: before it works on a project or a big folder, BlackBox indexes it by itself, as
  Cursor does: what kind of project it is, its README, its folders and what its main files define,
  and a search that finds where anything is by names or by what the code does. Indexes are kept in
  `Data\index` and bring themselves up to date on every use.
- Several BlackBox windows at once: **New window** at the top, Ctrl+Shift+N, or starting BlackBox
  again opens another window with a chat and an agent of its own, a step down and to the
  right of the one in front. The windows take turns with the mouse and keyboard, stay out of each
  other's screenshots, all pause while you type in any of them, and one Pause/Break stops them all.
  A chat open in one window brings that window forward when picked in another's History, and what
  Settings saves applies to every window; each keeps its own model.
- Filmmaking: a window that lays a film out on one timeline (its characters and places, its shots
  and its sound) and makes it shot by shot with ComfyUI. Say in words what to make or change, for
  one piece or the whole film, and BlackBox writes the prompts, picks the models, makes it and looks
  at it; earlier takes are kept. Or ask for a film in the chat.
- Films get songs about a minute long (the best of three, judged by SongEval), spoken lines in one
  cloned voice with lip sync, and fresh looks at every shot, cut and the whole film that catch
  broken bodies, frozen or jumping action, a camera that moves when it shouldn't and a change of
  style, and make the pieces again.
- Measured movement in short clips (which way the camera and what is in the picture moved, and how
  far) for every look the agent takes at a video.
- A guide to writing video prompts that LTX follows, given with LTX workflows, and a first frame
  made by an image model for every video.
- Look-ups: BlackBox searches the web for the questions and doubts the model has about how
  something is done and for the errors programs raise, and puts what it found under the step; ten
  minutes or eight tries with nothing looked up make it time to look.
- Claude through Anthropic's own API: type `api.anthropic.com` (or just `claude`) and an API key.
  Prompt caching, thinking kept between steps, Claude Opus 5.5 unless another is picked, with its
  whole 1M context on Auto.
- An effort dropdown next to the model, medium unless picked, for models whose server says they
  take one.
- Messages sent while BlackBox works wait above the box until the model reads them: × takes one
  back, and **Interrupt the model** (or Ctrl+Enter) has it read at once.
- A full-screen viewer for the pictures and videos in the chat and in Filmmaking, with the others
  an arrow key away. Videos in the chat play by themselves, muted, while they are on screen.
- **Prompt** under a picture made with ComfyUI or AUTOMATIC1111 shows the prompts and settings it
  was made with, read back from the file.
- Links in the chat open in the browser.
- The window's title names its chat.
- A sysadmin benchmark ticket that moves a live game API's database from SQLite to PostgreSQL while
  players keep writing to it.
- This changelog.

### Changed
- Filmmaking is an add-on of the chat it was opened from: what you tell it, and what its buttons ask
  for, go to that chat as messages, where you watch BlackBox do them, step by step, and can stop
  them. The window shows what the chat is doing and the film as it changes, opens beside the chat
  when a screen has room, and names the chat in its title. **Make what's missing** and **Make the
  whole film** are one button now, **Make the film**.
- A frame the film tool makes gets a fresh look, and every piece it makes again keeps the one before
  as a take, as the Filmmaking window did; a shot's or song's other tries are takes too.
- The context slider goes up to 1M.
- A click away from a piece in Filmmaking, or Esc, goes back to the whole film.

### Removed
- The conversation beside each piece in Filmmaking: what was said about a piece is in the chat now.

### Fixed
- A click of the agent's that lands on the Filmmaking window or a dialog of BlackBox's goes to what
  is behind it, as one on the chat window always did; keys meant for another program no longer
  land in them.
- Links whose address holds parentheses (Wikipedia's) open whole, and an address followed by
  Chinese or Japanese punctuation no longer takes it in.
- In Filmmaking, a click on the picked piece's own view (a frame not made yet) keeps it picked, and
  a soundtrack that is a video opens full screen.
- A film's camera is said to hold still only when that was measured: a pan there and back, or a
  plain night sky, no longer reads as a still camera.
- Look-ups: a search the model means to make after mentioning the screen is made; the words between
  two quoted names aren't searched; a request that is all research no longer gets told to ask the
  installed version.
- Claude through OpenRouter or Kilo no longer gets thinking turned on by the effort, which broke
  its tool calls.
- A Claude request bigger than the account's input tokens a minute no longer waits for ever: the
  context gets smaller.
- A chat whose window was closed while BlackBox worked says so when opened again, with Continue.
- A ComfyUI BlackBox started is no longer closed at the end of a task while another BlackBox (the
  command line) may be between two of its jobs.
- The sysadmin migration ticket finds backups written under /var/lib and /var/local too.
- The note to index a project no longer comes back with every message of a chat that indexed it.
- Messages sent while a task starts up are no longer stuck above the box, or sent on without the
  first one, when the start fails; a chat left for another while BlackBox works says it stopped,
  with Continue, when opened again, and a task that has just ended keeps its end when another chat
  is opened at once.
- Several windows: once a click is held back because another window's agent changed the screen,
  the rest of that step's clicks and keys are held back too, instead of typing into the other
  window's program, and a window's next task looks again first when another window acted meanwhile;
  saving Settings in one window no longer undoes what another window saved while the dialog was
  open; closing a window right after picking another chat no longer marks that chat stopped; a
  window closed while its MCP servers restarted no longer leaves them running.
- Filmmaking: a request still waiting when BlackBox is stopped, or another chat is opened, is no
  longer put in the chat's box without the film and piece it was about (the chat says it wasn't
  done), and Continue sends one the model never had whole; chats begun from the window are named
  after the film and piece too, not "Make it." alone; the storyboard guide comes back after the
  context starts afresh; a stopped shot of several takes no longer keeps a copy of its clip as one
  more take; a piece made from the window is made again when its look finds something wrong, up
  to twice, as before it ran in the chat; a change made by hand while BlackBox changes the
  storyboard in the chat no longer writes over BlackBox's change.
- Folder index: projects in OneDrive folders are indexed (only their top folder was), files kept
  online only aren't downloaded, linked files are read as the files they point to, a search that
  finds nothing says what the index doesn't search, big workbooks are read one at a time, and an
  index file damaged on disk is built again.
- BlackBox.exe and the MCP servers it starts say version 1.0.0, as released, not 0.1.0.
- Claude: MCP tools whose argument schema is a union work; a long task no longer stops on more than
  100 pictures in a request; a reply cut off mid-thought no longer breaks the next request; older
  Claude models point accurately on 4:3 and 5:4 screens.
- A chat video whose file was deleted no longer restarts over and over, and opening the full-screen
  viewer no longer freezes on a chat that names files on a share that is off.
- Films: a failed remake no longer leaves a piece looking made; remaking the song keeps the lines'
  voice and doesn't redo a song that still covers the film; restoring a take restores all of its
  settings; long titles and names like "Con" work; the film tool no longer writes over changes made
  in the Filmmaking window during a long job. A frame changed to be taken from where a shot ends
  is no longer dropped back to its old prompt while the shot still ends on it.

## [1.0.0] - 2026-10-19
The first release.

### Added
- A single portable `BlackBox.exe` that installs nothing and keeps its settings, chats and
  downloads in a `Data` folder next to it.
- Desktop control: the model sees the screen and uses the mouse and keyboard in any program, on
  every monitor. Every click is aimed by a fresh look at the screen, a click that changed nothing is
  said so, and models that point on a 0-1000 grid (Qwen3 and later, Gemma 4) use it.
- Clicks with keys held, held keys, drags, scrolling, sequences of key presses and waiting for the
  screen to change.
- Any OpenAI-compatible model server: LM Studio, llama.cpp, Ollama, vLLM, OpenRouter and other
  online APIs. BlackBox picks the strongest model the server offers by its agent benchmark score,
  and starts on Kilo's free models, which need no account or key.
- The server, API key, model and context size in the bar at the top, with how full the context is.
- Long jobs: the context starts afresh with a handoff note when it fills, dropped requests are sent
  again, and a server that can't be reached or fails is waited for.
- Pause/Break stops BlackBox from any window, Continue carries on after a stop, and messages typed
  while it works correct it; it holds off the mouse and keyboard while you type.
- A log of the agent's inputs in a corner of the screen, left out of its screenshots.
- History: every chat is saved from its first message and can be carried on. Replies are shown as
  Markdown, and every line says when it happened and how long it took.
- Files and screenshots dropped on the window or pasted with Ctrl+V go with a message;
  right-click → Copy on a picture in the chat.
- Documents: reading and writing Word, Excel, PowerPoint and PDF files without Office.
- Pictures, video, sound and 3D models with ComfyUI, using the one on the PC or installing it,
  finding and downloading models with their makers' settings, and previews in the chat.
- Servers over SSH: the ssh tool, copying files both ways, and checks of the config files it writes.
- Web search and page reading, portable helper programs fetched for files nothing on the PC opens,
  and reminders that pop up.
- MCP servers in Settings, and the agent setting up the ones a job needs.
- Chatting for you in chat apps, saying it's an assistant when asked.
- "Ask me first" in Settings for approving each step, and a crash log.
- A command-line runner, and benchmarks: desktop tasks, 2048 and Unity 2048, clicks, a sysadmin
  lab, a chat stand-in lab and ComfyUI requests.

[unreleased]: https://github.com/forcepusher/BlackBox/compare/1.0.0...HEAD
[1.0.0]: https://github.com/forcepusher/BlackBox/releases/tag/1.0.0
