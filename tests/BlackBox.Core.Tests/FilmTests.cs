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
            {"frames": [{"id": "f2", "prompt": "a close-up"}, {"id": "f3", "prompt": "the sea", "reference": true}, {"id": "f1", "delete": true}],
             "shots": [{"id": "s1", "seconds": 3}], "sound": {"source": "song", "tags": "folk"}}
            """)!);
        Assert.Equal("Keeper", merged.Title);
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

    /// <summary>A storyboard as BlackBox wrote them before nothing was film-wide.</summary>
    private const string Old = """
        {"title": "Keeper", "story": "An old keeper", "style": "warm light, 35mm",
         "frames": [{"id": "f1", "prompt": "a keeper at the door", "seed": 11}, {"id": "f2", "prompt": "closer", "from": ["f1"], "seed": 12}],
         "shots": [{"id": "s1", "first": "f1", "last": "f2", "prompt": "he waves", "camera": "still", "seed": 13}],
         "sound": {"source": "song", "tags": "folk", "seed": 14},
         "workflows": {"frame": "templates/image_z_image_turbo", "shot": "templates/video_ltx2_5_flf2v", "song": "audio_ace_step1_5_xl_turbo"}}
        """;

    [Fact]
    public void An_old_film_wide_look_and_templates_go_into_each_frame_and_shot()
    {
        var board = Storyboard.Parse(Old);
        Assert.Equal("a keeper at the door. warm light, 35mm", board.Frames[0].Prompt);
        Assert.Equal("he waves. warm light, 35mm", board.Shots[0].Prompt);
        Assert.Equal("templates/image_z_image_turbo", board.Frames[0].Model);
        Assert.Null(board.Frames[1].Model); // made from f1: an edit, and no edit template was named
        Assert.Equal("templates/video_ltx2_5_flf2v", board.Shots[0].Model);
        Assert.Equal("audio_ace_step1_5_xl_turbo", board.Sound.Model);
        Assert.False(board.IsOld);
        var json = board.ToJson();
        Assert.DoesNotContain("\"style\"", json);
        Assert.DoesNotContain("\"story\"", json);
        Assert.DoesNotContain("\"workflows\"", json);
    }

    [Fact]
    public void Upgrading_an_old_film_keeps_what_was_made_made()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, Storyboard.FileName), Old);
        var old = Storyboard.Load(_dir, upgrade: false);
        Assert.True(old.IsOld);
        void Made(string file, string workflow, string stamp)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, file);
            File.WriteAllText(workflow, new JsonObject { ["blackbox"] = new JsonObject { ["stamp"] = stamp } }.ToJsonString());
        }
        foreach (var f in old.Frames)
            Made(old.FramePicture(f), old.FrameWorkflow(f), FilmMaker.FrameStamp(old, f));
        Made(old.ShotVideo(old.Shots[0]), old.ShotWorkflow(old.Shots[0]), FilmMaker.ShotStamp(old, old.Shots[0]));
        Made(old.SongFile, old.SongWorkflow, FilmMaker.SongStamp(old));
        Assert.True(FilmMaker.IsCurrent(old, old.Shots[0]));

        var board = Storyboard.Load(_dir);
        Assert.False(board.IsOld);
        Assert.All(board.Frames, f => Assert.True(FilmMaker.IsCurrent(board, f)));
        Assert.True(FilmMaker.IsCurrent(board, board.Shots[0]));
        Assert.True(FilmMaker.SongIsCurrent(board));
        Assert.DoesNotContain("\"style\"", File.ReadAllText(board.FilePath)); // saved upgraded

        // From now on a change to one frame leaves the others made.
        board.Frames[1].Prompt = "closer still";
        Assert.True(FilmMaker.IsCurrent(board, board.Frames[0]));
        Assert.False(FilmMaker.IsCurrent(board, board.Frames[1]));
    }

    [Fact]
    public void A_look_given_with_a_change_goes_only_into_what_the_change_gives()
    {
        var board = Storyboard.Parse("""{"frames": [{"id": "f1", "prompt": "a keeper"}], "shots": [{"id": "s1", "first": "f1", "prompt": "he waves"}]}""");
        var merged = Storyboard.Merge(board, (JsonObject)JsonNode.Parse("""
            {"style": "film noir", "story": "x", "frames": [{"id": "f2", "prompt": "the sea"}], "workflows": {"frame": "juggernaut"}}
            """)!);
        Assert.Equal("a keeper", merged.Frame("f1")!.Prompt);
        Assert.Null(merged.Frame("f1")!.Model);
        Assert.Equal("the sea. film noir", merged.Frame("f2")!.Prompt);
        Assert.Equal("juggernaut", merged.Frame("f2")!.Model);
        Assert.Equal("he waves", merged.Shots[0].Prompt);
        Assert.False(merged.IsOld);
    }

    [Fact]
    public void Taking_a_line_out_stops_the_lip_sync_unless_kept()
    {
        var board = Storyboard.Parse("""
            {"shots": [{"id": "a", "line": "Hello."}, {"id": "b", "line": "Bye."}, {"id": "c", "lipSync": true}]}
            """);
        var merged = Storyboard.Merge(board, (JsonObject)JsonNode.Parse("""
            {"shots": [{"id": "a", "line": ""}, {"id": "b", "line": " ", "lipSync": true}, {"id": "c", "line": ""}]}
            """)!);
        Assert.False(merged.Shots[0].LipSync); // it would sing the song under it otherwise
        Assert.True(merged.Shots[1].LipSync);
        Assert.True(merged.Shots[2].LipSync); // sang the song before and still does
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

public sealed class FilmDirectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bb-filmdir-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private Storyboard Board()
    {
        var board = Storyboard.Parse("""
            {"title": "Keeper", "frames": [{"id": "r1", "reference": true, "name": "the keeper", "prompt": "a sheet"}, {"id": "f1", "prompt": "at the door", "from": ["r1"]},
               {"id": "f2", "prompt": "waving", "from": ["r1"]}, {"id": "f3", "prompt": "the sea"}],
             "shots": [{"id": "s1", "first": "f1", "last": "f2", "prompt": "he waves"}, {"id": "s2", "first": "f3", "prompt": "waves roll"}]}
            """);
        board.Folder = _dir;
        return board;
    }

    private static FilmAsk Answer(string json) => (_, _, _, _, _) => Task.FromResult<string?>("Sure!\n```json\n" + json + "\n```");

    [Fact]
    public async Task A_shots_request_changes_the_shot_and_its_frames_only_and_makes_frames_first()
    {
        var board = Board();
        var direction = await FilmDirector.DirectAsync(board, FilmPiece.Of(board.Shot("s1")!), "slower, and he smiles", FilmTalk.Load(""), [], null, Answer("""
            {"reply": "Slower now.", "change": {"title": "Other", "shots": [{"id": "s1", "seconds": 7, "seed": "new"}, {"id": "s2", "prompt": "nope"}],
                                              "frames": [{"id": "f2", "prompt": "smiling"}, {"id": "f3", "prompt": "nope"}]},
             "make": ["s1", "f2", "s2"]}
            """), CancellationToken.None);
        Assert.Equal("Slower now.", direction.Reply);
        Assert.Equal(7, direction.Board.Shot("s1")!.Seconds);
        Assert.NotEqual(board.Shot("s1")!.Seed, direction.Board.Shot("s1")!.Seed);
        Assert.Equal("smiling", direction.Board.Frame("f2")!.Prompt);
        Assert.Equal("waves roll", direction.Board.Shot("s2")!.Prompt); // not the picked shot's
        Assert.Equal("the sea", direction.Board.Frame("f3")!.Prompt);
        Assert.Equal("Keeper", direction.Board.Title);
        Assert.Equal(["f2", "s1"], direction.Make.Select(p => p.Id));
        Assert.Contains(direction.Notes, n => n.Contains("f3"));
        Assert.Equal("he waves", board.Shot("s1")!.Prompt); // the storyboard given is left as it was
    }

    [Fact]
    public async Task A_whole_films_request_may_change_anything_and_makes_nothing()
    {
        var board = Board();
        var direction = await FilmDirector.DirectAsync(board, null, "add a gull", FilmTalk.Load(""), [], null, Answer("""
            {"reply": "Added a gull.", "change": {"title": "Keeper and Gull", "shots": [{"id": "s3", "first": "f3", "seconds": 4, "prompt": "a gull lands"}]}, "make": ["s3"]}
            """), CancellationToken.None);
        Assert.Equal("Keeper and Gull", direction.Board.Title);
        Assert.Equal(3, direction.Board.Shots.Count);
        Assert.Empty(direction.Make);
    }

    [Fact]
    public async Task An_answer_that_isnt_json_is_asked_again_once()
    {
        var asked = 0;
        FilmAsk ask = (_, question, _, think, _) =>
        {
            asked++;
            Assert.True(think); // a whole film is laid out with thinking
            return Task.FromResult<string?>(asked == 1 ? "Let me think about that." : """{"reply": "Done.", "change": {}, "make": []}""");
        };
        var direction = await FilmDirector.DirectAsync(Board(), null, "anything", FilmTalk.Load(""), [], null, ask, CancellationToken.None);
        Assert.Equal(2, asked);
        Assert.Equal("Done.", direction.Reply);
    }

    [Fact]
    public void Takes_are_kept_and_put_back_with_their_settings()
    {
        var board = Board();
        board.Save();
        var frame = board.Frame("f3")!;
        var piece = FilmPiece.Of(frame);
        void Made(string what)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(board.FramePicture(frame))!);
            File.WriteAllText(board.FramePicture(frame), what);
            File.WriteAllText(board.FrameWorkflow(frame), new JsonObject { ["blackbox"] = new JsonObject { ["stamp"] = FilmMaker.FrameStamp(board, frame) } }.ToJsonString());
            FilmTakes.Remember(board.FrameWorkflow(frame), piece.Settings(board));
        }
        Made("the sea at dawn");
        Assert.Null(FilmTakes.Keep(board, FilmPiece.Of(board.Frame("f1")!))); // not made: nothing to keep
        FilmTakes.Keep(board, piece);
        frame.Prompt = "the sea at night";
        Made("the sea at night");
        var takes = FilmTakes.List(board, piece);
        Assert.Single(takes);
        Assert.Equal("the sea", (string?)takes[0].Settings?["prompt"]);

        var back = FilmTakes.Use(board, piece, takes[0]);
        Assert.Equal("the sea", back.Frame("f3")!.Prompt);
        Assert.Equal("the sea at dawn", File.ReadAllText(back.FramePicture(back.Frame("f3")!)));
        Assert.True(FilmMaker.IsCurrent(back, back.Frame("f3")!));
        Assert.Equal("the sea at night", File.ReadAllText(FilmTakes.List(back, piece).Single().File)); // the one replaced is a take now
    }

    [Fact]
    public void What_was_said_is_kept_beside_the_storyboard()
    {
        Directory.CreateDirectory(_dir);
        var talk = FilmTalk.Load(_dir);
        talk.Add("s1", FilmTalk.You, "slower");
        talk.Add("s1", FilmTalk.BlackBox, "Making it…");
        talk.ReplaceLast("s1", "Made it.");
        talk.Save(_dir);
        var back = FilmTalk.Load(_dir);
        Assert.Equal(["slower", "Made it."], back.About("s1").Select(l => l.Text));
        Assert.Empty(back.About(FilmTalk.Film));
    }
}

public sealed class FilmTemplatesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bb-filmtpl-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>An editor-format workflow of these node types (a muted one with mode 4).</summary>
    private static JsonNode Editor(params string[] types) => new JsonObject
    {
        ["nodes"] = new JsonArray(types.Select((t, i) => (JsonNode)new JsonObject
        {
            ["id"] = i + 1,
            ["type"] = t.TrimEnd('!'),
            ["mode"] = t.EndsWith('!') ? 4 : 0,
        }).ToArray()),
        ["links"] = new JsonArray(),
    };

    [Fact]
    public void What_a_workflow_makes_is_told_by_its_nodes()
    {
        var text2image = Editor("CheckpointLoaderSimple", "CLIPTextEncode", "KSampler", "VAEDecode", "SaveImage");
        Assert.True(FilmTemplates.Fits(text2image, FilmJob.Frame));
        Assert.False(FilmTemplates.Fits(text2image, FilmJob.Edit));
        var edit = Editor("LoadImage", "TextEncodeQwenImageEditPlus", "KSampler", "SaveImage");
        Assert.True(FilmTemplates.Fits(edit, FilmJob.Edit));
        Assert.False(FilmTemplates.Fits(edit, FilmJob.Frame));
        // A control picture loader that is muted doesn't count.
        Assert.True(FilmTemplates.Fits(Editor("LoadImage!", "CLIPTextEncode", "SaveImage"), FilmJob.Frame));
        var flf = Editor("LoadImage", "LoadImage", "CLIPTextEncode", "CreateVideo", "SaveVideo");
        Assert.True(FilmTemplates.Fits(flf, FilmJob.Shot));
        Assert.False(FilmTemplates.Fits(flf, FilmJob.ShotFromFirst));
        Assert.True(FilmTemplates.Fits(Editor("LoadImage", "LoadAudio", "CLIPTextEncode", "SaveVideo"), FilmJob.LipSync));
        Assert.True(FilmTemplates.Fits(Editor("TextEncodeAceStepAudio1.5", "SaveAudioMP3"), FilmJob.Song));
        // No prompt (an upscaler), or a 3D model: no job of a film's.
        Assert.False(FilmTemplates.Fits(Editor("LoadImage", "ImageUpscaleWithModel", "SaveImage"), FilmJob.Edit));
        Assert.False(FilmTemplates.Fits(Editor("CLIPTextEncode", "SaveImage", "SaveGLB"), FilmJob.Frame));
    }

    private static readonly FilmOption[] Installed =
    [
        new("workflows/WorkflowCollection/Templates/Image/Custom/FurryNovaIllustrious/nova-furry-v17", "nova-furry-v17", FilmOption.Yours, "", ["novaFurryXL_ilV170.safetensors"]),
        new("workflows/WorkflowCollection/Templates/Image/Custom/PonyDiffusion/ponydiffusion-v6", "ponydiffusion-v6", FilmOption.Yours, "", ["ponyDiffusionV6XL_v6StartWithThisOne.safetensors"]),
        new("templates/image_z_image_turbo", "Z-Image-Turbo Text to Image", FilmOption.Template, "", ["z_image_turbo_bf16.safetensors"]),
        new("checkpoints/novaFurryXL_ilV170.safetensors", "novaFurryXL_ilV170", FilmOption.Checkpoint, "An SDXL checkpoint", ["novaFurryXL_ilV170.safetensors"]),
        new("checkpoints/juggernautXL_ragnarok.safetensors", "juggernautXL_ragnarok", FilmOption.Checkpoint, "An SDXL checkpoint", ["juggernautXL_ragnarok.safetensors"]),
    ];

    [Theory]
    [InlineData("furry nova xl", "nova-furry-v17")] // the user's workflow before the bare checkpoint
    [InlineData("the Furry Nova XL checkpoint", "novaFurryXL_ilV170")]
    [InlineData("zimage turbo", "Z-Image-Turbo Text to Image")]
    [InlineData("my pony workflow", "ponydiffusion-v6")]
    [InlineData("Juggernaut", "juggernautXL_ragnarok")]
    [InlineData("nova-furry-v17", "nova-furry-v17")]
    public void Words_name_what_is_installed(string words, string title) =>
        Assert.Equal(title, FilmTemplates.Match(Installed, words)?.Title);

    [Theory]
    [InlineData("something for anime")]
    [InlineData("flux")]
    [InlineData("the model")]
    [InlineData("ltx 2.5")] // numbers alone are in many model files
    [InlineData("ace step")]
    public void Words_that_name_nothing_installed_match_nothing(string words) =>
        Assert.Null(FilmTemplates.Match(Installed, words));

    [Fact]
    public void A_checkpoint_is_told_by_its_tensors()
    {
        Directory.CreateDirectory(_dir);
        string Fake(string name, params string[] keys)
        {
            var head = System.Text.Encoding.UTF8.GetBytes(new JsonObject(keys.Select(k => KeyValuePair.Create(k, (JsonNode?)new JsonObject()))).ToJsonString());
            var path = Path.Combine(_dir, name);
            using var file = File.Create(path);
            file.Write(BitConverter.GetBytes((long)head.Length));
            file.Write(head);
            return path;
        }
        Assert.Equal("SDXL", FilmTemplates.CheckpointKind(Fake("xl.safetensors", "model.diffusion_model.input_blocks.0.0.weight", "conditioner.embedders.0.x", "conditioner.embedders.1.y", "first_stage_model.decoder.z")));
        Assert.Equal("SD 1.5", FilmTemplates.CheckpointKind(Fake("sd15.safetensors", "model.diffusion_model.input_blocks.0.0.weight", "cond_stage_model.transformer.text_model.a", "first_stage_model.b")));
        // A video model, and a pose model without a text encoder: not for the plain workflow.
        Assert.Null(FilmTemplates.CheckpointKind(Fake("ltx.safetensors", "model.diffusion_model.transformer_blocks.0.a", "vae.decoder.b", "vocoder.c")));
        Assert.Null(FilmTemplates.CheckpointKind(Fake("pose.safetensors", "model.diffusion_model.input_blocks.0.0.weight", "first_stage_model.b", "model.diffusion_model.heatmap_head.c")));
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
    public void A_changed_frame_puts_what_is_made_from_it_on_the_list_too()
    {
        var board = Storyboard.Parse("""
            {"title": "Keeper", "frames": [{"id": "f1", "prompt": "a keeper"}, {"id": "f2", "prompt": "closer", "from": ["f1"]}, {"id": "f3", "prompt": "the goal"}],
             "shots": [{"id": "s1", "first": "f2", "prompt": "he waves"}, {"id": "s2", "first": "f3", "prompt": "the net shakes"}]}
            """);
        board.Folder = _dir;
        void Made(string file, string workflow, string stamp)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, file);
            File.WriteAllText(workflow, new JsonObject { ["blackbox"] = new JsonObject { ["stamp"] = stamp } }.ToJsonString());
        }
        Made(board.FramePicture(board.Frame("f1")!), board.FrameWorkflow(board.Frame("f1")!), "made before f1 was changed");
        foreach (var id in new[] { "f2", "f3" })
            Made(board.FramePicture(board.Frame(id)!), board.FrameWorkflow(board.Frame(id)!), FilmMaker.FrameStamp(board, board.Frame(id)!));
        foreach (var shot in board.Shots)
            Made(board.ShotVideo(shot), board.ShotWorkflow(shot), FilmMaker.ShotStamp(board, shot));
        var text = FilmTool.Overview(board);
        Assert.Contains("To make: frame f1, frame f2, shot s1, then join.", text);
        Assert.Contains("made, but to be made again after f1", text);
        Assert.Contains("Made, but to be made again after frame f2", text);
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

        // Another task finds a film by its name, or is told which films there are.
        var byName = await new FilmTool().ExecuteAsync(new JsonObject { ["action"] = "storyboard", ["path"] = "Paper Boat 2" }, ctx, CancellationToken.None);
        Assert.False(byName.IsError);
        Assert.Contains(Path.Combine(films, "Paper Boat 2"), byName.Text);
        var unsaid = await new FilmTool().ExecuteAsync(new JsonObject { ["action"] = "storyboard" }, ctx, CancellationToken.None);
        Assert.True(unsaid.IsError);
        Assert.Contains(Path.Combine(films, "Paper Boat 2"), unsaid.Text);
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
