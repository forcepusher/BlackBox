using System.Buffers.Binary;
using System.Text;
using Sloperator.Core.Media;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>Prompts and settings read back from the files ComfyUI and AUTOMATIC1111 save.</summary>
public sealed class MediaRecipeTests
{
    /// <summary>A text-to-image workflow as ComfyUI saves it: checkpoint, LoRA, two prompts, a KSampler.</summary>
    private const string TextToImage = """
        {"3": {"inputs": {"seed": 7, "steps": 25, "cfg": 7.0, "sampler_name": "euler", "scheduler": "normal", "denoise": 1.0,
               "model": ["10", 0], "positive": ["6", 0], "negative": ["7", 0], "latent_image": ["5", 0]}, "class_type": "KSampler", "_meta": {"title": "KSampler"}},
         "4": {"inputs": {"ckpt_name": "juggernautXL_ragnarok.safetensors"}, "class_type": "CheckpointLoaderSimple"},
         "10": {"inputs": {"lora_name": "pixel-art-xl.safetensors", "strength_model": 0.9, "strength_clip": 1.0, "model": ["4", 0], "clip": ["4", 1]}, "class_type": "LoraLoader"},
         "11": {"inputs": {"lora_name": "unused.safetensors", "strength_model": 0.0, "model": ["4", 0]}, "class_type": "LoraLoaderModelOnly"},
         "5": {"inputs": {"width": 1024, "height": 768, "batch_size": 1}, "class_type": "EmptyLatentImage"},
         "6": {"inputs": {"text": "a knight in steel armour, pixel art", "clip": ["10", 1]}, "class_type": "CLIPTextEncode"},
         "7": {"inputs": {"text": "blurry, text", "clip": ["10", 1]}, "class_type": "CLIPTextEncode"},
         "9": {"inputs": {"filename_prefix": "knight", "images": ["8", 0]}, "class_type": "SaveImage"}}
        """;

    /// <summary>
    /// An LTX video workflow: the prompt in a primitive behind a switch, through LTXVConditioning and
    /// two guides that take and give back both prompts, to a guider and SamplerCustomAdvanced.
    /// </summary>
    private const string Video = """
        {"1": {"inputs": {"unet_name": "ltx-2.5-distilled.safetensors", "weight_dtype": "default"}, "class_type": "UNETLoader"},
         "9": {"inputs": {"value": "A bunny waves at the camera"}, "class_type": "PrimitiveStringMultiline"},
         "10": {"inputs": {"value": false}, "class_type": "PrimitiveBoolean"},
         "12": {"inputs": {"prompt": ["9", 0]}, "class_type": "TextGenerateLTX2Prompt"},
         "14": {"inputs": {"switch": ["10", 0], "on_false": ["9", 0], "on_true": ["12", 0]}, "class_type": "ComfySwitchNode"},
         "16": {"inputs": {"text": ["14", 0], "clip": ["2", 0]}, "class_type": "CLIPTextEncode"},
         "17": {"inputs": {"text": "blurry, frozen", "clip": ["2", 0]}, "class_type": "CLIPTextEncode"},
         "25": {"inputs": {"frame_rate": 24, "positive": ["16", 0], "negative": ["17", 0]}, "class_type": "LTXVConditioning"},
         "27": {"inputs": {"frame_idx": 0, "strength": 0.7, "positive": ["25", 0], "negative": ["25", 1]}, "class_type": "LTXVAddGuide"},
         "28": {"inputs": {"frame_idx": -1, "strength": 0.7, "positive": ["27", 0], "negative": ["27", 1]}, "class_type": "LTXVAddGuide"},
         "5": {"inputs": {"image": "first.png"}, "class_type": "LoadImage"},
         "31": {"inputs": {"noise_seed": 20261021}, "class_type": "RandomNoise"},
         "32": {"inputs": {"video_cfg": 1.0, "audio_cfg": 1.0, "model": ["1", 0], "positive": ["28", 0], "negative": ["28", 1]}, "class_type": "LTXVDualCFGGuider"},
         "33": {"inputs": {"eta": 0.0}, "class_type": "SamplerEulerAncestral"},
         "34": {"inputs": {"sigmas": "1.0, 0.99, 0.9, 0.7, 0.0"}, "class_type": "ManualSigmas"},
         "35": {"inputs": {"noise": ["31", 0], "guider": ["32", 0], "sampler": ["33", 0], "sigmas": ["34", 0], "latent_image": ["30", 0]}, "class_type": "SamplerCustomAdvanced"}}
        """;

    /// <summary>An ACE-Step song: tags and lyrics in one encoder, the negative its zeroed copy.</summary>
    private const string Song = """
        {"3": {"inputs": {"seed": ["109", 0], "steps": 8, "cfg": 1.0, "sampler_name": "euler", "scheduler": "simple", "denoise": 1.0,
               "model": ["78", 0], "positive": ["94", 0], "negative": ["47", 0], "latent_image": ["98", 0]}, "class_type": "KSampler"},
         "47": {"inputs": {"conditioning": ["94", 0]}, "class_type": "ConditioningZeroOut"},
         "78": {"inputs": {"shift": 3.0, "sampling": "flow", "model": ["104", 0]}, "class_type": "ModelSamplingAuraFlow"},
         "94": {"inputs": {"tags": "gentle solo piano, ambient", "lyrics": "[verse]\nThe moon is high", "bpm": 70, "keyscale": "E minor", "timesignature": "4"}, "class_type": "TextEncodeAceStepAudio1.5"},
         "98": {"inputs": {"seconds": 60.0, "batch_size": 1}, "class_type": "EmptyAceStep1.5LatentAudio"},
         "104": {"inputs": {"unet_name": "acestep_v1.5_xl_turbo.safetensors"}, "class_type": "UNETLoader"},
         "109": {"inputs": {"value": 3100094932465}, "class_type": "PrimitiveInt"}}
        """;

    private static string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("bb-recipe-").FullName, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>A 4x4 PNG with these tEXt chunks before its pixels, as ComfyUI and AUTOMATIC1111 write them.</summary>
    private static byte[] Png(params (string Key, string Text)[] texts)
    {
        using var bitmap = new SkiaSharp.SKBitmap(4, 4);
        bitmap.Erase(SkiaSharp.SKColors.Teal);
        using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        var png = data.ToArray();
        var chunks = new MemoryStream();
        foreach (var (key, text) in texts)
        {
            var body = Encoding.Latin1.GetBytes(key + "\0" + text);
            var head = new byte[8];
            BinaryPrimitives.WriteInt32BigEndian(head, body.Length);
            Encoding.ASCII.GetBytes("tEXt").CopyTo(head, 4);
            chunks.Write(head);
            chunks.Write(body);
            chunks.Write(new byte[4]); // the CRC, which nothing here checks
        }
        // After the IHDR chunk (8 + 25 bytes in), as the text goes in front of the pixels.
        return [.. png[..33], .. chunks.ToArray(), .. png[33..]];
    }

    private static Dictionary<string, string> Settings(MediaRecipe recipe) => recipe.Settings.GroupBy(s => s.Name).ToDictionary(g => g.Key, g => string.Join(" + ", g.Select(s => s.Value)));

    [Fact]
    public void A_ComfyUI_picture_gives_its_prompts_model_LoRA_and_sampler()
    {
        var recipe = MediaRecipe.Read(Write("knight_00001_.png", Png(("prompt", TextToImage), ("workflow", """{"nodes": []}"""))));

        Assert.NotNull(recipe);
        Assert.Equal("a knight in steel armour, pixel art", recipe.Prompt);
        Assert.Equal("blurry, text", recipe.NegativePrompt);
        var s = Settings(recipe);
        Assert.Equal("juggernautXL_ragnarok.safetensors", s["Model"]);
        // A LoRA at strength 0 changes nothing, and one the sampler's model doesn't come through isn't there.
        Assert.Equal("pixel-art-xl.safetensors (0.9)", s["LoRA"]);
        Assert.Equal("euler", s["Sampler"]);
        Assert.Equal("normal", s["Scheduler"]);
        Assert.Equal("25", s["Steps"]);
        Assert.Equal("7", s["CFG"]);
        Assert.Equal("7", s["Seed"]);
        Assert.Equal("1024x768", s["Size"]);
        Assert.Equal("ComfyUI", recipe.MadeWith);
    }

    [Fact]
    public void A_ComfyUI_video_keeps_its_workflow_in_the_middle_of_the_file()
    {
        // As SaveVideo's MP4: the workflow in the header's tags, the frames around it.
        var bytes = (byte[])[.. new byte[5000], .. Encoding.ASCII.GetBytes("mdtaprompt\0\0\0data\0\0\0\0"), .. Encoding.UTF8.GetBytes(Video.ReplaceLineEndings(" ")), .. new byte[20000]];

        var recipe = MediaRecipe.Read(Write("LTX_2.5_flf2v_00001_.mp4", bytes));

        Assert.NotNull(recipe);
        // Through the switch (its prompt enhancer off), the conditioning and both guides, to the guider.
        Assert.Equal("A bunny waves at the camera", recipe.Prompt);
        Assert.Equal("blurry, frozen", recipe.NegativePrompt);
        var s = Settings(recipe);
        Assert.Equal("ltx-2.5-distilled.safetensors", s["Model"]);
        Assert.Equal("Euler Ancestral", s["Sampler"]);
        Assert.Equal("4", s["Steps"]);
        Assert.Equal("1", s["CFG"]);
        Assert.Equal("20261021", s["Seed"]);
        Assert.Equal("first.png", s["From"]);
    }

    [Fact]
    public void A_workflow_written_as_a_JSON_string_is_read_too()
    {
        // Video Combine's MP4s keep the workflow as a string of JSON.
        // As Python writes it: quotes as \", which System.Text.Json would write as \u0022.
        var quoted = "\"" + TextToImage.ReplaceLineEndings(" ").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        var bytes = (byte[])[.. new byte[300], .. Encoding.UTF8.GetBytes(quoted), .. new byte[300]];

        var recipe = MediaRecipe.Read(Write("AnimateDiff_00001.mp4", bytes));

        Assert.Equal("a knight in steel armour, pixel art", recipe?.Prompt);
    }

    [Fact]
    public void A_song_gives_its_tags_lyrics_tempo_and_key_and_a_zeroed_negative_is_none()
    {
        var bytes = (byte[])[.. Encoding.ASCII.GetBytes("ID3\u0004\0\0\0\0\0\0TXXX\0\0\0\0\0\0\0prompt\0"), .. Encoding.UTF8.GetBytes(Song.ReplaceLineEndings(" ")), .. new byte[100]];

        var recipe = MediaRecipe.Read(Write("song_00001.mp3", bytes));

        Assert.NotNull(recipe);
        Assert.Equal("gentle solo piano, ambient", recipe.Prompt);
        Assert.Null(recipe.NegativePrompt);
        Assert.Equal("[verse]\nThe moon is high", recipe.Texts.Single(t => t.Label == "Lyrics").Text);
        var s = Settings(recipe);
        Assert.Equal("acestep_v1.5_xl_turbo.safetensors", s["Model"]);
        Assert.Equal("3", s["Shift"]);
        Assert.Equal("3100094932465", s["Seed"]);
        Assert.Equal("70", s["BPM"]);
        Assert.Equal("E minor", s["Key"]);
        Assert.Equal("60 s", s["Length"]);
    }

    [Fact]
    public void An_AUTOMATIC1111_picture_gives_its_parameters()
    {
        const string parameters = "masterpiece, a fox in the snow\nNegative prompt: lowres, bad anatomy\n" +
                                  "Steps: 28, Sampler: Euler a, Schedule type: Karras, CFG scale: 6.5, Seed: 1234, Size: 832x1216, Model hash: abc123, Model: ponyDiffusionV6XL, Lora hashes: \"fox: 1a2b, snow: 3c4d\", Version: f2.0";

        var recipe = MediaRecipe.Read(Write("00001-1234.png", Png(("parameters", parameters))));

        Assert.NotNull(recipe);
        Assert.Equal("masterpiece, a fox in the snow", recipe.Prompt);
        Assert.Equal("lowres, bad anatomy", recipe.NegativePrompt);
        var s = Settings(recipe);
        Assert.Equal("Euler a", s["Sampler"]);
        Assert.Equal("Karras", s["Schedule type"]);
        Assert.Equal("6.5", s["CFG scale"]);
        Assert.Equal("ponyDiffusionV6XL", s["Model"]);
        // Hashes and versions say nothing about how to make it again.
        Assert.DoesNotContain("Model hash", s.Keys);
        Assert.DoesNotContain("Lora hashes", s.Keys);
        Assert.DoesNotContain("Version", s.Keys);
    }

    [Fact]
    public void A_file_that_says_nothing_gives_nothing()
    {
        Assert.Null(MediaRecipe.Read(Write("plain.png", Png())));
        Assert.Null(MediaRecipe.Read(Write("noise.mp4", new byte[4096])));
        Assert.Null(MediaRecipe.Read(Path.Combine(Path.GetTempPath(), "bb-no-such-file.png")));
    }

    [Fact]
    public void Describe_puts_the_prompts_and_settings_in_one_text_to_copy()
    {
        var recipe = MediaRecipe.FromComfy(System.Text.Json.Nodes.JsonNode.Parse(TextToImage)!.AsObject());

        var text = recipe.Describe();

        Assert.StartsWith("Prompt:\na knight in steel armour, pixel art\n\nNegative prompt:\nblurry, text\n\nModel: juggernautXL_ragnarok.safetensors\n", text);
        Assert.Contains("\nSeed: 7", text);
    }
}
