using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BlackBox.Core.Llm;
using BlackBox.Core.Mcp;
using BlackBox.Core.Settings;
using BlackBox.Core.Tools;

namespace BlackBox.Core.Agent;

public enum AgentStopReason { Completed, Cancelled, Error }

/// <summary>Callbacks from the agent loop. Called on a background thread.</summary>
public interface IAgentObserver
{
    void OnStepStarted(int step);
    void OnDelta(StreamDelta delta);
    void OnAssistantMessage(ChatMessage message);
    Task<bool> RequestApprovalAsync(ToolCall call, Tool tool, string summary, CancellationToken ct);
    void OnToolStarted(ToolCall call, string summary);

    /// <summary>How a long call is getting on ("KSampler, step 3 of 20 · 1:12"), for its card while it runs.</summary>
    void OnToolProgress(ToolCall call, string text) { }

    /// <summary>
    /// A message the user sent during the run (see <see cref="AgentSession.Steer(UserInput)"/>) has
    /// joined the conversation: the model reads it in the step that starts next. The same instance
    /// that was sent. Called before that step's <see cref="OnStepStarted"/>.
    /// </summary>
    void OnUserMessageDelivered(UserInput input) { }

    /// <summary>Status the user should see that is not part of the conversation (e.g. reconnecting).</summary>
    void OnNotice(string text);

    /// <summary>
    /// How much of a step's request has gone out: it carries every screenshot in the context, and
    /// over a slow VPN sending it took minutes that looked like the model thinking.
    /// </summary>
    void OnSending(long sent, long total) { }
    void OnToolFinished(ToolCall call, ToolResult result);

    /// <summary>
    /// The run is ending on an exception that is no server's answer: a bug, whose message alone
    /// ("Object reference not set to an instance of an object.") tells nobody where. Called before
    /// <see cref="OnFinished"/>, with the whole exception for a log.
    /// </summary>
    void OnFailed(Exception error) { }

    void OnFinished(AgentStopReason reason, string? detail);
}

/// <summary>
/// One conversation with the model. Each user turn runs the loop: ask the model, run the tools it
/// calls, feed results back, repeat until it answers without tool calls.
/// </summary>
public sealed partial class AgentSession(
    OpenAiChatClient client,
    ChatRequestOptions requestOptions,
    ToolRegistry tools,
    ToolContext toolContext,
    string systemPrompt,
    List<ChatMessage>? history = null,
    int contextTokens = 0)
{
    /// <summary>The model's context size as this session knows it: as reported at the start, or corrected by a server error.</summary>
    public int ContextTokens => contextTokens;

    /// <summary>Conversation so far (without the system prompt). Can be shared across sessions so settings changes keep the chat.</summary>
    public List<ChatMessage> History { get; } = history ?? [];

    /// <summary>Messages the user sent while the run goes on, waiting for the model's next step. Locked on itself.</summary>
    private readonly List<UserInput> _incoming = [];

    private bool HasIncoming
    {
        get { lock (_incoming) return _incoming.Count > 0; }
    }

    /// <summary>Cancelled when such a message arrives, so a wait in progress ends early (see <see cref="ToolContext.UserMessage"/>).</summary>
    private CancellationTokenSource _arrived = new();

    /// <inheritdoc cref="Steer(UserInput)"/>
    public void Steer(string text) => Steer(new UserInput(text));

    /// <summary>
    /// A message the user sent while the agent works: it joins the conversation before the model's
    /// next step, and a wait in progress ends early so the model reads it soon. Until now Enter did
    /// nothing during a run, even while BlackBox waited for an OK, and the text sat in the box.
    /// Any thread.
    /// </summary>
    public void Steer(UserInput input)
    {
        lock (_incoming)
        {
            _incoming.Add(input);
            _arrived.Cancel();
        }
    }

    /// <summary>
    /// Takes back a message the model hasn't read yet (Cancel on it in the app): true while it was
    /// still waiting, false once the model has it. The very instance that was sent. Any thread.
    /// </summary>
    public bool Unsteer(UserInput input)
    {
        lock (_incoming)
        {
            if (_incoming.RemoveAll(i => ReferenceEquals(i, input)) == 0)
                return false;
            // None left: a wait that starts now goes on, rather than ending for a message that isn't there.
            if (_incoming.Count == 0 && _arrived.IsCancellationRequested)
            {
                _arrived = new CancellationTokenSource();
                toolContext.UserMessage = _arrived.Token;
                _interruptAsked = false;
            }
            return true;
        }
    }

    /// <summary>Messages that arrived too late for the run (it ended first); the caller sends them on.</summary>
    public List<UserInput> TakeUndelivered()
    {
        lock (_incoming)
        {
            var left = _incoming.ToList();
            _incoming.Clear();
            return left;
        }
    }

    /// <summary>The model's reply in progress, or the tool calls of its reply, which <see cref="Interrupt"/> cuts short. Locked with <see cref="_incoming"/>.</summary>
    private CancellationTokenSource? _step;

    /// <summary>Interrupt was pressed with messages waiting: what of the step is still to come is cut short as well.</summary>
    private bool _interruptAsked;

    /// <summary>
    /// The Interrupt button: the messages the user sent meanwhile go to the model now, not after
    /// what it is doing. Its reply in progress (thinking or writing) is cut off, or the tool calls
    /// it is running are stopped and the rest of them not run, and the next step starts with the
    /// messages. A long think could otherwise go on for minutes past a correction that made it
    /// pointless. False when no message waits. Any thread.
    /// </summary>
    public bool Interrupt()
    {
        CancellationTokenSource? step;
        lock (_incoming)
        {
            if (_incoming.Count == 0)
                return false;
            _interruptAsked = true;
            step = _step;
        }
        try { step?.Cancel(); }
        catch (ObjectDisposedException) { } // the step ended at that very moment; the next one delivers the messages
        return true;
    }

    /// <summary>The scope of one part of a step that <see cref="Interrupt"/> can cut short; cut at once when Interrupt came just before it.</summary>
    private sealed class StepScope : IDisposable
    {
        private readonly AgentSession _session;
        public CancellationTokenSource Source { get; }

        public StepScope(AgentSession session, CancellationToken ct)
        {
            _session = session;
            Source = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (session._incoming)
            {
                session._step = Source;
                if (session._interruptAsked && session._incoming.Count > 0)
                    Source.Cancel();
            }
        }

        public CancellationToken Token => Source.Token;

        /// <summary>Interrupt cut it short, and not a stop of the whole run.</summary>
        public bool Interrupted(CancellationToken run) => Source.IsCancellationRequested && !run.IsCancellationRequested;

        public void Dispose()
        {
            lock (_session._incoming)
                if (ReferenceEquals(_session._step, Source))
                    _session._step = null;
            Source.Dispose();
        }
    }

    /// <summary>Said in the chat where Interrupt cut the model's reply off.</summary>
    internal const string InterruptedNotice = "Interrupted to read your message.";

    /// <summary>The result of a tool call Interrupt stopped, or kept from running.</summary>
    public const string InterruptedResult = "Interrupted: the user's message (below) came in before this finished, and the user asked for it to be read at once. " +
                                            "It may have done part of its work: check before doing it again.";

    public const string NotRunResult = "Not run: the user's message (below) came in first, and the user asked for it to be read at once.";

    // The same where the user took the message back after Interrupt, as the step was already being cut short.
    public const string TakenBackResult = "Interrupted: the user asked for a message of theirs to be read at once, then took it back. " +
                                          "It may have done part of its work: check before doing it again.";

    public const string TakenBackNotRunResult = "Not run: the user asked for a message of theirs to be read at once, then took it back. Run it if it is still needed.";

    /// <summary>The result of a call Interrupt stopped (or kept from running), saying whether the message it was for is still there to read.</summary>
    private string CutShort(bool ran)
    {
        lock (_incoming)
            return _incoming.Count > 0 ? ran ? InterruptedResult : NotRunResult : ran ? TakenBackResult : TakenBackNotRunResult;
    }

    /// <summary>Whether a call's result is one Interrupt left: it stopped, rather than failed or was declined.</summary>
    public static bool IsCutShort(string text) => text is InterruptedResult or NotRunResult or TakenBackResult or TakenBackNotRunResult;

    /// <summary>Adds the user's messages that arrived since the last step, as user messages: the history only grows.</summary>
    /// <param name="turnEnded">
    /// The model's last reply ended its turn: the messages are what it works on next, as if sent
    /// after the run, not something on top of a task in progress. Marked as that, a message sent as
    /// the model wrote its final answer was answered, and then the model was asked whether the task
    /// it had already finished was done, and answered the same thing again.
    /// </param>
    private void DeliverIncoming(bool turnEnded, IAgentObserver observer)
    {
        List<UserInput> delivered;
        lock (_incoming)
        {
            if (!_arrived.IsCancellationRequested)
                return;
            // A fresh token for the next message; the one a wait may still hold stays cancelled.
            _arrived = new CancellationTokenSource();
            toolContext.UserMessage = _arrived.Token;
            _interruptAsked = false;
            delivered = _incoming.ToList();
            _incoming.Clear();
            var first = true;
            foreach (var input in delivered)
            {
                if (turnEnded)
                {
                    if (first)
                    {
                        (_request, _interrupted) = (RequestOf(input), false);
                        _guesswork?.StartRun(input.Text);
                    }
                    first = false;
                    History.Add(input.ToMessage(toolContext.SeesImages, toolContext.CoordinateGrid));
                    continue;
                }
                _guesswork?.Heard(input.Text);
                History.Add(Interjection(input.ToMessage(toolContext.SeesImages, toolContext.CoordinateGrid)));
                _interrupted = true;
            }
            toolContext.UserPictures = UserInput.PicturesIn(History);
        }
        foreach (var input in delivered)
            observer.OnUserMessageDelivered(input);
    }

    /// <summary>What the user asked for in this run, quoted to the model when a message of theirs comes in on top of it.</summary>
    private string? _request;

    /// <summary>
    /// A message came in on top of the request, and the model hasn't gone back to work since (no
    /// tool call): it may have answered it and stopped there.
    /// </summary>
    private bool _interrupted;

    /// <summary>
    /// A message that came in while the model works, marked as one: answered or acted on, and then
    /// the request goes on. Unmarked, a question read as the new task: asked mid-task why something
    /// had failed, while it was trying Pony Diffusion for a picture, the model answered the question
    /// and ended there, the picture forgotten.
    /// </summary>
    private ChatMessage Interjection(ChatMessage message)
    {
        var request = _request is { } r ? $"\"{r}\"" : "the task above";
        var note = $"[Sent while you were working on {request}. Answer it or do what it asks, then carry on with {(_request is null ? "the task" : "that")} unless it changes or cancels it.]\n";
        if (message.Parts is { Count: > 0 } parts)
            parts[0] = ContentPart.FromText(note + parts[0].Text);
        else
            message.Content = note + message.Content;
        return message;
    }

    /// <summary>What the model is told when it would end a run a message of the user's came in on.</summary>
    private string ResumeText() =>
        $"[BlackBox] Before you finish: the user's message came in while you were working on {(_request is { } r ? $"\"{r}\"" : "the task before it")}. " +
        "If that isn't done and the user didn't change or cancel it, carry on with it now. If it is done or no longer wanted, reply without tools.";

    /// <summary>
    /// The request a run works on, in the user's words (its first lines): the message that started
    /// it, or for one that only says to go on ("Continue."), the last message before it that asked
    /// for something. Null for a message of files alone.
    /// </summary>
    private string? RequestOf(UserInput input)
    {
        var text = input.Text.Trim();
        if (GoOn().IsMatch(text))
            text = History.LastOrDefault(m => m.FromUser && !GoOn().IsMatch(TypedText(m))) is { } earlier ? TypedText(earlier) : "";
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length == 0 ? null : text.Length <= 200 ? text : text[..197].TrimEnd() + "…";
    }

    /// <summary>What the user typed in a message of theirs, without the list of files that came with it.</summary>
    private static string TypedText(ChatMessage m)
    {
        var text = (m.Parts is { Count: > 0 } parts ? parts[0].Text : m.Content) ?? "";
        var files = text.IndexOf("[The user attached ", StringComparison.Ordinal);
        return (files < 0 ? text : text[..files]).Trim();
    }

    [GeneratedRegex(@"^(continue|go on|carry on|keep going)[.!]?$", RegexOptions.IgnoreCase)]
    private static partial Regex GoOn();

    public Task RunAsync(string userText, IAgentObserver observer, CancellationToken ct) => RunAsync(new UserInput(userText), observer, ct);

    /// <summary>Runs the task the user sent: the model works on it, with its tools, until it is done or stopped.</summary>
    public async Task RunAsync(UserInput input, IAgentObserver observer, CancellationToken ct)
    {
        _request = RequestOf(input);
        _interrupted = false;
        History.Add(input.ToMessage(toolContext.SeesImages, toolContext.CoordinateGrid));
        // A picture given earlier in the chat (a reference sheet) is still the one to follow.
        toolContext.UserPictures = UserInput.PicturesIn(History);
        toolContext.UserMessage = _arrived.Token;
        if (toolContext.Locate is null && FreshLook.Enabled)
        {
            var look = new FreshLook(client, requestOptions);
            // Not once the server has refused pictures: every click would send it one more.
            toolContext.Locate = (image, space, target, ct2) => toolContext.SeesImages
                ? look.LocateAsync(image, space.Describe(), target, ct2)
                : Task.FromResult<(double X, double Y)?>(null);
        }
        if (toolContext.Ask is null)
        {
            var eyes = new FreshLook(client, requestOptions);
            toolContext.Ask = (images, question, ct2) => toolContext.SeesImages ? eyes.AskAsync(images, question, ct2) : Task.FromResult<string?>(null);
        }
        var nudges = 0;
        var cutCalls = 0;
        var loops = new LoopDetector();
        // Pointing at web_search only makes sense when the model has it. Kept for the chat: "still
        // wrong" in the next message is a failed try, and the time since the last look-up goes on.
        var guesswork = _guesswork ??= tools.Find("web_search") is not null ? new Guesswork() : null;
        guesswork?.StartRun(input.Text);
        var justReset = false;
        var turnEnded = false;

        try
        {
            // No step limit: a run ends when the model is done or the user presses Stop or Pause/Break.
            // Tasks like "keep chatting for 12 hours" are valid;
            // runaway loops are handled by LoopDetector, and growth by context resets.
            for (var step = 1; ; step++)
            {
                ct.ThrowIfCancellationRequested();
                DeliverIncoming(turnEnded, observer);
                turnEnded = false;
                // The reset first: its handoff note is a reply of its own, and the step's reply
                // comes after it, in a new step.
                if (contextTokens > 0 && TokensInUse() > ResetThreshold)
                    await ResetContextAsync(observer, ct).ConfigureAwait(false);
                observer.OnStepStarted(step);

                var request = new List<ChatMessage>(History.Count + 1) { ChatMessage.System(systemPrompt) };
                request.AddRange(History);

                ChatMessage reply;
                using (var thinking = new StepScope(this, ct))
                {
                    try
                    {
                        reply = await CompleteWithResetAsync(request, observer, ct, thinking.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (thinking.Interrupted(ct))
                    {
                        // Interrupt: what streamed stays on screen, and the reply is left out of the
                        // conversation, which goes on with the user's messages.
                        observer.OnNotice(InterruptedNotice);
                        continue;
                    }
                }
                var cutCall = reply.FinishReason == "length" && DropCutCalls(reply);
                var cutOff = reply.FinishReason == "length" && !reply.HasToolCalls;
                if (cutOff && contextTokens > 0 && !justReset && OutOfRoom(reply))
                {
                    // The server ran out of context mid-reply, so the size estimate was off. The
                    // cut-off reply is thrown away and this step is redone after a reset.
                    observer.OnNotice("The model ran out of context in the middle of its reply.");
                    observer.OnAssistantMessage(new ChatMessage { Role = ChatRole.Assistant, Content = "" });
                    await ResetContextAsync(observer, ct).ConfigureAwait(false);
                    justReset = true;
                    continue;
                }
                var afterReset = justReset;
                justReset = false;
                TextToolCallParser.TryExtract(reply, tools);
                cutCall |= reply.FinishReason == "length" && DropCutCalls(reply);
                if (cutCall)
                    observer.OnNotice("The server's length limit cut a tool call short (in LM Studio: Limit Response Length); it wasn't run, and the model is asked to write in smaller parts.");
                else if (cutOff && !afterReset)
                    observer.OnNotice("The server stopped the reply at its length limit (in LM Studio: Limit Response Length); asking the model to go on.");
                // "The task is complete" followed by yet another screenshot: small models can keep
                // re-verifying forever. A completion claim with only look-only calls ends the turn.
                if (reply.HasToolCalls && LooksFinished(reply.Content) &&
                    reply.ToolCalls!.All(c => ToolCallRepair.ResolveTool(c.Name, tools)?.Risk == ToolRisk.ReadOnly))
                    reply.ToolCalls = null;
                History.Add(reply);
                observer.OnAssistantMessage(reply);

                if (!reply.HasToolCalls)
                {
                    // Its only call was cut short: it is told why nothing ran, a few times in a row at most.
                    if (cutCall && ++cutCalls <= MaxCutCalls)
                    {
                        History.Add(ChatMessage.User(CutToolCallNote));
                        continue;
                    }
                    // The user wrote while the model answered: it goes on with that, as its next
                    // request when this answer finished the task.
                    if (HasIncoming)
                    {
                        turnEnded = !cutOff && !LooksUnfinished(reply);
                        continue;
                    }
                    // Small models sometimes announce an action ("Let me click Save.") and stop,
                    // or return nothing at all. Push them on a couple of times before giving up.
                    if (nudges < MaxNudges && (cutOff || LooksUnfinished(reply)))
                    {
                        nudges++;
                        History.Add(ChatMessage.User(NudgeText));
                        continue;
                    }
                    // A message came in on top of the request and the model answered it without
                    // going back to work: the request may still be waiting. Once per such message,
                    // the model is asked.
                    if (_interrupted)
                    {
                        _interrupted = false;
                        History.Add(ChatMessage.User(ResumeText()));
                        continue;
                    }
                    observer.OnFinished(AgentStopReason.Completed, null);
                    return;
                }
                nudges = 0; // it is moving again; a later stall gets its own nudges
                // Working again after the user's message, it is back on the request. Asked at the
                // end whether that was done anyway, the model wrote its answer a second time.
                _interrupted = false;
                if (!cutCall)
                    cutCalls = 0;

                // Only the last screenshot goes to the model: the tools' results come before it.
                ChatMessage? shown = null;
                ContentPart? screenshot = null;
                string? caption = null;
                ChatMessage? last = null;
                // This reply's calls were decided on the screen in the last screenshot.
                toolContext.Seen = toolContext.LastShot;
                var called = reply.ToolCalls!.Select(c => ToolCallRepair.ResolveTool(c.Name, tools)).ToList();
                guesswork?.Read(reply, called);
                using var calls = new StepScope(this, ct);
                foreach (var call in reply.ToolCalls!)
                {
                    // Interrupt: the rest of the reply's calls give way to the user's message.
                    if (calls.Interrupted(ct))
                    {
                        History.Add(last = ChatMessage.ToolResult(call.Id, CutShort(ran: false)));
                        continue;
                    }
                    var result = await RunToolAsync(call, observer, ct, calls.Token).ConfigureAwait(false);
                    // A call Interrupt stopped failed for no fault of the model's.
                    if (!calls.Interrupted(ct) && loops.Check(call, result) is { } warning)
                        result = result with { Text = result.Text + "\n" + warning };
                    if (!calls.Interrupted(ct) && guesswork?.Check(ToolCallRepair.ResolveTool(call.Name, tools), result, call.Arguments) is { } lookItUp)
                        result = result with { Text = result.Text + "\n" + lookItUp };
                    if (tools.Find("index_folder") is not null && _projects.Check(ToolCallRepair.ResolveTool(call.Name, tools), call.Arguments, result, toolContext) is { } indexIt)
                        result = result with { Text = result.Text + "\n" + indexIt };
                    // A model that can't see gets no picture (an MCP tool's, say): its server
                    // refused that request and every one after it, since the conversation keeps it.
                    if (result.Image is not null && !toolContext.SeesImages)
                        result = result with { Image = null, Text = result.Text + "\n(A picture came with this, left out: the model in use can't see images.)" };
                    var message = last = ChatMessage.ToolResult(call.Id, result.Text);
                    History.Add(message);
                    if (result.Image is not null)
                        (shown, screenshot, caption) = (message, ContentPart.FromImage(result.Image, result.ImageMime), result.ImageCaption);
                }
                // The whole calls ran; the cut one is said with the last result.
                if (cutCall && last is not null)
                    last.Content += "\n" + CutToolCallNote;
                // Not when Interrupt cut the calls short: the user's message is to be read at once.
                if (guesswork?.Question is { } question && last is not null && !calls.Interrupted(ct))
                    await LookUpAsync(guesswork, question, called, last, observer, ct).ConfigureAwait(false);

                // Most OpenAI-compatible servers only accept images in user messages,
                // so the screenshot (or a picture read_file opened) is delivered right after the tool results.
                if (screenshot is not null)
                {
                    shown!.Content += caption is null ? "\n(The screenshot is attached in the next message.)" : "\n(The picture is attached in the next message.)";
                    History.Add(new ChatMessage { Role = ChatRole.User, Parts = [ContentPart.FromText(caption ?? ScreenshotText), screenshot] });
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Said in the conversation: stopped in Paint and given a story to write next, the model
            // wrote it and then "returned to the Paint task, which is still incomplete".
            History.Add(ChatMessage.User(StoppedNote));
            observer.OnFinished(AgentStopReason.Cancelled, null);
        }
        catch (Exception ex)
        {
            if (ex is not LlmException)
                observer.OnFailed(ex);
            observer.OnFinished(AgentStopReason.Error, ex.Message);
        }
        finally
        {
            RepairDanglingToolCalls();
            // What tools left to do once the task is over (ComfyUI letting go of its models), a
            // few seconds at most: a server that went away must not hold up the end of the run.
            try { await toolContext.FinishRunAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
    }

    private const int MaxNudges = 2;

    /// <summary>How many dropped connections one step survives.</summary>
    internal const int MaxReconnects = 12;

    /// <summary>How many looping replies one step survives before the run ends with an error.</summary>
    private const int MaxRepetitionRetries = 3;

    internal const string RepetitionNote =
        "[BlackBox] Your last reply was cut off because your thinking kept repeating the same lines. " +
        "Don't re-check earlier screens from memory: you may have misread one. Trust what the current screen shows " +
        "(take a new screenshot if needed), decide, and call the next tool.";

    internal const string LongToolCallNote =
        "[BlackBox] Your last reply was lost: the connection drops when a single tool call takes more than about a minute to write. " +
        "Keep every tool call short. Write long files in parts of at most about 100 lines: create the file with write_file, " +
        "then add each next part with write_file and append set to true. Continue with the task.";
    internal const string StillTooLongNote =
        "[BlackBox] Your reply was lost again for the same reason: that part was still too long to write in under a minute. " +
        "Make each part much smaller, at most about 40 lines (about 1,500 characters), and add the rest with further " +
        "write_file calls with append set to true. Continue with the task.";

    // Marked as BlackBox's: taken for the user's words, "continue" read as a go-ahead, and the
    // model asked again for a command the user had just declined twice.
    /// <summary>A reply's tool call the server's length limit cut short, its arguments half written (see <see cref="DropCutCalls"/>).</summary>
    internal const string CutToolCallNote =
        "[BlackBox] Your last tool call was cut off by the server's limit on how long a reply may be, before its arguments were complete, " +
        "so it was not run. Write long content in parts of at most about 100 lines: create the file with write_file, then add each next part " +
        "with write_file and append set to true. Continue with the task.";

    private const int MaxCutCalls = 3;

    /// <summary>
    /// Takes the tool calls whose arguments aren't whole JSON out of a reply the length limit
    /// ended; true when there was one. Repaired by closing what was left open, such a call ran:
    /// write_file wrote half the file, and edit_file put half its text in, and both said done.
    /// </summary>
    internal static bool DropCutCalls(ChatMessage reply)
    {
        if (reply.ToolCalls is not { Count: > 0 } calls)
            return false;
        var whole = calls.Where(c => ToolCallRepair.IsWholeJson(c.Arguments)).ToList();
        if (whole.Count == calls.Count)
            return false;
        reply.ToolCalls = whole.Count > 0 ? whole : null;
        return true;
    }

    internal const string NudgeText =
        "[BlackBox] Continue with the task. If there is another step, call the tool for it now. If the task is finished, reply with a short summary of what you did.";

    /// <summary>Where the user stopped a run (Stop, Pause/Break), so what they write next is read in that light (see <see cref="SystemPrompt"/>).</summary>
    internal const string StoppedNote = "[BlackBox] The user stopped you here.";

    /// <summary>Said above a screenshot a tool took; any other picture a tool gives comes with its own caption.</summary>
    internal const string ScreenshotText = "Screenshot after the last action:";

    // Not "try another approach": told that after a declined listing command, the model asked for
    // it twice more in other words and then listed the folder with another tool.
    internal const string DeclinedText =
        "The user declined this action. Don't do the same thing another way; ask them what they would like instead, or go on with the parts of the task that don't need it.";

    /// <summary>
    /// Whether a reply cut off by the length limit ran out of context, rather than into a
    /// reply-length cap set on the server; without a token count it is taken as out of context.
    /// </summary>
    private bool OutOfRoom(ChatMessage reply) =>
        reply.PromptTokens is not { } prompt || prompt + (reply.CompletionTokens ?? 0) >= contextTokens * 9L / 10;

    internal static bool LooksFinished(string? content) => content is { Length: > 0 } && CompletionClaim().IsMatch(content);

    internal static bool LooksUnfinished(ChatMessage reply)
    {
        var text = (reply.Content ?? "").Trim();
        if (text.Length == 0)
            return true;
        var tail = text.Length > 240 ? text[^240..] : text;
        // "Let me know how you'd like to proceed." asks the user; it announces no step.
        return tail.EndsWith(':') || AnnouncedAction().IsMatch(tail);
    }

    [GeneratedRegex(@"\b((?<!\b(whether|if|once|when)\s+(the\s+)?)task (is|has been) (now )?(complete|completed|done|finished)|i('ve| have) (successfully )?(completed|finished) (the|your|this) task|everything is done|all done)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CompletionClaim();

    [GeneratedRegex(@"(^|[.!?\n]\s*)(let me(?! know)|let's|i'll|i will|i'm going to|i am going to|next,? i|now i|i need to)\b[^.!?\n]*[.!]?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex AnnouncedAction();

    /// <summary>Guesswork seen in this chat (see <see cref="Guesswork"/>); null without web_search.</summary>
    private Guesswork? _guesswork;

    /// <summary>The projects indexed in this chat, and those the model was told to index (see <see cref="ProjectNudge"/>).</summary>
    private readonly ProjectNudge _projects = new();

    /// <summary>The web search BlackBox runs for a question of the model's: five results, as web_search finds them.</summary>
    internal Func<string, CancellationToken, Task<IReadOnlyList<Web.SearchHit>>>? SearchWeb { get; set; }

    /// <summary>
    /// Searches the web for a question the model asked itself and went on without looking up, and
    /// puts what it found under the step's last result: told to search, models rarely did, and
    /// trying from memory cost hours where a guide had the answer.
    /// </summary>
    private async Task LookUpAsync(Guesswork guesswork, string question, IReadOnlyList<Tool?> called, ChatMessage last, IAgentObserver observer, CancellationToken ct)
    {
        // What the question is about, when it doesn't say: the program the step's calls drive.
        var subject = called.OfType<McpTool>().Select(t => t.ServerName).FirstOrDefault() ??
                      (called.Any(t => t is ComfyTool or FilmTool) ? "ComfyUI" : guesswork.Subject);
        // The request's "Blender 5.2" says more than the server's name "blender".
        if (subject is not null && guesswork.Subject is { } named && named.Contains(subject, StringComparison.OrdinalIgnoreCase))
            subject = named;
        var query = Guesswork.Query(question, subject);
        var error = guesswork.QuestionIsError;
        guesswork.Searched(question);
        IReadOnlyList<Web.SearchHit> hits;
        try
        {
            hits = await (SearchWeb ?? ((q, c) => Web.WebSearch.SearchAsync(q, 5, toolContext.Browser, c)))(query, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Web.WebSearchException or HttpRequestException or IOException ||
                                   ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return; // no answer is no worse than not asking: the model has web_search itself
        }
        if (hits.Count == 0)
            return;
        observer.OnNotice(error ? $"Looked up on the web the error the model went on without looking up: {query}" : $"Looked up on the web what the model asked itself: {query}");
        last.Content += "\n" + Guesswork.Found(question, hits, error);
    }

    /// <param name="ct">The run's: Stop and Pause/Break.</param>
    /// <param name="step">Also cancelled by <see cref="Interrupt"/>, which stops the call and leaves the run going.</param>
    private async Task<ToolResult> RunToolAsync(ToolCall call, IAgentObserver observer, CancellationToken ct, CancellationToken step)
    {
        var tool = ToolCallRepair.ResolveTool(call.Name, tools);
        if (tool is null)
        {
            var unknown = ToolResult.Error($"Unknown tool '{call.Name}'. Available: {string.Join(", ", tools.All.Select(t => t.Name))}.");
            observer.OnToolStarted(call, call.Name);
            observer.OnToolFinished(call, unknown);
            return unknown;
        }

        JsonObject args;
        string summary;
        try
        {
            args = ToolCallRepair.ParseArguments(call.Arguments);
            // The conversation keeps the call as repaired: sent back as it came, arguments that
            // aren't JSON can make a server's chat template fail every request after it.
            if (!ToolCallRepair.IsWholeJson(call.Arguments))
                call.Arguments = args.ToJsonString(Comfy.ComfyWorkflow.Compact);
            ToolCallRepair.ApplyNameHints(call.Name, args, tool);
            ToolCallRepair.NormalizeArguments(args, tool);
            summary = tool.Describe(args);
        }
        catch (ToolArgumentException ex)
        {
            var bad = ToolResult.Error($"{ex.Message} Parameters for {tool.Name}: {tool.Parameters.ToJsonString()}");
            observer.OnToolStarted(call, call.Name);
            observer.OnToolFinished(call, bad);
            return bad;
        }

        if (NeedsApproval(tool) && !await observer.RequestApprovalAsync(call, tool, summary, step).ConfigureAwait(false))
        {
            // A stop while the call waited for an OK is no answer to it. Taken for a no, it went into
            // the conversation as declined, and after Continue the model asked what to do instead.
            ct.ThrowIfCancellationRequested();
            var denied = step.IsCancellationRequested ? ToolResult.Error(CutShort(ran: false)) : ToolResult.Error(DeclinedText);
            observer.OnToolFinished(call, denied);
            return denied;
        }

        observer.OnToolStarted(call, summary);
        ToolResult result;
        toolContext.Progress = text => observer.OnToolProgress(call, text);
        try
        {
            result = await tool.ExecuteAsync(args, toolContext, step).ConfigureAwait(false);
        }
        catch (ToolArgumentException ex)
        {
            result = ToolResult.Error($"{ex.Message} Parameters for {tool.Name}: {tool.Parameters.ToJsonString()}");
        }
        catch (OperationCanceledException) when (step.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            result = ToolResult.Error(CutShort(ran: true));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Whatever goes wrong in one tool (a timeout, a file in use, a bug) is the model's to
            // work around; only Stop ends the run.
            result = ToolResult.Error(ex.Message);
        }
        finally
        {
            toolContext.Progress = null;
        }
        observer.OnToolFinished(call, result);
        return result;
    }

    /// <summary>The system prompt from the next request on (an MCP server added mid-run, which the prompt lists).</summary>
    internal void UsePrompt(string prompt) => systemPrompt = prompt;

    /// <summary>Whether the model takes pictures as far as this run found: false once its server refused them, so the next runs are set up without.</summary>
    public bool SeesImages => toolContext.SeesImages;

    private bool HasImages() => History.Any(m => m.Parts?.Any(p => p.IsImage) == true);

    /// <summary>Puts each picture in the conversation in words: the model can't be shown them.</summary>
    private void DropImages()
    {
        foreach (var m in History.Where(m => m.Parts?.Any(p => p.IsImage) == true))
        {
            m.Content = string.Join("\n", m.Parts!.Select(p => p.IsImage ? "(A picture, left out: the model in use can't see images.)" : p.Text));
            m.Parts = null;
        }
    }

    private bool NeedsApproval(Tool tool) => toolContext.Settings.Approval switch
    {
        ApprovalMode.AskAll => tool.Risk != ToolRisk.ReadOnly,
        ApprovalMode.AskRisky => tool.Risk == ToolRisk.Risky,
        _ => false,
    };

    /// <summary>
    /// Sends the request; if the server says it does not fit, resets the context and tries again
    /// (twice at most) before giving up.
    /// </summary>
    /// <param name="replying">The run's token, or also cancelled by <see cref="Interrupt"/>: the reply and the waits to send it again. A reset goes by <paramref name="ct"/>.</param>
    private async Task<ChatMessage> CompleteWithResetAsync(List<ChatMessage> request, IAgentObserver observer, CancellationToken ct, CancellationToken replying)
    {
        var repeats = 0;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var guard = new RepetitionGuard(observer.OnDelta);
                return await CompleteWithReconnectAsync(request, tools.Definitions(), requestOptions, observer, guard.OnDelta, replying).ConfigureAwait(false);
            }
            catch (RepetitionException) when (++repeats <= MaxRepetitionRetries)
            {
                // Dropping the stream stops the server too. The looping reply is thrown away and
                // the model is told to trust the screen instead of its memory; appending the note
                // keeps the server's cache.
                observer.OnNotice("The model's thinking started repeating itself; cut it off and asked it to look again.");
                observer.OnAssistantMessage(new ChatMessage { Role = ChatRole.Assistant, Content = "" });
                observer.OnStepStarted(0);
                if (History[^1].Content != RepetitionNote)
                {
                    var note = ChatMessage.User(RepetitionNote);
                    History.Add(note);
                    request.Add(note);
                }
                attempt--;
            }
            catch (ImagesRefusedException ex) when (HasImages())
            {
                // The model was taken to see, or the chat holds pictures from one that could, and
                // its server refused them: they leave the conversation (which the server then reads
                // anew, once), and later ones are left out as they come. Kept in, they failed every
                // request after the first screenshot, or every message after switching models.
                toolContext.SeesImages = false;
                observer.OnNotice(ex.Message.TrimEnd().TrimEnd('.') + ". Going on without them.");
                DropImages();
                request.Clear();
                request.Add(ChatMessage.System(systemPrompt));
                request.AddRange(History);
                attempt--;
            }
            catch (ContextOverflowException ex) when (attempt < 2)
            {
                if (ex is InputRateException rate)
                {
                    // Four fifths of it, so a request with its new screenshots and replies still fits.
                    var fits = Math.Max(16_384, rate.PerMinute * 4 / 5);
                    if (contextTokens == 0 || fits < contextTokens)
                    {
                        observer.OnNotice($"The API key's account takes {rate.PerMinute / 1000}k input tokens a minute, and one request of the whole conversation was more than that; working with {fits / 1000}k tokens of context from now on.");
                        contextTokens = fits;
                    }
                }
                else if (ex is RequestTooLargeException tooLarge)
                {
                    // Too many bytes, not tokens: the screenshots in it. A context a quarter smaller
                    // resets before a request gets that large again.
                    var smaller = Math.Max(16_384, TokensInUse() * 3 / 4);
                    if (contextTokens == 0 || smaller < contextTokens)
                    {
                        observer.OnNotice($"The request, {tooLarge.Bytes / 1e6:0.#} MB with its screenshots, was more than the server takes; working with {smaller / 1000}k tokens of context from now on.");
                        contextTokens = smaller;
                    }
                }
                // The context size was assumed or wrong; most servers name the real one.
                else if (ex.ContextLimit is { } limit && (contextTokens == 0 || limit < contextTokens))
                {
                    observer.OnNotice($"The server's context is {limit / 1000}k tokens, not {contextTokens / 1000}k; working with that from now on.");
                    contextTokens = limit;
                }
                await ResetContextAsync(observer, ct, overflowed: true).ConfigureAwait(false);
                request.Clear();
                request.Add(ChatMessage.System(systemPrompt));
                request.AddRange(History);
                observer.OnStepStarted(0); // the retried reply is a new one, after the handoff note
            }
        }
    }

    /// <summary>
    /// Sends one request, retrying when the connection drops. Unstable links (VPNs, proxies that
    /// cut silent connections) drop long requests, but the server keeps what it already processed
    /// in its prompt cache, so sending the identical request again resumes where it stopped.
    /// </summary>
    private async Task<ChatMessage> CompleteWithReconnectAsync(
        IReadOnlyList<ChatMessage> request, IReadOnlyList<ToolDefinition> toolDefs, ChatRequestOptions options,
        IAgentObserver observer, Action<StreamDelta>? onDelta, CancellationToken ct)
    {
        var drops = 0;
        var held = 0;
        var away = 0;
        while (true)
        {
            try
            {
                return await client.CompleteAsync(request, toolDefs, options, onDelta, ct, observer.OnSending).ConfigureAwait(false);
            }
            catch (RateLimitedException ex)
            {
                // Not a broken link: the server holds requests back for a while (Kilo's free models
                // take 200 an hour), so the same request goes again after the wait it names, or a
                // growing one. As many times as it takes: Stop ends the wait.
                var wait = RateLimitWait(ex.RetryAfter, ++held);
                // Kilo doesn't say for how long, and five-minute waits read as BlackBox being stuck.
                // A free model's daily allowance, which Kilo shares among all its users, comes back no sooner.
                var why = !KiloGateway.Is(client.BaseUrl) ? ""
                    : ex.Message.Contains("daily", StringComparison.OrdinalIgnoreCase)
                        ? " Kilo shares a free model's daily allowance among all its users, so this can last until tomorrow: pick another free model at the top, or use a Kilo key with credits or a model server of your own."
                        : " Kilo's free models take 200 requests an hour from each PC, so this can last until the hour is up; a Kilo key with credits, or a model server of your own, has no such limit.";
                observer.OnNotice($"{ex.Message}{why} Trying again in {Describe(wait)} (Stop ends the wait)…");
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            catch (ConnectionLostException ex) when (ex.Unreachable)
            {
                // Nothing got through: the server's PC asleep or restarting, LM Studio closed, the
                // VPN down. Twelve quick tries were over in half a minute, and an unattended task of
                // hours ended on a short outage; it waits for the server instead, the waits growing
                // as for a busy server. Stop ends the wait.
                var wait = RateLimitWait(null, ++away);
                observer.OnNotice($"{ex.Message} Trying again in {Describe(wait)} (Stop ends the wait)…");
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            catch (ConnectionLostException ex) when (++drops <= MaxReconnects)
            {
                // Still no room in llama.cpp's KV cache after three tries: what other requests hold
                // (another BlackBox on the same LM Studio keeps its cached prompt there) leaves too
                // little for this conversation. 2048 waited out all twelve tries that way. A fresh,
                // shorter conversation fits, and a smaller context keeps it fitting.
                if (ex.ServerFailed && drops > 3 && OpenAiChatClient.OutOfRoomWords().IsMatch(ex.Message) && TokensInUse() > 16_384)
                {
                    var smaller = Math.Max(16_384, TokensInUse() * 3 / 4);
                    if (contextTokens == 0 || smaller < contextTokens)
                    {
                        observer.OnNotice($"The server has no room for {TokensInUse() / 1000}k tokens of conversation right now; working with {smaller / 1000}k tokens of context from now on.");
                        contextTokens = smaller;
                    }
                    throw new ContextOverflowException("the server has no room left for it (" + ex.Message + ")");
                }
                // The server's own failure is given time to pass: other requests filling its memory,
                // its model reloading after a crash. Sent again at once, all twelve tries were gone in seconds.
                var pause = ex.ServerFailed ? TimeSpan.FromTicks(Math.Min(RateLimitWaits[Math.Min(drops, RateLimitWaits.Length) - 1].Ticks, TimeSpan.TicksPerMinute))
                    : drops > 1 ? TimeSpan.FromSeconds(Math.Min(drops - 1, 3)) : TimeSpan.Zero;
                observer.OnNotice(ex.ServerFailed
                    ? $"{ex.Message} Trying again in {Describe(pause)}, attempt {drops} of {MaxReconnects}…"
                    : $"Connection lost ({ex.Message}). Reconnecting, attempt {drops} of {MaxReconnects}…");
                // A model server that fails a request may have got stuck on how the prompt begins,
                // as Kilo's free Nemotron did for half an hour: the request goes again a line lower.
                if (ex.Upstream)
                    client.ShiftPrompt();
                // Resending would only repeat a tool call too long to get through, so the model is
                // asked to split it. The note stays in the history so later calls stay short too.
                // If the smaller parts are still too long, it is told once more, with a firmer limit.
                if (!ex.ServerFailed && ex.ToolCallInProgress is not null && toolDefs.Count > 0 && request[^1].Content != StillTooLongNote)
                {
                    var again = request[^1].Content == LongToolCallNote;
                    var note = ChatMessage.User(again ? StillTooLongNote : LongToolCallNote);
                    History.Add(note);
                    request = [.. request, note];
                    observer.OnNotice(again
                        ? "The model's smaller parts were still too long; asking for much smaller ones."
                        : "The reply was lost while the model was writing a long tool call; asking it to write in smaller parts.");
                }
                if (ex.ReceivedTokens && onDelta is not null)
                {
                    // Discard the half-streamed reply; the retry produces it again.
                    observer.OnAssistantMessage(new ChatMessage { Role = ChatRole.Assistant, Content = "" });
                    observer.OnStepStarted(0);
                }
                // The server keeps what it processed, so resending at once loses nothing; a short
                // pause only after repeated drops, in case the link itself is down.
                if (pause > TimeSpan.Zero)
                    await Task.Delay(pause, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The waits after a 429 that names none: a server that was only busy answers after the first,
    /// an hourly allowance used up gets asked every five minutes.
    /// </summary>
    internal static readonly TimeSpan[] RateLimitWaits =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];

    /// <summary>
    /// How long to wait after the <paramref name="attempt"/>th 429 in a row: what the server says,
    /// within a second and an hour, else <see cref="RateLimitWaits"/>. From the second try on, no
    /// sooner than those, up to a minute: a server that went on saying "in a second" (or a reset
    /// time already past) was asked once a second for as long as it said no, with a line each time.
    /// </summary>
    internal static TimeSpan RateLimitWait(TimeSpan? said, int attempt)
    {
        var growing = RateLimitWaits[Math.Clamp(attempt, 1, RateLimitWaits.Length) - 1];
        if (said is not { } s)
            return growing;
        var named = TimeSpan.FromTicks(Math.Clamp(s.Ticks, TimeSpan.TicksPerSecond, TimeSpan.TicksPerHour));
        var least = attempt <= 1 ? TimeSpan.Zero : growing < TimeSpan.FromMinutes(1) ? growing : TimeSpan.FromMinutes(1);
        return named >= least ? named : least;
    }

    private static string Describe(TimeSpan wait) => wait.TotalSeconds < 90 ? $"{Math.Ceiling(wait.TotalSeconds):0} s" : $"{Math.Ceiling(wait.TotalMinutes):0} min";

    /// <summary>Rough token count of the conversation (about 3.5 characters per token; a screenshot one per 32x32 pixels).</summary>
    internal int EstimateTokens() =>
        (int)(((long)systemPrompt.Length + _toolChars.Value + History.Sum(CharsOf)) * 2 / 7);

    /// <summary>Size of the tool definitions, which go into every request; MCP servers can add a lot.</summary>
    private readonly Lazy<long> _toolChars = new(() =>
        tools.Definitions().Sum(d => (long)d.Name.Length + d.Description.Length + d.Parameters.ToJsonString().Length + 40));

    /// <summary>Rough token count of one message.</summary>
    internal static int EstimateTokens(ChatMessage m) => (int)((long)CharsOf(m) * 2 / 7);

    private static long CharsOf(ChatMessage m) =>
        (m.Content?.Length ?? 0) + (m.Reasoning?.Length ?? 0) +
        (m.ToolCalls?.Sum(c => c.Name.Length + c.Arguments.Length) ?? 0) +
        (m.Parts?.Sum(p => p.IsImage ? ImageTokens(p.ImageBytes!) * 7 / 2 : p.Text?.Length ?? 0) ?? 0);

    /// <summary>What the Qwen vision encoder spends on an image: a token per 32x32 pixels (2000 if it can't be read).</summary>
    internal static long ImageTokens(byte[] image)
    {
        using var codec = SkiaSharp.SKCodec.Create(new SkiaSharp.SKMemoryStream(image));
        return codec is null ? 2000 : (long)Math.Ceiling(codec.Info.Width / 32.0) * (long)Math.Ceiling(codec.Info.Height / 32.0);
    }

    /// <summary>
    /// Keeps the prompt under <paramref name="budget"/> tokens. Preserved thinking is the bulk of
    /// a long run, so older reasoning goes first, then the text of old tool results.
    /// </summary>
    internal void FitContext(int budget)
    {
        if (budget <= 0 || EstimateTokens() <= budget)
            return;

        DropOldReasoning(budget);
        if (EstimateTokens() <= budget)
            return;

        foreach (var m in History.Take(Math.Max(0, History.Count - 8)).Where(m => m.Role == ChatRole.Tool && m.Content is { Length: > 400 }))
        {
            m.Content = m.Content![..300] + "\n[older output trimmed to save context]";
            if (EstimateTokens() <= budget) return;
        }

        // Last resort: drop whole steps from the middle. The original request (first message)
        // and the most recent steps stay, and tool results never lose their tool call.
        const string gap = "[Earlier steps were removed to fit the context window. Take a screenshot if you need to see the current state.]";
        var removedAny = false;
        while (EstimateTokens() > budget && History.Count > 8)
        {
            var at = History.Count > 1 && History[1].Content == gap ? 2 : 1;
            History.RemoveAt(at);
            while (at < History.Count - 6 && History[at].Role == ChatRole.Tool)
                History.RemoveAt(at); // results whose call was just removed
            removedAny = true;
        }
        if (removedAny && History[1].Content != gap)
            History.Insert(1, ChatMessage.User(gap));
    }

    /// <summary>
    /// If a run was interrupted between the model asking for tools and the results arriving,
    /// add placeholder results so the next request is still a valid conversation.
    /// </summary>
    private void RepairDanglingToolCalls() => RepairDanglingToolCalls(History);

    /// <inheritdoc cref="RepairDanglingToolCalls()"/>
    public static void RepairDanglingToolCalls(List<ChatMessage> history)
    {
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var msg = history[i];
            if (msg.Role != ChatRole.Assistant || !msg.HasToolCalls)
                continue;
            var answered = history.Skip(i + 1).Where(m => m.Role == ChatRole.Tool).Select(m => m.ToolCallId).ToHashSet();
            var insertAt = i + 1 + history.Skip(i + 1).TakeWhile(m => m.Role == ChatRole.Tool).Count();
            foreach (var call in msg.ToolCalls!.Where(c => !answered.Contains(c.Id)))
                history.Insert(insertAt++, ChatMessage.ToolResult(call.Id, "Not run: the user stopped the agent."));
            break;
        }
    }
}
