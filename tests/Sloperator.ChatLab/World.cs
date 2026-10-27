using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sloperator.ChatLab;

/// <summary>Someone in the lab: the user (Alex, whom Sloperator stands in for) or one of the scripted people.</summary>
public sealed class Person(string id, string name, string color)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Color { get; } = color;
    public string Status { get; set; } = "last seen recently";
    /// <summary>A blue tick next to the name, as official accounts have.</summary>
    public bool Verified { get; set; }

    public string Initials
    {
        get
        {
            var words = Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetter(w[0])).ToList();
            return words.Count switch
            {
                0 => "?",
                1 => words[0][..1].ToUpperInvariant(),
                _ => (words[0][..1] + words[^1][..1]).ToUpperInvariant(),
            };
        }
    }
}

public sealed class Message
{
    public required int Id { get; init; }
    public required string ChatId { get; init; }
    /// <summary>Who wrote it; empty for a line from the app itself ("Maria joined the chat").</summary>
    public required string From { get; init; }
    public required string Text { get; init; }
    public required DateTime Time { get; init; }
    /// <summary>Alex's message: the other side has read it. Anyone else's: Alex has.</summary>
    public bool Read { get; set; }
    /// <summary>Written during the run, not part of the chat's history.</summary>
    public bool Live { get; init; }
}

public sealed class Chat
{
    public required string Id { get; init; }
    /// <summary>Which site shows it: pigeon, friendspace, gadgethub or swapit.</summary>
    public required string App { get; init; }
    /// <summary>direct, group, service (the app's own account) or support.</summary>
    public required string Kind { get; init; }
    public required string Title { get; init; }
    public List<string> Members { get; } = [];
    public List<Message> Messages { get; } = [];
    public HashSet<string> Typing { get; } = [];
    public bool Pinned { get; set; }
    public bool Muted { get; set; }
    /// <summary>Alex has opened it at least once.</summary>
    public bool Opened { get; set; }

    // Alex's messages are answered as a turn, once Alex has been quiet for a moment, so a reply
    // split over several messages gets one answer.
    internal int AlexSequence;
    internal int LastTurnMessageId;
    internal Task Speaking = Task.CompletedTask;

    /// <summary>What Alex wrote during the run (not the chat's history).</summary>
    public IEnumerable<Message> FromAlex => Messages.Where(m => m.From == World.Me && m.Live);
    public string AlexText => string.Join("\n", FromAlex.Select(m => m.Text));
}

public sealed class Comment
{
    public required string Id { get; init; }
    public required string PostId { get; init; }
    public required string Author { get; init; }
    public required string Text { get; init; }
    public required DateTime Time { get; init; }
    /// <summary>The comment this one answers (a reply in its thread), or null for a comment on the post.</summary>
    public string? ReplyTo { get; init; }
    public int Likes { get; set; }
    public bool LikedByMe { get; set; }
    /// <summary>Alex hasn't seen it yet (it counts in the notifications).</summary>
    public bool Unseen { get; set; }
}

public sealed class Post
{
    public required string Id { get; init; }
    public required string App { get; init; }
    public required string Author { get; init; }
    public required string Text { get; init; }
    public required DateTime Time { get; init; }
    /// <summary>A picture drawn by the page: an emoji and a caption.</summary>
    public string? Picture { get; init; }
    public int Likes { get; set; }
    public bool LikedByMe { get; set; }
    public List<Comment> Comments { get; } = [];
}

/// <summary>
/// Everything the lab's sites show, the scripted people's timers, and a log of what happened.
/// One lock guards it all: page requests, timers and the scenario's reactions take turns.
/// </summary>
public sealed class World : IDisposable
{
    public const string Me = "me";

    /// <summary>How long Alex must stay quiet before the other side answers what Alex wrote.</summary>
    private const double QuietSeconds = 4;

    private readonly object _gate = new();
    private readonly StreamWriter _log;
    private readonly CancellationTokenSource _stop = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _version = 1;
    private int _nextId = 1;

    public World(Scenario scenario, string outDir, double speed)
    {
        Scenario = scenario;
        Speed = speed;
        OutDir = outDir;
        Directory.CreateDirectory(outDir);
        _log = new StreamWriter(Path.Combine(outDir, "events.jsonl"), append: false, new UTF8Encoding(false)) { AutoFlush = true };
        AddPerson(Me, "Alex Morgan", "#5288c1");
    }

    public Scenario Scenario { get; }
    public string OutDir { get; }
    /// <summary>Time runs this many times faster than real time (self-tests only).</summary>
    public double Speed { get; }
    public Dictionary<string, Person> People { get; } = [];
    public List<Chat> Chats { get; } = [];
    public List<Post> Posts { get; } = [];
    /// <summary>Everything that happened, for the checks: (time, kind, details).</summary>
    public List<(DateTime Time, string Kind, JsonObject Data)> Events { get; } = [];
    public DateTime? StartedAt { get; private set; }
    public DateTime? FinishedAt { get; private set; }
    /// <summary>A page asked for the state: the browser window shows the site.</summary>
    public bool PageSeen { get; private set; }
    public CancellationToken Stopping => _stop.Token;

    public T Locked<T>(Func<T> f)
    {
        lock (_gate) return f();
    }

    public void Locked(Action f)
    {
        lock (_gate) f();
    }

    // ---------------------------------------------------------------- setting up

    public Person AddPerson(string id, string name, string color, string status = "last seen recently")
    {
        var p = new Person(id, name, color) { Status = status };
        People[id] = p;
        return p;
    }

    public Chat AddChat(string app, string id, string kind, string title, params string[] members)
    {
        var chat = new Chat { Id = id, App = app, Kind = kind, Title = title };
        chat.Members.AddRange(members);
        Chats.Add(chat);
        return chat;
    }

    /// <summary>A message from before the scenario starts. Unread ones show as new.</summary>
    public Message Old(Chat chat, string from, string text, DateTime time, bool read = true)
    {
        var m = new Message { Id = _nextId++, ChatId = chat.Id, From = from, Text = text, Time = time, Read = read };
        chat.Messages.Add(m);
        return m;
    }

    public Post AddPost(string app, string id, string author, string text, DateTime time, string? picture = null, int likes = 0)
    {
        var post = new Post { Id = id, App = app, Author = author, Text = text, Time = time, Picture = picture, Likes = likes };
        Posts.Add(post);
        return post;
    }

    public Comment AddComment(Post post, string id, string author, string text, DateTime time, string? replyTo = null, bool unseen = false, int likes = 0)
    {
        var c = new Comment { Id = id, PostId = post.Id, Author = author, Text = text, Time = time, ReplyTo = replyTo, Unseen = unseen, Likes = likes };
        post.Comments.Add(c);
        return c;
    }

    public Chat Chat(string id) => Chats.First(c => c.Id == id);

    // ---------------------------------------------------------------- time

    /// <summary>Lab time for a span of scenario time: shorter in a sped-up self-test.</summary>
    public TimeSpan Scaled(double seconds) => TimeSpan.FromSeconds(seconds / Speed);

    /// <summary>Runs <paramref name="action"/> under the lock after <paramref name="seconds"/> of scenario time, unless the run has finished.</summary>
    public void After(double seconds, Action action)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Scaled(seconds), _stop.Token);
                lock (_gate)
                {
                    if (!_stop.IsCancellationRequested)
                        action();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log("error", new JsonObject { ["where"] = "timer", ["error"] = ex.ToString() });
            }
        });
    }

    public void Start()
    {
        lock (_gate)
        {
            if (StartedAt is not null)
                return;
            StartedAt = DateTime.Now;
            Log("start", new JsonObject { ["scenario"] = Scenario.Name });
            Scenario.Start(this);
            Changed();
        }
    }

    public void Finish()
    {
        lock (_gate)
        {
            if (FinishedAt is not null)
                return;
            FinishedAt = DateTime.Now;
            _stop.Cancel();
            foreach (var chat in Chats)
                chat.Typing.Clear();
            Log("finish", []);
            Changed();
        }
    }

    // ---------------------------------------------------------------- what the people do

    /// <summary>Posts a message right away (from anyone, Alex included).</summary>
    public Message Post(Chat chat, string from, string text)
    {
        lock (_gate)
        {
            var m = new Message { Id = _nextId++, ChatId = chat.Id, From = from, Text = text, Time = DateTime.Now, Live = true };
            chat.Messages.Add(m);
            chat.Typing.Remove(from);
            Log("message", new JsonObject { ["chat"] = chat.Id, ["from"] = from, ["id"] = m.Id, ["text"] = text });
            Changed();
            return m;
        }
    }

    /// <summary>A line from the app itself, such as "Maria joined the chat".</summary>
    public void Notice(Chat chat, string text) => Post(chat, "", text);

    /// <summary>
    /// Someone answers in a chat the way a person does: reads what Alex wrote, types for a while
    /// ("typing…" in the header), then sends. Answers in one chat come out in order.
    /// </summary>
    /// <param name="typingSeconds">How long the typing takes; by default from the length of the text.</param>
    /// <param name="posted">Called with the message once it is sent (under the lock).</param>
    public void Say(Chat chat, string from, string text, double? typingSeconds = null, double readSeconds = 1.5, Action<Message>? posted = null)
    {
        lock (_gate)
        {
            var previous = chat.Speaking;
            chat.Speaking = Task.Run(async () =>
            {
                try
                {
                    await previous;
                    await Task.Delay(Scaled(readSeconds), _stop.Token);
                    lock (_gate)
                    {
                        MarkRead(chat, from);
                        chat.Typing.Add(from);
                        Changed();
                    }
                    await Task.Delay(Scaled(typingSeconds ?? Math.Clamp(text.Length / 14.0, 2, 8)), _stop.Token);
                    lock (_gate)
                    {
                        if (!_stop.IsCancellationRequested)
                        {
                            var m = Post(chat, from, text);
                            posted?.Invoke(m);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Log("error", new JsonObject { ["where"] = "say", ["error"] = ex.ToString() });
                }
            });
        }
    }

    /// <summary>The other side has read Alex's messages so far (two ticks).</summary>
    private void MarkRead(Chat chat, string reader)
    {
        if (reader == Me)
            return;
        foreach (var m in chat.Messages.Where(m => m.From == Me && !m.Read))
            m.Read = true;
    }

    // ---------------------------------------------------------------- what Alex does (the pages call these)

    public string? AlexSend(string chatId, string text)
    {
        lock (_gate)
        {
            var chat = Chats.FirstOrDefault(c => c.Id == chatId);
            if (chat is null)
                return "no such chat";
            text = text.Replace("\r\n", "\n").Trim();
            if (text.Length == 0)
                return "empty message";
            if (FinishedAt is not null)
                return "the lab has finished";
            // Writing in a chat also reads it.
            foreach (var m in chat.Messages.Where(m => m.From != Me))
                m.Read = true;
            Post(chat, Me, text);
            var sequence = ++chat.AlexSequence;
            After(QuietSeconds, () =>
            {
                if (chat.AlexSequence != sequence)
                    return; // Alex wrote again; the answer waits for the whole turn
                var turn = chat.FromAlex.Where(m => m.Id > chat.LastTurnMessageId).ToList();
                if (turn.Count == 0)
                    return;
                chat.LastTurnMessageId = turn[^1].Id;
                Scenario.OnAlexTurn(this, chat, turn);
            });
            return null;
        }
    }

    public void AlexOpen(string chatId)
    {
        lock (_gate)
        {
            if (Chats.FirstOrDefault(c => c.Id == chatId) is not { } chat)
                return;
            var unread = chat.Messages.Count(m => m.From != Me && !m.Read);
            foreach (var m in chat.Messages.Where(m => m.From != Me))
                m.Read = true;
            var first = !chat.Opened;
            chat.Opened = true;
            Log("open", new JsonObject { ["chat"] = chat.Id, ["unread"] = unread });
            if (first && StartedAt is not null && FinishedAt is null)
                Scenario.OnChatOpened(this, chat);
            Changed();
        }
    }

    public string? AlexComment(string postId, string? replyTo, string text)
    {
        lock (_gate)
        {
            if (Posts.FirstOrDefault(p => p.Id == postId) is not { } post)
                return "no such post";
            text = text.Replace("\r\n", "\n").Trim();
            if (text.Length == 0)
                return "empty comment";
            if (FinishedAt is not null)
                return "the lab has finished";
            if (replyTo is not null && post.Comments.All(c => c.Id != replyTo))
                return "no such comment";
            // Replies stay one level deep, as on most sites: a reply to a reply joins its thread.
            var parent = post.Comments.FirstOrDefault(c => c.Id == replyTo);
            var thread = parent?.ReplyTo ?? parent?.Id;
            var c = AddComment(post, "c" + _nextId++, Me, text, DateTime.Now, thread);
            Log("comment", new JsonObject { ["post"] = post.Id, ["id"] = c.Id, ["replyTo"] = thread, ["answers"] = replyTo, ["text"] = text });
            Scenario.OnAlexComment(this, post, c, parent);
            Changed();
            return null;
        }
    }

    public void AlexLike(string postId, string? commentId)
    {
        lock (_gate)
        {
            if (Posts.FirstOrDefault(p => p.Id == postId) is not { } post)
                return;
            if (commentId is null)
            {
                post.LikedByMe = !post.LikedByMe;
                post.Likes += post.LikedByMe ? 1 : -1;
            }
            else if (post.Comments.FirstOrDefault(c => c.Id == commentId) is { } c)
            {
                c.LikedByMe = !c.LikedByMe;
                c.Likes += c.LikedByMe ? 1 : -1;
            }
            Log("like", new JsonObject { ["post"] = postId, ["comment"] = commentId });
            Changed();
        }
    }

    public void AlexSawNotifications(string app)
    {
        lock (_gate)
        {
            foreach (var c in Posts.Where(p => p.App == app).SelectMany(p => p.Comments).Where(c => c.Unseen))
                c.Unseen = false;
            Changed();
        }
    }

    // ---------------------------------------------------------------- the pages' view

    /// <summary>Waits until the state differs from <paramref name="version"/> (or a while has passed), then returns it.</summary>
    public async Task<JsonObject> StateAsync(string app, long version, CancellationToken ct)
    {
        Task changed;
        lock (_gate)
        {
            PageSeen = true;
            changed = _changed.Task;
            if (version != _version)
                return Snapshot(app);
        }
        await Task.WhenAny(changed, Task.Delay(TimeSpan.FromSeconds(20), ct));
        lock (_gate)
            return Snapshot(app);
    }

    /// <summary>Tells the pages to fetch the state again, after a change made directly (a like, a comment from someone).</summary>
    public void Touch()
    {
        lock (_gate)
            Changed();
    }

    private void Changed()
    {
        _version++;
        var previous = _changed;
        _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }

    private JsonObject Snapshot(string app)
    {
        var people = new JsonObject();
        foreach (var p in People.Values)
            people[p.Id] = new JsonObject
            {
                ["name"] = p.Name, ["initials"] = p.Initials, ["color"] = p.Color, ["status"] = p.Status, ["verified"] = p.Verified,
            };
        var chats = new JsonArray();
        foreach (var c in Chats.Where(c => c.App == app))
        {
            var messages = new JsonArray();
            foreach (var m in c.Messages)
                messages.Add(new JsonObject { ["id"] = m.Id, ["from"] = m.From, ["text"] = m.Text, ["time"] = Iso(m.Time), ["read"] = m.Read });
            chats.Add(new JsonObject
            {
                ["id"] = c.Id, ["kind"] = c.Kind, ["title"] = c.Title,
                ["members"] = new JsonArray(c.Members.Select(x => (JsonNode)x).ToArray()),
                ["pinned"] = c.Pinned, ["muted"] = c.Muted,
                ["typing"] = new JsonArray(c.Typing.Select(x => (JsonNode)x).ToArray()),
                ["unread"] = c.Messages.Count(m => m.From != Me && m.From != "" && !m.Read),
                ["messages"] = messages,
            });
        }
        var posts = new JsonArray();
        foreach (var p in Posts.Where(p => p.App == app))
        {
            var comments = new JsonArray();
            foreach (var c in p.Comments)
                comments.Add(new JsonObject
                {
                    ["id"] = c.Id, ["author"] = c.Author, ["text"] = c.Text, ["time"] = Iso(c.Time), ["replyTo"] = c.ReplyTo,
                    ["likes"] = c.Likes, ["liked"] = c.LikedByMe, ["unseen"] = c.Unseen,
                });
            posts.Add(new JsonObject
            {
                ["id"] = p.Id, ["author"] = p.Author, ["text"] = p.Text, ["time"] = Iso(p.Time), ["picture"] = p.Picture,
                ["likes"] = p.Likes, ["liked"] = p.LikedByMe, ["comments"] = comments,
            });
        }
        return new JsonObject
        {
            ["v"] = _version, ["now"] = Iso(DateTime.Now), ["me"] = Me, ["people"] = people, ["chats"] = chats, ["posts"] = posts,
            ["finished"] = FinishedAt is not null,
        };
    }

    private static string Iso(DateTime t) => t.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- the log

    public void Log(string kind, JsonObject data)
    {
        lock (_gate)
        {
            var now = DateTime.Now;
            Events.Add((now, kind, data));
            var line = new JsonObject { ["t"] = now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), ["kind"] = kind };
            foreach (var (k, v) in data)
                line[k] = v?.DeepClone();
            _log.WriteLine(line.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
    }

    /// <summary>Every chat and comment thread that has something new in it, as a person would read them.</summary>
    public string Transcript()
    {
        lock (_gate)
        {
            var sb = new StringBuilder();
            foreach (var chat in Chats.Where(c => c.Messages.Count > 0))
            {
                sb.AppendLine($"=== {chat.App} · {chat.Title} ({chat.Kind})");
                foreach (var m in chat.Messages)
                {
                    var who = m.From == "" ? "--" : People.TryGetValue(m.From, out var p) ? p.Name : m.From;
                    sb.AppendLine($"[{m.Time:MM-dd HH:mm:ss}] {who}: {m.Text.Replace("\n", "\n    ")}");
                }
                sb.AppendLine();
            }
            foreach (var post in Posts)
            {
                sb.AppendLine($"=== {post.App} · post by {People[post.Author].Name}: {post.Text}");
                foreach (var c in post.Comments.Where(c => c.ReplyTo is null))
                {
                    sb.AppendLine($"[{c.Time:MM-dd HH:mm:ss}] {People[c.Author].Name}: {c.Text}");
                    foreach (var r in post.Comments.Where(r => r.ReplyTo == c.Id))
                        sb.AppendLine($"    ↳ [{r.Time:MM-dd HH:mm:ss}] {People[r.Author].Name}: {r.Text}");
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        lock (_gate)
            _log.Dispose();
    }
}
