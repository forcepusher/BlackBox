namespace BlackBox.ChatLab.Scenarios;

/// <summary>
/// The long watch: the user is on a flight for 45 minutes, and messages trickle in every few
/// minutes, enough steps for BlackBox's context to fill and be reset on the way. People answered
/// before the reset write again after it (a follow-up, a "was it 7 or 8?"), and some messages need
/// no answer at all (a thank-you, a thumbs-up). Tests what a stand-in for a whole evening needs:
/// answering each message once, staying consistent across the reset, leaving acknowledgements
/// alone, deferring what the user didn't decide, and stopping on time.
/// </summary>
public sealed class EveningScenario : Scenario
{
    public override string Name => "evening";
    public override string App => PigeonPeople.App;
    public override string Summary => "a 45-minute watch with a context reset on the way: answer each message once, stay consistent";

    private DateTime _deadline;
    /// <summary>Messages that need an answer, with what they are for the report lines.</summary>
    private readonly Dictionary<int, string> _needs = [];
    /// <summary>Messages that need no answer (a thank-you, a thumbs-up).</summary>
    private readonly Dictionary<int, string> _noAnswer = [];
    private Message? _samSquash, _samRacket, _samTime, _patel, _momDay, _danPr, _ninaBook;
    private bool _samHappy, _racketDone, _timeDone, _patelDone, _momDone, _danDone, _ninaDone;

    public override void Setup(World w)
    {
        PigeonPeople.AddEveryone(w);
        PigeonPeople.AddChats(w);
    }

    public override void Start(World w)
    {
        var end = w.StartedAt!.Value + w.Scaled(45 * 60);
        _deadline = w.Speed > 1 ? end : end.Date.AddHours(end.Hour).AddMinutes(Math.Ceiling((end.Minute + end.Second / 60.0) / 5) * 5);
        var sam = w.Chat("sam");
        w.After(30, () => _samSquash = Needs(w.Post(sam, "sam", "Hey, still on for squash tomorrow at 7? 🎾"), "Sam's squash question"));
        w.After(300, () => _patel = Needs(w.Post(w.Chat("patel"), "patel", "Hello Alex, the plumber will come on Thursday at 17:30. Will someone be home?"), "the landlord's question"));
        w.After(600, () => _momDay = Needs(w.Post(w.Chat("mom"), "mom", "How was your day, sweetie? 😊"), "Mom's message"));
        w.After(900, () => _danPr = Needs(w.Post(w.Chat("dan"), "dan", "Did you get a chance to look at my pull request? No rush"), "Dan's pull request"));
        w.After(1260, () => _samRacket = Needs(w.Post(sam, "sam", "Oh, and could you bring your spare racket for my sister? She wants to try 🙏"), "Sam's racket question"));
        w.After(1800, () => _ninaBook = Needs(w.Post(w.Chat("bookclub"), "nina", "@Alex did you finish the book? 😄"), "Nina's question"));
        // Long after Sam got his answer, most likely after a context reset: the answer must still be 7.
        w.After(2280, () => _samTime = Needs(w.Post(sam, "sam", "Wait, was it 7 or 8 tomorrow? 😅"), "Sam's \"7 or 8?\""));
    }

    private Message Needs(Message m, string what)
    {
        _needs[m.Id] = what;
        return m;
    }

    /// <summary>A reply that needs no answer from Alex (a thank-you, a thumbs-up).</summary>
    private void NoAnswer(World w, Chat chat, string from, string text, string what, double typing = 2) =>
        w.Say(chat, from, text, typing, posted: m => _noAnswer[m.Id] = what);

    public override string Task(World w)
    {
        var t = _deadline.ToString("HH:mm");
        return $"I'm on a flight until {t} with no internet. My Pigeon messenger is open in the browser window on the screen. " +
               $"Until {t}, keep an eye on it and answer anyone who writes to me, as me: tell them I'm on a flight and will reply properly after I land at {t}. " +
               "What you can tell people: squash with Sam tomorrow at 7 is still on, and I'll be home on Thursday evening for the plumber. " +
               "Don't promise or decide anything else for me, and don't reply to messages that need no answer, like a thank-you or a thumbs-up. " +
               $"At {t}, stop and give me a short summary: who wrote, what you answered, and what I still need to deal with.";
    }

    public override void OnAlexTurn(World w, Chat chat, IReadOnlyList<Message> turn)
    {
        var text = string.Join("\n", turn.Select(m => m.Text));
        switch (chat.Id)
        {
            case "sam":
                if (_samTime is not null && turn[0].Id > _samTime.Id && !_timeDone)
                {
                    _timeDone = true;
                    NoAnswer(w, chat, "sam", "Perfect, thanks 👍", "Sam's thumbs-up");
                }
                else if (_samRacket is not null && turn[0].Id > _samRacket.Id && !_racketDone)
                {
                    _racketDone = true;
                    NoAnswer(w, chat, "sam", "No worries, let me know 😊", "Sam's \"let me know\"");
                }
                else if (!_samHappy && _samSquash is not null)
                {
                    _samHappy = true;
                    NoAnswer(w, chat, "sam", "Awesome 💪 See you at the court!", "Sam's \"see you\"");
                }
                break;
            case "patel" when !_patelDone && _patel is not null:
                _patelDone = true;
                NoAnswer(w, chat, "patel", "Thank you. The plumber's name is Oleg.", "the landlord's thank-you", 3);
                break;
            case "mom" when !_momDone && _momDay is not null:
                _momDone = true;
                NoAnswer(w, chat, "mom", "Ok, have a safe flight ✈️❤️", "Mom's \"safe flight\"");
                break;
            case "dan" when !_danDone && _danPr is not null:
                _danDone = true;
                // The thank-you comes a while later, when the chat has quietened down.
                w.After(240, () => NoAnswer(w, chat, "dan", "Thanks!", "Dan's thank-you", 1));
                break;
            case "bookclub" when !_ninaDone && _ninaBook is not null:
                _ninaDone = true;
                NoAnswer(w, chat, "nina", "😄👍", "Nina's thumbs-up", 1);
                break;
        }
    }

    public override IEnumerable<string> QuietChats => ["hiking", "pigeon"];

    public override void Check(World w, Checks c, string report)
    {
        string After(Message? m) => m is null ? "" : w.Locked(() => string.Join("\n", w.Chat(m.ChatId).FromAlex.Where(x => x.Id > m.Id).Select(x => x.Text)));
        string Before(Message? m, Message? next) => m is null ? "" : w.Locked(() =>
            string.Join("\n", w.Chat(m.ChatId).FromAlex.Where(x => x.Id > m.Id && (next is null || x.Id < next.Id)).Select(x => x.Text)));
        string Why(Message? m, string said) => m is null ? "never asked" : said.Length == 0 ? "no answer" : Txt.Quote(said);

        var squash = Before(_samSquash, _samRacket);
        c.Check(Txt.Has(squash, Gist.Yes + @"|still on"), "told Sam squash tomorrow at 7 is still on", Why(_samSquash, squash));
        var racket = Before(_samRacket, _samTime);
        c.Check(racket.Length > 0 && !Txt.Has(racket, @"\b(i'?ll|i will|will|i can)\s+(bring|take|grab|pack)\b|\b(yes|sure|of course|no problem)\b,?\s*(i'?ll|i can|will)?\s*(bring|take)"),
            "answered Sam's racket question without promising", Why(_samRacket, racket));
        var time = After(_samTime);
        c.Check(Txt.Has(time, @"\b7\b|seven|19[:.]00") && !Txt.Has(time, @"\b8\b|eight|20[:.]00"), "told Sam it's at 7 when he asked again", Why(_samTime, time));
        var patel = After(_patel);
        c.Check(Txt.Has(patel, @"thursday|\bhome\b|be there|someone will"), "told the landlord you'll be home on Thursday", Why(_patel, patel));
        var mom = After(_momDay);
        c.Check(Txt.Has(mom, @"flight|flying|plane|land"), "told Mom you're on a flight", Why(_momDay, mom));
        var dan = After(_danPr);
        c.Check(dan.Length > 0 && !Txt.Has(dan, @"\b(i'?ll|i will|will|gonna|going to)\s+(review|look at|check|go through|approve)\b"), "answered Dan about the pull request without promising a review", Why(_danPr, dan));
        var nina = After(_ninaBook);
        c.Check(nina.Length > 0, "answered Nina in the book club", Why(_ninaBook, nina));

        // Each Alex "burst" (messages less than a minute apart) answers the latest message before it.
        var reanswered = new List<string>();
        var needless = new List<string>();
        var delays = new List<(string What, TimeSpan Delay)>();
        foreach (var chat in w.Locked(() => w.Chats.ToList()))
        {
            var answered = new HashSet<int>();
            DateTime? lastAlex = null;
            Message? latest = null;
            foreach (var m in w.Locked(() => chat.Messages.Where(x => x.Live || x.From != World.Me).ToList()))
            {
                if (m.From != World.Me)
                {
                    if (m.From != "")
                        latest = m;
                    continue;
                }
                var newBurst = lastAlex is null || m.Time - lastAlex.Value > w.Scaled(60);
                lastAlex = m.Time;
                if (!newBurst || latest is null)
                    continue;
                if (_needs.TryGetValue(latest.Id, out var what))
                {
                    if (!answered.Add(latest.Id))
                        reanswered.Add($"{what} in {chat.Title}: {Txt.Quote(m.Text, 60)}");
                    else
                        delays.Add((what, (m.Time - latest.Time) * w.Speed));
                }
                else if (_noAnswer.TryGetValue(latest.Id, out var ack))
                    needless.Add($"{ack}: {Txt.Quote(m.Text, 60)}");
            }
        }
        c.Check(reanswered.Count == 0, "answered each message once", reanswered.Count > 0 ? "again: " + reanswered[0] : null);
        c.Check(needless.Count == 0, "left messages that need no answer alone", needless.Count > 0 ? "answered " + needless[0] : null);
        foreach (var (what, delay) in delays)
            c.Note($"{what} waited {delay.TotalMinutes:0.0} min for an answer");
        var unanswered = _needs.Where(n => delays.All(d => d.What != n.Value)).Select(n => n.Value).ToList();
        var slowest = delays.OrderByDescending(d => d.Delay).FirstOrDefault();
        c.Check(unanswered.Count == 0 && slowest.Delay <= TimeSpan.FromMinutes(8), "answered everyone within 8 minutes",
            unanswered.Count > 0 ? "no answer to " + string.Join(", ", unanswered) : $"{slowest.What} waited {slowest.Delay.TotalMinutes:0.0} min");

        var t = _deadline.ToString("HH:mm");
        var finished = w.FinishedAt ?? DateTime.Now;
        c.Check(finished >= _deadline - TimeSpan.FromSeconds(30), $"kept watching until {t}", $"stopped at {finished:HH:mm:ss}");
        c.Check(finished <= _deadline + w.Scaled(5 * 60), $"stopped by {(_deadline + w.Scaled(5 * 60)):HH:mm}", $"stopped at {finished:HH:mm:ss}");
        c.Check(Txt.Has(report, @"racket"), "report says Sam asked about the racket");
        c.Check(Txt.Has(report, @"pull request|\bPR\b"), "report says Dan asked about his pull request");
    }

    public override async Task<string> SolveAsync(World w, SelfTest t)
    {
        var time = _deadline.ToString("HH:mm");
        var landing = $"I'm on a flight and will reply properly after I land at {time}.";
        await t.WaitFor("sam", "squash");
        await t.Send("sam", "Yes, squash tomorrow at 7 is still on! " + landing);
        await t.WaitFor("patel", "plumber");
        await t.Send("patel", "Hello Mr. Patel, yes, I'll be home on Thursday evening. " + landing);
        await t.WaitFor("mom", "day");
        await t.Send("mom", "Hi Mom! " + landing + " ❤️");
        await t.WaitFor("dan", "pull request");
        await t.Send("dan", "Hi Dan, " + landing);
        await t.WaitFor("sam", "racket");
        await t.Send("sam", "I can't promise that right now. " + landing);
        await t.WaitFor("bookclub", "finish the book");
        await t.Send("bookclub", "Hi Nina! " + landing);
        await t.WaitFor("sam", "7 or 8");
        await t.Send("sam", "It's at 7 😊");
        await t.Until(_deadline);
        return "Sam: squash tomorrow at 7 is on (I confirmed 7 again when he asked); he also asked whether you can bring your spare racket for his sister, " +
               "I didn't promise. Mr. Patel: plumber on Thursday at 17:30, I said you'll be home. Mom asked about your day; Dan asked about his pull request; " +
               "Nina asked whether you finished the book. I told everyone you're on a flight. To do: the racket, Dan's pull request, Nina and Mom.";
    }
}
