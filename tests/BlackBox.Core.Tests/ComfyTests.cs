using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BlackBox.Core.Comfy;
using BlackBox.Core.Media;
using BlackBox.Core.Settings;
using BlackBox.Core.Tools;
using Xunit;

namespace BlackBox.Core.Tests;

/// <summary>A small ComfyUI on 127.0.0.1: a few node types, a queue that finishes each job at the second look, and files on disk.</summary>
internal sealed class FakeComfyServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Task _loop;
    private readonly Dictionary<string, int> _looks = [];

    public string Root { get; } = Directory.CreateTempSubdirectory("bb-comfy-").FullName;
    public string CodeDir => Path.Combine(Root, "ComfyUI");
    public string OutputDir => Path.Combine(CodeDir, "output");
    public Uri Url { get; }
    public List<JsonObject> Prompts { get; } = [];
    public List<string> Uploads { get; } = [];
    public List<string> Interrupts { get; } = [];
    public int Frees { get; private set; }
    public int NodeTypeFetches { get; private set; }

    /// <summary>Whether the queue says a job is running.</summary>
    public bool QueueBusy { get; set; }

    /// <summary>Whether it says it runs on the CPU, as on a PC without a graphics card ComfyUI can use.</summary>
    public bool OnCpu { get; set; }
    public List<string> Checkpoints { get; } = ["v1-5-pruned-emaonly-fp16.safetensors", "sdxl.safetensors"];

    /// <summary>The LoRA files it lists (models/loras).</summary>
    public List<string> Loras { get; } = [];

    /// <summary>What a finished job's history says; the default saves one picture.</summary>
    public Func<string, JsonObject>? History { get; set; }

    /// <summary>Whether its jobs never reach the history, as one taken out of the queue or lost to a restart.</summary>
    public bool Forgets { get; set; }

    public static readonly string ObjectInfo = """
    {
      "CheckpointLoaderSimple": {"input": {"required": {"ckpt_name": [["v1-5-pruned-emaonly-fp16.safetensors", "sdxl.safetensors"], {}]}},
        "input_order": {"required": ["ckpt_name"]}, "output": ["MODEL", "CLIP", "VAE"], "output_name": ["MODEL", "CLIP", "VAE"],
        "name": "CheckpointLoaderSimple", "display_name": "Load Checkpoint", "category": "loaders", "python_module": "nodes", "output_node": false},
      "CLIPTextEncode": {"input": {"required": {"text": ["STRING", {"multiline": true}], "clip": ["CLIP", {}]}},
        "output": ["CONDITIONING"], "output_name": ["CONDITIONING"], "name": "CLIPTextEncode", "display_name": "CLIP Text Encode (Prompt)",
        "category": "conditioning", "python_module": "nodes", "output_node": false},
      "EmptyLatentImage": {"input": {"required": {"width": ["INT", {"default": 512, "min": 16, "max": 16384}], "height": ["INT", {"default": 512, "min": 16, "max": 16384}],
        "batch_size": ["INT", {"default": 1, "min": 1, "max": 4096}]}}, "output": ["LATENT"], "output_name": ["LATENT"], "name": "EmptyLatentImage",
        "display_name": "Empty Latent Image", "category": "latent", "python_module": "nodes", "output_node": false},
      "KSampler": {"input": {"required": {"model": ["MODEL", {}], "seed": ["INT", {"default": 0, "min": 0, "max": 18446744073709551615, "control_after_generate": true}],
        "steps": ["INT", {"default": 20, "min": 1, "max": 10000}], "cfg": ["FLOAT", {"default": 8.0, "min": 0.0, "max": 100.0}],
        "sampler_name": [["euler", "euler_ancestral", "dpmpp_2m"], {}], "scheduler": [["normal", "karras"], {}],
        "positive": ["CONDITIONING", {}], "negative": ["CONDITIONING", {}], "latent_image": ["LATENT", {}], "denoise": ["FLOAT", {"default": 1.0, "min": 0.0, "max": 1.0}]}},
        "input_order": {"required": ["model", "seed", "steps", "cfg", "sampler_name", "scheduler", "positive", "negative", "latent_image", "denoise"]},
        "output": ["LATENT"], "output_name": ["LATENT"], "name": "KSampler", "display_name": "KSampler", "category": "sampling", "python_module": "nodes", "output_node": false},
      "VAEDecode": {"input": {"required": {"samples": ["LATENT", {}], "vae": ["VAE", {}]}}, "output": ["IMAGE"], "output_name": ["IMAGE"],
        "name": "VAEDecode", "display_name": "VAE Decode", "category": "latent", "python_module": "nodes", "output_node": false},
      "SaveImage": {"input": {"required": {"images": ["IMAGE", {}], "filename_prefix": ["STRING", {"default": "ComfyUI"}]}}, "output": [], "output_name": [],
        "name": "SaveImage", "display_name": "Save Image", "category": "image", "python_module": "nodes", "output_node": true},
      "LoraLoader": {"input": {"required": {"model": ["MODEL", {}], "clip": ["CLIP", {}], "lora_name": [["pixel.safetensors"], {}],
        "strength_model": ["FLOAT", {"default": 1.0, "min": -100.0, "max": 100.0}], "strength_clip": ["FLOAT", {"default": 1.0, "min": -100.0, "max": 100.0}]}},
        "output": ["MODEL", "CLIP"], "output_name": ["MODEL", "CLIP"], "name": "LoraLoader", "display_name": "Load LoRA", "category": "loaders", "python_module": "nodes", "output_node": false},
      "LoadImage": {"input": {"required": {"image": [["example.png"], {"image_upload": true}]}}, "output": ["IMAGE", "MASK"], "output_name": ["IMAGE", "MASK"],
        "name": "LoadImage", "display_name": "Load Image", "category": "image", "python_module": "nodes", "output_node": false},
      "ImageScaleToTotalPixels": {"input": {"required": {"image": ["IMAGE", {}], "upscale_method": [["nearest-exact", "bilinear", "area", "bicubic", "lanczos"], {}],
        "megapixels": ["FLOAT", {"default": 1.0, "min": 0.01, "max": 16.0}], "resolution_steps": ["INT", {"advanced": true, "default": 1, "min": 1, "max": 256}]}}, "output": ["IMAGE"], "output_name": ["IMAGE"],
        "name": "ImageScaleToTotalPixels", "display_name": "Scale Image to Total Pixels", "category": "image/upscaling", "python_module": "comfy_extras.nodes_upscale_model", "output_node": false},
      "VAEEncode": {"input": {"required": {"pixels": ["IMAGE", {}], "vae": ["VAE", {}]}}, "output": ["LATENT"], "output_name": ["LATENT"],
        "name": "VAEEncode", "display_name": "VAE Encode", "category": "latent", "python_module": "nodes", "output_node": false},
      "ComfyMathExpression": {"input": {"required": {"expression": ["STRING", {"default": "a + b"}], "values": ["COMFY_AUTOGROW_V3", {"template": {"names": ["a", "b"], "min": 1}}]}},
        "output": ["FLOAT", "INT"], "output_name": ["FLOAT", "INT"], "name": "ComfyMathExpression", "display_name": "Math Expression", "category": "math", "python_module": "comfy_extras.nodes_math", "output_node": false},
      "VHS_VideoCombine": {"input": {"required": {"images": ["IMAGE"], "frame_rate": ["FLOAT", {"default": 8}]}}, "output": [], "output_name": [],
        "name": "VHS_VideoCombine", "display_name": "Video Combine", "category": "Video Helper Suite", "python_module": "custom_nodes.ComfyUI-VideoHelperSuite", "output_node": true},
      "RIFE VFI": {"input": {"required": {"frames": ["IMAGE", {}], "scale_factor": [[0.25, 0.5, 1.0, 2.0, 4.0], {"default": 1.0}]}}, "output": ["IMAGE"], "output_name": ["IMAGE"],
        "name": "RIFE VFI", "display_name": "RIFE VFI", "category": "ComfyUI-Frame-Interpolation/VFI", "python_module": "custom_nodes.ComfyUI-Frame-Interpolation", "output_node": false}
    }
    """;

    public FakeComfyServer()
    {
        Directory.CreateDirectory(Path.Combine(CodeDir, "custom_nodes"));
        Directory.CreateDirectory(OutputDir);
        var port = FreePort();
        Url = new Uri($"http://127.0.0.1:{port}/");
        _listener.Prefixes.Add(Url.ToString());
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception) { return; }
            try { Answer(ctx); }
            catch (Exception ex) { Reply(ctx, 500, ex.Message); }
        }
    }

    private void Answer(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url!.AbsolutePath.TrimStart('/');
        var body = ctx.Request.HasEntityBody ? new StreamReader(ctx.Request.InputStream, Encoding.UTF8).ReadToEnd() : "";
        switch (path)
        {
            case "system_stats":
                Reply(ctx, 200, OnCpu
                    ? """{"system": {"comfyui_version": "0.41.0", "ram_total": 16865644544, "ram_free": 8926543872, "argv": ["ComfyUI\\main.py", "--cpu"]}, "devices": [{"name": "cpu", "type": "cpu", "vram_total": 16865644544, "vram_free": 8903266304}]}"""
                    : """{"system": {"comfyui_version": "0.19.0", "ram_total": 68000000000, "ram_free": 40000000000, "argv": ["ComfyUI\\main.py"]}, "devices": [{"name": "cuda:0 Test GPU", "vram_total": 17000000000, "vram_free": 15000000000}]}""");
                return;
            case "object_info":
                NodeTypeFetches++;
                Reply(ctx, 200, ObjectInfo);
                return;
            case "models":
                Reply(ctx, 200, """["checkpoints", "loras"]""");
                return;
            case "models/checkpoints":
                Reply(ctx, 200, new JsonArray([.. Checkpoints.Select(c => JsonValue.Create(c))]).ToJsonString());
                return;
            case "models/loras":
                Reply(ctx, 200, new JsonArray([.. Loras.Select(l => JsonValue.Create(l))]).ToJsonString());
                return;
            case "internal/folder_paths":
                Reply(ctx, 200, new JsonObject
                {
                    ["custom_nodes"] = new JsonArray(Path.Combine(CodeDir, "custom_nodes")),
                    ["checkpoints"] = new JsonArray(Path.Combine(CodeDir, "models", "checkpoints")),
                    ["loras"] = new JsonArray(Path.Combine(CodeDir, "models", "loras")),
                }.ToJsonString());
                return;
            case "queue":
                if (ctx.Request.HttpMethod == "POST") { Reply(ctx, 200, ""); return; }
                Reply(ctx, 200, QueueBusy ? """{"queue_running": [[1, "job1", {}, {}, []]], "queue_pending": []}""" : """{"queue_running": [], "queue_pending": []}""");
                return;
            case "prompt":
            {
                var prompt = JsonNode.Parse(body)!.AsObject();
                Prompts.Add(prompt);
                Reply(ctx, 200, new JsonObject { ["prompt_id"] = $"job{Prompts.Count}", ["number"] = Prompts.Count, ["node_errors"] = new JsonObject() }.ToJsonString());
                return;
            }
            case "upload/image":
                Uploads.Add(body.Length > 0 ? "uploaded" : "");
                Reply(ctx, 200, """{"name": "uploaded.png", "subfolder": "", "type": "input"}""");
                return;
            case "free":
                Frees++;
                Reply(ctx, 200, "");
                return;
            case "interrupt":
                Interrupts.Add(body);
                Reply(ctx, 200, "");
                return;
            case "userdata":
                Reply(ctx, 200, """["Mine/README.json", "Mine/fox.json"]""");
                return;
            case "templates/index.json":
                Reply(ctx, 200, """
                    [{"title": "Image", "templates": [{"name": "image_fox", "title": "Fox picture", "description": "Text to image.", "tags": ["Image"], "openSource": true},
                    {"name": "api_paid", "title": "Paid", "description": "An API template.", "openSource": false}]}]
                    """);
                return;
            case "workflow_templates":
                Reply(ctx, 200, "{}");
                return;
        }
        if (path.StartsWith("history/", StringComparison.Ordinal))
        {
            var id = path["history/".Length..];
            _looks[id] = _looks.GetValueOrDefault(id) + 1;
            if (_looks[id] < 2 || Forgets)
            {
                Reply(ctx, 200, "{}");
                return;
            }
            var entry = History?.Invoke(id) ?? DefaultHistory(id);
            Reply(ctx, 200, new JsonObject { [id] = entry }.ToJsonString());
            return;
        }
        if (path.StartsWith("userdata/", StringComparison.Ordinal))
        {
            var file = Uri.UnescapeDataString(path["userdata/".Length..]);
            Reply(ctx, 200, file.EndsWith("README.json")
                ? """{"nodes": [{"type": "MarkdownNote", "widgets_values": ["# My pictures\n\n- `fox.json` -- a fox in the snow, quickly."]}], "links": []}"""
                : ApiWorkflow);
            return;
        }
        if (path.StartsWith("templates/", StringComparison.Ordinal))
        {
            Reply(ctx, 200, ApiWorkflow);
            return;
        }
        Reply(ctx, 404, "not found");
    }

    /// <summary>A text-to-image workflow in the API format.</summary>
    public const string ApiWorkflow = """
    {
      "4": {"class_type": "CheckpointLoaderSimple", "inputs": {"ckpt_name": "v1-5-pruned-emaonly-fp16.safetensors"}},
      "6": {"class_type": "CLIPTextEncode", "inputs": {"text": "a fox", "clip": ["4", 1]}, "_meta": {"title": "Positive"}},
      "7": {"class_type": "CLIPTextEncode", "inputs": {"text": "blurry", "clip": ["4", 1]}},
      "5": {"class_type": "EmptyLatentImage", "inputs": {"width": 512, "height": 512, "batch_size": 1}},
      "3": {"class_type": "KSampler", "inputs": {"seed": 1, "steps": 20, "cfg": 7, "sampler_name": "euler", "scheduler": "normal", "denoise": 1,
        "model": ["4", 0], "positive": ["6", 0], "negative": ["7", 0], "latent_image": ["5", 0]}},
      "8": {"class_type": "VAEDecode", "inputs": {"samples": ["3", 0], "vae": ["4", 2]}},
      "9": {"class_type": "SaveImage", "inputs": {"images": ["8", 0], "filename_prefix": "fox"}}
    }
    """;

    private JsonObject DefaultHistory(string id)
    {
        var file = Path.Combine(OutputDir, $"fox_{id}_.png");
        using (var bitmap = new SkiaSharp.SKBitmap(64, 48))
        {
            using var canvas = new SkiaSharp.SKCanvas(bitmap);
            canvas.Clear(SkiaSharp.SKColors.OrangeRed);
            using var png = SkiaSharp.SKImage.FromBitmap(bitmap).Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
            File.WriteAllBytes(file, png.ToArray());
        }
        return new JsonObject
        {
            ["status"] = new JsonObject { ["status_str"] = "success", ["completed"] = true, ["messages"] = new JsonArray() },
            ["outputs"] = new JsonObject
            {
                ["9"] = new JsonObject { ["images"] = new JsonArray(new JsonObject { ["filename"] = Path.GetFileName(file), ["subfolder"] = "", ["type"] = "output" }) },
            },
        };
    }

    private static void Reply(HttpListenerContext ctx, int status, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return; // a test may stop it early, as a server gone
        _disposed = true;
        _listener.Stop();
        _listener.Close();
        try { _loop.Wait(2000); } catch (AggregateException) { }
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}

public class ComfyWorkflowTests
{
    private static readonly ComfyNodeLibrary Library = ComfyNodeLibrary.Parse(JsonNode.Parse(FakeComfyServer.ObjectInfo)!.AsObject());

    private static ComfyWorkflow Load(string json) => ComfyWorkflow.FromApi(ComfyWorkflow.ParseText(json));

    [Fact]
    public void A_wrapped_workflow_is_unwrapped_and_its_links_get_string_ids()
    {
        var wf = Load("""{"prompt": {"1": {"class_type": "SaveImage", "inputs": {"images": [8, "0"]}}}}""");
        Assert.Equal("""["8",0]""", wf.Nodes["1"]!["inputs"]!["images"]!.ToJsonString());
    }

    [Fact]
    public void Something_that_is_not_a_workflow_says_what_one_looks_like()
    {
        var ex = Assert.Throws<ComfyWorkflowException>(() => Load("""{"nodes": 3}"""));
        Assert.Contains("class_type", ex.Message);
        Assert.Throws<ComfyWorkflowException>(() => ComfyWorkflow.ParseText("{not json"));
    }

    [Fact]
    public void The_editor_format_is_told_apart_from_the_api_format()
    {
        Assert.True(ComfyWorkflow.IsEditorFormat(JsonNode.Parse("""{"nodes": [], "links": []}""")));
        Assert.False(ComfyWorkflow.IsEditorFormat(JsonNode.Parse(FakeComfyServer.ApiWorkflow)));
    }

    [Fact]
    public void A_correct_workflow_passes()
    {
        Assert.Empty(Load(FakeComfyServer.ApiWorkflow).Validate(Library));
    }

    [Fact]
    public void Every_wrong_name_is_reported_at_once_with_the_right_one_beside_it()
    {
        var wf = Load(FakeComfyServer.ApiWorkflow
            .Replace("\"sampler_name\": \"euler\"", "\"sampler\": \"euler\"")
            .Replace("v1-5-pruned-emaonly-fp16.safetensors", "v1-5-pruned.safetensors")
            .Replace("\"SaveImage\"", "\"SaveImag\""));
        var warnings = new List<string>();
        var problems = wf.Validate(Library, warnings: warnings);
        Assert.Contains(warnings, p => p.Contains("no input \"sampler\"") && p.Contains("did you mean \"sampler_name\""));
        Assert.Contains(problems, p => p.Contains("required input \"sampler_name\" is missing"));
        Assert.Contains(problems, p => p.Contains("v1-5-pruned.safetensors") && p.Contains("isn't installed") && p.Contains("closest: v1-5-pruned-emaonly-fp16.safetensors"));
        Assert.Contains(problems, p => p.Contains("no node type \"SaveImag\"") && p.Contains("did you mean SaveImage"));
    }

    [Fact]
    public void Inputs_the_editor_adds_and_values_comfyui_converts_pass()
    {
        // The editor sends an audio player's "audioUI" and a node pack's format options; ComfyUI
        // ignores inputs a node doesn't declare, and turns true into 1 for a number.
        var wf = Load(FakeComfyServer.ApiWorkflow.Replace("\"filename_prefix\": \"fox\"", "\"filename_prefix\": \"fox\", \"audioUI\": \"\", \"crf\": 19")
            .Replace("\"batch_size\": 1", "\"batch_size\": true").Replace("\"steps\": 20", "\"steps\": \"20\""));
        var warnings = new List<string>();
        Assert.Empty(wf.Validate(Library, warnings: warnings));
        Assert.Empty(warnings);
    }

    [Fact]
    public void A_template_example_input_file_that_isnt_there_asks_for_a_file_of_ones_own()
    {
        var wf = Load("""{"1": {"class_type": "LoadImage", "inputs": {"image": "egyptian_queen.png"}}, "2": {"class_type": "SaveImage", "inputs": {"images": ["1", 0], "filename_prefix": "x"}}}""");
        Assert.Contains(wf.Validate(Library), p => p.Contains("isn't in ComfyUI's input folder") && p.Contains("full local path"));
        Assert.Contains(wf.UsualChanges(Library), c => c.StartsWith("1.image") && c.Contains("example isn't there"));
    }

    [Fact]
    public void A_shown_name_used_as_class_type_points_to_the_real_one()
    {
        var wf = Load("""{"1": {"class_type": "Load Checkpoint", "inputs": {}}}""");
        Assert.Contains(wf.Validate(Library), p => p.Contains("shown name of CheckpointLoaderSimple"));
    }

    [Fact]
    public void Links_to_missing_nodes_outputs_that_dont_exist_and_wrong_types_are_reported()
    {
        var wf = Load(FakeComfyServer.ApiWorkflow
            .Replace("\"latent_image\": [\"5\", 0]", "\"latent_image\": [\"55\", 0]")
            .Replace("\"vae\": [\"4\", 2]", "\"vae\": [\"4\", 5]")
            .Replace("\"model\": [\"4\", 0]", "\"model\": [\"4\", 1]"));
        var problems = wf.Validate(Library);
        Assert.Contains(problems, p => p.Contains("links to node \"55\""));
        Assert.Contains(problems, p => p.Contains("output 5 of node 4") && p.Contains("2 VAE"));
        Assert.Contains(problems, p => p.Contains("\"model\" needs MODEL") && p.Contains("is CLIP"));
    }

    [Fact]
    public void Numbers_out_of_range_and_values_where_links_belong_are_reported()
    {
        var wf = Load(FakeComfyServer.ApiWorkflow.Replace("\"width\": 512", "\"width\": 8").Replace("\"denoise\": 1", "\"denoise\": 1.5")
            .Replace("\"vae\": [\"4\", 2]", "\"vae\": \"vae.safetensors\""));
        var problems = wf.Validate(Library);
        Assert.Contains(problems, p => p.Contains("\"width\" is 8, below its least value 16"));
        Assert.Contains(problems, p => p.Contains("\"denoise\" is 1.5, above its greatest value 1"));
        Assert.Contains(problems, p => p.Contains("\"vae\" takes a link"));
    }

    [Fact]
    public void A_number_among_numeric_choices_is_one_of_them_as_comfyui_compares_numbers()
    {
        const string wf = """
            {"1": {"class_type": "LoadImage", "inputs": {"image": "example.png"}},
             "2": {"class_type": "RIFE VFI", "inputs": {"frames": ["1", 0], "scale_factor": SCALE}},
             "3": {"class_type": "SaveImage", "inputs": {"images": ["2", 0], "filename_prefix": "x"}}}
            """;
        Assert.Empty(Load(wf.Replace("SCALE", "1")).Validate(Library));
        Assert.Empty(Load(wf.Replace("SCALE", "0.50")).Validate(Library));
        Assert.Contains("isn't one of its choices", Assert.Single(Load(wf.Replace("SCALE", "3")).Validate(Library)));
    }

    [Fact]
    public void Inputs_the_editor_hides_get_their_defaults_and_others_stay_missing()
    {
        // ImageScaleToTotalPixels got resolution_steps in a later ComfyUI; a workflow written before is still run.
        var wf = Load("""
            {"1": {"class_type": "LoadImage", "inputs": {"image": "example.png"}},
             "2": {"class_type": "ImageScaleToTotalPixels", "inputs": {"image": ["1", 0], "upscale_method": "lanczos"}},
             "3": {"class_type": "SaveImage", "inputs": {"images": ["2", 0], "filename_prefix": "x"}}}
            """);
        Assert.Equal(2, wf.Validate(Library).Count(p => p.Contains("is missing")));
        Assert.Equal(["2.resolution_steps = 1"], wf.FillAdvancedDefaults(Library));
        // megapixels is in sight in the editor: leaving it out is the workflow's to fix.
        Assert.Contains("required input \"megapixels\" is missing", Assert.Single(wf.Validate(Library)));
    }

    [Fact]
    public void A_workflow_that_saves_nothing_is_told_to()
    {
        var wf = Load("""{"5": {"class_type": "EmptyLatentImage", "inputs": {"width": 512, "height": 512, "batch_size": 1}}}""");
        Assert.Contains(wf.Validate(Library), p => p.Contains("saves or shows"));
    }

    [Fact]
    public void Inputs_of_a_growing_v3_input_are_accepted()
    {
        var wf = Load("""{"1": {"class_type": "ComfyMathExpression", "inputs": {"expression": "a*2", "values.a": 3}}, "2": {"class_type": "SaveImage", "inputs": {"images": ["9", 0], "filename_prefix": "x"}}, "9": {"class_type": "LoadImage", "inputs": {"image": "example.png"}}}""");
        Assert.DoesNotContain(wf.Validate(Library), p => p.Contains("values.a"));
    }

    /// <summary>ComfyUI 0.41's SaveAudioAdvanced, and its SaveVideo cut down: choices whose options bring inputs of their own.</summary>
    private static readonly ComfyNodeLibrary OptionLibrary = ComfyNodeLibrary.Parse(JsonNode.Parse("""
        {
          "LoadAudio": {"input": {"required": {"audio": [["a.wav"], {"audio_upload": true}]}}, "output": ["AUDIO"], "output_name": ["AUDIO"],
            "name": "LoadAudio", "category": "audio", "python_module": "comfy_extras.nodes_audio", "output_node": false},
          "SaveAudioAdvanced": {"input": {"required": {"audio": ["AUDIO", {}], "filename_prefix": ["STRING", {"default": "audio/ComfyUI"}],
            "format": ["COMFY_DYNAMICCOMBO_V3", {"tooltip": "The file format in which to save the audio.", "options": [
              {"key": "flac", "inputs": {"required": {}}},
              {"key": "mp3", "inputs": {"required": {"quality": ["COMBO", {"default": "V0", "options": ["V0", "128k", "320k"]}]}}},
              {"key": "opus", "inputs": {"required": {"quality": ["COMBO", {"default": "128k", "options": ["64k", "96k", "128k", "192k", "320k"]}]}}}]}]}},
            "output": [], "output_name": [], "name": "SaveAudioAdvanced", "category": "audio", "python_module": "comfy_extras.nodes_audio", "output_node": true},
          "LoadVideo": {"input": {"required": {"file": [["v.mp4"], {"video_upload": true}]}}, "output": ["VIDEO"], "output_name": ["VIDEO"],
            "name": "LoadVideo", "category": "video", "python_module": "comfy_extras.nodes_video", "output_node": false},
          "SaveVideo": {"input": {"required": {"video": ["VIDEO", {}], "format": ["COMFY_DYNAMICCOMBO_V3", {"options": [
              {"key": "mp4", "inputs": {"required": {"codec": ["COMFY_DYNAMICCOMBO_V3", {"options": [
                {"key": "auto", "inputs": {"required": {}}},
                {"key": "h264", "inputs": {"required": {}, "optional": {"encoding": ["COMFY_DYNAMICCOMBO_V3", {"options": [
                  {"key": "auto", "inputs": {"required": {}}},
                  {"key": "re-encode", "inputs": {"required": {"crf": ["FLOAT", {"default": 18.0, "min": 0.0, "max": 51.0}]}}}]}]}}}]}]}}},
              {"key": "webm", "inputs": {"required": {}}}]}]}},
            "output": [], "output_name": [], "name": "SaveVideo", "category": "video", "python_module": "comfy_extras.nodes_video", "output_node": true}
        }
        """)!.AsObject());

    [Fact]
    public void A_chosen_option_brings_its_inputs_and_they_get_their_defaults()
    {
        // ComfyUI 0.41 refused SaveAudioAdvanced as mp3 without its quality, "format.quality".
        var wf = Load("""
            {"1": {"class_type": "LoadAudio", "inputs": {"audio": "a.wav"}},
             "2": {"class_type": "SaveAudioAdvanced", "inputs": {"audio": ["1", 0], "filename_prefix": "x", "format": "mp3"}}}
            """);
        Assert.Contains("required input \"format.quality\" is missing (one of V0, 128k, 320k)", Assert.Single(wf.Validate(OptionLibrary)));
        Assert.Equal(["2.format.quality = \"V0\""], wf.FillAdvancedDefaults(OptionLibrary));
        Assert.Empty(wf.Validate(OptionLibrary));
    }

    [Fact]
    public void A_chosen_options_inputs_are_checked_against_that_option()
    {
        const string wf = """
            {"1": {"class_type": "LoadAudio", "inputs": {"audio": "a.wav"}},
             "2": {"class_type": "SaveAudioAdvanced", "inputs": {"audio": ["1", 0], "filename_prefix": "x", "format": "FORMAT", "format.quality": "V0"}}}
            """;
        Assert.Empty(Load(wf.Replace("FORMAT", "mp3")).Validate(OptionLibrary));
        // V0 is an mp3 quality only.
        Assert.Contains("\"format.quality\" is \"V0\", which isn't one of its choices", Assert.Single(Load(wf.Replace("FORMAT", "opus")).Validate(OptionLibrary)));
        // flac takes no quality, and ComfyUI leaves it out.
        Assert.Empty(Load(wf.Replace("FORMAT", "flac")).Validate(OptionLibrary));
        // ComfyUI drops a format that isn't one, and the node fails without it.
        var wav = Assert.Single(Load(wf.Replace("FORMAT", "wav")).Validate(OptionLibrary));
        Assert.Contains("\"format\" is \"wav\", which isn't one of its choices", wav);
        Assert.EndsWith("Its choices: flac, mp3, opus.", wav);
    }

    [Fact]
    public void Options_chosen_in_a_chosen_option_bring_theirs_too()
    {
        var wf = Load("""
            {"1": {"class_type": "LoadVideo", "inputs": {"file": "v.mp4"}},
             "2": {"class_type": "SaveVideo", "inputs": {"video": ["1", 0], "format": "mp4"}}}
            """);
        // As the editor shows it: the first codec until another is chosen; the optional encoding stays out.
        Assert.Equal(["2.format.codec = \"auto\""], wf.FillAdvancedDefaults(OptionLibrary));
        Assert.Empty(wf.Validate(OptionLibrary));
        Assert.Empty(wf.Set(new JsonObject { ["2.format.codec"] = "h264", ["2.format.codec.encoding"] = "re-encode" }, OptionLibrary));
        Assert.StartsWith("2.format.codec.encoding.crf = 18", Assert.Single(wf.FillAdvancedDefaults(OptionLibrary)));
        Assert.Empty(wf.Validate(OptionLibrary));
        wf.Set(new JsonObject { ["2.format.codec.encoding.crf"] = 60 }, OptionLibrary);
        Assert.Contains("\"format.codec.encoding.crf\" is 60, above its greatest value 51", Assert.Single(wf.Validate(OptionLibrary)));
    }

    [Fact]
    public void A_required_choice_with_options_left_out_is_missing()
    {
        var wf = Load("""
            {"1": {"class_type": "LoadAudio", "inputs": {"audio": "a.wav"}},
             "2": {"class_type": "SaveAudioAdvanced", "inputs": {"audio": ["1", 0], "filename_prefix": "x"}}}
            """);
        Assert.Contains("required input \"format\" is missing (one of flac, mp3, opus", Assert.Single(wf.Validate(OptionLibrary)));
    }

    [Fact]
    public void A_nodes_details_list_the_inputs_each_option_brings()
    {
        var audio = OptionLibrary.Find("SaveAudioAdvanced")!.Details().ReplaceLineEndings("\n").Split('\n');
        Assert.Contains("  format: one of flac, mp3, opus — The file format in which to save the audio.", audio);
        Assert.Contains("    format.quality (when format is mp3): one of V0, 128k, 320k", audio);
        Assert.Contains("    format.quality (when format is opus): one of 64k, 96k, 128k, 192k, 320k", audio);
        var video = OptionLibrary.Find("SaveVideo")!.Details().ReplaceLineEndings("\n").Split('\n');
        Assert.Contains("    format.codec (when format is mp4): one of auto, h264", video);
        Assert.Contains("      format.codec.encoding (optional, when format.codec is h264): one of auto, re-encode", video);
        Assert.Contains(video, l => l.StartsWith("        format.codec.encoding.crf (when format.codec.encoding is re-encode): FLOAT, default 18", StringComparison.Ordinal));
    }

    /// <summary>ComfyUI 0.41's nodes for hearing a sound with a Gemma 4, cut down.</summary>
    private static ComfyNodeLibrary HearingLibrary(string models, bool audio = true) => ComfyNodeLibrary.Parse(JsonNode.Parse("""
        {
          "LoadAudio": {"input": {"required": {"audio": [["a.wav"], {"audio_upload": true}]}}, "output": ["AUDIO"], "output_name": ["AUDIO"],
            "name": "LoadAudio", "category": "audio", "python_module": "comfy_extras.nodes_audio", "output_node": false},
          "CLIPLoader": {"input": {"required": {"clip_name": [[MODELS], {}], "type": [["stable_diffusion", "wan", "ltxv"], {}]}, "optional": {"device": [["default", "cpu"], {"advanced": true}]}},
            "output": ["CLIP"], "output_name": ["CLIP"], "name": "CLIPLoader", "category": "loaders", "python_module": "nodes", "output_node": false},
          "TextGenerate": {"input": {"required": {"clip": ["CLIP", {}], "prompt": ["STRING", {"multiline": true, "default": ""}], "max_length": ["INT", {"default": 512, "min": 1, "max": 32768}],
              "sampling_mode": ["COMFY_DYNAMICCOMBO_V3", {"options": [{"key": "on", "inputs": {"required": {"temperature": ["FLOAT", {"default": 0.7, "min": 0.01, "max": 2.0}]}}}, {"key": "off", "inputs": {"required": {}}}]}]},
              "optional": {"AUDIO": ["AUDIO", {}], "image": ["IMAGE", {}]}},
            "output": ["STRING"], "output_name": ["generated_text"], "name": "TextGenerate", "category": "text", "python_module": "comfy_extras.nodes_textgen", "output_node": false},
          "PreviewAny": {"input": {"required": {"source": ["*", {}]}}, "output": ["STRING"], "output_name": ["STRING"], "name": "PreviewAny", "category": "utilities", "python_module": "comfy_extras.nodes_preview_any", "output_node": true}
        }
        """.Replace("MODELS", models).Replace("\"AUDIO\": [\"AUDIO\"", audio ? "\"audio\": [\"AUDIO\"" : "\"sound_unused\": [\"AUDIO\""))!.AsObject());

    [Fact]
    public void A_small_gemma_4_that_comfyui_has_hears_the_sound_of_what_a_job_made()
    {
        // LTX-2.5's templates bring gemma4_e2b; its 12B text encoder with the LTX projection is no use for it.
        var library = HearingLibrary("\"gemma4-12b-with-proj-ltx-2.5-comfy-int8-convrot.safetensors\", \"gemma-4-E4B-it.safetensors\", \"gemma4_e2b_it_int8_convrot.safetensors\", \"t5xxl_fp16.safetensors\"");
        var model = ComfyTool.HearingModel(library);
        Assert.Equal("gemma4_e2b_it_int8_convrot.safetensors", model);
        Assert.Equal("gemma-4-E4B-it.safetensors", ComfyTool.HearingModel(HearingLibrary("\"gemma-4-E4B-it.safetensors\", \"t5xxl_fp16.safetensors\"")));
        Assert.Null(ComfyTool.HearingModel(HearingLibrary("\"gemma4-12b-with-proj-ltx-2.5-comfy-int8-convrot.safetensors\", \"t5xxl_fp16.safetensors\"")));
        Assert.Null(ComfyTool.HearingModel(HearingLibrary("\"gemma4_e2b_it_int8_convrot.safetensors\"", audio: false)));

        var wf = ComfyTool.HearingWorkflow(library, model!, "video/fisherman_00001_.mp4 [output]");
        Assert.Empty(wf.FillAdvancedDefaults(library));
        Assert.Empty(wf.Validate(library));
        Assert.Equal("ltxv", (string?)wf.Nodes["2"]!["inputs"]!["type"]);
        Assert.Equal("video/fisherman_00001_.mp4 [output]", (string?)wf.Nodes["1"]!["inputs"]!["audio"]);
    }

    [Fact]
    public void Only_files_with_sound_in_the_output_folder_are_heard()
    {
        // Lines as MediaPreview writes them.
        Assert.True(ComfyTool.HasSound(@"C:\out\video\wave_00001_.mp4: video 704x1280, 121 frames at 24 fps (5.0 s), with sound (48 kHz stereo), 1.6 MB, its sound peak -4.8 dBFS, average -27 dBFS"));
        Assert.True(ComfyTool.HasSound(@"C:\out\Anton_greeting_00001.flac: sound 1.9 s, 48 kHz mono, peak -3.3 dBFS, average -21 dBFS, 93 KB"));
        Assert.False(ComfyTool.HasSound(@"C:\out\video\fox_00001_.mp4: video 832x480, 81 frames at 16 fps (5.1 s), no sound, 900 KB"));
        Assert.False(ComfyTool.HasSound(@"C:\out\quiet_00001_.flac: sound 3.0 s, 44.1 kHz stereo, silent throughout, 20 KB"));

        var dirs = new Dictionary<string, string> { ["output"] = @"C:\ComfyUI\output" };
        Assert.Equal("video/wave_00001_.mp4 [output]", ComfyTool.NameInComfy(new ComfyOutput("75", "wave_00001_.mp4", "video", "output", @"C:\ComfyUI\output\video\wave_00001_.mp4"), dirs));
        Assert.Equal("song.mp3 [output]", ComfyTool.NameInComfy(new ComfyOutput("9", "song.mp3", "", "output", @"C:\ComfyUI\output\song.mp3"), dirs));
        Assert.Null(ComfyTool.NameInComfy(new ComfyOutput("9", "preview.flac", "", "temp", @"C:\ComfyUI\temp\preview.flac"), dirs));
        // Files a node named in its text: by where they are.
        Assert.Equal("sfx/rain.wav [output]", ComfyTool.NameInComfy(new ComfyOutput("", "rain.wav", "", "output", @"C:\ComfyUI\output\sfx\rain.wav"), dirs));
        Assert.Null(ComfyTool.NameInComfy(new ComfyOutput("", "rain.wav", "", "output", @"D:\elsewhere\rain.wav"), dirs));
    }

    [Fact]
    public void ComfyUIs_complaint_about_an_options_input_names_it_in_full()
    {
        var errors = JsonNode.Parse("""
            {"28": {"class_type": "SaveAudioAdvanced", "errors": [
              {"type": "required_input_missing", "message": "Required input is missing", "details": "quality", "extra_info": {"input_name": "format.quality"}},
              {"type": "value_not_in_list", "message": "Value not in list", "details": "quality: 'V9' not in ['V0', '128k', '320k']", "extra_info": {"input_name": "format.quality"}},
              {"type": "required_input_missing", "message": "Required input is missing", "details": "audio", "extra_info": {"input_name": "audio"}}]}}
            """)!.AsObject();
        Assert.Equal([
            "node 28 SaveAudioAdvanced: Required input is missing: format.quality",
            "node 28 SaveAudioAdvanced: Value not in list: format.quality: quality: 'V9' not in ['V0', '128k', '320k']",
            "node 28 SaveAudioAdvanced: Required input is missing: audio"], ComfyRunner.NodeErrors(errors));
    }

    [Fact]
    public void Changes_go_in_by_node_and_input_including_ids_with_colons()
    {
        var wf = Load("""{"129:93": {"class_type": "CLIPTextEncode", "inputs": {"text": "old", "clip": ["4", 1]}}, "4": {"class_type": "CheckpointLoaderSimple", "inputs": {"ckpt_name": "sdxl.safetensors"}}}""");
        Assert.Empty(wf.Set(new JsonObject { ["129:93.text"] = "a cat", ["4"] = new JsonObject { ["ckpt_name"] = "v1-5-pruned-emaonly-fp16.safetensors" } }, Library));
        Assert.Equal("a cat", (string?)wf.Nodes["129:93"]!["inputs"]!["text"]);
        Assert.Equal("v1-5-pruned-emaonly-fp16.safetensors", (string?)wf.Nodes["4"]!["inputs"]!["ckpt_name"]);

        var problems = wf.Set(new JsonObject { ["129:39.text"] = "x", ["129:93.txt"] = "y", ["text"] = "z" }, Library);
        Assert.Contains(problems, p => p.Contains("no node \"129:39\"") && p.Contains("129:93"));
        Assert.Contains(problems, p => p.Contains("no input \"txt\"") && p.Contains("\"text\""));
        Assert.Contains(problems, p => p.Contains("node id.input name"));

        // A template's on/off switch set by its id alone: the node holds a single value.
        var lightning = Load("""
            {"433:443": {"class_type": "PrimitiveBoolean", "inputs": {"value": true}},
             "3": {"class_type": "KSampler", "inputs": {"seed": 1, "steps": 20, "model": ["4", 0]}}}
            """);
        Assert.Empty(lightning.Set(new JsonObject { ["433:443"] = false }, Library));
        Assert.False((bool)lightning.Nodes["433:443"]!["inputs"]!["value"]!);
        Assert.Contains(lightning.Set(new JsonObject { ["3"] = 7 }, Library), p => p.Contains("node id.input name"));
    }

    [Fact]
    public void Full_local_paths_in_file_inputs_are_found_for_upload()
    {
        var wf = Load("""{"1": {"class_type": "LoadImage", "inputs": {"image": "C:\\pics\\cat.png"}}, "2": {"class_type": "LoadImage", "inputs": {"image": "example.png"}}}""");
        var files = wf.LocalFiles(Library, p => p);
        Assert.Equal([("1", "image", @"C:\pics\cat.png")], files);
    }

    [Fact]
    public void The_listing_is_one_node_per_line_and_reads_back_as_the_same_workflow()
    {
        var wf = Load(FakeComfyServer.ApiWorkflow);
        var listing = wf.Listing();
        Assert.Equal(wf.Nodes.Count + 2, listing.Split('\n').Length);
        var back = Load(listing);
        Assert.Equal("Positive", ComfyWorkflow.TitleOf((JsonObject)back.Nodes["6"]!));
        Assert.Empty(back.Validate(Library));
        Assert.Contains("6.text (Positive)", wf.UsualChanges(Library));
        Assert.Contains(wf.UsualChanges(Library), c => c.StartsWith("3.seed"));
    }

    [Fact]
    public void Node_search_finds_by_name_shown_name_and_pack()
    {
        Assert.Equal("KSampler", Library.Search("sampler", 5)[0].Name);
        Assert.Equal("VHS_VideoCombine", Library.Search("video combine", 5)[0].Name);
        Assert.Contains("seed: INT, default 0", Library.Find("KSampler")!.Details());
        Assert.Contains("one of euler, euler_ancestral, dpmpp_2m", Library.Find("KSampler")!.Details());
        Assert.Contains("ComfyUI-VideoHelperSuite", Library.Find("VHS_VideoCombine")!.Signature());
    }

    [Fact]
    public void Node_search_finds_what_a_node_does_by_its_inputs()
    {
        // Finding only paid nodes for "background remove", qwen3.8 pip-installed rembg, while the
        // Trellis2 node in the workflow it was running had remove_background.
        var library = ComfyNodeLibrary.Parse(JsonNode.Parse("""
            {"Trellis2PreProcessImage": {"display_name": "Trellis2 - PreProcess Image", "category": "Trellis2Wrapper", "python_module": "custom_nodes.ComfyUI-Trellis2",
               "input": {"required": {"image": ["IMAGE"], "padding": ["INT", {"default": 0}], "remove_background": ["BOOLEAN", {"default": false}], "max_size": ["INT", {"default": 2048}]}},
               "output": ["IMAGE"], "output_name": ["image"]},
             "BriaRemoveImageBackground": {"display_name": "Bria Remove Image Background", "category": "api node/image/Bria", "api_node": true,
               "input": {"required": {"image": ["IMAGE"], "seed": ["INT", {"default": 0}]}}, "output": ["IMAGE"], "output_name": ["IMAGE"]},
             "LoadImage": {"display_name": "Load Image", "category": "image", "input": {"required": {"image": ["COMBO", {"options": []}]}}, "output": ["IMAGE", "MASK"], "output_name": ["IMAGE", "MASK"]}}
            """)!.AsObject());
        var found = library.Search("background remove", 5);
        Assert.Equal("Trellis2PreProcessImage", found[0].Name);
        Assert.Contains("remove_background", found[0].Signature());
        Assert.Equal("LoadImage", library.Search("load image", 5)[0].Name);
    }
}

public class ComfyTemplateTests
{
    private static readonly List<ComfyTemplate> All =
    [
        new("workflows/Video/Wan/video_wan2_2_14B_i2v", "video_wan2_2_14B_i2v", "one picture -> video.", "", "saved", ["Video", "Wan"]),
        new("templates/video_wan2_2_14B_i2v", "Wan 2.2 Image to Video", "Turn a still into motion.", "", "ComfyUI template", ["Video"]),
        new("templates/audio_stable_audio_example", "Stable Audio", "Text to sound effects.", "", "ComfyUI template", ["Audio"]),
        new("templates/image_z_image_turbo", "Z-Image-Turbo Text to Image", "Fast pictures.", "", "ComfyUI template", ["Image"]),
    ];

    [Fact]
    public void A_short_name_means_the_users_own_copy_over_comfyuis_original()
    {
        Assert.Equal("workflows/Video/Wan/video_wan2_2_14B_i2v", ComfyTemplates.Resolve(All, "video_wan2_2_14B_i2v.json", out _)!.Name);
        Assert.Equal("templates/video_wan2_2_14B_i2v", ComfyTemplates.Resolve(All, "templates/video_wan2_2_14B_i2v", out _)!.Name);
        Assert.Null(ComfyTemplates.Resolve(All, "image_z_image_trubo", out var nearest));
        Assert.Contains("templates/image_z_image_turbo", nearest);
    }

    [Fact]
    public void Search_reads_through_little_words_synonyms_and_abbreviations()
    {
        Assert.StartsWith("workflows/", ComfyTemplates.Search(All, "a video from a photo", 3)[0].Name);
        Assert.Equal("templates/audio_stable_audio_example", ComfyTemplates.Search(All, "sound effect", 3)[0].Name);
        Assert.Equal("templates/image_z_image_turbo", ComfyTemplates.Search(All, "make a picture", 3)[0].Name);
    }

    private static readonly JsonNode Editor = JsonNode.Parse("""
    {
      "nodes": [
        {"id": 1, "type": "MarkdownNote", "mode": 0, "widgets_values": ["Use 4 steps."]},
        {"id": 2, "type": "Reroute", "mode": 0},
        {"id": 3, "type": "UNETLoader", "mode": 0, "widgets_values": ["wan.safetensors", "default"],
         "properties": {"models": [{"name": "wan.safetensors", "url": "https://example.com/wan.safetensors", "directory": "diffusion_models"}]}},
        {"id": 4, "type": "SomePackNode", "mode": 4},
        {"id": 5, "type": "11111111-2222-3333-4444-555555555555", "mode": 0}
      ],
      "links": [],
      "definitions": {"subgraphs": [{"id": "11111111-2222-3333-4444-555555555555", "nodes": [{"id": 9, "type": "MissingPackNode", "mode": 0}, {"id": 10, "type": "Note", "widgets_values": ["Inside."]}]}]}
    }
    """)!;

    [Fact]
    public void A_custom_node_that_writes_its_flags_as_text_or_numbers_is_read_too()
    {
        // "forceInput": "true" or "output_node": 1 made reading the flag throw, and with it the whole node list.
        var info = JsonNode.Parse("""
            {"OddNode": {"input": {"required": {"text": ["STRING", {"multiline": "true"}], "image": ["INT", {"forceInput": 1}]}},
                         "output": ["IMAGE"], "output_name": ["IMAGE"], "name": "OddNode", "display_name": "Odd", "category": "x", "output_node": "true"}}
            """)!.AsObject();
        var odd = ComfyNodeLibrary.Parse(info).Find("OddNode")!;
        Assert.True(odd.IsOutput);
        Assert.False(odd.Inputs.Single(i => i.Name == "image").IsWidget);
        Assert.True(odd.Inputs.Single(i => i.Name == "text").IsWidget);
    }

    [Fact]
    public void Notes_missing_node_types_and_missing_models_are_read_from_the_editor_format()
    {
        var library = ComfyNodeLibrary.Parse(JsonNode.Parse(FakeComfyServer.ObjectInfo)!.AsObject());
        Assert.Equal("Use 4 steps.\n\nInside.", ComfyTemplates.Notes(Editor, 500).ReplaceLineEndings("\n"));
        // UNETLoader isn't in the small library; a bypassed node and editor-only ones don't count.
        Assert.Equal(["UNETLoader", "MissingPackNode"], ComfyTemplates.MissingNodeTypes(Editor, library));
        var none = new Dictionary<string, HashSet<string>> { ["diffusion_models"] = [] };
        var need = Assert.Single(ComfyTemplates.Models(Editor, null, none));
        Assert.False(need.Installed);
        Assert.Equal("https://example.com/wan.safetensors", need.Url);
        var have = new Dictionary<string, HashSet<string>> { ["diffusion_models"] = ["Wan/wan.safetensors"] };
        Assert.True(Assert.Single(ComfyTemplates.Models(Editor, null, have)).Installed);
    }
}

public class ComfyToolTests : IDisposable
{
    private readonly FakeComfyServer _server = new();
    private readonly string _home = Directory.CreateTempSubdirectory("bb-comfy-home-").FullName;
    private readonly ComfyHub _hub;
    private readonly ToolContext _ctx;

    public ComfyToolTests()
    {
        var paths = new AppPaths(_home);
        Directory.CreateDirectory(paths.ComfyDir);
        File.WriteAllText(Path.Combine(paths.ComfyDir, "state.json"), new JsonObject { ["url"] = _server.Url.ToString() }.ToJsonString());
        _hub = ComfyHub.For(paths.ComfyDir);
        _ctx = new ToolContext
        {
            Platform = new FakePlatform(),
            Settings = new AgentSettings(),
            WorkingDirectory = _home,
            Comfy = _hub,
            ScratchDirectory = paths.ScratchDir,
        };
    }

    public void Dispose()
    {
        _server.Dispose();
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    private Task<ToolResult> Call(string json) => new ComfyTool().ExecuteAsync(JsonNode.Parse(json)!.AsObject(), _ctx, CancellationToken.None);

    [Fact]
    public async Task A_wrong_workflow_is_not_queued_and_says_how_to_fix_it()
    {
        var wf = FakeComfyServer.ApiWorkflow.Replace("\"SaveImage\"", "\"SaveImag\"");
        var result = await Call(new JsonObject { ["action"] = "run", ["workflow"] = JsonNode.Parse(wf) }.ToJsonString());
        Assert.True(result.IsError);
        Assert.Contains("did you mean SaveImage", result.Text);
        Assert.Empty(_server.Prompts);
    }

    [Fact]
    public async Task A_run_waits_for_the_job_and_hands_back_its_files_with_a_look_at_them()
    {
        var result = await Call(new JsonObject
        {
            ["action"] = "run",
            ["workflow"] = FakeComfyServer.ApiWorkflow, // as text, the way some models send it
            ["set"] = new JsonObject { ["6.text"] = "a red fox in snow", ["3.seed"] = 7 },
        }.ToJsonString());
        Assert.False(result.IsError, result.Text);
        var sent = Assert.Single(_server.Prompts);
        Assert.Equal("a red fox in snow", (string?)sent["prompt"]!["6"]!["inputs"]!["text"]);
        Assert.Equal(7, (int?)sent["prompt"]!["3"]!["inputs"]!["seed"]);
        var file = Assert.Single(result.Files!);
        Assert.StartsWith(_server.OutputDir, file);
        Assert.Contains("picture 64x48", result.Text);
        Assert.NotNull(result.Image);
        // The models are let go once the task is over.
        await _ctx.FinishRunAsync();
        Assert.Equal(1, _server.Frees);
    }

    [Fact]
    public async Task A_job_gone_from_comfyui_or_a_server_gone_is_said_so_instead_of_waited_on()
    {
        // Taken out of the queue, ComfyUI restarted or its process gone: it was waited on for the
        // whole 30 minutes, then said to be still running.
        var giveUp = ComfyRunner.GiveUpAfter;
        ComfyRunner.GiveUpAfter = 2;
        try
        {
            _server.Forgets = true;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var gone = await Call(new JsonObject { ["action"] = "run", ["workflow"] = JsonNode.Parse(FakeComfyServer.ApiWorkflow) }.ToJsonString());
            Assert.Contains("isn't in ComfyUI's queue or history any more", gone.Text);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"took {clock.Elapsed}");

            // ComfyUI's process gone in the middle: nothing answers any more.
            var api = new ComfyApi(_server.Url);
            _server.Dispose();
            var job = await ComfyRunner.WaitAsync(api, "job9", "client", new Dictionary<string, string>(), TimeSpan.FromMinutes(30), null, CancellationToken.None, TestContext.Current.CancellationToken);
            Assert.Equal("error", job.Status);
            Assert.Contains("ComfyUI stopped answering", job.Error);
        }
        finally
        {
            ComfyRunner.GiveUpAfter = giveUp;
        }
    }

    // Asked for a video with the sound of wind, qwen3.8 searched ComfyUI's folder for ffmpeg.exe for three
    // minutes to put the sound on it.
    [Fact]
    public async Task A_sound_or_video_made_comes_with_where_ffmpeg_is()
    {
        File.WriteAllBytes(Path.Combine(_server.OutputDir, "wind_00001_.mp3"), [0, 1, 2]);
        _server.History = _ => JsonNode.Parse("""
            {"status": {"status_str": "success", "completed": true, "messages": []},
             "outputs": {"9": {"audio": [{"filename": "wind_00001_.mp3", "subfolder": "", "type": "output"}]}}}
            """)!.AsObject();
        var ctx = new ToolContext
        {
            Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = _home, Comfy = _hub,
            ScratchDirectory = _ctx.ScratchDirectory, Media = new FfmpegOnly(@"C:	oolsfmpeg.exe"),
        };

        var sound = await new ComfyTool().ExecuteAsync(JsonNode.Parse("""{"action": "run", "name": "Mine/fox"}""")!.AsObject(), ctx, CancellationToken.None);

        Assert.False(sound.IsError, sound.Text);
        Assert.Contains(@"To cut, join or put a sound on these: C:	oolsfmpeg.exe", sound.Text);
        // A picture needs no ffmpeg.
        _server.History = null;
        var picture = await new ComfyTool().ExecuteAsync(JsonNode.Parse("""{"action": "run", "name": "Mine/fox"}""")!.AsObject(), ctx, CancellationToken.None);
        Assert.DoesNotContain("ffmpeg", picture.Text);
    }

    private sealed class FfmpegOnly(string program) : BlackBox.Core.Media.IMediaDecoder
    {
        public BlackBox.Core.Media.MediaInfo? Probe(string path) => null;
        public BlackBox.Core.Media.IVideoReader? OpenVideo(string path, int maxSide) => null;
        public BlackBox.Core.Media.IAudioReader? OpenAudio(string path) => null;
        public string? Ffmpeg => program;
    }

    [Fact]
    public async Task A_failed_job_says_where_and_why_with_a_hint_for_running_out_of_memory()
    {
        _server.History = _ => JsonNode.Parse("""
            {"status": {"status_str": "error", "completed": true, "messages": [["execution_error", {"node_id": "3", "node_type": "KSampler",
              "exception_type": "torch.OutOfMemoryError", "exception_message": "CUDA out of memory. Tried to allocate 2.00 GiB", "traceback": ["  File x.py\n"]}]]},
             "outputs": {}}
            """)!.AsObject();
        var result = await Call("""{"action": "run", "name": "Mine/fox"}""");
        Assert.True(result.IsError);
        Assert.Contains("Node 3 KSampler failed: OutOfMemoryError: CUDA out of memory", result.Text);
        Assert.Contains("make the picture or video smaller", result.Text);
    }

    [Fact]
    public async Task A_file_a_node_names_in_its_text_is_a_result_too()
    {
        // Trellis2's mesh export gives its .glb's path as text rather than listing the file.
        Directory.CreateDirectory(Path.Combine(_server.OutputDir, "3D"));
        File.WriteAllText(Path.Combine(_server.OutputDir, "3D", "chest_00001_.obj"), "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");
        _server.History = _ => JsonNode.Parse("""
            {"status": {"status_str": "success", "completed": true, "messages": []},
             "outputs": {"5": {"text": ["3D/chest_00001_.obj"]}}}
            """)!.AsObject();
        var result = await Call("""{"action": "run", "name": "Mine/fox"}""");
        Assert.False(result.IsError, result.Text);
        Assert.EndsWith(Path.Combine("3D", "chest_00001_.obj"), Assert.Single(result.Files!));
        Assert.Contains("3D model: 1 triangles", result.Text);
    }

    [Fact]
    public async Task A_comfyui_blackbox_started_closes_after_the_task_once_its_jobs_are_done()
    {
        var stops = 0;
        _hub.OwnsForTests = _ => stops == 0;
        _hub.StopForTests = (_, _) => { stops++; return Task.CompletedTask; };
        _server.QueueBusy = true;
        await Call("""{"action": "status"}""");
        await _ctx.FinishRunAsync();
        Assert.Equal(0, stops);
        Assert.False(_hub.Closing.IsCompleted);

        // A task that starts using it meanwhile keeps it open; the end of that one closes it.
        var next = new ComfyTool();
        await next.ExecuteAsync(JsonNode.Parse("""{"action": "status"}""")!.AsObject(), _ctx, CancellationToken.None);
        _server.QueueBusy = false;
        await Task.Delay(6000, TestContext.Current.CancellationToken);
        Assert.Equal(0, stops);
        await _ctx.FinishRunAsync();
        await _hub.Closing.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(1, stops);
        Assert.Equal(0, _server.Frees);
    }

    [Fact]
    public async Task A_comfyui_an_earlier_blackbox_left_running_is_closed_once_its_jobs_are_done()
    {
        // BlackBox closed while its jobs were still queued left it running, hidden, until a later
        // task that used ComfyUI ended.
        var stops = 0;
        _hub.OwnsForTests = _ => stops == 0;
        _hub.StopForTests = (_, _) => { stops++; return Task.CompletedTask; };
        _hub.OthersRunForTests = () => false;
        _server.QueueBusy = true;
        _hub.CloseLeftover();
        await Task.Delay(1000, TestContext.Current.CancellationToken);
        Assert.Equal(0, stops);
        Assert.False(_hub.Closing.IsCompleted);

        _server.QueueBusy = false;
        await _hub.Closing.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(1, stops);
    }

    [Fact]
    public async Task A_comfyui_is_left_running_at_start_while_another_blackbox_runs()
    {
        // A second window, or the command line, may be between two of its jobs.
        var stops = 0;
        _hub.OwnsForTests = _ => true;
        _hub.StopForTests = (_, _) => { stops++; return Task.CompletedTask; };
        _hub.OthersRunForTests = () => true;
        _hub.CloseLeftover();
        await _hub.Closing.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(0, stops);
    }

    [Fact]
    public async Task A_masked_repaint_without_differential_diffusion_is_told_how_to_lose_the_edge()
    {
        // An outpainted panorama met the photo with a hard edge on both sides, and was called natural.
        var masked = ComfyWorkflow.FromApi(ComfyWorkflow.ParseText("""
            {"1": {"class_type": "CheckpointLoaderSimple", "inputs": {"ckpt_name": "flux1-dev-fp8.safetensors"}},
             "2": {"class_type": "LoadImage", "inputs": {"image": "waterfall.jpg"}},
             "3": {"class_type": "ImagePadForOutpaint", "inputs": {"image": ["2", 0], "left": 640, "top": 0, "right": 640, "bottom": 0, "feathering": 40}},
             "4": {"class_type": "VAEEncode", "inputs": {"pixels": ["3", 0], "vae": ["1", 2]}},
             "5": {"class_type": "SetLatentNoiseMask", "inputs": {"samples": ["4", 0], "mask": ["3", 1]}},
             "9": {"class_type": "KSampler", "inputs": {"model": ["1", 0], "latent_image": ["5", 0]}}}
            """));
        Assert.Contains("DifferentialDiffusion", ComfyTool.MaskNote(masked));
        masked.Nodes["12"] = new JsonObject { ["class_type"] = "DifferentialDiffusion", ["inputs"] = new JsonObject { ["model"] = new JsonArray("1", 0) } };
        Assert.Null(ComfyTool.MaskNote(masked));
        Assert.Null(ComfyTool.MaskNote(Load(FakeComfyServer.ApiWorkflow)));
        // LTX's picture-and-sound-to-video keeps the given sound through a mask of one value: no edge to tell of.
        Assert.Null(ComfyTool.MaskNote(ComfyWorkflow.FromApi(ComfyWorkflow.ParseText("""
            {"1": {"class_type": "LoadAudio", "inputs": {"audio": "voice.flac"}},
             "2": {"class_type": "LTXVAudioVAEEncode", "inputs": {"audio": ["1", 0]}},
             "3": {"class_type": "SolidMask", "inputs": {"value": 0, "width": 1024, "height": 1024}},
             "4": {"class_type": "SetLatentNoiseMask", "inputs": {"samples": ["2", 0], "mask": ["3", 0]}}}
            """))));

        // Asked about the nodes before the first run, it hears it then.
        var nodes = await Call("""{"action": "nodes", "names": ["ImagePadForOutpaint", "KSampler"]}""");
        Assert.Contains("unless the model goes through DifferentialDiffusion", nodes.Text);
        nodes = await Call("""{"action": "nodes", "names": ["KSampler"]}""");
        Assert.DoesNotContain("DifferentialDiffusion", nodes.Text);
    }

    private static ComfyWorkflow Load(string json) => ComfyWorkflow.FromApi(ComfyWorkflow.ParseText(json));

    [Fact]
    public async Task A_job_the_users_message_made_pointless_is_cancelled_alone()
    {
        // Told "and more furry" while a picture was being made, qwen3.8 stopped the whole server.
        var result = await Call("""{"action": "cancel", "prompt_id": "job7"}""");
        Assert.False(result.IsError, result.Text);
        Assert.Contains("Job job7 is stopped and out of the queue. ComfyUI keeps running", result.Text);
        Assert.Contains("\"job7\"", Assert.Single(_server.Interrupts));
        await Assert.ThrowsAsync<ToolArgumentException>(() => Call("""{"action": "cancel"}"""));

        // A run the user wrote during says how.
        _ctx.UserMessage = new CancellationToken(true);
        var going = await Call(new JsonObject { ["action"] = "run", ["workflow"] = JsonNode.Parse(FakeComfyServer.ApiWorkflow) }.ToJsonString());
        Assert.Contains("The user wrote to you meanwhile", going.Text);
        Assert.Contains("stop it with action=cancel prompt_id=job1 (not action=stop, which stops ComfyUI)", going.Text);
    }

    [Fact]
    public async Task Status_names_the_python_of_a_portable_comfyui_for_cropping_pictures()
    {
        // A crop of a reference sheet guessed at python\python.exe and searched the disk first.
        var result = await Call("""{"action": "status"}""");
        Assert.DoesNotContain("Its Python", result.Text);

        File.WriteAllText(Path.Combine(_server.CodeDir, "main.py"), "");
        File.WriteAllText(Path.Combine(_server.CodeDir, "nodes.py"), "");
        Directory.CreateDirectory(Path.Combine(_server.CodeDir, "comfy"));
        var python = Path.Combine(_server.Root, "python_embeded", "python.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(python)!);
        File.WriteAllText(python, "");
        result = await Call("""{"action": "status"}""");
        Assert.Contains($"Its Python has Pillow and numpy, for working on the pictures and models it makes: {python}.", result.Text);

        // To cut a character out for a 3D model, rembg was pip-installed into the PC's own Python, and trimesh to look into the model.
        var site = Path.Combine(_server.Root, "python_embeded", "Lib", "site-packages");
        Directory.CreateDirectory(Path.Combine(site, "rembg"));
        Directory.CreateDirectory(Path.Combine(site, "trimesh"));
        result = await Call("""{"action": "status"}""");
        // rembg can't run without onnxruntime, which this ComfyUI's Python hadn't got.
        Assert.Contains("rembg (cuts out a background once onnxruntime is installed into this Python;", result.Text);
        Directory.CreateDirectory(Path.Combine(site, "onnxruntime"));
        result = await Call("""{"action": "status"}""");
        Assert.Contains("Its Python has Pillow, numpy, rembg (cuts out a background) and trimesh (3D models), for working", result.Text);

        // A task that never asks for the status hears it with its first call, once: a walk cycle run
        // pip-installed Pillow into the PC's own Python.
        var tool = new ComfyTool();
        var first = await tool.ExecuteAsync(JsonNode.Parse("""{"action": "templates", "search": "fox"}""")!.AsObject(), _ctx, CancellationToken.None);
        Assert.Contains("Its Python has Pillow", first.Text);
        var second = await tool.ExecuteAsync(JsonNode.Parse("""{"action": "templates", "search": "fox"}""")!.AsObject(), _ctx, CancellationToken.None);
        Assert.DoesNotContain("Its Python has", second.Text);
    }

    [Fact]
    public async Task A_comfyui_the_user_started_keeps_running_and_lets_go_only_of_models_a_job_loaded()
    {
        var stops = 0;
        _hub.OwnsForTests = _ => false;
        _hub.StopForTests = (_, _) => { stops++; return Task.CompletedTask; };
        await Call("""{"action": "status"}""");
        await _ctx.FinishRunAsync();
        Assert.Equal(0, _server.Frees);

        await Call("""{"action": "run", "name": "Mine/fox"}""");
        await _ctx.FinishRunAsync();
        Assert.Equal(1, _server.Frees);
        Assert.Equal(0, stops);
    }

    [Fact]
    public async Task A_change_to_a_node_the_template_hasnt_got_comes_back_with_the_inputs_it_has()
    {
        var result = await Call("""{"action": "run", "name": "Mine/fox", "set": {"66.text": "a red fox"}}""");
        Assert.True(result.IsError);
        Assert.Contains("there is no node \"66\"", result.Text);
        Assert.Contains("usually changes: 6.text (Positive), 7.text (text)", result.Text);
        Assert.Empty(_server.Prompts);
    }

    [Fact]
    public async Task A_rerun_of_a_template_without_changes_the_last_run_made_runs_with_a_note()
    {
        var tool = new ComfyTool();
        Task<ToolResult> Run(string json) => tool.ExecuteAsync(JsonNode.Parse(json)!.AsObject(), _ctx, CancellationToken.None);
        var first = await Run("""{"action": "run", "name": "Mine/fox", "set": {"6.text": "a fox", "5": {"width": 768, "height": 768}, "3.seed": 1}}""");
        Assert.False(first.IsError, first.Text);

        // A new seed, and the size forgotten: it runs as asked (a guideline, not a refusal), and says so.
        var second = await Run("""{"action": "run", "name": "Mine/fox", "set": {"6.text": "a fox", "3.seed": 2}}""");
        Assert.False(second.IsError, second.Text);
        Assert.Contains("also set 5.width (768), 5.height (768)", second.Text);
        Assert.Contains("they have the template's values (512, 512)", second.Text);
        Assert.Equal(512, (int?)_server.Prompts[1]["prompt"]!["5"]!["inputs"]!["width"]);

        var third = await Run("""{"action": "run", "name": "Mine/fox", "set": {"6.text": "a fox", "3.seed": 3}}""");
        Assert.DoesNotContain("also set", third.Text);
    }

    [Fact]
    public async Task A_template_search_names_the_installed_loras_and_checkpoints_that_match_it()
    {
        // Finding no pixel art template, qwen3.8 drew a 32x32 knight by hand with Pixel Art XL installed.
        var models = new Dictionary<string, HashSet<string>>
        {
            ["checkpoints"] = ["juggernautXL_ragnarok.safetensors", "novaFurryXL_ilV170.safetensors"],
            ["loras"] = ["pixel-art-xl-v1.1.safetensors", "Qwen-Edit-2509-Multiple-angles.safetensors", "animeflatLTX.2.3.safetensors"],
            ["vae"] = ["pixel_vae.safetensors"],
        };
        Assert.Equal(["loras/pixel-art-xl-v1.1.safetensors"], ComfyTool.MatchingModels("pixel art sprite of a knight", models));
        Assert.Equal(["loras/Qwen-Edit-2509-Multiple-angles.safetensors"], ComfyTool.MatchingModels("character angles", models));
        Assert.Equal(["checkpoints/novaFurryXL_ilV170.safetensors"], ComfyTool.MatchingModels("furry picture", models));
        Assert.Empty(ComfyTool.MatchingModels("make a picture with a model", models));

        _server.Loras.Add("pixel-art-xl-v1.1.safetensors");
        var list = await Call("""{"action": "templates", "search": "pixel art"}""");
        Assert.Contains("Installed models whose names match: loras/pixel-art-xl-v1.1.safetensors.", list.Text);
    }

    [Fact]
    public async Task A_search_for_a_seamless_texture_says_to_roll_it_and_repaint_the_seam_cross()
    {
        // A cross-fade of the edges tiled with doubled, blurred stones along the joins.
        var list = await Call("""{"action": "templates", "search": "tileable texture seamless"}""");
        Assert.Contains("roll it by half so its edges meet in a cross in the middle (numpy's roll in ComfyUI's Python", list.Text);
        Assert.Contains("the model through DifferentialDiffusion, denoise about 0.75", list.Text);
        // A tileset's grass came out lighter in the middle: a grid of light squares when tiled.
        Assert.Contains("flat, even light with no vignette", list.Text);
        Assert.DoesNotContain("roll it by half", (await Call("""{"action": "templates", "search": "fox"}""")).Text);

        var withPack = ComfyNodeLibrary.Parse(JsonNode.Parse("""
            {"TextureOffset": {"display_name": "Texture Offset", "category": "Texture Alchemist/Texture",
               "input": {"required": {"image": ["IMAGE"], "offset_x": ["FLOAT", {"default": 0.0}], "offset_y": ["FLOAT", {"default": 0.0}]}},
               "output": ["IMAGE", "IMAGE"], "output_name": ["image", "edge_mask"]}}
            """)!.AsObject());
        Assert.Contains("TextureOffset with offset_x and offset_y 0.5", ComfyTool.SeamlessAdvice(withPack));
    }

    [Fact]
    public async Task A_search_for_outpainting_with_no_ready_template_says_how_to_do_it_with_installed_models()
    {
        // One panorama run left seams, the next downloaded Flux Fill and its text encoder, 32 GB,
        // and two of three drew the photo's waterfall again on a side.
        var list = await Call("""{"action": "templates", "search": "outpaint extend image"}""");
        Assert.Contains("No outpaint template here is ready.", list.Text);
        Assert.Contains("pad the picture (ImagePadForOutpaint) and repaint the padding", list.Text);
        Assert.Contains("DifferentialDiffusion", list.Text);
        Assert.Contains("describe in the prompt what the new parts should hold", list.Text);
        list = await Call("""{"action": "templates", "search": "fox"}""");
        Assert.DoesNotContain("outpaint", list.Text);
    }

    [Fact]
    public async Task Templates_are_listed_with_the_users_notes_and_shown_as_a_workflow_saved_to_edit()
    {
        var list = await Call("""{"action": "templates", "search": "fox"}""");
        Assert.Contains("workflows/Mine/fox", list.Text);
        Assert.Contains("a fox in the snow, quickly", list.Text);
        Assert.DoesNotContain("api_paid", list.Text);

        var shown = await Call("""{"action": "templates", "name": "fox"}""");
        Assert.False(shown.IsError, shown.Text);
        Assert.Contains("\"9\": {\"class_type\":\"SaveImage\"", shown.Text);
        Assert.Contains("Models: all 1 installed.", shown.Text);
        var saved = Path.Combine(_home, "Data", "scratch", "comfyui", "fox.json");
        Assert.True(File.Exists(saved));

        // The saved file runs as it is.
        var run = await Call(new JsonObject { ["action"] = "run", ["workflow_file"] = saved }.ToJsonString());
        Assert.False(run.IsError, run.Text);
    }

    [Fact]
    public async Task Nodes_and_models_are_looked_up_on_the_server()
    {
        var nodes = await Call("""{"action": "nodes", "names": ["KSampler", "KSamplr"]}""");
        Assert.Contains("sampler_name: one of euler", nodes.Text);
        Assert.Contains("There is no node type \"KSamplr\"; closest: KSampler", nodes.Text);
        var models = await Call("""{"action": "models", "search": "sdxl"}""");
        Assert.Contains("sdxl.safetensors", models.Text);
        Assert.DoesNotContain("v1-5", models.Text);
    }

    [Fact]
    public async Task A_model_that_turned_up_gets_the_node_types_fetched_again()
    {
        await Call("""{"action": "models"}""");
        await Call("""{"action": "nodes", "names": ["CheckpointLoaderSimple"]}""");
        var fetches = _server.NodeTypeFetches;
        await Call("""{"action": "models"}""");
        await Call("""{"action": "nodes", "names": ["CheckpointLoaderSimple"]}""");
        Assert.Equal(fetches, _server.NodeTypeFetches);

        // The agent downloaded one: its loader's choices may have changed too.
        _server.Checkpoints.Add("flux.safetensors");
        await Call("""{"action": "models"}""");
        await Call("""{"action": "nodes", "names": ["CheckpointLoaderSimple"]}""");
        Assert.Equal(fetches + 1, _server.NodeTypeFetches);
    }

    [Fact]
    public async Task A_local_input_file_is_uploaded_and_named_as_comfyui_knows_it()
    {
        var picture = Path.Combine(_home, "cat.png");
        File.WriteAllBytes(picture, [137, 80, 78, 71]);
        var wf = """{"1": {"class_type": "LoadImage", "inputs": {"image": "PATH"}}, "2": {"class_type": "SaveImage", "inputs": {"images": ["1", 0], "filename_prefix": "x"}}}"""
            .Replace("PATH", picture.Replace("\\", "\\\\"));
        var result = await Call(new JsonObject { ["action"] = "run", ["workflow"] = JsonNode.Parse(wf) }.ToJsonString());
        Assert.False(result.IsError, result.Text);
        Assert.Single(_server.Uploads);
        Assert.Equal("uploaded.png", (string?)_server.Prompts[0]["prompt"]!["1"]!["inputs"]!["image"]);
    }

    [Fact]
    public async Task A_3d_model_from_one_picture_comes_with_how_to_give_it_more_sides()
    {
        // From the front picture of the user's character Trellis2 made a relief on a wall; four views
        // through Hunyuan3D's multi-view template made a whole figure.
        var picture = Path.Combine(_home, "bunny.png");
        File.WriteAllBytes(picture, [137, 80, 78, 71]);
        Directory.CreateDirectory(Path.Combine(_server.OutputDir, "3D"));
        File.WriteAllText(Path.Combine(_server.OutputDir, "3D", "bunny_00001_.obj"), "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");
        _server.History = _ => JsonNode.Parse("""
            {"status": {"status_str": "success", "completed": true, "messages": []}, "outputs": {"2": {"text": ["3D/bunny_00001_.obj"]}}}
            """)!.AsObject();
        var wf = """{"1": {"class_type": "LoadImage", "inputs": {"image": "PATH"}}, "2": {"class_type": "SaveImage", "inputs": {"images": ["1", 0], "filename_prefix": "x"}}}"""
            .Replace("PATH", picture.Replace("\\", "\\\\"));
        var run = new JsonObject { ["action"] = "run", ["workflow"] = JsonNode.Parse(wf) };
        var tool = new ComfyTool();
        var result = await tool.ExecuteAsync(run.DeepClone().AsObject(), _ctx, CancellationToken.None);
        Assert.Contains("a 3D model made from one picture guesses the sides it can't see", result.Text);
        // Once in a task.
        Assert.DoesNotContain("guesses the sides", (await tool.ExecuteAsync(run.DeepClone().AsObject(), _ctx, CancellationToken.None)).Text);

        var note = ComfyTool.ViewsNote([
            new ComfyTemplate("workflows/Mesh/3d_hunyuan3d_multiview_to_model", "", "", "", "user", []),
            new ComfyTemplate("workflows/ImageRotate/templates-1_click_multiple_character_angles-v1.0", "", "", "", "user", []),
            new ComfyTemplate("workflows/Image/nova-furry-v17", "", "", "", "user", [])]);
        Assert.Contains("a multi-view template (workflows/Mesh/3d_hunyuan3d_multiview_to_model)", note);
        Assert.Contains("(workflows/ImageRotate/templates-1_click_multiple_character_angles-v1.0 turns the camera;", note);
        Assert.Contains("rotate the camera 90 degrees left and right", ComfyTool.ViewsNote([]));
    }
}

public class MediaTests
{
    [Fact]
    public void File_paths_in_a_reply_are_found_with_spaces_and_without_the_punctuation_around_them()
    {
        var dir = Directory.CreateTempSubdirectory("bb mentions ").FullName;
        try
        {
            var a = Path.Combine(dir, "my picture.png");
            var b = Path.Combine(dir, "song.mp3");
            File.WriteAllText(a, "x");
            File.WriteAllText(b, "x");
            var text = $"Here it is: **{a}** (1024×1024).\nAnd `{b}`, plus {b} again and {Path.Combine(dir, "missing.png")}.\nAlso file:///{a.Replace('\\', '/').Replace(" ", "%20")}";
            Assert.Equal([a, b], FileMentions.Find(text));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_long_line_full_of_paths_that_are_not_there_is_searched_in_a_moment()
    {
        // Minified JSON: every word end of the line was a look at the disk, for every path in it.
        var line = string.Concat(Enumerable.Repeat(@"{""file"": ""C:\no such folder\made up name of a thing.txt"", ""size"": 12} ", 2000));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.Empty(FileMentions.Find(line));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
    }

    [Fact]
    public void Sound_stats_tell_loudness_and_where_it_is_silent()
    {
        const int rate = 1000;
        var samples = new float[rate * 6];
        for (var i = rate; i < rate * 3; i++) samples[i] = (float)(0.5 * Math.Sin(i * 0.3)); // 1 s silence, 2 s tone,
        for (var i = rate * 4; i < rate * 5; i++) samples[i] = (float)(0.5 * Math.Sin(i * 0.3)); // a 1 s gap, 1 s tone, 1 s silence
        var text = SoundStats.Of(samples, rate).Describe();
        Assert.Contains("peak -6.0 dBFS", text);
        Assert.Contains("silent for the first 1.0 s", text);
        Assert.Contains("silent for the last 1.0 s", text);
        Assert.Contains("silent gaps at 3.0–4.0 s", text);
        Assert.Equal("silent throughout", SoundStats.Of(new float[rate], rate).Describe());
    }

    [Fact]
    public void Ffmpeg_output_tells_what_a_file_holds()
    {
        var info = FfmpegDecoder.ParseProbe("""
            Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'a.mp4':
              Duration: 00:00:05.06, start: 0.000000, bitrate: 1234 kb/s
              Stream #0:0[0x1](und): Video: h264 (High) (avc1 / 0x31637661), yuv420p(progressive), 640x480 [SAR 1:1 DAR 4:3], 1100 kb/s, 16 fps, 16 tbr, 16384 tbn (default)
              Stream #0:1[0x2](und): Audio: aac (LC) (mp4a / 0x6134706D), 44100 Hz, stereo, fltp, 128 kb/s (default)
            """)!;
        Assert.Equal((640, 480, 16.0, 44100, 2), (info.Width, info.Height, info.FrameRate, info.SampleRate, info.Channels));
        Assert.Equal(5.06, info.Duration.TotalSeconds, 2);
        Assert.Equal("video 640x480, 81 frames at 16 fps (5.1 s), with sound (44.1 kHz stereo)", info.Describe());
    }

    [Fact]
    public void A_job_that_ran_out_of_gpu_memory_gets_a_hint()
    {
        var text = ComfyRunner.DescribeError(JsonNode.Parse("""{"node_id": "5", "node_type": "VAEDecode", "exception_type": "torch.OutOfMemoryError", "exception_message": "CUDA out of memory"}""")!.AsObject());
        Assert.StartsWith("Node 5 VAEDecode failed: OutOfMemoryError: CUDA out of memory", text);
        Assert.Contains("GPU ran out of memory", text);
    }
}

public class MeshTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-mesh-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void Obj_faces_become_triangles_with_their_material_colour()
    {
        Write("box.mtl", "newmtl red\nKd 1 0 0\n");
        var mesh = Mesh.Load(Write("box.obj", "mtllib box.mtl\nv 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\nusemtl red\nf 1 2 3 4\nf -4 -2 -1\n"));
        Assert.Equal(3, mesh.TriangleCount);
        Assert.Equal(new SkiaSharp.SKColor(255, 0, 0), mesh.Materials[mesh.TriangleMaterials[0]].Color);
        Assert.Contains("3 triangles", mesh.Describe());
    }

    [Fact]
    public void Ascii_and_binary_stl_and_ply_with_vertex_colours_are_read()
    {
        var ascii = Mesh.Load(Write("t.stl", "solid t\nfacet normal 0 0 1\nouter loop\nvertex 0 0 0\nvertex 1 0 0\nvertex 0 1 0\nendloop\nendfacet\nendsolid\n"));
        Assert.Equal(1, ascii.TriangleCount);

        var binary = new byte[84 + 50];
        BitConverter.GetBytes(1u).CopyTo(binary, 80);
        foreach (var (v, i) in new[] { (1f, 0), (1f, 4), (0f, 8) }.Select((x, n) => (x.Item1, 84 + 12 + n * 12 + x.Item2)))
            BitConverter.GetBytes(v).CopyTo(binary, i);
        var path = Path.Combine(_dir, "b.stl");
        File.WriteAllBytes(path, binary);
        Assert.Equal(1, Mesh.Load(path).TriangleCount);

        var ply = Mesh.Load(Write("c.ply", "ply\nformat ascii 1.0\nelement vertex 3\nproperty float x\nproperty float y\nproperty float z\nproperty uchar red\nproperty uchar green\nproperty uchar blue\nelement face 1\nproperty list uchar int vertex_indices\nend_header\n0 0 0 255 0 0\n1 0 0 0 255 0\n0 1 0 0 0 255\n3 0 1 2\n"));
        Assert.Equal(1, ply.TriangleCount);
        Assert.True(ply.HasVertexColors);
        Assert.Equal(new SkiaSharp.SKColor(0, 255, 0), ply.Colors[1]);
    }

    [Fact]
    public void A_gltf_whose_nodes_loop_back_is_read_in_a_moment()
    {
        // Each node its own two children: within the depth limit of 64, 2^64 visits.
        var path = Write("loop.gltf", """
            {"asset": {"version": "2.0"}, "nodes": [{"children": [0, 0, 7, -1]}], "meshes": []}
            """);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var mesh = Mesh.Load(path);
        Assert.Equal(0, mesh.TriangleCount);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
    }

    [Fact]
    public void A_glb_is_read_with_its_node_transform_and_drawn_in_the_middle_of_the_picture()
    {
        // One triangle, moved 10 units along x by its node.
        var positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
        var bin = new byte[positions.Length * 4];
        Buffer.BlockCopy(positions, 0, bin, 0, bin.Length);
        var json = $$$"""
            {"asset": {"version": "2.0"}, "scene": 0, "scenes": [{"nodes": [0]}], "nodes": [{"mesh": 0, "translation": [10, 0, 0]}],
             "meshes": [{"primitives": [{"attributes": {"POSITION": 0}, "material": 0}]}],
             "materials": [{"pbrMetallicRoughness": {"baseColorFactor": [0, 0, 1, 1]}}],
             "buffers": [{"byteLength": {{{bin.Length}}}}], "bufferViews": [{"buffer": 0, "byteLength": {{{bin.Length}}}}],
             "accessors": [{"bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3"}]}
            """;
        var jsonBytes = Encoding.UTF8.GetBytes(json.PadRight((json.Length + 3) / 4 * 4));
        using var glb = new MemoryStream();
        using (var w = new BinaryWriter(glb, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(0x46546C67u); w.Write(2u); w.Write((uint)(12 + 8 + jsonBytes.Length + 8 + bin.Length));
            w.Write((uint)jsonBytes.Length); w.Write(0x4E4F534Au); w.Write(jsonBytes);
            w.Write((uint)bin.Length); w.Write(0x004E4942u); w.Write(bin);
        }
        var path = Path.Combine(_dir, "t.glb");
        File.WriteAllBytes(path, glb.ToArray());

        using var mesh = Mesh.Load(path);
        Assert.Equal(1, mesh.TriangleCount);
        Assert.Equal(10, mesh.Positions[0].X);
        using var picture = MeshRenderer.Render(mesh, 100, 100, 0, 0, supersample: 1);
        // The model is centred whatever its position; front-facing blue, shaded.
        var centre = picture.GetPixel(45, 52);
        Assert.True(centre.Blue > 100 && centre.Red < 60, centre.ToString());
        Assert.NotEqual(centre, picture.GetPixel(2, 2));
    }
}
