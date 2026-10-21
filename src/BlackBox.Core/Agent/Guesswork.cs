using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BlackBox.Core.Llm;
using BlackBox.Core.Mcp;
using BlackBox.Core.Tools;
using BlackBox.Core.Web;

namespace BlackBox.Core.Agent;

/// <summary>
/// Notices the model guessing, tries that fail with no look-up in between, and points it to
/// web_search, which the system prompt already asks for. It didn't search in the blind-spot runs:
/// an Excel chart call failed the same way four times ("Method invocation failed ... does not
/// contain a method named 'Add'") before the model decided Excel's COM server was "a limited stub"
/// and wrote the chart's XML by hand; one search would have shown that in PowerShell ChartObjects()
/// is a method. And building a geometry nodes graph in Blender 5.2 over MCP, Nemotron went through
/// names from Blender 3 one failed script after another; the server reports a script's error as
/// plain text, which wasn't taken for an error at all. A note, never a block: an error naming
/// something that doesn't exist gets it at once, the same error a second time, any failure from
/// the third since the last look-up, and the first script or command that fails in a task before
/// anything was looked up.
/// <para>
/// Errors weren't enough. Asked to make a character follow a video's motion with LTX, a run tried
/// workflows for two hours; nothing failed, the videos just didn't move as asked, and the job was
/// done right after the user said to look for guidance (LTX's motion control guide). So a try the
/// model itself calls wrong, or the user does, counts as a failed one; a question the model asks
/// itself about how something is done is searched for it (see <see cref="Question"/>); and after
/// ten minutes of tries with nothing looked up, every next try says it is time to look.
/// </para>
/// </summary>
internal sealed partial class Guesswork(Func<DateTime>? clock = null)
{
    /// <summary>Failures further back than this many calls no longer count.</summary>
    private const int Window = 20;

    /// <summary>Failures since the last look-up (within <see cref="Window"/>) before every next one gets a note.</summary>
    internal const int Tries = 3;

    /// <summary>Time spent on tries with nothing looked up before every next try says it is time to look.</summary>
    internal static readonly TimeSpan CheckAfter = TimeSpan.FromMinutes(10);

    /// <summary>Tries in that time it takes as well: a long wait or one long render is no guesswork.</summary>
    internal const int CheckTries = 3;

    /// <summary>Calls after a look-up before a question of the model's is searched for it: it is reading what it found.</summary>
    internal const int SearchGap = 4;

    internal const string RepeatNote =
        "[BlackBox] This error came up before in this chat. Unless you know exactly why, look it up with web_search " +
        "(the error message, and the program or language it comes from) before you try again.";

    internal const string NameNote =
        "[BlackBox] The error says a name doesn't exist here. Names change between versions, and yours may be from another one: " +
        "look up the right one for the version in use with web_search (then fetch_page with find= the name) instead of trying other names.";

    internal static string TriesNote(int failed) =>
        $"[BlackBox] {failed} tries have failed since you last looked anything up. Unless you know exactly why this one failed, " +
        "search for the error or for how this is done in the version you are working with (web_search, then fetch_page) " +
        "before you try again: guessing costs more than a search.";

    internal const string FirstNote =
        "[BlackBox] Nothing has been looked up in this task yet. Unless you know exactly why this failed, look up how this is done " +
        "in the version you are working with (web_search, then fetch_page with find=) before you try again, rather than trying variations.";

    internal const string DoubtNote =
        "[BlackBox] By your own account the last try didn't give what was wanted, and nothing has been looked up in this task yet. " +
        "Before the next try, search the web for how this is done (the official documentation, the makers' guides and examples, " +
        "for the version in use: web_search, then fetch_page with find=) rather than trying variations.";

    internal const string UserDoubtNote =
        "[BlackBox] The user says the result isn't right yet. Before you try again, search the web for how this is done " +
        "(the official documentation, the makers' guides and examples, for the version in use: web_search, then fetch_page with find=) " +
        "rather than trying variations of what didn't work.";

    internal static string CheckNote(TimeSpan worked, int tries) =>
        $"[BlackBox] Time to look things up: {(int)worked.TotalMinutes} minutes and {tries} tries since anything was last looked up. " +
        "Search the web for guidance on what you are doing now (the official documentation, the makers' guides and examples, " +
        "for the version in use) before your next try: a better way is often one search away, even when things seem to work.";

    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);
    private readonly Dictionary<string, int> _seen = [];
    private readonly Queue<int> _failures = new();
    private readonly List<HashSet<string>> _searched = [];
    private int _calls, _triesSince, _lookedUpAt = -SearchGap;
    private TimeSpan _worked;
    private DateTime? _tick;
    private bool _lookedUp, _firstSaid, _tried, _triedInRun;
    private string? _pending;

    /// <summary>
    /// A question the model asked itself in its last reply about how something is done, which it
    /// went on without looking up: BlackBox searches it for the model (see <see cref="Searched"/>).
    /// </summary>
    public string? Question { get; private set; }

    /// <summary>
    /// A new run, on the user's message: the clock only counts time spent working, and a message
    /// saying the result of the run before isn't right counts as a failed try.
    /// </summary>
    public void StartRun(string? userText)
    {
        (_tick, _lookedUp, _firstSaid, _tried, Question) = (_now(), false, false, false, null);
        Heard(userText);
        _triedInRun = false;
    }

    /// <summary>A message of the user's, at the start of a run or while it goes on: one saying a result isn't right counts as a failed try.</summary>
    public void Heard(string? userText)
    {
        if (_triedInRun && userText is { } text && UserDoubts(text))
            _pending = Failed() ?? UserDoubtNote;
    }

    /// <summary>
    /// Reads a reply before its calls run: a try it calls wrong counts as a failed one (its note
    /// comes with the reply's first result), and a question about how something is done that it
    /// doesn't look up becomes <see cref="Question"/>.
    /// </summary>
    public void Read(ChatMessage reply, IReadOnlyList<Tool?> calls)
    {
        Question = null;
        if (calls.Count == 0 || calls.Any(t => t is WebSearchTool or FetchPageTool))
            return;
        var text = reply.Reasoning + "\n" + reply.Content;
        if (_tried && SaysItFailed(text))
        {
            _tried = false;
            _pending = Failed() ?? (_lookedUp || _firstSaid ? null : DoubtNote);
            _firstSaid |= _pending == DoubtNote;
        }
        if (_calls - _lookedUpAt >= SearchGap && QuestionIn(text) is { } question && !SearchedBefore(question))
            Question = question;
    }

    /// <summary>The question was searched for the model: not again, and the clock starts over.</summary>
    public void Searched(string question)
    {
        _searched.Add(Words(question));
        Question = null;
        (_worked, _triesSince, _lookedUpAt, _lookedUp) = (TimeSpan.Zero, 0, _calls, true);
    }

    /// <summary>The note for a call's result, or null.</summary>
    /// <param name="arguments">The call's arguments: what a comfyui or film call does, what a command runs.</param>
    public string? Check(Tool? tool, ToolResult result, string? arguments = null)
    {
        _calls++;
        var now = _now();
        if (_tick is { } tick)
            _worked += now - tick;
        _tick = now;
        if (tool is WebSearchTool or FetchPageTool)
        {
            _failures.Clear();
            (_lookedUp, _worked, _triesSince, _lookedUpAt, _pending) = (true, TimeSpan.Zero, 0, _calls, null);
            return null;
        }
        // Reading the installed version's own API (inspect.signature, --help) is looking it up too.
        if (Reads(tool, arguments))
            (_worked, _triesSince) = (TimeSpan.Zero, 0);
        var isTry = IsTry(tool, arguments is null ? null : ActionOf(arguments)) && !Reads(tool, arguments);
        if (isTry)
        {
            _triesSince++;
            _triedInRun = true;
        }
        var pending = _pending;
        _pending = null;
        _tried = false;
        // A call the user declined failed for no lack of knowledge.
        if (result.Text == AgentSession.DeclinedText)
            return pending;
        if (ErrorLine(result, ErrorsInText(tool)) is not { } line)
        {
            _tried = isTry;
            return pending ?? (isTry && _worked >= CheckAfter && _triesSince >= CheckTries ? CheckNote(_worked, _triesSince) : null);
        }

        var signature = Signature(line);
        var count = _seen[signature] = _seen.GetValueOrDefault(signature) + 1;
        var failed = Failed(count);
        if (failed is not null)
            return failed;
        if (count == 1 && NamesWhatIsNotThere(line))
            return NameNote;
        // The first script or command that fails in a task, before anything was looked up: in the
        // Blender runs the first script was written from memory, and more failed before a search.
        if (!_lookedUp && !_firstSaid && ErrorsInText(tool))
        {
            _firstSaid = true;
            return FirstNote;
        }
        return pending;
    }

    /// <summary>Counts a failed try; the note for a failure seen before or for one too many since the last look-up, or null.</summary>
    private string? Failed(int seen = 1)
    {
        _failures.Enqueue(_calls);
        while (_failures.Peek() <= _calls - Window)
            _failures.Dequeue();
        return seen == 2 ? RepeatNote : _failures.Count >= Tries ? TriesNote(_failures.Count) : null;
    }

    /// <summary>
    /// Calls that try to get something done, as against looking or reading: running code or a
    /// command, writing a file, a ComfyUI job, making a piece of a film.
    /// </summary>
    internal static bool IsTry(Tool? tool, string? action) => tool switch
    {
        ShellTool or SshTool or WriteFileTool or EditFileTool => true,
        McpTool mcp => mcp.Risk != ToolRisk.ReadOnly,
        ComfyTool => action is "run",
        FilmTool => action is "frame" or "shot" or "sound" or "join",
        _ => false,
    };

    /// <summary>Whether a command only reads how the installed program or library works: its signatures, help or members.</summary>
    internal static bool Reads(Tool? tool, string? arguments) =>
        tool is ShellTool or SshTool && arguments is not null && Introspects().IsMatch(arguments);

    [GeneratedRegex(@"inspect\.(signature|getsource|getdoc|getmembers)|\bhelp\(|\.__doc__|--help\b|\bGet-Help\b|\bGet-Member\b|\bpip3? show\b", RegexOptions.IgnoreCase)]
    private static partial Regex Introspects();

    /// <summary>The action argument of a call's arguments, if it has one.</summary>
    internal static string? ActionOf(string arguments)
    {
        try
        {
            return JsonNode.Parse(arguments) is JsonObject args && args["action"] is JsonValue v && v.TryGetValue<string>(out var a)
                ? a.Trim().ToLowerInvariant() : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The last question in a reply's thinking or text about how something is done, what it is
    /// called or whether a program has it, or a statement of not knowing that; null when there is
    /// none. Questions about the screen, the user or how a result looks are no matter for a search.
    /// </summary>
    internal static string? QuestionIn(string text)
    {
        string? found = null;
        foreach (var sentence in Sentences().Split(Plain(text)))
        {
            // The question is the part of a sentence that asks: "Node 1 loads Pony — wait, which checkpoint should I use?"
            var asked = Clauses().Split(sentence).Select(c => c.Trim().Trim('*', '_', '`', '-', ' ', '"', '\''))
                .LastOrDefault(c => (c.EndsWith('?') && Asks().IsMatch(c)) || DoesntKnow().IsMatch(c) || MeansToLookUp(c) is not null);
            // "How should I proceed with this?" names nothing to search for.
            if (asked is null || asked.Length is < 20 or > 300 || Screenish().IsMatch(asked) || Words(asked).Count < 3)
                continue;
            found = asked;
        }
        return found;
    }

    /// <summary>
    /// What to search for a question: without the thinking's fillers, and with the program it is
    /// about when the question doesn't name it (<paramref name="subject"/>: ComfyUI, an MCP server's name).
    /// </summary>
    internal static string Query(string question, string? subject)
    {
        var q = Filler().Replace(question.Replace("`", ""), "");
        // "I'm not sure how LTX takes a video" is searched as "how LTX takes a video", and "Let me
        // search for the node names in Blender 5.2" as "the node names in Blender 5.2".
        if (!q.EndsWith('?') && DoesntKnow().Match(q) is { Success: true } m)
            q = AfterThought().Replace(q[m.Groups["what"].Index..], "");
        else if (MeansToLookUp(q) is { } what)
            q = AfterThought().Replace(what, "");
        q = q.TrimEnd('?', '.', ' ');
        if (subject is { Length: > 0 } && !q.Contains(subject, StringComparison.OrdinalIgnoreCase))
            q += " " + subject;
        if (q.Length > 160)
            q = q[..q.LastIndexOf(' ', 160)];
        return q;
    }

    /// <summary>
    /// What a clause says the model means to look up ("Let me do a quick web search for Blender
    /// 5.2's geometry nodes Python API", "I need to check the node names in Blender 5.2"), or null.
    /// In the Blender runs it said so, then wrote the script from memory: "Actually, let me think
    /// about what I know".
    /// </summary>
    internal static string? MeansToLookUp(string clause) =>
        LookUpIntent().Match(clause) is { Success: true } m && !LocalThing().IsMatch(m.Groups["what"].Value) ? m.Groups["what"].Value : null;

    /// <summary>What BlackBox found for the model's question, said with the step's last result.</summary>
    internal static string Found(string question, IReadOnlyList<SearchHit> hits) =>
        $"[BlackBox] You thought: \"{question}\" Rather than go by memory, here is what a web search for it found:\n" +
        WebSearchTool.Format(hits) +
        "\nRead the page that answers it with fetch_page (find= the part you need) before your next try; " +
        "if none does, search again with the exact program or model and its version.";

    private bool SearchedBefore(string question)
    {
        var words = Words(question);
        return _searched.Any(s => s.Intersect(words).Count() * 10 >= Math.Max(1, Math.Min(s.Count, words.Count)) * 7);
    }

    private static HashSet<string> Words(string text) =>
        Word().Matches(text.ToLowerInvariant()).Select(m => m.Value).Where(w => w.Length > 2 && !Common.Contains(w)).ToHashSet();

    private static readonly HashSet<string> Common =
        ["the", "and", "how", "can", "could", "should", "would", "does", "what", "which", "this", "that", "with", "for", "not", "sure", "know",
         "there", "way", "any", "into", "from", "you", "use", "proceed", "task", "next", "step", "now", "here", "then", "need", "want", "get",
         "done", "doing", "thing", "things", "it's", "i'm", "will", "work", "best", "about", "right", "correct", "exactly"];

    // Curly apostrophes as straight ones, so "doesn’t" reads as "doesn't".
    private static string Plain(string text) => text.Replace('’', '\'').Replace('‘', '\'');

    /// <summary>Whether the model's words say its last try didn't do what it should.</summary>
    internal static bool SaysItFailed(string text) => Doubts().IsMatch(Plain(text));

    /// <summary>Whether the user's message says the last result isn't right.</summary>
    internal static bool UserDoubts(string text) => SaysItFailed(text) || UserSaysWrong().IsMatch(Plain(text));

    /// <summary>
    /// Tools that run code report its errors as their output: an MCP server's "Error executing
    /// code: ..." comes as an ordinary result, and Blender run with a script prints the script's
    /// traceback and exits with 0.
    /// </summary>
    private static bool ErrorsInText(Tool? tool) => tool is ShellTool or SshTool or McpTool;

    private const string Traceback = "Traceback (most recent call last):";

    /// <summary>
    /// The first line of the error that says what went wrong; null when the result isn't an
    /// error. A PowerShell error record counts even when the script went on and its exit code was
    /// 0, and with <paramref name="errorsInText"/> so do a Python traceback and output that begins
    /// with "Error". For a traceback it is the line under its stack ("AttributeError: ..."): the
    /// one above is the same for every Python error.
    /// </summary>
    internal static string? ErrorLine(ToolResult result, bool errorsInText = false)
    {
        var text = result.Text;
        var traceback = text.LastIndexOf(Traceback, StringComparison.Ordinal);
        if (!result.IsError && !text.Contains("FullyQualifiedErrorId", StringComparison.Ordinal) &&
            !(errorsInText && (traceback >= 0 || text.TrimStart().StartsWith("Error", StringComparison.OrdinalIgnoreCase))))
            return null;
        // The stack's lines are indented; the error follows them. mcp-for-blender sends the
        // traceback inside JSON, on one line with its line breaks written as \n.
        var stack = traceback >= 0 ? text[(traceback + Traceback.Length)..] : "";
        if (stack.StartsWith("\\n", StringComparison.Ordinal))
            stack = JsonEscape().Replace(stack, m => m.Groups[1].Value switch { "n" => "\n", "t" => "\t", "r" => "", var c => c });
        if (traceback >= 0 && FirstMeaningful(stack.Split('\n').Where(l => l.Length > 0 && !char.IsWhiteSpace(l[0]))) is { } raised)
            return raised;
        return FirstMeaningful(text.Split('\n'));
    }

    [GeneratedRegex(@"\\(.)")]
    private static partial Regex JsonEscape();

    private static string? FirstMeaningful(IEnumerable<string> lines) =>
        lines.Select(l => l.Trim()).FirstOrDefault(l => l.Length >= 8 && !Noise().IsMatch(l));

    /// <summary>The error line with its numbers made general, so the same error matches however the call differed (another line, another cell).</summary>
    internal static string Signature(string errorLine)
    {
        var line = Spaces().Replace(Numbers().Replace(errorLine.ToLowerInvariant(), "#"), " ");
        return line.Length > 160 ? line[..160] : line;
    }

    /// <inheritdoc cref="Signature(string)"/>
    internal static string? Signature(ToolResult result, bool errorsInText = false) =>
        ErrorLine(result, errorsInText) is { } line ? Signature(line) : null;

    /// <summary>
    /// Whether the error says a name doesn't exist: an attribute, method, argument, enum value,
    /// node type or command the model remembered from another version, or made up.
    /// </summary>
    internal static bool NamesWhatIsNotThere(string errorLine) =>
        // Python's "'NoneType' object has no attribute 'modifiers'" says a variable was empty, not that a name is wrong.
        MissingName().IsMatch(errorLine) && !errorLine.Contains("'NoneType' object", StringComparison.Ordinal);

    // Lines that say where, not what: exit codes, PowerShell's "At line:3 char:5", "+ ..." echoes, ~~~ markers.
    [GeneratedRegex(@"^(exit code -?\d+|at line:\d+|at [a-z]:\\.*:\d+ char:\d+|\+|~+$|screenshot)", RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Numbers();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}.+-]*")]
    private static partial Regex Word();

    [GeneratedRegex(@"(?<=[.!?])\s+|\n")]
    private static partial Regex Sentences();

    [GeneratedRegex(@"\s+[—–-]+\s+|[:;]\s+")]
    private static partial Regex Clauses();

    // Python, Blender, C#, PowerShell, JavaScript, GDScript and command-line tools, as they say it.
    [GeneratedRegex(@"has no attribute|is not defined|not declared|no module named|cannot import name|unexpected keyword|positional argument|" +
                    @"\b(key|enum|attribute|property|method|function|module|command|option|parameter|argument|identifier|member|field|node|socket|type)\b[^.;]{0,80}\bnot found|" +
                    @"(could|can) ?not be found|unable to find type|cannot find (module|an overload|type|symbol)|" +
                    @"does not contain an? (definition|method|property|member)|does not exist in the current context|" +
                    @"is not recognized as (the name|an internal or external command)|is not a function|nonexistent (function|method|property)|\btype \S+ undefined\b|" +
                    @"\bunknown (option|argument|parameter|command|flag|property|attribute|field|key|type|function|method|keyword|encoder|decoder|format|node|socket)|" +
                    @"\bunrecognized (option|argument|command|parameter)|\binvalid (choice|option|keyword|enum)|\bno such (option|command|method|property|attribute|module|function)",
        RegexOptions.IgnoreCase)]
    private static partial Regex MissingName();

    // The model saying a try didn't do what it should: nothing failed, it just didn't work.
    [GeneratedRegex(@"\bstill\s+(\w+\s+){0,3}?(wrong|broken|static|frozen|stuck|missing|empty|blank|the same|not|no|isn't|doesn't|didn't|won't|fails?|failing|failed)\b|" +
                    @"\b(didn't|did not|doesn't|does not|isn't|is not|wasn't|was not|won't|aren't|are not)\s+(work|working|help|fix|change|move|moving|follow|following|match|matching|look right|come out right|do (anything|what))\b|" +
                    @"\bno (visible )?(change|effect|difference|improvement|motion|movement) (in|at all|to|from)\b|\b(had|has|made|makes) no (effect|difference)\b|" +
                    @"\bnot what (i|we|the user|they|you) (wanted|asked|expected|intended|meant)\b|" +
                    @"\b(try|use|need) (a |an )?(different|another|other|new) (approach|way|method|node|workflow|technique|strategy)\b|" +
                    @"\b(that|this|it) (failed|did not work|didn't work|doesn't work)\b|\bfailed (again|to (work|follow|move|change))\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex Doubts();

    // What else a user writes when a result isn't right: "Dude it's still ...", "wrong again", "nope",
    // or what it should have done: "it should fade from transparent", "it was supposed to move".
    [GeneratedRegex(@"\bstill\b|\b(wrong|worse|nope|not right|isn't right|no good|try again|redo it|start over)\b|" +
                    @"\b(it|they|that|this|he|she) (should|shouldn't|should not|was supposed to|were supposed to|is supposed to)\b|\binstead of\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex UserSaysWrong();

    // A question about how something is done, what it is called, which one does it, whether it exists.
    [GeneratedRegex(@"\bhow (do|does|can|could|should|would|to)\b(?! (it|this|that|they|things) (look|seem|feel)\b)|" +
                    @"\bhow (is|are) [^?]{0,60}\b(done|made|set|configured|called|named|used|enabled|controlled|specified|passed|handled|supported|defined|installed|loaded|connected)\b|" +
                    @"\bwhat('s| is| are| was) the (right|correct|proper|best|usual|official|recommended|exact|actual|current|new) (way|name|syntax|node|setting|parameter|argument|option|value|method|function|api|format|workflow|approach|command|flag|field|property|input|output|class)|" +
                    @"\bwhat('s| is| are) [^?]{1,50}\b(called|named)\b|" +
                    @"\b(which|what) (nodes?|models?|settings?|parameters?|arguments?|options?|functions?|methods?|api|loras?|workflows?|samplers?|schedulers?|flags?|propert(y|ies)|attributes?|class(es)?|modules?|packages?|librar(y|ies)|codecs?|formats?|versions?|templates?|checkpoints?|controlnets?|preprocessors?|inputs?|sockets?|operators?|commands?|fields?|keys?) " +
                    @"(do|does|should|would|can|could|is|are|was|were|to|i|we|handles?|supports?|controls?|makes?|gives?)\b|" +
                    @"\bis there (a|an|any) (way|node|setting|option|parameter|function|method|api|built-in|builtin|tool|template|model|lora|flag|feature|command|official|example)\b|" +
                    @"\bdoes (?!(it|this|that|they)\b)(the \w+|\w+) (support|accept|allow|handle|have an?|expose|provide)\b|" +
                    @"\b((is|are) (it|they|these|those|the \w+)|it's|they're|it is|they are) (built into|part of|included in|supported (by|in))\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex Asks();

    // Not knowing, said as a statement: "I'm not sure how LTX takes a reference video." What isn't known starts at "what".
    // "Whether" and "if" only for what a program can do: "I don't know if the original s3 was good" is no question for the web.
    [GeneratedRegex(@"\b((i'm|i am|not) (not )?(sure|certain)|i (don't|do not) (know|remember|recall)|i (need|have|will need) to (figure|find) out|unclear) " +
                    @"((?<what>how|what|which|where)\b|(?<what>whether|if)\b[^,;]*\b(supports?|accepts?|allows?|handles?|has|have|exists?|works? with|is built into|comes with)\b|" +
                    @"(?<what>the (exact|right|correct|proper) (name|syntax|parameter|argument|option|setting|node|function|method|api|flag|command|format|signature|value)s?)\b)",
        RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture)]
    private static partial Regex DoesntKnow();

    // Meaning to look something up on the web: a web search, the docs, or names in a version
    // ("Let me search for TTS templates" is a search in ComfyUI, which the model then makes).
    [GeneratedRegex(@"\b(let me|let's|i'll|i will|i should|i need to|i have to|i could|i must|i'd better|i want to)\s+(quickly\s+|first\s+|just\s+)?(" +
                    @"((do|run|make) an?\s+)?((quick|fast)\s+)?(web|online|internet) search\s+((for|on|about)\s+)?(?<what>\S.*)|" +
                    @"(search|look|check|look up)\s+(online|on the web|the web|the internet|the docs|the documentation|the api docs)\s+((for|about|on)\s+)?(?<what>\S.*)|" +
                    @"google\s+(?<what>\S.*)|" +
                    @"(look up|search for|search the web for)\s+(?<what>(the\s+)?((correct|right|exact|current|actual|new)\s+)?((node|api|python|input|socket|parameter)\s+)?" +
                    @"(names?|api|syntax|signatures?|parameters?|arguments?|options?|docs|documentation)\b.*?\b(in|for|of) [\w.+-]+ (v(ersion)?\s*)?\d.*))",
        RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture)]
    private static partial Regex LookUpIntent();

    // What is looked up on this PC, not on the web: "let me search for the templates", "look up the output folder".
    [GeneratedRegex(@"^((the|my|our|this|that|a|an)\s+)?(templates?|files?|folders?|output|logs?|results?|images?|pictures?|videos?|frames?|status|queue|screenshot|window)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LocalThing();

    // What a statement goes on to say after what isn't known: ", so I'll keep euler".
    [GeneratedRegex(@",\s*(so|and|but|then|because|which|i'll|i will)\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex AfterThought();

    // Thinking's fillers before a question: "Hmm, so", "But wait,", "OK".
    [GeneratedRegex(@"^((so|but|hmm+|ok(ay)?|well|wait|now|and|also|actually|then|right|oh|ah)\b[,.:]?\s*)+", RegexOptions.IgnoreCase)]
    private static partial Regex Filler();

    // Questions the web can't answer: about the screen, the user, how something looks, what a check
    // would show here, or BlackBox's own tools.
    [GeneratedRegex(@"\b(screen|screenshot|cursor|click|clicked|button|the user|user's|they want|he|she)\b|\b(look|looks|seem|seems)\?$|\blooks? (best|better|good|right|nicer)\b|" +
                    @"\b(determine|tell|check|see|verify|confirm) (whether|if|which)\b|\b(film|comfyui) tool\b|\bthe tool\b|\bblackbox\b|\bstoryboard\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex Screenish();
}
