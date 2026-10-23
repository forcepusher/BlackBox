using System.Text.Json.Nodes;

namespace BlackBox.Core.Film;

/// <summary>
/// Two changes to one storyboard made at once, put together: the Filmmaking panel's (a shot made
/// longer by hand) and the chat's (the film tool rewriting a shot's prompt, or a seed it settled),
/// each made on the storyboard as it was before. Saved as it was in the window, the window's
/// whole copy wrote over what the chat had just changed.
/// </summary>
public static class StoryboardMerge
{
    /// <summary>
    /// <paramref name="theirs"/> with what changed from <paramref name="before"/> to
    /// <paramref name="mine"/> put over it: key by key within objects, frames and shots by their
    /// ids (the order changed on one side is kept). A value both changed is <paramref name="mine"/>'s.
    /// </summary>
    public static Storyboard Merge(Storyboard before, Storyboard mine, Storyboard theirs)
    {
        var merged = Node(before.ToJsonObject(), mine.ToJsonObject(), theirs.ToJsonObject());
        var board = Storyboard.Parse(merged?.ToJsonString() ?? "{}", upgrade: false);
        board.Folder = mine.Folder;
        return board;
    }

    internal static JsonNode? Node(JsonNode? before, JsonNode? mine, JsonNode? theirs)
    {
        if (JsonNode.DeepEquals(mine, before))
            return theirs?.DeepClone(); // not changed here
        if (JsonNode.DeepEquals(theirs, before) || JsonNode.DeepEquals(theirs, mine))
            return mine?.DeepClone(); // not changed there, or changed the same
        if (mine is JsonObject m && theirs is JsonObject t)
            return Object(before as JsonObject ?? [], m, t);
        if (mine is JsonArray ma && theirs is JsonArray ta && Ids(ma) is { } mineIds && Ids(ta) is { } theirIds)
            return Array(before as JsonArray ?? [], ma, mineIds, ta, theirIds);
        return mine?.DeepClone(); // both changed it: the window's hand wins
    }

    private static JsonObject Object(JsonObject before, JsonObject mine, JsonObject theirs)
    {
        var result = new JsonObject();
        foreach (var key in theirs.Select(p => p.Key).Concat(mine.Select(p => p.Key)).Concat(before.Select(p => p.Key)).Distinct().ToList())
        {
            var (inBefore, inMine, inTheirs) = (before.ContainsKey(key), mine.ContainsKey(key), theirs.ContainsKey(key));
            if (inMine == inBefore && JsonNode.DeepEquals(mine[key], before[key]))
            {
                if (inTheirs) result[key] = theirs[key]?.DeepClone();
            }
            else if (inTheirs == inBefore && JsonNode.DeepEquals(theirs[key], before[key]))
            {
                if (inMine) result[key] = mine[key]?.DeepClone();
            }
            else if (inMine && inTheirs)
                result[key] = Node(before[key], mine[key], theirs[key]);
            else if (inMine)
                result[key] = mine[key]?.DeepClone();
        }
        return result;
    }

    /// <summary>Frames and shots: kept, added, changed and taken out by their ids, on either side.</summary>
    private static JsonArray Array(JsonArray before, JsonArray mine, List<string> mineIds, JsonArray theirs, List<string> theirIds)
    {
        var was = Ids(before) ?? [];
        var b = ById(before);
        var (m, t) = (ById(mine), ById(theirs));
        // The order: mine where it moved what was there or put something new in, else theirs; what
        // the other side added comes after.
        var mineMoved = !mineIds.Where(was.Contains).SequenceEqual(was.Where(mineIds.Contains)) || mineIds.Any(id => !was.Contains(id));
        var order = mineMoved ? mineIds : theirIds;
        var ids = order.Concat(mineIds).Concat(theirIds).Distinct().ToList();
        var result = new JsonArray();
        foreach (var id in ids)
        {
            var (inMine, inTheirs) = (m.TryGetValue(id, out var mi), t.TryGetValue(id, out var ti));
            b.TryGetValue(id, out var bi);
            if (inMine && inTheirs)
                result.Add(Node(bi, mi, ti));
            else if (inMine && !(bi is not null && JsonNode.DeepEquals(mi, bi)))
                result.Add(mi!.DeepClone()); // added here, or changed here though taken out there
            else if (inTheirs && !(bi is not null && JsonNode.DeepEquals(ti, bi)))
                result.Add(ti!.DeepClone());
        }
        return result;
    }

    /// <summary>The ids of an array of objects that all have one, in order; null for any other array.</summary>
    private static List<string>? Ids(JsonArray array)
    {
        var ids = new List<string>();
        foreach (var item in array)
        {
            if (item is not JsonObject o || o["id"] is not JsonValue v || !v.TryGetValue<string>(out var id))
                return null;
            ids.Add(id);
        }
        return ids.Count == ids.Distinct().Count() ? ids : null;
    }

    private static Dictionary<string, JsonObject> ById(JsonArray array) =>
        array.OfType<JsonObject>().Where(o => o["id"] is JsonValue).GroupBy(o => o["id"]!.ToString()).ToDictionary(g => g.Key, g => g.First());
}
