namespace BlackBox.ChatLab.Scenarios;

/// <summary>Alex's messenger as it looks on an ordinary day: the people and the chats' history, shared by the Pigeon scenarios.</summary>
internal static class PigeonPeople
{
    public const string App = "pigeon";

    public static void AddEveryone(World w)
    {
        w.AddPerson("mom", "Mom", "#e17076");
        w.AddPerson("dan", "Dan Kowalski", "#7bc862");
        w.AddPerson("kate", "Kate Lin", "#faa774");
        w.AddPerson("leo", "Leo Fischer", "#6ec9cb");
        w.AddPerson("priya", "Priya Shah", "#ee7aae");
        w.AddPerson("sam", "Sam Rivera", "#65aadd");
        w.AddPerson("patel", "Mr. Patel (landlord)", "#a695e7");
        w.AddPerson("nina", "Nina Ortega", "#e5a64d");
        w.AddPerson("tom", "Tom Becker", "#5fbed5");
        w.AddPerson("pigeon", "Pigeon", "#3390ec").Verified = true;
    }

    public static DateTime DaysAgo(int days, int hour, int minute) => DateTime.Today.AddDays(-days).AddHours(hour).AddMinutes(minute);

    /// <summary>The chats every Pigeon scenario has, with their history; a scenario adds what is new.</summary>
    public static void AddChats(World w)
    {
        var mom = w.AddChat(App, "mom", "direct", "Mom", "mom");
        mom.Pinned = true;
        w.Old(mom, "mom", "Did you get home ok? 🙂", DaysAgo(1, 20, 14));
        w.Old(mom, World.Me, "Yes, all good! Thanks for dinner ❤️", DaysAgo(1, 20, 31));
        w.Old(mom, "mom", "❤️", DaysAgo(1, 20, 32));

        var dan = w.AddChat(App, "dan", "direct", "Dan Kowalski", "dan");
        w.Old(dan, "dan", "Morning! The client moved the call to Thursday", DaysAgo(3, 10, 2));
        w.Old(dan, World.Me, "Ok, noted 👍", DaysAgo(3, 10, 5));

        // No open question to Alex in the history: the scenarios decide when the group asks something.
        var hiking = w.AddChat(App, "hiking", "group", "Hiking crew 🏔", "kate", "leo", "priya");
        w.Old(hiking, "kate", "Photos from Sunday's hike are in the shared album 📸", DaysAgo(2, 19, 10));
        w.Old(hiking, "leo", "That view from the top 😍", DaysAgo(2, 19, 12));
        w.Old(hiking, World.Me, "So good! Thanks for organizing, Kate", DaysAgo(2, 19, 31));
        w.Old(hiking, "priya", "Next one in two weeks?", DaysAgo(2, 19, 40));
        w.Old(hiking, "kate", "Yes! I'll post the date 🙌", DaysAgo(2, 19, 42));

        var sam = w.AddChat(App, "sam", "direct", "Sam Rivera", "sam");
        w.Old(sam, "sam", "Thanks again for helping with the move!", DaysAgo(6, 21, 40));
        w.Old(sam, World.Me, "Anytime! Enjoy the new place 🏠", DaysAgo(6, 21, 52));

        var patel = w.AddChat(App, "patel", "direct", "Mr. Patel (landlord)", "patel");
        w.Old(patel, "patel", "Rent received, thank you.", DaysAgo(13, 9, 30));

        var bookclub = w.AddChat(App, "bookclub", "group", "Book club 📚", "nina", "tom");
        w.Old(bookclub, "nina", "Next book: The Midnight Library. Happy reading!", DaysAgo(9, 18, 0));
        w.Old(bookclub, "tom", "Already started it 😄", DaysAgo(9, 18, 25));

        var service = w.AddChat(App, "pigeon", "service", "Pigeon", "pigeon");
        w.Old(service, "pigeon", "Welcome to Pigeon! Your chats are synced across all your devices.", DaysAgo(200, 12, 0));
    }
}
