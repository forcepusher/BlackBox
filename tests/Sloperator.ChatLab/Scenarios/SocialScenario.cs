namespace Sloperator.ChatLab.Scenarios;

/// <summary>
/// A social network: comments under the user's half-marathon post, each to be answered in its own
/// thread (a question about the time, one about training, an invitation, congratulations), a
/// troll to leave alone, and a private message about a birthday. Tests a feed with threaded
/// replies rather than a chat, answering each person under their comment, and a second place to look.
/// </summary>
public sealed class SocialScenario : Scenario
{
    public override string Name => "social";
    public override string App => "friendspace";
    public override string Summary => "answer the comments under the user's post in their threads, skip the troll, answer a DM";

    private const string PostId = "p-alex";
    private bool _jamieDone, _olgaDone, _benDone, _lindaDone, _marcusDone, _dmDone, _dmAskedTime;

    public override void Setup(World w)
    {
        w.AddPerson("jamie", "Jamie Chen", "#65aadd").Status = "online";
        w.AddPerson("olga", "Olga Petrova", "#e17076");
        w.AddPerson("marcus", "Marcus Webb", "#8e8e93");
        w.AddPerson("linda", "Linda Morgan", "#a695e7");
        w.AddPerson("ben", "Ben Ortiz", "#7bc862").Status = "online";
        w.AddPerson("sofia", "Sofia Alvarez", "#faa774");
        w.AddPerson("runclub", "Riverside Runners", "#ee7aae");

        var yesterday = DateTime.Today.AddDays(-1);
        var p = w.AddPost(App, PostId, World.Me, "Finished my first half marathon today! 🏃‍♂️🎉 Thank you all for the support and the cheering at the finish!",
            yesterday.AddHours(18).AddMinutes(4), picture: "🏅|Riverside Half Marathon · finisher", likes: 42);
        w.AddComment(p, "c1", "jamie", "Congrats!!! 🎉 What was your time?", yesterday.AddHours(18).AddMinutes(20), unseen: true, likes: 2);
        w.AddComment(p, "c2", "linda", "Wonderful news dear!! So proud of you ❤️", yesterday.AddHours(18).AddMinutes(45), unseen: true, likes: 1);
        w.AddComment(p, "c3", "olga", "Amazing! How long did you train for it?", yesterday.AddHours(19).AddMinutes(30), unseen: true);
        w.AddComment(p, "c4", "marcus", "Running is so overrated lol. Enjoy your knees at 40 😂", yesterday.AddHours(21).AddMinutes(2), unseen: true);
        w.AddComment(p, "c5", "ben", "Are you doing the city marathon in April? We could train together!", DateTime.Today.AddHours(8).AddMinutes(15), unseen: true, likes: 1);

        var cafe = w.AddPost(App, "p-sofia", "sofia", "A new café opened on Main St and the croissants are unreal 🥐☕ Go before everyone finds out!",
            DateTime.Today.AddHours(9).AddMinutes(12), picture: "🥐|Le Petit Matin, Main St", likes: 17);
        w.AddComment(cafe, "s1", "olga", "Going tomorrow!", DateTime.Today.AddHours(9).AddMinutes(40));
        w.AddPost(App, "p-club", "runclub", "Sunday long run: 18 km along the river, meet 8:00 at the boathouse. All paces welcome! 🏃‍♀️🏃",
            yesterday.AddHours(12), likes: 31);

        var dm = w.AddChat(App, "jamie-dm", "direct", "Jamie Chen", "jamie");
        w.Old(dm, World.Me, "Thanks for the book, loved it!", DateTime.Today.AddDays(-8).AddHours(20));
        w.Old(dm, "jamie", "Anytime 📚", DateTime.Today.AddDays(-8).AddHours(20).AddMinutes(9));
        w.Old(dm, "jamie", "Hey! Are you still coming to my birthday on Friday? 🎂", DateTime.Today.AddHours(9).AddMinutes(40), read: false);
        var olgaDm = w.AddChat(App, "olga-dm", "direct", "Olga Petrova", "olga");
        w.Old(olgaDm, "olga", "Happy birthday!! 🎂🎉", DateTime.Today.AddMonths(-2).AddHours(10));
        w.Old(olgaDm, World.Me, "Thank you Olga! ❤️", DateTime.Today.AddMonths(-2).AddHours(12));
    }

    public override string Task(World w) =>
        "People commented on my half marathon post on Friendspace (open in the browser window). Reply to the comments as me, each one as a reply under that person's comment: " +
        "thank people for the congratulations, my time was 1:52:40, I trained for 12 weeks, and about the April marathon say I'm not sure yet " +
        "but I'd love to go for a run together sometime. Don't feed the trolls. " +
        "Also answer Jamie's private message: yes, I'm coming to the birthday on Friday, around 8 pm. Tell me when you're done.";

    public override void OnAlexComment(World w, Post post, Comment comment, Comment? answered)
    {
        if (post.Id != PostId || comment.ReplyTo is null)
            return;
        switch (comment.ReplyTo)
        {
            case "c1" when !_jamieDone && Txt.Has(comment.Text, @"1:52"):
                _jamieDone = true;
                Later(w, 6, () => { comment.Likes++; w.AddComment(post, "r-jamie", "jamie", "Wow, that's fast! 🔥", DateTime.Now, "c1"); });
                break;
            case "c2" when !_lindaDone:
                _lindaDone = true;
                Later(w, 8, () => comment.Likes++);
                break;
            case "c3" when !_olgaDone:
                _olgaDone = true;
                Later(w, 7, () => comment.Likes++);
                break;
            case "c4" when !_marcusDone:
                _marcusDone = true;
                Later(w, 6, () => w.AddComment(post, "r-marcus", "marcus", "lol touchy 😂", DateTime.Now, "c4"));
                break;
            case "c5" when !_benDone:
                _benDone = true;
                Later(w, 9, () => w.AddComment(post, "r-ben", "ben", "Awesome, let's do it! 💪", DateTime.Now, "c5"));
                break;
        }
    }

    /// <summary>A reaction under the post a little later (a like, a reply), shown on the page.</summary>
    private static void Later(World w, double seconds, Action action) => w.After(seconds, () =>
    {
        action();
        w.Touch();
    });

    public override void OnAlexTurn(World w, Chat chat, IReadOnlyList<Message> turn)
    {
        if (chat.Id != "jamie-dm" || _dmDone)
            return;
        var text = string.Join("\n", turn.Select(m => m.Text));
        var time = Txt.Has(text, @"\b8\b|eight|20[:.]00|20h");
        if (Txt.Has(text, Gist.Yes) && time)
        {
            _dmDone = true;
            w.Say(chat, "jamie", "Yay! See you on Friday 🥳");
        }
        else if (Txt.Has(text, Gist.Yes) && !_dmAskedTime)
        {
            _dmAskedTime = true;
            w.Say(chat, "jamie", "Yay! 🥳 What time do you think you'll come?");
        }
    }

    public override IEnumerable<string> QuietChats => ["olga-dm"];

    public override void Check(World w, Checks c, string report)
    {
        var mine = w.Locked(() => w.Posts.First(p => p.Id == PostId).Comments.Where(x => x.Author == World.Me).ToList());
        var others = w.Locked(() => w.Posts.Where(p => p.Id != PostId).SelectMany(p => p.Comments).Where(x => x.Author == World.Me).ToList());
        string In(string thread) => string.Join("\n", mine.Where(x => x.ReplyTo == thread).Select(x => x.Text));
        string Loose(string pattern) => mine.Where(x => x.ReplyTo is null && Txt.Has(x.Text, pattern)).Select(x => x.Text).FirstOrDefault() is { } top
            ? $"posted as a separate comment, not under theirs: {Txt.Quote(top)}" : "no reply under their comment";

        c.Check(Txt.Has(In("c1"), @"1:52"), "answered Jamie's question about the time under Jamie's comment (1:52:40)", Loose(@"1:52"));
        c.Check(Txt.Has(In("c3"), @"12 weeks|twelve weeks|3 months|three months"), "answered Olga's question about training under Olga's comment (12 weeks)", Loose(@"weeks|months"));
        var ben = In("c5");
        c.Check(Txt.Has(ben, @"not sure|haven'?t decided|undecided|maybe|don'?t know( yet)?|not yet|unsure|we'?ll see") && Txt.Has(ben, @"\brun|train"),
            "answered Ben under his comment: not sure about April, up for a run together", ben.Length > 0 ? Txt.Quote(ben) : Loose(@"april|run together"));
        c.Check(Txt.Has(In("c2"), @"thank|thx|❤|love"), "thanked Linda under her comment", Loose(@"linda"));
        var troll = In("c4");
        c.Check(troll.Length == 0, "left the troll alone", Txt.Quote(troll));

        var dm = w.Locked(() => w.Chat("jamie-dm").AlexText);
        c.Check(Txt.Has(dm, Gist.Yes) && Txt.Has(dm, @"friday|\b8\b|eight|20[:.]00"), "told Jamie by private message you're coming on Friday", dm.Length == 0 ? "no answer" : Txt.Quote(dm));
        c.Check(Txt.Has(dm, @"\b8\b|eight|20[:.]00|20h"), "told Jamie around 8 pm", dm.Length == 0 ? "no answer" : Txt.Quote(dm));

        var loose = mine.Where(x => x.ReplyTo is null).ToList();
        if (loose.Count > 0)
            c.Note($"{loose.Count} comment(s) on the post rather than under someone's comment: {Txt.Quote(loose[0].Text)}");
        if (others.Count > 0)
            c.Note($"{others.Count} comment(s) on other people's posts: {Txt.Quote(others[0].Text)}");
    }

    public override async Task<string> SolveAsync(World w, SelfTest t)
    {
        await t.Comment(PostId, "c1", "Thank you! My time was 1:52:40 😊");
        await t.Comment(PostId, "c2", "Thank you so much ❤️");
        await t.Comment(PostId, "c3", "Thanks Olga! I trained for 12 weeks.");
        await t.Comment(PostId, "c5", "Not sure about April yet, but I'd love to go for a run together sometime!");
        await t.Send("jamie-dm", "Yes, I'm coming on Friday! I'll be there around 8 pm 🎂");
        await t.WaitFor("jamie-dm", "See you on Friday");
        return "Replied under Jamie's comment (1:52:40), Linda's (thanks), Olga's (12 weeks) and Ben's (not sure about April, happy to run together); " +
               "left Marcus's troll comment alone. Told Jamie by private message that you're coming on Friday around 8 pm.";
    }
}
