# Changelog
All notable changes to Sloperator are written down in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]
### Added
- Right-click any picture in Sloperator for **Copy image**, and any other file (a video, a sound, a
  document) for **Copy file**: in the chat, the full-screen viewer, the File inspector, the Video
  generator and File changes.
- Right-click a message of yours in the chat for **Undo message and everything below**: it and every
  later line leave the chat and the saved chat, the files the agent wrote or edited from then on go back
  to how they were, and the message goes back into the box. What commands, copies and clicks in other
  programs changed isn't undone; the chat says which of those ran.
- **Enable screenshots** and **Enable keyboard and mouse control** under Settings → Safety. With
  screenshots off the agent doesn't look at the screen, and can't use the mouse and keyboard either;
  with control off it looks but doesn't click or type.
- **Servers** next to the server address lists every server that answered, with its API key and model:
  pick one to switch to it, or remove it with its trash button.
- Ask the agent to connect another model ("use my LM Studio", an API address and key, a model's
  name) and it switches Sloperator's server, key and model itself, once the server answers; your next
  message goes to the new model.

### Changed
- On the start screen, "What should I do on your computer?" sits right over the chat box and stays in
  view, with the cards scrolling above it. In a very short window it makes room for a long message.
- A chat video's right-click Copy is now two entries: **Copy image** for the frame on screen and
  **Copy file** for the video.
- A new look in neutral greys: a darker background, filled buttons, fields darker than the window,
  rounder cards and controls, softer diff colours, and every title bar dark. Nothing moved: every
  control is where it was and as big. The thoughts over a reply have a header of their own and
  larger text.
- Every model clicks in pixels of the screenshot. Qwen 3 and later, Gemma 4 and Nemotron no longer get
  a 0-1000 grid: told the screenshot's size, Qwen clicks as well in pixels.
- Claude through OpenRouter (and any server whose model list says it takes reasoning) gets the effort
  picked in the top bar, so its thinking shows in the chat; the thinking goes back with each reply, so
  Claude keeps it across tool calls.
- After a click that changed nothing on screen, the agent is told to zoom in and click again, and to
  stop only when the zoom shows the thing was already selected, so it corrects more of its misses.
- File changes lists each file on one compact line, its name and its counts, so a long list fits;
  whether it was modified or renamed shows in its tooltip.
- The model list is read from the server again each time its dropdown opens, so a model loaded there
  meanwhile shows up. The wider model box, the shorter effort dropdown ("Medium" rather than "Medium
  effort") and a context box as wide as its size make the top bar tidier.
- The model list is sorted by name, and a list wider than its dropdown opens from the dropdown's left
  edge instead of sticking out on the left. **Servers** looks like the dropdowns around it.
- **Toolkit** is now called **Side panel**, in the top bar and in Settings, and its left arrow sits
  beside it, outside the button.
- In the chat, the screen a click, a wait or another action left is folded under **Screenshot**, as
  a call's output is; a screenshot the agent took to look stays open.
- A shell command's card says **Run a command** and, in a few words, what the command does, with the
  whole command shown under it in a code box instead of in the title.
- The single-file Sloperator.exe is 25 MB instead of 31.8: its native libraries are packed with
  Brotli, PDF pages are drawn by Windows' own PDF renderer instead of PDFium, and the window is drawn
  with OpenGL instead of Direct3D.
- One look everywhere: every button, dropdown and text box is the same height in the chat, Settings
  and every side panel (small buttons in cards and players share one smaller size), the top bars of
  the chat and of each panel are alike and line up, field labels are the same grey, and spacing is
  even throughout.
- **History** and **Servers** open the same kind of list as the dropdowns, and right-click menus have
  rows as tall as a dropdown's.
- The Video generator's menu says **Show in folder**, as everywhere else.

### Fixed
- The app icon has a soft shadow, so the yellow blob keeps a clear edge on a light taskbar.
- The blob on the start screen is sharp: it was a large picture shrunk down, with soft edges.
- A text box (the prompt, the server address, the API key, the context size, the boxes in Settings)
  kept the focus and its caret after a click on empty space, a panel or a label; a click anywhere away
  from it now takes the focus off it.
- Every list opened by a click hangs under its button, flush with its left edge: the Video generator's
  videos menu opened where the mouse was, over "Frame size", and History and Servers hung from their
  right edge. A second click on the video's name closes its menu, and Rename's box covers the name
  rather than the window's title bar.
- Text sat 1-2 px over the middle of dropdowns (the model, the effort), buttons, dropdown rows, the
  lines of File changes and the tool's name on a card; it is in the middle now.
- With the chat narrow, the server address box covered the "Server" label beside it.
- The **Side panel** dropdown went dark under the pointer while the buttons beside it lit up, and
  **Stop** lost its red; both now change as the buttons around them do.
- Settings' scrollbar sat inside the page rather than at the window's edge.
- **Area**, **Prompt** and the API key's eye were filled grey beside outlined buttons; they are
  outlined as the others until pressed in.
- Undoing a message from before a context reset kept only the messages before it: the handoff note
  went too, and the model had no record of the work done. The note now stays, with a word that later
  messages were undone.
- A Compact context that was stopped or failed offered Continue, which took up the previous task
  instead; and compacting took Continue off a stopped task, which now gets it back.
- `type_text` on keyboard layouts with dead keys (US-International, German, French…): a quote, caret
  or accent waited for the next letter and joined it (`"a` came out as `ä`).
- Asked to switch back to a saved server without its key, the agent's model_server now uses the key
  saved for it.
- Undoing a message in a long chat whose oldest lines were no longer shown could take more from the
  model than was undone, when an older message had the same words ("continue"). Messages are now
  found by an id of their own.

### Removed
- The ↻ button next to the model: opening the model dropdown does what it did.
- "in development" beside the **Toolkit** dropdown.

## [1.1.0] - 2026-10-29
### Added
- The agent can zoom in on part of the screen: a sharp, enlarged picture of it with rulers in
  screenshot coordinates, to aim at small things such as a list's rows or a toolbar's buttons.
- Saved chats keep the pictures the model was shown (screenshots, zoomed crops) in a folder beside
  the chat, `Data/chats/<chat id>/`. Opening a chat again gives the model its pictures back, also
  after copying the chat and its folder to another computer.
- The blob beside **Send** dances while a prompt runs, and goes back to the still logo when it ends.
  It shows once a chat has started; the start screen keeps its own logo. It dances on the beat of
  120 BPM music.
- File changes opens by itself the first time the agent changes a file in a chat, unless another tool
  is open. Closed, it stays closed in that chat. Turn it off under Settings → Toolkit.
- File changes in a git repository lists the files in **Committed**, **Staged** and **Unstaged**
  foldouts.
- **Stage**, **Unstage** and **Discard** over the File changes diff act on the picked lines, or the
  selected file when none is picked; click a "Lines" divider to pick its block, a file's name to pick all of it.
  Discard puts unstaged changes back as staged, and files outside git as before the agent's changes; a
  file the agent made goes to the Recycle Bin.
- **Max screenshot size** under Settings → Advanced: screenshots are scaled down to fit it, so a 4K
  screen uses a quarter of the context. 2560 × 1080 by default; clicks still land on the real screen.
- **Wrap** in the File changes bar wraps long lines inside the panel instead of running them off
  its edge. On by default, and remembered.
- **Watch** in the File changes bar follows the agent: as it changes a file, the panel goes to the
  lines it changed and lights them up a moment. It stays on while you scroll or pick lines, and the
  agent's next change brings you back to it. On by default, and remembered.
- Video generator takes videos as references: File… on a reference card and dropping a video copy it
  into References as a video card you can play. Ask in the chat for a clip that moves as it does, one
  that goes on from where it ends, or frames in its look.
- Every clip in Video generator shows its start and end frames at its ends: click one to pick it, or,
  not made yet, File… or Generate on it. They left the clip's right-click menu.
- In File inspector, drag a box over a PDF page, a picture or a video frame to ask about that area:
  it goes into your message as "Area on page 2", and the model gets a picture of it with where it is.
  **Area** in the panel's bar freezes a text or an Office file's preview to drag over it too.
- File inspector zooms pictures, video frames and PDF pages: the wheel (Ctrl+wheel on a PDF), the
  − and + buttons, and a right or middle drag to pan.
- Video in File inspector is a plain player that steps one frame back or on (the buttons, or Left and
  Right) and says which frame shows and when.
- Sound in File inspector is a waveform you zoom into and click or scrub to set the playhead, with the
  time to the thousandth. Drag over it to ask about a stretch: it goes into your message as
  "0.80-1.45 s", and the model gets the times, how it sounds there and a picture of it.
- Animations (GIF, WebP, APNG) in File inspector step frame by frame like video, and an area of a frame
  can be asked about.
- 3D models in File inspector turn with a left drag, zoom with the wheel, move with a right drag, and
  have Front, Right, Back, Left and Top views; **Area** marks a part of the view to ask about.
- File inspector shows any other file as hex; select bytes to ask about them.
- The folder button in File inspector's bar opens a file from your PC in it.
- FBX 3D models open in the chat and in File inspector, their animations playing there frame by
  frame, and the agent's `read_file` shows them, an animation at four times too.
- "in development" beside the **Toolkit** dropdown: its tools are still being built.
- With Video generator open and nothing picked in it, the video itself shows over the chat's box, as a
  pick does: what you type is about the whole video.
- **File changes** in the **Toolkit** dropdown: every file the agent changes in the chat (writes, edits, and
  what its commands change) on the left of the chat, each beside its diff, updated as it works and kept
  with the chat. Lines you click go into your message, so you can ask about them or have them changed.
- In File changes, a click away from the lines (or Esc) unpicks them and takes them off your message.
- In File changes, right-click a file for **Show in folder**.
- In File changes, drag over lines to pick them all; past the panel's edge the diff scrolls along.
- With a toolkit panel open, what goes with your message from it always shows as a card over the
  chat's box, and in the sent message as its attachments do. In File changes, with no lines picked,
  that is the file selected in its list.
- The agent's `diff` tool: it looks through what changed in a repository, or between two files or
  folders, instead of reading whole files again.
- Code blocks in replies and Markdown previews show in a box of their own, with their language and a
  **Copy** button.
- **File inspector** in the **Toolkit** dropdown: a file from the chat shown whole on the left of
  the chat. Markdown is formatted as replies are, .csv as a table, text and code as they are, and
  pictures, video and sound as in the chat, PDF pages drawn by Sloperator itself (with PDFium, Chrome's
  PDF engine); Word, Excel, PowerPoint and web pages show as in Explorer's preview pane.
- With File inspector open, the file shows over the chat's box, and text you select in it goes into
  your message as where it is (lines, or cells of a table), so you can ask about that part. A click
  elsewhere in the panel, or Esc, lets go of it.
- **Inspect** on every file in the chat opens it in File inspector, in place of **Open**, which the
  full-screen viewer no longer has either.
- Documents in the chat (Markdown, tables, text and code, PDF, Word, Excel, PowerPoint, web pages)
  show a small preview on their card, as big as a picture pasted into a message: a PDF's first
  page, the picture an Office file keeps of itself, or the start of the text. Click it to see the
  file full screen, as pictures and videos open, with the arrow keys going on to the chat's other
  files.
- Markdown files (.md) get a card like other documents, under the reply that names them and under
  write_file when the agent writes one.
- `Data\errors.log`: every error Sloperator shows (in the chat, in the banner under the top bar, in
  Video generator, or when it can't start), with the details behind it, to read after the chat or the
  window is gone.
- Folder index: before it works on a project or a big folder, Sloperator indexes it by itself, as
  Cursor does: what kind of project it is, its README, its folders and what its main files define,
  and a search that finds where anything is by names or by what the code does, with every line that
  has a name it asks for. Indexes are kept in `Data\index` and bring themselves up to date on every use.
- Several Sloperator windows at once: **New window** at the top, or starting Sloperator again, opens
  another window with a chat and an agent of its own, a step down and to the
  right of the one in front. The windows take turns with the mouse and keyboard, stay out of each
  other's screenshots, all pause while you type in any of them, and one Pause/Break stops them all.
  A chat open in one window brings that window forward when picked in another's History, and what
  Settings saves applies to every window; each keeps its own model.
- Video generator: pick it in the **Toolkit** dropdown at the top left, and it opens on the left of the
  chat, the window growing to that side: a viewer, and the video on one timeline you drag sideways,
  where all of it is worked on: References (pictures), the video clips and the sound lines.
  Right-click a row to create a card, clip or sound there: **File…** copies a file of yours in,
  **Generate** has Sloperator make it; dropping files on the timeline copies them in too. Click
  anything there and it goes into the chat's box; say what to make or change and Sloperator writes
  the prompts, picks the models, makes it with ComfyUI and looks at it, in the chat, where you watch
  and can stop it. Earlier versions are kept, and **Use this version** puts one back. Or ask for a
  video in the chat.
- In Video generator each sound line has a dashed **+** after its last sound. The new sound offers
  **File…**, **Foley** (the sounds of what the clips under it show, made from them with MMAudio in
  ComfyUI) and **Music** (a song).
- Each sound line in Video generator has a volume slider under its name, kept with the video and
  heard when you play it and in the joined video.
- A video is its folder, with two folders in it: References (your pictures and sounds, copied in, and
  the images Sloperator generates for frames to be made from) and Generated (each clip's frames and
  file, the songs), and the joined video. Every file has a `.json` of its name beside it saying what
  it is and how it was made (`s1.json` for `s1.mp4`), and the ComfyUI workflow that made it, which
  opens in ComfyUI as it is.
- Videos get sound lines, as a video editor has sound tracks: add as many as you need, lay your sounds and songs
  Sloperator makes (about a minute long, the best of three, judged by SongEval) on them, drag each to
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
- Messages sent while Sloperator works wait above the box until the model reads them: × takes one
  back, and **Interrupt the model** (or Ctrl+Enter) has it read at once.
- A full-screen viewer for the pictures and videos in the chat and in Video generator, with the others
  an arrow key away. Videos in the chat play by themselves, muted, while they are on screen.
- **Prompt** under a picture made with ComfyUI or AUTOMATIC1111 shows the prompts and settings it
  was made with, read back from the file.
- Links in the chat open in the browser.
- The window's title names its chat.
- A sysadmin benchmark ticket that moves a live game API's database from SQLite to PostgreSQL while
  players keep writing to it.
- This changelog.
- Video generator's videos are in a menu on the video's name at the top: open another, start a new one, or
  rename or delete this one (to the Recycle Bin). On the timeline, right-click a card, clip or sound
  to rename or delete it, or to see an earlier version of it.
- A × at the top right of Video generator, File changes and File inspector closes the tool.
- A slim line in the chat when Sloperator sends the model a note: that it is going in circles, should
  look something up on the web, or should index the project first.

### Changed
- Every Toolkit panel (File changes, File inspector, Video generator) opens at the same width.
- **Prompt** has moved to the File inspector panel: press **Inspect** on a picture, animation, video,
  sound or 3D model, then **Prompt** in the panel's top bar to see the prompts and settings it was made
  with under it. The chat's previews and the full-screen viewer no longer have a Prompt button.
- **Toolkit** is the first section in Settings, above Safety.
- Sloperator's notes to the model come as a message of their own after the tool results, and the model
  is told they come from Sloperator: Claude took one inside a screenshot's result for an injected
  instruction and ignored it.
- A toolkit panel puts a card over the box, and tells the model about itself, only once you pick
  something in it. File changes no longer selects a file by itself (Watch included). Its file list selects as
  Sourcetree's does: click one, Ctrl+click to add or take off, Shift+click a run. Selected files go in your
  message, a second click on the only one takes it out. A file whose lines you pick turns grey and goes
  as those lines, and yellow and whole again when they are let go. The whole-video
  and whole-document cards are gone.
- The File changes diff shows one line number instead of two, the old one greyed out on removed lines,
  with + or - right after it. The numbers take only as much room as the file's longest one, so the code
  starts much further left.
- Each block of changes in the File changes diff starts with a slim "Lines 96-104" divider instead of
  git's `@@ -96,8 +96,9 @@` line. Clicking it still picks the block.
- The +3 and -2 counts in File changes (the file list, each file's header and the summary at the top)
  show added lines in green and removed lines in red.
- Clicks go exactly where the agent aims. The fresh look that aimed every click again, in a request of
  its own, is gone: it re-guessed from one picture without what the agent knew, and moved DeepSeek's
  well-aimed clicks a row off or into another panel. The agent zooms in instead when it isn't sure.
- On Auto, Sloperator takes a server that doesn't say how large its context is to have 1M tokens (it
  was 100k), and uses all of a hosted service's 1M, Kilo's and OpenRouter's too (it used 100k of it).
- When a server refuses a conversation as too long without naming a smaller size (Kilo names 1M,
  and counts more than the model does), the context is made a quarter smaller than where it broke, so
  the run doesn't run into the same refusal again.
- The ring where the agent clicks is bigger, in the accent yellow with a dark outline, and widens
  out over 3 seconds instead of just over one.
- A reply without tool calls ends the task: Sloperator no longer sends "Continue with the task" after
  a reply that only looks unfinished, nor a note after thinking that went in circles, nor Claude a
  "Continue." in your name. A reply the server's length limit cut off is still told so and goes on.
- The app has a new name, Sloperator: the exe is `Sloperator.exe`, and `SLOPERATOR_HOME` sets the
  portable root.
- A new icon: a yellow blob in place of the grey box, in the taskbar, the windows and the exe.
- The accent colour is the blob's yellow in place of amber: buttons, icons, links, the message box's
  border, sliders, check boxes and toggles, with dark ticks and icons on it.
- Tables in replies are real tables: a grid with a header row, in the chat's font, each cell
  wrapping inside its own column. One too wide for the chat scrolls sideways.
- Replies read as they would on GitHub: headings by their size, list items whose lines wrap under
  their own text, task boxes, quotes with a bar (nested ones and lists in them too), rules across,
  code blocks indented in their list item, `~~struck~~`, `__bold__`, `_italic_`, bold inside italics,
  `<https://…>` links, `<br>` and `<b>`-style HTML, pictures as links, footnotes and `> [!NOTE]` boxes.
- A drag over a reply selects across its paragraphs, list items, tables and code, and Ctrl+C copies all of it.
- Asked for code, commands or facts that change (versions, prices, news), the agent looks them up
  on the web before it answers, rather than writing them from memory.
- Every dropdown's arrow (Toolkit, History, Model, Effort) is the same solid triangle, and so is the
  Toolkit's arrow to the left.
- Every scrollbar (the chat, panels, lists, previews) always shows in full, with its arrows,
  instead of a thin line that grows under the pointer.
- The context slider goes up to 1M.
- The second Sloperator name and logo under the title bar made way for the Toolkit dropdown.
- The first-run hint for Ctrl+V is reworded.
- What the agent just changed stays lit in File changes longer: 4 seconds, then a 2 second fade.

### Removed
- **Show in folder** in the full-screen viewer.

### Fixed
- On a screen scaled above 100%, a click, double-click or drag could land short of where the agent
  aimed, towards the top left: the pointer and the screenshots now always agree.
- A saved Claude chat keeps Claude's thinking, so opened again it goes on with it.
- A broken glTF whose nodes loop back on themselves opens at once instead of after a pause.
- Typing works in Blender's text fields: the agent types by pressing the keys, as a person does,
  where before Blender dropped every character it typed.
- The click and key log no longer feeds the app being worked in a stream of mouse moves while it
  shows: with the pointer still, a text box got 17 a second as the agent typed into it.
- A server that takes only so many pictures in one request (munito takes 8) no longer ends a long run
  with "too many image inputs": the screenshots before the latest are left out and the run goes on.
  A request too large for the server (Kilo takes 20 MB) does the same, rather than resetting the
  context.
- Text on the yellow accent is readable again: the picked file in File changes, picked rows in
  lists and dropdowns, selected text, and the Thoughts header, which had gone near black.
- When File changes opens by itself on the agent's first change, that change lights up too.
- A message sent during a run no longer brings a second, unasked-for answer at the end of the run,
  after the model has already answered it.
- File changes no longer lists files other programs write while a command runs, such as your
  `.claude.json`: it lists only the files the model writes or edits with its file tools, not what
  its commands change.
- File changes no longer lists the agent's own notes in Sloperator's scratch folder, or other files in
  Sloperator's Data folder.
- Text with a link in it shows the text cursor, as all other chat text does; it showed the arrow.
- A table whose alignment line has single dashes (`|:-:|`) is read as a table, in the chat and in
  documents written from Markdown.
- A long message you send shows all its lines in the chat: the last one, often after an empty line,
  could be cut off under the bubble's edge (the model always got the whole text). Long notices too.
- A message keeps the indentation of its first line (pasted code, a list) when sent.
- With two monitors, a new message (Continue too) clicks on the screen the last screenshot showed:
  it clicked on the main screen with the other one's positions.
- The model tests what it set up for an AI app by asking that app, instead of leaving the test to you
  and stopping, and a check it planned before a context reset isn't dropped as optional after it.
- With the model server off, a message says it can't reach it after about 4 seconds instead of 16.
- With a video of yours picked on Video generator's References row, "this video" in your message
  means that video: asked for a clip that goes on from where it ends, the model went on from the
  last clip instead.
- In the joined video, a sound whose start you cut (its left edge dragged) starts where it sits on
  its sound line. It played from the video's start.
- A song made with Music on a new sound shows in Video generator's viewer once it is made, instead
  of the empty new sound still asking for its style.
- A clip made with a workflow that can take its length from the prompt (an LTX duration head
  switch) is as long as the clip says: the switch is turned off. It had been left on, and a 4 s clip
  came out 2 s.
- A clip made with New clip (or Create) in Video generator is saved at once: on a video just opened
  it stayed only in the panel and was gone the next time, and its frames could show a picture a
  deleted clip left behind.
- Video generator says when the joined video is older than a clip or sound in it (one made again, or
  a version put back): "all made, not joined since", until you join it again.
- The scrollbar under Video generator's timeline drags again: its thumb follows the mouse instead
  of the timeline sliding on its own.
- With a tool open and little room for the chat, the server's status dot no longer draws over **History**.
- Picking a tool in the **Toolkit** dropdown, or None, changes the window in one go: no more frame
  of the chat at the wrong width, an empty or black panel, or the list still open over it.
- A reply that asks you something ends the turn, even when it says what the agent will do after your
  answer: Sloperator no longer tells it to "Continue with the task", which made it ask you again.
- The `Sloperator.exe` and `sloperator-cli.exe` that `dotnet build` makes run on a PC without .NET installed,
  instead of asking for the .NET Desktop Runtime.
- A picture Sloperator was reading when a program started no longer stays locked until that program
  ends: it can be deleted or replaced at once.
- Selecting text in the chat starts wherever the drag does: beside a short line, between lines or
  letters, just left of or above a reply or code block, and in the padding of your messages, not only
  on a letter.
- A message sent with LM Studio at `localhost` no longer waits about 4 seconds before LM Studio
  starts loading the model, and each later connection to it no longer waits 2 seconds.
- ComfyUI templates no longer leave an Edge button on the taskbar where Edge isn't allowed to run
  headless: the copy Edge hands over to has its window hidden too.
- With Video generator or File changes open, the window can no longer be made so narrow that the chat is
  squeezed to a few letters across: the panel gives way first, then the window stops. Video generator
  also keeps the window tall enough for its viewer, and a long video name is cut short at the top.
- Video generator: a file dropped on a new card or sound becomes it, which is marked while you drag over
  it; a copied file can be renamed. A line over the timeline says what right-click and dropping do,
  and the viewer names the video and says it is selected instead of "Whole video".
- Picking Video generator widens the window to the left in one smooth step: the chat no longer jumps
  sideways and back, and the new part of the window isn't black while the panel appears.
- In Video generator, a picture you give for a reference (pasted, attached or dropped) is kept exactly as
  it is, in References, and the references are made from all of it: Sloperator cut one
  pose out of a character lineup and showed the cut-out as your picture.
- A task whose last step the server failed a few times before answering no longer looks as if it
  ended on that error: the lines about the tries come before the answer, not after it.
- A click of the agent's that lands on a dialog of Sloperator's goes to what is behind it, as one on
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
- A chat whose window was closed while Sloperator worked says so when opened again, with Continue.
- A ComfyUI Sloperator started is no longer closed at the end of a task while another Sloperator (the
  command line) may be between two of its jobs.
- The sysadmin migration ticket finds backups written under /var/lib and /var/local too.
- The note to index a project no longer comes back with every message of a chat that indexed it.
- Messages sent while a task starts up are no longer stuck above the box, or sent on without the
  first one, when the start fails; a chat left for another while Sloperator works says it stopped,
  with Continue, when opened again, a task that has just ended keeps its end when another chat is
  opened at once, and a message still waiting when its window closes is kept in the chat.
- Several windows: once a click is held back because another window's agent changed the screen,
  the rest of that step's clicks and keys are held back too, instead of typing into the other
  window's program, and a window's next task looks again first when another window acted meanwhile;
  saving Settings in one window no longer undoes what another window saved while the dialog was
  open; closing a window right after picking another chat no longer marks that chat stopped; a
  window closed while its MCP servers restarted no longer leaves them running.
- Video generator: Continue sends a message the model never had whole, with the video it was about; chats
  begun with Video generator open are named after the video and what was picked too, not "Make it." alone;
  a change made by hand while Sloperator changes the video in the chat no longer writes over Sloperator's
  change, nor is a shot's length being dragged lost to it; a shot added after one was deleted no
  longer brings back the deleted shot's clip; a video named again keeps its joined video; a version isn't
  put back while Sloperator makes a piece of the video; two new videos started in two windows at once no
  longer share a folder; a piece of the video open is no longer shown as being made when the chat
  makes another video's.
- A piece asked for in Video generator is made again when its look finds something wrong, up to
  twice, as before it ran in the chat; the storyboard guide comes back after the context starts
  afresh; a stopped shot of several tries no longer keeps a copy of its clip as one more version; a
  video the model names without a full path goes into `Data\videos`, not your home folder; a second
  video laid out in the same task without a path no longer replaces the first; a remake that fails
  or is stopped leaves the piece with its settings and look; a clip asked for while several were
  being made is made with its own seed; a speaking shot's version keeps its line.
- A ComfyUI that Sloperator started, with nothing left to do, is closed when Sloperator closes, also
  when its window was closed in the middle of a task.
- Folder index: projects in OneDrive folders are indexed (only their top folder was), files kept
  online only aren't downloaded, linked files are read as the files they point to, a search that
  finds nothing says what the index doesn't search, big workbooks are read one at a time, and an
  index file damaged on disk is built again.
- Sloperator.exe and the MCP servers it starts say the version released, not 0.1.0.
- Claude: MCP tools whose argument schema is a union work; a long task no longer stops on more than
  100 pictures in a request; a reply cut off mid-thought no longer breaks the next request; older
  Claude models point accurately on 4:3 and 5:4 screens.
- A chat video whose file was deleted no longer restarts over and over, and opening the full-screen
  viewer no longer freezes on a chat that names files on a share that is off.
- Videos: a failed remake no longer leaves a piece looking made; remaking the song keeps the lines'
  voice and doesn't redo a song that still covers the video; restoring a version restores all of its
  settings; long titles and names like "Con" work; the video tool no longer writes over changes made
  in Video generator during a long job. A frame changed to be taken from where a shot ends
  is no longer dropped back to its old prompt while the shot still ends on it.

## [1.0.0] - 2026-10-19
The first release.

### Added
- A single portable `Sloperator.exe` that installs nothing and keeps its settings, chats and
  downloads in a `Data` folder next to it.
- Desktop control: the model sees the screen and uses the mouse and keyboard in any program, on
  every monitor. Every click is aimed by a fresh look at the screen, a click that changed nothing is
  said so, and models that point on a 0-1000 grid (Qwen3 and later, Gemma 4) use it.
- Clicks with keys held, held keys, drags, scrolling, sequences of key presses and waiting for the
  screen to change.
- Any OpenAI-compatible model server: LM Studio, llama.cpp, Ollama, vLLM, OpenRouter and other
  online APIs. Sloperator picks the strongest model the server offers by its agent benchmark score,
  and starts on Kilo's free models, which need no account or key.
- The server, API key, model and context size in the bar at the top, with how full the context is.
- Long jobs: the context starts afresh with a handoff note when it fills, dropped requests are sent
  again, and a server that can't be reached or fails is waited for.
- Pause/Break stops Sloperator from any window, Continue carries on after a stop, and messages typed
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

[unreleased]: https://github.com/forcepusher/Sloperator/compare/1.1.0...HEAD
[1.1.0]: https://github.com/forcepusher/Sloperator/compare/1.0.0...1.1.0
[1.0.0]: https://github.com/forcepusher/Sloperator/releases/tag/1.0.0
