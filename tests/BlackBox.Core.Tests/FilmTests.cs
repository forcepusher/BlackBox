using System.Text.Json.Nodes;
using BlackBox.Core.Comfy;
using BlackBox.Core.Film;
using BlackBox.Core.Tools;
using SkiaSharp;
using Xunit;

namespace BlackBox.Core.Tests;

public sealed class StoryboardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bb-film-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void A_storyboard_a_model_wrote_loosely_is_read_as_meant()
    {
        var board = Storyboard.Parse("""
            {"Title": "Rain", "frames": [{"id": 1, "prompt": "a robot", "from": "2"}, {"id": "2", "prompt": "a street"}, {"prompt": "no id"}],
             "shots": [{"first": 1, "last": "2", "seconds": "4", "transition": "crossfade"}, {"first": "nope"}],
             "sound": "C:\\music\\song.mp3"}
            """);
        Assert.Equal("Rain", board.Title);
        Assert.Equal(["1", "2", "f1"], board.Frames.Select(f => f.Id));
        Assert.Equal(["2"], board.Frames[0].From);
        Assert.All(board.Frames.Concat<object>(board.Shots), item => Assert.True(item is FilmFrame f ? f.Seed > 0 : ((FilmShot)item).Seed > 0));
        Assert.Equal(4, board.Shots[0].Seconds);
        Assert.Equal(FilmShot.Dissolve, board.Shots[0].Transition);
        Assert.Null(board.Shots[1].First); // a frame that isn't there
        Assert.Equal(5, board.Shots[1].Seconds);
        Assert.Equal(FilmSound.FromFile, board.Sound.Source);
        Assert.Equal(@"C:\music\song.mp3", board.Sound.File);
    }

    [Fact]
    public void A_change_is_merged_by_id_and_a_frame_can_be_taken_out()
    {
        var board = Storyboard.New(_dir, "Keeper");
        board.Shots[0].Prompt = "he waves";
        var merged = Storyboard.Merge(board, (JsonObject)JsonNode.Parse("""
            {"style": "35mm", "frames": [{"id": "f2", "prompt": "a close-up"}, {"id": "f3", "prompt": "the sea", "reference": true}, {"id": "f1", "delete": true}],
             "shots": [{"id": "s1", "seconds": 3}], "sound": {"source": "song", "tags": "folk"}}
            """)!);
        Assert.Equal("Keeper", merged.Title);
        Assert.Equal("35mm", merged.Style);
        Assert.Equal(["f2", "f3"], merged.Frames.Select(f => f.Id));
        Assert.Equal("a close-up", merged.Frame("f2")!.Prompt);
        Assert.True(merged.Frame("f3")!.Reference);
        Assert.Equal("he waves", merged.Shots[0].Prompt);
        Assert.Equal(3, merged.Shots[0].Seconds);
        Assert.Null(merged.Shots[0].First); // its frame was taken out
        Assert.Equal(FilmSound.Song, merged.Sound.Source);
        Assert.Equal("folk", merged.Sound.Tags);
        Assert.Equal(board.Sound.Seed, merged.Sound.Seed);
        Assert.Equal(_dir, merged.Folder);
    }

    [Fact]
    public void A_new_film_is_saved_only_when_asked_and_loads_back_the_same()
    {
        var board = Storyboard.New(_dir, "Keeper");
        Assert.False(File.Exists(board.FilePath));
        board.Save();
        var loaded = Storyboard.Load(_dir);
        Assert.Equal(board.ToJson(), loaded.ToJson());
        Assert.Equal(Path.Combine(_dir, "Keeper.mp4"), loaded.FilmFile);
    }

    [Fact]
    public void Dissolves_and_fades_overlap_the_shot_before()
    {
        var board = Storyboard.Parse("""
            {"shots": [{"id": "a", "seconds": 5}, {"id": "b", "seconds": 4, "transition": "dissolve"}, {"id": "c", "seconds": 3}, {"id": "d", "seconds": 2, "transition": "fade"}]}
            """);
        var (starts, length) = board.Timing();
        Assert.Equal([0, 4.5, 8.5, 10.5], starts);
        Assert.Equal(12.5, length);
    }
}

public sealed class FilmRolesTests
{
    private static readonly ComfyNodeLibrary Empty = ComfyNodeLibrary.Parse([]);

    private static ComfyWorkflow Load(string json) => ComfyWorkflow.FromApi(ComfyWorkflow.ParseText(json));

    /// <summary>LTX-2.5's first and last frame template as ComfyUI's page turns it into the API format (trimmed).</summary>
    private const string Ltx = """
        {
          "31": {"class_type": "LoadImage", "inputs": {"image": "robot_hand.png"}, "_meta": {"title": "Load First Frame"}},
          "39": {"class_type": "LoadImage", "inputs": {"image": "robot_hand_energy.png"}, "_meta": {"title": "Load Last Frame"}},
          "68": {"class_type": "SaveVideo", "inputs": {"filename_prefix": "video/ltx2.5_flf2v", "video": ["251:218", 0]}, "_meta": {"title": "Save Video"}},
          "251:196": {"class_type": "RandomNoise", "inputs": {"noise_seed": 873293130933086}, "_meta": {"title": "RandomNoise"}},
          "251:222": {"class_type": "CLIPTextEncode", "inputs": {"text": ["251:248", 0], "clip": ["251:228", 0]}, "_meta": {"title": "CLIP Text Encode (Prompt)"}},
          "251:248": {"class_type": "ComfySwitchNode", "inputs": {"switch": ["251:250", 0], "on_false": ["251:252", 0], "on_true": ["251:247", 0]}},
          "251:250": {"class_type": "PrimitiveBoolean", "inputs": {"value": true}, "_meta": {"title": "Boolean (Enable Prompt Enhance)"}},
          "251:215": {"class_type": "PrimitiveInt", "inputs": {"value": 1280}, "_meta": {"title": "Width"}},
          "251:216": {"class_type": "PrimitiveInt", "inputs": {"value": ["403", 1]}, "_meta": {"title": "height"}},
          "251:198": {"class_type": "PrimitiveInt", "inputs": {"value": 5}, "_meta": {"title": "Duration"}},
          "251:205": {"class_type": "PrimitiveInt", "inputs": {"value": 24}, "_meta": {"title": "Frame Rate(int)"}},
          "251:252": {"class_type": "PrimitiveStringMultiline", "inputs": {"value": "a robot hand"}, "_meta": {"title": "Prompt"}},
          "251:201": {"class_type": "EmptyLTXVLatentVideo", "inputs": {"width": ["251:203", 0], "height": ["251:203", 1], "length": ["251:226", 1], "batch_size": 1}}
        }
        """;

    [Fact]
    public void A_ltx_template_gets_its_prompt_frames_size_length_and_seed_by_their_titles()
    {
        var wf = Load(Ltx);
        var missed = new List<string>();
        var set = FilmRoles.Apply(wf, Empty, new FilmInputs
        {
            Prompt = "the keeper waves", Seed = 7, Width = 1280, Height = 720, Seconds = 4.5, Fps = 24,
            FirstImage = @"C:\f\first.png", LastImage = @"C:\f\last.png", Prefix = "BlackBox films/Keeper/s1",
        }, missed);
        Assert.Empty(missed);
        Assert.Equal("the keeper waves", (string?)wf.ValueOf("251:252.value"));
        Assert.Equal(7, (long?)wf.ValueOf("251:196.noise_seed"));
        Assert.Equal(720, (int?)wf.ValueOf("251:216.value")); // its link replaced by the value
        Assert.Equal(5, (int?)wf.ValueOf("251:198.value")); // whole seconds: 4.5 made 5, cut in the film
        Assert.Equal(24, (int?)wf.ValueOf("251:205.value"));
        Assert.Equal(@"C:\f\first.png", (string?)wf.ValueOf("31.image"));
        Assert.Equal(@"C:\f\last.png", (string?)wf.ValueOf("39.image"));
        Assert.Equal(false, (bool?)wf.ValueOf("251:250.value"));
        Assert.Equal("BlackBox films/Keeper/s1", (string?)wf.ValueOf("68.filename_prefix"));
        Assert.Contains(set, s => s.StartsWith("251:252.value", StringComparison.Ordinal));
    }

    /// <summary>Z-Image's text to image: the prompt only in the text encoder the sampler's positive comes from.</summary>
    private const string ZImage = """
        {
          "9": {"class_type": "SaveImage", "inputs": {"filename_prefix": "z-image-turbo", "images": ["57:8", 0]}},
          "57:33": {"class_type": "ConditioningZeroOut", "inputs": {"conditioning": ["57:27", 0]}},
          "57:27": {"class_type": "CLIPTextEncode", "inputs": {"text": "Latina female", "clip": ["57:30", 0]}, "_meta": {"title": "CLIP Text Encode (Prompt)"}},
          "57:13": {"class_type": "EmptySD3LatentImage", "inputs": {"width": 1024, "height": 1024, "batch_size": 1}},
          "57:3": {"class_type": "KSampler", "inputs": {"seed": 0, "steps": 8, "cfg": 1, "model": ["57:11", 0], "positive": ["57:27", 0], "negative": ["57:33", 0], "latent_image": ["57:13", 0]}}
        }
        """;

    [Fact]
    public void Without_titles_the_prompt_is_found_from_the_samplers_positive_and_the_size_on_the_empty_latent()
    {
        var wf = Load(ZImage);
        var missed = new List<string>();
        FilmRoles.Apply(wf, Empty, new FilmInputs { Prompt = "a lighthouse", Negative = "blurry", Seed = 11, Width = 1280, Height = 720 }, missed);
        Assert.Empty(missed);
        // The negative comes from the same encoder (zeroed out), so it isn't set over the prompt.
        Assert.Equal("a lighthouse", (string?)wf.ValueOf("57:27.text"));
        Assert.Equal(11, (long?)wf.ValueOf("57:3.seed"));
        Assert.Equal(1280, (int?)wf.ValueOf("57:13.width"));
        Assert.Equal(720, (int?)wf.ValueOf("57:13.height"));
    }

    [Fact]
    public void Pictures_past_the_templates_own_are_wired_into_the_edit_encoders()
    {
        var wf = Load("""
            {
              "78": {"class_type": "LoadImage", "inputs": {"image": "input.png"}, "_meta": {"title": "Load Image"}},
              "435": {"class_type": "PrimitiveStringMultiline", "inputs": {"value": "a dalmatian"}, "_meta": {"title": "Prompt"}},
              "433:110": {"class_type": "TextEncodeQwenImageEditPlus", "inputs": {"prompt": "", "image1": ["433:117", 0]}},
              "433:111": {"class_type": "TextEncodeQwenImageEditPlus", "inputs": {"prompt": ["435", 0], "image1": ["433:117", 0]}},
              "433:117": {"class_type": "FluxKontextImageScale", "inputs": {"image": ["78", 0]}},
              "60": {"class_type": "SaveImage", "inputs": {"filename_prefix": "ComfyUI", "images": ["433:8", 0]}}
            }
            """);
        var missed = new List<string>();
        FilmRoles.Apply(wf, Empty, new FilmInputs { Prompt = "the same man singing", Pictures = [@"C:\f\a.png", @"C:\f\b.png", @"C:\f\c.png", @"C:\f\d.png"] }, missed);
        Assert.Equal("the same man singing", (string?)wf.ValueOf("435.value"));
        Assert.Equal(@"C:\f\a.png", (string?)wf.ValueOf("78.image"));
        Assert.Equal(@"C:\f\b.png", (string?)wf.ValueOf("film_picture_2.image"));
        Assert.Equal(@"C:\f\c.png", (string?)wf.ValueOf("film_picture_3.image"));
        Assert.Equal("""["film_picture_2",0]""", wf.ValueOf("433:110.image2")!.ToJsonString());
        Assert.Equal("""["film_picture_3",0]""", wf.ValueOf("433:111.image3")!.ToJsonString());
        Assert.Single(missed); // the fourth has no place
    }

    [Fact]
    public void A_song_gets_its_style_lyrics_length_and_seed()
    {
        var wf = Load("""
            {
              "3": {"class_type": "KSampler", "inputs": {"seed": ["109", 0], "positive": ["94", 0]}},
              "94": {"class_type": "TextEncodeAceStepAudio1.5", "inputs": {"tags": "trap", "lyrics": "[Verse 1]", "seed": ["109", 0], "bpm": 95, "duration": 120}},
              "98": {"class_type": "EmptyAceStep1.5LatentAudio", "inputs": {"seconds": 120, "batch_size": 1}},
              "107": {"class_type": "SaveAudioMP3", "inputs": {"filename_prefix": "audio/ACE", "audio": ["18", 0]}},
              "109": {"class_type": "PrimitiveInt", "inputs": {"value": 0}, "_meta": {"title": "Int (Seed)"}}
            }
            """);
        var missed = new List<string>();
        FilmRoles.Apply(wf, Empty, new FilmInputs { Tags = "folk", Lyrics = "[Verse]\nla la", Seed = 5, Seconds = 9.5 }, missed);
        Assert.Empty(missed);
        Assert.Equal("folk", (string?)wf.ValueOf("94.tags"));
        Assert.Equal("[Verse]\nla la", (string?)wf.ValueOf("94.lyrics"));
        Assert.Equal(5, (long?)wf.ValueOf("109.value"));
        Assert.Equal(9.5, (double?)wf.ValueOf("94.duration"));
        Assert.Equal(9.5, (double?)wf.ValueOf("98.seconds"));
    }

    [Fact]
    public void A_seed_taken_from_an_untitled_primitive_is_set_there()
    {
        var wf = Load("""
            {
              "3": {"class_type": "KSampler", "inputs": {"seed": ["109", 0], "steps": 50}},
              "94": {"class_type": "TextEncodeAceStepAudio1.5", "inputs": {"tags": "trap", "seed": ["109", 0]}},
              "109": {"class_type": "PrimitiveInt", "inputs": {"value": 0}, "_meta": {"title": "Int"}}
            }
            """);
        var missed = new List<string>();
        FilmRoles.Apply(wf, Empty, new FilmInputs { Seed = 77 }, missed);
        Assert.Empty(missed);
        Assert.Equal(77, (long?)wf.ValueOf("109.value"));
    }

    [Fact]
    public void A_song_is_a_minute_long_at_least_and_takes_the_tempo_its_style_names()
    {
        var board = Storyboard.Parse("""{"shots": [{"id": "s1", "seconds": 8}], "sound": {"source": "song", "tags": "lullaby, 70 BPM"}}""");
        Assert.Equal(60, FilmMaker.SongSeconds(board));
        board.Shots[0].Seconds = 75;
        Assert.Equal(77, FilmMaker.SongSeconds(board));
        Assert.Equal(70, FilmMaker.Bpm("dreamy lo-fi piano, 70 BPM, instrumental"));
        Assert.Equal(128, FilmMaker.Bpm("house at 128bpm"));
        Assert.Null(FilmMaker.Bpm("slow and dreamy"));
    }

    [Fact]
    public void A_songs_score_says_whether_it_sounds_like_music()
    {
        Assert.Contains("sounds bad", new SongScore(2.3, 2.4, 2.4, 2.1, 2.2).Describe());
        Assert.Contains("passable", new SongScore(3.5, 3.4, 3.3, 3.2, 3.1).Describe());
        Assert.Contains("like a real song", new SongScore(4.3, 4.2, 4.4, 4.2, 3.9).Describe());
    }

    [Theory]
    [InlineData(5, 24, 121)]
    [InlineData(4, 24, 97)]
    [InlineData(2.5, 24, 65)]
    [InlineData(3, 25, 81)]
    public void Ltx_gets_the_fewest_8n_plus_1_frames_that_last_the_shot(double seconds, int fps, int frames) =>
        Assert.Equal(frames, FilmRoles.LtxFrames(seconds, fps));
}

public sealed class FilmMontageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bb-montage-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private Storyboard Film(string json)
    {
        var board = Storyboard.Parse(json);
        board.Folder = _dir;
        foreach (var shot in board.Shots)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(board.ShotVideo(shot))!);
            File.WriteAllText(board.ShotVideo(shot), "");
        }
        return board;
    }

    [Fact]
    public void Cuts_join_dissolves_overlap_and_the_soundtrack_goes_under_all_of_it()
    {
        var song = Path.Combine(_dir, "song.mp3");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(song, "");
        var sound = new JsonObject { ["source"] = "file", ["file"] = song }.ToJsonString();
        var board = Film("""
            {"title": "Keeper", "shots": [{"id": "s1", "seconds": 5}, {"id": "s2", "seconds": 4, "transition": "dissolve", "lipSync": true}, {"id": "s3", "seconds": 3}],
             "sound": SOUND}
            """.Replace("SOUND", sound));
        var (args, length, notes) = FilmMontage.Plan(board, _ => true, "out.mp4");
        var graph = args[args.IndexOf("-filter_complex") + 1];
        Assert.Equal(11.5, length);
        Assert.Empty(notes);
        Assert.Contains("[v0][v1]xfade=transition=fade:duration=0.5:offset=4.5[vj1]", graph);
        Assert.Contains("[aj1][a2]concat=n=2:v=0:a=1[aj2]", graph);
        // The lip-synced shot's own sound is the soundtrack again: silence in its place.
        Assert.Contains("anullsrc=r=48000:cl=stereo,atrim=duration=4", graph);
        Assert.Contains("[3:a]", graph);
        Assert.Contains("amix=inputs=2", graph);
        Assert.DoesNotContain("[0:a]", graph); // the shots' own sound is left out under a soundtrack unless asked for
        board.ShotVolume = 0.25;
        var (asked, _, _) = FilmMontage.Plan(board, _ => true, "out.mp4");
        Assert.Contains("volume=0.25", asked[asked.IndexOf("-filter_complex") + 1]);
        Assert.Equal("out.mp4", args[^1]);
        Assert.Equal("11.5", args[^2]);
    }

    [Fact]
    public void A_shots_line_is_heard_clean_and_the_song_goes_quieter_under_it()
    {
        var song = Path.Combine(_dir, "song.mp3");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(song, "");
        var sound = new JsonObject { ["source"] = "file", ["file"] = song }.ToJsonString();
        var board = Film("""
            {"shots": [{"id": "s1", "seconds": 3}, {"id": "s2", "seconds": 4, "line": "We're working on it."}], "sound": SOUND}
            """.Replace("SOUND", sound));
        Assert.True(board.Shots[1].LipSync); // a line is said lip-synced
        File.WriteAllText(board.ShotAudio(board.Shots[1]), "");
        var (args, _, _) = FilmMontage.Plan(board, _ => true, "out.mp4");
        var graph = args[args.IndexOf("-filter_complex") + 1];
        Assert.Equal(board.ShotAudio(board.Shots[1]), args[args.LastIndexOf("-i") + 1]);
        Assert.Contains("[3:a]aresample=48000", graph); // the line, after the two clips and the song
        Assert.Contains("volume='if(between(t,3,7),0.35,1)'", graph);
    }

    [Fact]
    public void Shots_without_a_clip_are_named()
    {
        var board = Film("""{"shots": [{"id": "s1"}, {"id": "s2"}]}""");
        File.Delete(board.ShotVideo(board.Shots[1]));
        var ex = Assert.Throws<FilmException>(() => FilmMontage.Plan(board, _ => true, "out.mp4"));
        Assert.Contains("s2", ex.Message);
    }

    [Fact]
    public async Task Without_ffmpeg_joining_says_how_to_get_it()
    {
        var board = Film("""{"shots": [{"id": "s1"}]}""");
        var ex = await Assert.ThrowsAsync<FilmException>(() => FilmMontage.JoinAsync(board, null, null, null, CancellationToken.None));
        Assert.Contains("Data\\tools", ex.Message);
    }
}

public sealed class FilmCheckTests
{
    private static SKBitmap Picture(int width, int height, Action<SKCanvas> draw)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        draw(canvas);
        return bitmap;
    }

    [Fact]
    public void Black_bars_drawn_in_a_picture_are_found_and_a_dark_picture_is_left_whole()
    {
        using var letterboxed = Picture(400, 300, c =>
        {
            c.Clear(SKColors.Black);
            using var paint = new SKPaint { Color = new SKColor(200, 160, 40) };
            c.DrawRect(0, 40, 400, 220, paint);
        });
        Assert.Equal(new SKRectI(0, 40, 400, 260), FilmMaker.Bars(letterboxed));
        using var night = Picture(400, 300, c =>
        {
            c.Clear(SKColors.Black);
            using var paint = new SKPaint { Color = new SKColor(200, 200, 220) };
            c.DrawCircle(200, 150, 20, paint); // a moon in a night sky
        });
        Assert.Equal(new SKRectI(0, 0, 400, 300), FilmMaker.Bars(night));
    }

    [Fact]
    public void The_same_picture_a_little_changed_is_alike_and_another_one_is_not()
    {
        void Scene(SKCanvas c, int shift, SKColor sky)
        {
            c.Clear(sky);
            using var sea = new SKPaint { Color = new SKColor(20, 60, 120) };
            c.DrawRect(0, 120, 320, 60, sea);
            using var house = new SKPaint { Color = SKColors.White };
            c.DrawRect(200 + shift, 40, 50, 100, house);
        }
        using var a = Picture(320, 180, c => Scene(c, 0, new SKColor(240, 200, 150)));
        using var b = Picture(320, 176, c => Scene(c, 3, new SKColor(236, 198, 148)));
        using var other = Picture(320, 180, c => Scene(c, -150, new SKColor(30, 30, 60)));
        Assert.True(FilmCheck.Alike(a, b) > 0.85, $"{FilmCheck.Alike(a, b)}");
        Assert.True(FilmCheck.Alike(a, other) < 0.5, $"{FilmCheck.Alike(a, other)}");
    }

    [Theory]
    [InlineData("1. Yes.\nVERDICT: good", "good")]
    [InlineData("It morphs.\n**VERDICT: redo**: his face melts", "redo")]
    [InlineData("verdict: Redo: the hand", "redo")]
    [InlineData("No verdict here.", null)]
    public void A_fresh_looks_verdict_is_read_off_its_last_line(string answer, string? verdict) =>
        Assert.Equal(verdict, FilmCheck.Verdict(answer));
}

public sealed class FilmToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bb-filmtool-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void The_overview_says_what_to_make_in_order_frames_then_the_song_then_shots()
    {
        var board = Storyboard.Parse("""
            {"title": "Keeper", "frames": [{"id": "f1", "prompt": "a keeper"}, {"id": "f2", "prompt": "closer", "from": ["f1"]}],
             "shots": [{"id": "s1", "first": "f1", "last": "f2", "prompt": "he waves"}, {"id": "s2", "first": "f2", "lipSync": true, "prompt": "he sings"}],
             "sound": {"source": "song", "tags": "folk", "lyrics": "la"}}
            """);
        board.Folder = _dir;
        var text = FilmTool.Overview(board);
        Assert.Contains("To make: frame f1, frame f2, the song (action=sound), shot s1, shot s2, then join.", text);
        Assert.Contains("s2 (5–10 s, 5 s, goes on from s1)", text);
        Assert.Contains("(lip-synced, first frame only)", text);
    }

    [Fact]
    public async Task Without_a_path_a_new_film_goes_into_the_films_folder_and_never_over_another()
    {
        var tool = new FilmTool();
        var films = Path.Combine(_dir, "Data", "films");
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new Settings.AgentSettings(), WorkingDirectory = _dir, FilmsDirectory = films };
        JsonObject Call() => new()
        {
            ["action"] = "storyboard",
            ["storyboard"] = JsonNode.Parse("""{"title": "Paper Boat", "shots": [{"id": "s1", "prompt": "it sails"}]}"""),
        };
        Assert.False((await tool.ExecuteAsync(Call(), ctx, CancellationToken.None)).IsError);
        Assert.False((await new FilmTool().ExecuteAsync(Call(), ctx, CancellationToken.None)).IsError);
        Assert.True(File.Exists(Path.Combine(films, "Paper Boat", "storyboard.json")));
        Assert.True(File.Exists(Path.Combine(films, "Paper Boat 2", "storyboard.json")));
    }

    [Fact]
    public async Task A_new_storyboard_goes_where_the_path_says_and_a_shot_call_changes_it_first()
    {
        var tool = new FilmTool();
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new Settings.AgentSettings(), WorkingDirectory = _dir };
        var made = await tool.ExecuteAsync(new JsonObject
        {
            ["action"] = "storyboard",
            ["path"] = Path.Combine(_dir, "Keeper"),
            ["storyboard"] = JsonNode.Parse("""{"title": "Keeper", "frames": [{"id": "f1", "prompt": "a keeper"}], "shots": [{"id": "s1", "first": "f1", "prompt": "he waves"}]}"""),
        }, ctx, CancellationToken.None);
        Assert.False(made.IsError, made.Text);
        Assert.True(File.Exists(Path.Combine(_dir, "Keeper", "storyboard.json")));
        // No ComfyUI in this run: the shot isn't made, but its new prompt is saved first.
        var shot = await tool.ExecuteAsync(new JsonObject { ["action"] = "shot", ["id"] = "s1", ["prompt"] = "he waves at the camera", ["seconds"] = 3 }, ctx, CancellationToken.None);
        Assert.True(shot.IsError);
        var board = Storyboard.Load(Path.Combine(_dir, "Keeper"));
        Assert.Equal("he waves at the camera", board.Shots[0].Prompt);
        Assert.Equal(3, board.Shots[0].Seconds);
    }
}
