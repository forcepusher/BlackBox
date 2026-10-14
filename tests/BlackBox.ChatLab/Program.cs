using System.Text;
using System.Text.Json.Nodes;
using BlackBox.ChatLab;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Usage: chatlab --list
//        chatlab --scenario NAME --out DIR [--port 5170]     the sites for one run; scripts/chat-test.ps1 drives it over /api
//        chatlab --scenario NAME --out DIR --selftest [--speed 20]   the reference stand-in instead of BlackBox, no browser

Console.OutputEncoding = Encoding.UTF8;
var argsList = args.ToList();
string? Take(string flag)
{
    var i = argsList.IndexOf(flag);
    if (i < 0 || i + 1 >= argsList.Count) return null;
    var v = argsList[i + 1];
    argsList.RemoveRange(i, 2);
    return v;
}

if (argsList.Remove("--list"))
{
    foreach (var s in Scenario.All)
        Console.WriteLine($"{s.Name,-10} {s.App,-12} {s.Summary}");
    return 0;
}

var name = Take("--scenario");
var outDir = Take("--out");
var port = int.Parse(Take("--port") ?? "5170");
var speed = double.Parse(Take("--speed") ?? "1", System.Globalization.CultureInfo.InvariantCulture);
var selfTest = argsList.Remove("--selftest");
var scenario = Scenario.All.FirstOrDefault(s => s.Name == name);
if (scenario is null || outDir is null)
{
    Console.Error.WriteLine("Usage: chatlab --scenario NAME --out DIR [--port N] [--selftest [--speed N]] | --list");
    Console.Error.WriteLine("Scenarios: " + string.Join(", ", Scenario.All.Select(s => s.Name)));
    return 2;
}

using var world = new World(scenario, outDir, selfTest ? speed : 1);
world.Locked(() => scenario.Setup(world));

if (selfTest)
{
    world.Start();
    var task = world.Locked(() => scenario.Task(world));
    File.WriteAllText(Path.Combine(outDir, "task.txt"), task);
    string report;
    try
    {
        report = await scenario.SolveAsync(world, new SelfTest(world));
    }
    catch (Exception ex)
    {
        world.Log("error", new JsonObject { ["where"] = "solution", ["error"] = ex.ToString() });
        Console.Error.WriteLine("The reference solution failed: " + ex.Message);
        report = "";
    }
    var lines = Finish(world, report);
    Console.WriteLine(string.Join("\n", lines));
    return lines.Any(l => l.StartsWith("FAIL ")) ? 1 : 0;
}

var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.Logging.ClearProviders();
var app = builder.Build();
var www = Path.Combine(AppContext.BaseDirectory, "wwwroot");
string[] sites = ["pigeon", "friendspace", "gadgethub", "swapit"];

// Edits to the pages show up on reload, and the browser never shows a stale state.
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.CacheControl = "no-store";
    await next();
});
app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(www) });

// Each site is its own origin when opened as http://pigeon.localhost:port/ (browsers send
// *.localhost to this PC); http://127.0.0.1:port/pigeon works too.
IResult Page(HttpContext ctx, string site)
{
    world.Log("page", new JsonObject { ["site"] = site });
    return Results.File(Path.Combine(www, site + ".html"), "text/html; charset=utf-8");
}
app.MapGet("/", (HttpContext ctx) =>
{
    var host = ctx.Request.Host.Host;
    var site = sites.FirstOrDefault(s => host.Equals(s + ".localhost", StringComparison.OrdinalIgnoreCase));
    if (site is not null)
        return Page(ctx, site);
    var links = string.Join("", sites.Select(s => $"<li><a href=\"/{s}\">{s}</a></li>"));
    return Results.Content($"<!doctype html><title>Chat lab</title><h1>Chat lab: {scenario.Name}</h1><ul>{links}</ul>", "text/html; charset=utf-8");
});
foreach (var site in sites)
    app.MapGet("/" + site, (HttpContext ctx) => Page(ctx, site));

// What the pages do.
app.MapGet("/api/state", async (HttpContext ctx) =>
{
    var site = (string?)ctx.Request.Query["app"] ?? "";
    long.TryParse(ctx.Request.Query["v"], out var version);
    try
    {
        var state = await world.StateAsync(site, version, ctx.RequestAborted);
        return Results.Content(state.ToJsonString(), "application/json; charset=utf-8");
    }
    catch (OperationCanceledException)
    {
        return Results.Empty;
    }
});
app.MapPost("/api/send", async (HttpContext ctx) =>
{
    var body = await Body(ctx);
    return world.AlexSend(Str(body, "chat"), Str(body, "text")) is { } error ? Results.BadRequest(error) : Results.Ok();
});
app.MapPost("/api/open", async (HttpContext ctx) =>
{
    world.AlexOpen(Str(await Body(ctx), "chat"));
    return Results.Ok();
});
app.MapPost("/api/comment", async (HttpContext ctx) =>
{
    var body = await Body(ctx);
    var replyTo = (string?)body["replyTo"];
    return world.AlexComment(Str(body, "post"), string.IsNullOrEmpty(replyTo) ? null : replyTo, Str(body, "text")) is { } error
        ? Results.BadRequest(error) : Results.Ok();
});
app.MapPost("/api/like", async (HttpContext ctx) =>
{
    var body = await Body(ctx);
    world.AlexLike(Str(body, "post"), (string?)body["comment"]);
    return Results.Ok();
});
app.MapPost("/api/seen", async (HttpContext ctx) =>
{
    world.AlexSawNotifications(Str(await Body(ctx), "app"));
    return Results.Ok();
});

// What the harness does.
app.MapGet("/api/health", () => Results.Content(new JsonObject
{
    ["scenario"] = scenario.Name, ["app"] = scenario.App, ["pageSeen"] = world.PageSeen,
    ["started"] = world.StartedAt is not null, ["finished"] = world.FinishedAt is not null,
}.ToJsonString(), "application/json"));
app.MapPost("/api/start", () =>
{
    world.Start();
    var task = world.Locked(() => scenario.Task(world));
    File.WriteAllText(Path.Combine(outDir, "task.txt"), task);
    return Results.Content(new JsonObject { ["task"] = task }.ToJsonString(), "application/json");
});
app.MapPost("/api/finish", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
    var report = await reader.ReadToEndAsync();
    return Results.Text(string.Join("\n", Finish(world, report)) + "\n", "text/plain; charset=utf-8");
});
app.MapGet("/api/transcript", () => Results.Text(world.Transcript(), "text/plain; charset=utf-8"));
app.MapPost("/api/quit", (IHostApplicationLifetime life) =>
{
    life.StopApplication();
    return Results.Ok();
});

Console.WriteLine($"chat lab: {scenario.Name} on http://127.0.0.1:{port}/{scenario.App} (log: {outDir})");
await app.RunAsync();
return 0;

static List<string> Finish(World w, string report)
{
    w.Finish();
    File.WriteAllText(Path.Combine(w.OutDir, "report.txt"), report);
    var c = new Checks();
    try
    {
        w.Scenario.Check(w, c, report);
    }
    catch (Exception ex)
    {
        c.Check(false, "the scenario's checks ran", ex.Message);
    }
    CommonChecks.Run(w, c, report);
    File.WriteAllLines(Path.Combine(w.OutDir, "check.txt"), c.Lines);
    File.WriteAllText(Path.Combine(w.OutDir, "transcript.txt"), w.Transcript());
    return c.Lines;
}

static async Task<JsonObject> Body(HttpContext ctx)
{
    using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
    var text = await reader.ReadToEndAsync();
    return JsonNode.Parse(text.Length == 0 ? "{}" : text) as JsonObject ?? [];
}

static string Str(JsonObject body, string key) => (string?)body[key] ?? "";
