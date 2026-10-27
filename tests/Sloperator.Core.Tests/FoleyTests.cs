using Sloperator.Core.Comfy;
using Sloperator.Core.Tools;
using Sloperator.Core.Video;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>Foley on a sound line, made from the clips under it, and each sound line's volume.</summary>
public sealed class FoleyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bb-foley-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private Storyboard Video(string json, bool made = true)
    {
        var board = Storyboard.Parse(json);
        board.Folder = _dir;
        if (made)
            foreach (var shot in board.Shots)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(board.ShotVideo(shot))!);
                File.WriteAllText(board.ShotVideo(shot), "");
            }
        return board;
    }

    [Fact]
    public void Foley_is_a_source_of_its_own_with_a_seed_and_the_old_ones_read_as_before()
    {
        var board = Storyboard.Parse("""
            {"sounds": [{"id": "a1", "source": "foley"}, {"id": "a2", "source": "sfx"}, {"id": "a3", "source": "audio"}, {"id": "a4"}]}
            """);
        Assert.Equal([VideoSound.Foley, VideoSound.Foley, VideoSound.FromFile, VideoSound.Song], board.Sounds.Select(s => s.Source));
        Assert.True(board.Sounds[0].Seed > 0);
        Assert.Equal(0, board.Sounds[2].Seed);
    }

    [Fact]
    public void Each_sound_line_has_a_volume_kept_with_the_video_and_used_when_it_is_joined()
    {
        var board = Video("""
            {"title": "Loud", "soundLines": 3, "soundLineVolumes": [0.5, 9],
             "shots": [{"id": "s1", "seconds": 4}],
             "sounds": [{"id": "a1", "source": "file", "place": "References/rain.mp3", "soundLine": 1, "volume": 0.8}]}
            """);
        Assert.Equal([0.5, 2, 1], board.SoundLineVolumes); // one for each line, at most twice as loud
        Assert.Equal(1, board.LineVolume(7));
        Directory.CreateDirectory(board.ReferencesFolder);
        File.WriteAllText(board.SoundFile(board.Sounds[0]), "");
        var (args, _, _) = VideoMontage.Plan(board, _ => true, "out.mp4");
        Assert.Contains("asetpts=PTS-STARTPTS,volume=1.6,adelay=0|0", args[args.IndexOf("-filter_complex") + 1]); // 0.8 on a line at 2

        board.Save();
        var again = Storyboard.Load(_dir);
        Assert.Equal([0.5, 2, 1], again.SoundLineVolumes);
        Assert.Contains("sound line 0 at volume 0.5, sound line 1 at volume 2", VideoGeneratorTool.Overview(again));
    }

    [Fact]
    public void Foley_is_made_from_the_stretch_of_the_clips_under_it_as_the_joined_video_shows_them()
    {
        var board = Video("""
            {"width": 640, "height": 360, "shots": [{"id": "s1", "seconds": 5}, {"id": "s2", "seconds": 4, "transition": "dissolve"}, {"id": "s3", "seconds": 3}],
             "sounds": [{"id": "a1", "source": "foley", "start": 6, "trim": 1, "seconds": 3}]}
            """);
        var sound = board.Sounds[0];
        Assert.Equal((5, 9), VideoMaker.FoleyStretch(board, sound)); // from where its file starts playing (its start less the trim)
        var args = VideoMontage.StretchPlan(board, 5, 4, "stretch.mp4");
        // s1 is 0-5 s, s2 4.5-8.5 (a dissolve), s3 8.5-11.5: s2 and s3 are under 5-9 s.
        Assert.Equal(new[] { board.ShotVideo(board.Shots[1]), board.ShotVideo(board.Shots[2]) }, args.Where((a, i) => i > 0 && args[i - 1] == "-i"));
        var graph = args[args.IndexOf("-filter_complex") + 1];
        Assert.Contains("fps=25", graph);
        Assert.Contains("[v0][v1]concat=n=2:v=1:a=0[vj1]", graph);
        Assert.Contains("trim=start=0.5:duration=4,setpts=PTS-STARTPTS,scale=-2:384[vout]", graph);
        Assert.Contains("-an", args);
        Assert.Equal("stretch.mp4", args[^1]);

        // Past the video's end it stops there; a stretch with no clip under it, or clips not made, says so.
        Assert.Equal((0, 11.5), VideoMaker.FoleyStretch(board, new VideoSound { Source = VideoSound.Foley }));
        Assert.Contains("No clip is under 20 s", Assert.Throws<VideoException>(() => VideoMontage.StretchPlan(board, 20, 4, "x.mp4")).Message);
        File.Delete(board.ShotVideo(board.Shots[2]));
        Assert.Contains("s3", Assert.Throws<VideoException>(() => VideoMontage.StretchPlan(board, 5, 4, "x.mp4")).Message);
        VideoMontage.StretchPlan(board, 0, 4, "x.mp4"); // s3 isn't needed there
    }

    [Fact]
    public void Foley_is_out_of_date_when_a_clip_under_it_is_made_again_not_one_elsewhere()
    {
        var board = Video("""
            {"shots": [{"id": "s1", "seconds": 5}, {"id": "s2", "seconds": 5}], "sounds": [{"id": "a1", "source": "foley", "seconds": 4}]}
            """);
        var sound = board.Sounds[0];
        var before = VideoMaker.FoleyStamp(board, sound);
        File.WriteAllText(board.ShotVideo(board.Shots[1]), "made again");
        Assert.Equal(before, VideoMaker.FoleyStamp(board, sound));
        File.WriteAllText(board.ShotVideo(board.Shots[0]), "made again");
        Assert.NotEqual(before, VideoMaker.FoleyStamp(board, sound));
        sound.Tags = "footsteps";
        Assert.NotEqual(VideoMaker.FoleyStamp(board, sound), before);
    }

    [Fact]
    public void The_overview_lists_foley_to_make_after_its_clips()
    {
        var board = Video("""
            {"shots": [{"id": "s1", "seconds": 5, "prompt": "rain on a tin roof"}], "sounds": [{"id": "a1", "source": "foley", "tags": "rain"}]}
            """, made: false);
        var text = VideoGeneratorTool.Overview(board);
        Assert.Contains("a1 \"a1\" (soundLine 0, from 0 s): foley, made from what the clips show from 0 to 5 s, heard as \"rain\"", text);
        Assert.Contains("To make: shot s1, sound a1 (action=sound), then join.", text);
    }

    [Fact]
    public void The_built_in_video_to_audio_workflow_is_foley_and_gets_its_video_words_length_and_seed()
    {
        var builtin = ComfyBuiltins.All.Single(b => b.Name == "video_to_audio");
        var json = ComfyWorkflow.ParseText(builtin.Workflow);
        Assert.True(VideoTemplates.Fits(json, VideoJob.Foley));
        Assert.False(VideoTemplates.Fits(json, VideoJob.Song));
        Assert.False(VideoTemplates.Fits(ComfyWorkflow.ParseText(ComfyBuiltins.All.Single(b => b.Name == "text_to_image").Workflow), VideoJob.Foley));
        // Every model it names has a link to download it from, into the folder kijai's nodes read.
        foreach (var file in new[] { "mmaudio_large_44k_v2_fp16.safetensors", "mmaudio_vae_44k_fp16.safetensors", "mmaudio_synchformer_fp16.safetensors", "apple_DFN5B-CLIP-ViT-H-14-384_fp16.safetensors" })
        {
            Assert.Contains(file, builtin.Workflow);
            Assert.Equal("mmaudio", ComfyBuiltins.Known[file].Folder);
        }

        var wf = ComfyWorkflow.FromApi(json);
        var missed = new List<string>();
        VideoRoles.Apply(wf, ComfyNodeLibrary.Parse([]), new VideoInputs { Video = @"C:\v\a1-1.mp4", Prompt = "rain", Negative = "music", Seed = 7, Seconds = 6.5, Prefix = "Sloperator videos/x/a1" }, missed);
        Assert.Empty(missed);
        Assert.Equal(@"C:\v\a1-1.mp4", (string?)wf.ValueOf("1.file"));
        Assert.Equal("rain", (string?)wf.ValueOf("5.prompt"));
        Assert.Equal("music", (string?)wf.ValueOf("5.negative_prompt"));
        Assert.Equal(7, (long?)wf.ValueOf("5.seed"));
        Assert.Equal(6.5, (double?)wf.ValueOf("5.duration"));
        Assert.Equal("Sloperator videos/x/a1", (string?)wf.ValueOf("6.filename_prefix"));
    }
}
