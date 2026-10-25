# Changelog
All notable changes to BlackBox are written down in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]
### Added
- "in development" beside the **Toolkit** dropdown: its tools are still being built.
- With Video editor open and nothing picked in it, the video itself shows over the chat's box, as a
  pick does: what you type is about the whole video.
- **Code changes** in the **Toolkit** dropdown: every file the agent changes in the chat (writes, edits, and
  what its commands change) on the left of the chat, each beside its diff, updated as it works and kept
  with the chat. Lines you click go into your message, so you can ask about them or have them changed.
- The agent's `diff` tool: it looks through what changed in a repository, or between two files or
  folders, instead of reading whole files again.
- Code blocks in replies and Markdown previews show in a box of their own, with their language and a
  **Copy** button.
- Markdown files (.md) preview formatted in the chat, as replies are: headings, lists, code, tables
  and links, in a box that scrolls. They show under the reply that names them and under write_file
  when the agent writes one.
- `Data\errors.log`: every error BlackBox shows (in the chat, in the banner under the top bar, in
  Video editor, or when it can't start), with the details behind it, to read after the chat or the
  window is gone.
- Folder index: before it works on a project or a big folder, BlackBox indexes it by itself, as
  Cursor does: what kind of project it is, its README, its folders and what its main files define,
  and a search that finds where anything is by names or by what the code does, with every line that
  has a name it asks for. Indexes are kept in `Data\index` and bring themselves up to date on every use.
- Several BlackBox windows at once: **New window** at the top, or starting BlackBox again, opens
  another window with a chat and an agent of its own, a step down and to the
  right of the one in front. The windows take turns with the mouse and keyboard, stay out of each
  other's screenshots, all pause while you type in any of them, and one Pause/Break stops them all.
  A chat open in one window brings that window forward when picked in another's History, and what
  Settings saves applies to every window; each keeps its own model.
- Video editor: pick it in the **Toolkit** dropdown at the top left, and it opens on the left of the
  chat, the window growing to that side: a viewer, and the video on one timeline you drag sideways,
  where all of it is worked on: References (pictures), the video clips, the lines and the sound lines.
  Right-click a row to create a card, clip or sound there: **File…** copies a file of yours in,
  **Generate** has BlackBox make it; dropping files on the timeline copies them in too. Click
  anything there and it goes into the chat's box; say what to make or change and BlackBox writes
  the prompts, picks the models, makes it with ComfyUI and looks at it, in the chat, where you watch
  and can stop it. Earlier versions are kept, and **Use this version** puts one back. Or ask for a
  video in the chat.
- A video is its folder, with two folders in it: References (your pictures and sounds, copied in, and
  the images BlackBox generates for frames to be made from) and Generated (each clip's frames and
  file, the songs), and the joined video. Every file has a `.json` of its name beside it saying what
  it is and how it was made (`s1.json` for `s1.mp4`), and the ComfyUI workflow that made it, which
  opens in ComfyUI as it is.
- Videos get sound lines, as a video editor has sound tracks: add as many as you need, lay your sounds and songs
  BlackBox makes (about a minute long, the best of three, judged by SongEval) on them, drag each to
  where it starts or to another sound line, and trim it at either end. Spoken lines are in one cloned
  voice with lip sync, and fresh looks at every shot, cut and the whole video catch
  broken bodies, frozen or jumping action, a camera that moves when it shouldn't and a change of
  style, and make the pieces again.
- Measured movement in short clips (which way the camera and what is in the picture moved, and how
  far) for every look the agent takes at a video.
- A guide to writing video prompts that LTX follows, given with LTX workflows, and a first frame
  made by an image model for every video.
- Look-ups: a question or doubt the model goes on with about how something is done, a try it or
  you call wrong, and an error it was told to look up and went past each get a note to search the
  web; ten minutes or eight tries with nothing looked up make it time to look.
- Claude through Anthropic's own API: type `api.anthropic.com` (or just `claude`) and an API key.
  Prompt caching, thinking kept between steps, Claude Opus 5.5 unless another is picked, with its
  whole 1M context on Auto.
- An effort dropdown next to the model, medium unless picked, for models whose server says they
  take one.
- Messages sent while BlackBox works wait above the box until the model reads them: × takes one
  back, and **Interrupt the model** (or Ctrl+Enter) has it read at once.
- A full-screen viewer for the pictures and videos in the chat and in Video editor, with the others
  an arrow key away. Videos in the chat play by themselves, muted, while they are on screen.
- **Prompt** under a picture made with ComfyUI or AUTOMATIC1111 shows the prompts and settings it
  was made with, read back from the file.
- Links in the chat open in the browser.
- The window's title names its chat.
- A sysadmin benchmark ticket that moves a live game API's database from SQLite to PostgreSQL while
  players keep writing to it.
- This changelog.
- Video editor's videos are in a menu on the video's name at the top: open another, start a new one, or
  rename or delete this one (to the Recycle Bin). On the timeline, right-click a card, clip or sound
  to rename or delete it, or to see an earlier version of it.

### Changed
- Asked for code, commands or facts that change (versions, prices, news), the agent looks them up
  on the web before it answers, rather than writing them from memory.
- A video's name is its folder's: renamed in Video editor, in Explorer, or by the agent giving it a
  title, the video is.
- Video editor's frame size and frame rate are at the top of the panel, and a new clip is the dashed +
  after the last one on the timeline.
- Every dropdown's arrow (Toolkit, History, Model, Effort) is the same solid triangle, and so is the
  Toolkit's arrow to the left.
- The chat's scrollbar always shows.
- A frame the video tool makes gets a fresh look, and every piece it makes again keeps the one before
  as a version; a shot's or song's other tries are versions too.
- The context slider goes up to 1M.
- The second BlackBox name and logo under the title bar made way for the Toolkit dropdown.
- The first-run hints for Ctrl+V and **New window** are reworded.
- A new video in Video editor is called "Untitled video".
- Video editor says video, not film, everywhere, and so does the agent. Its takes are clips (Clip 1,
  Clip 2…) on the **Video clips** row, and its sound tracks are sound lines (Sound line 1…).
  Videos are kept in `Data\videos`; ones made before, in `Data\films`, no longer open.

### Removed
- Ctrl+Shift+N: **New window** at the top opens another window.
- Videos kept in a `storyboard.json` (from 1.0.0) no longer open: a video is its folder now.
- Video editor's video dropdown, its Open… and Show folder buttons, the settings button with the video's
  title, and the soundtrack and shots' own sound sliders.
- Video editor's **New video** button beside **Make the video**: New video is in the menu on the video's name.

### Fixed
- A reply that asks you something ends the turn, even when it says what the agent will do after your
  answer: BlackBox no longer tells it to "Continue with the task", which made it ask you again.
- The `BlackBox.exe` and `blackbox-cli.exe` that `dotnet build` makes run on a PC without .NET installed,
  instead of asking for the .NET Desktop Runtime.
- A picture BlackBox was reading when a program started no longer stays locked until that program
  ends: it can be deleted or replaced at once.
- Selecting text in the chat starts wherever the drag does: beside a short line, between lines or
  letters, just left of or above a reply or code block, and in the padding of your messages, not only
  on a letter.
- A message sent with LM Studio at `localhost` no longer waits about 4 seconds before LM Studio
  starts loading the model, and each later connection to it no longer waits 2 seconds.
- ComfyUI templates no longer leave an Edge button on the taskbar where Edge isn't allowed to run
  headless: the copy Edge hands over to has its window hidden too.
- With Video editor or Code changes open, the window can no longer be made so narrow that the chat is
  squeezed to a few letters across: the panel gives way first, then the window stops. Video editor
  also keeps the window tall enough for its viewer, and a long video name is cut short at the top.
- Video editor: a file dropped on a new card or sound becomes it, which is marked while you drag over
  it; a copied file can be renamed. A line over the timeline says what right-click and dropping do,
  and the viewer names the video and says it is selected instead of "Whole video".
- Picking Video editor widens the window to the left in one smooth step: the chat no longer jumps
  sideways and back, and the new part of the window isn't black while the panel appears.
- In Video editor, a picture you give for a reference (pasted, attached or dropped) is kept exactly as
  it is, in References, and the references are made from all of it: BlackBox cut one
  pose out of a character lineup and showed the cut-out as your picture.
- A task whose last step the server failed a few times before answering no longer looks as if it
  ended on that error: the lines about the tries come before the answer, not after it.
- A click of the agent's that lands on a dialog of BlackBox's goes to what is behind it, as one on
  the chat window always did; keys meant for another program no longer land in it.
- Links whose address holds parentheses (Wikipedia's) open whole, and an address followed by
  Chinese or Japanese punctuation no longer takes it in.

- Making a shot, the song or the video again no longer fails with "the process cannot access the
  file" while the one before plays in the chat.
- A frame made from a picture of another shape (a square picture of a character) comes out the
  video's size with all of the picture in it, and a reference keeps its own shape: both were cut to
  the video's frame, and the character's head went with it.
- A video's camera is said to hold still only when that was measured: a pan there and back, or a
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
  with Continue, when opened again, a task that has just ended keeps its end when another chat is
  opened at once, and a message still waiting when its window closes is kept in the chat.
- Several windows: once a click is held back because another window's agent changed the screen,
  the rest of that step's clicks and keys are held back too, instead of typing into the other
  window's program, and a window's next task looks again first when another window acted meanwhile;
  saving Settings in one window no longer undoes what another window saved while the dialog was
  open; closing a window right after picking another chat no longer marks that chat stopped; a
  window closed while its MCP servers restarted no longer leaves them running.
- Video editor: Continue sends a message the model never had whole, with the video it was about; chats
  begun with Video editor open are named after the video and what was picked too, not "Make it." alone;
  a change made by hand while BlackBox changes the video in the chat no longer writes over BlackBox's
  change, nor is a shot's length being dragged lost to it; a shot added after one was deleted no
  longer brings back the deleted shot's clip; a video named again keeps its joined video; a version isn't
  put back while BlackBox makes a piece of the video; two new videos started in two windows at once no
  longer share a folder; a piece of the video open is no longer shown as being made when the chat
  makes another video's.
- A piece asked for in Video editor is made again when its look finds something wrong, up to
  twice, as before it ran in the chat; the storyboard guide comes back after the context starts
  afresh; a stopped shot of several tries no longer keeps a copy of its clip as one more version; a
  video the model names without a full path goes into `Data\videos`, not your home folder; a second
  video laid out in the same task without a path no longer replaces the first; a remake that fails
  or is stopped leaves the piece with its settings and look; a clip asked for while several were
  being made is made with its own seed; a speaking shot's version keeps its line.
- A ComfyUI that BlackBox started, with nothing left to do, is closed when BlackBox closes, also
  when its window was closed in the middle of a task.
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
- Videos: a failed remake no longer leaves a piece looking made; remaking the song keeps the lines'
  voice and doesn't redo a song that still covers the video; restoring a version restores all of its
  settings; long titles and names like "Con" work; the video tool no longer writes over changes made
  in Video editor during a long job. A frame changed to be taken from where a shot ends
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
