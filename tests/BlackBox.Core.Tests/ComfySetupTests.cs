using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BlackBox.Core.Comfy;
using BlackBox.Core.Settings;
using BlackBox.Core.Tools;
using BlackBox.Core.Web;
using Xunit;

namespace BlackBox.Core.Tests;

/// <summary>
/// Files and answers on 127.0.0.1 by path: a download that honours Range, and can be cut off part
/// of the way the first time, and canned JSON standing in for Civitai's and Hugging Face's APIs.
/// </summary>
internal sealed class FileServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Task _loop;

    public Uri Url { get; }
    public Dictionary<string, (byte[] Body, string Type, int Status, string? FileName)> Routes { get; } = new(StringComparer.Ordinal);

    /// <summary>The first request for a file is cut off after this many bytes.</summary>
    public long? CutFirstAfter { get; set; }
    public List<string> Requests { get; } = [];

    public FileServer()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Url = new Uri($"http://127.0.0.1:{port}/");
        _listener.Prefixes.Add(Url.ToString());
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    public string Base => Url.ToString().TrimEnd('/');

    public void File(string path, byte[] body, string? fileName = null) => Routes[path] = (body, "application/octet-stream", 200, fileName);
    public void Json(string path, string json) => Routes[path] = (Encoding.UTF8.GetBytes(json), "application/json", 200, null);
    public void Page(string path, string html, int status = 200) => Routes[path] = (Encoding.UTF8.GetBytes(html), "text/html", status, null);

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception) { return; }
            try { Answer(ctx); }
            catch (Exception) { try { ctx.Response.Abort(); } catch (Exception) { } }
        }
    }

    private void Answer(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url!.AbsolutePath;
        lock (Requests) Requests.Add(ctx.Request.Url.PathAndQuery + (ctx.Request.Headers["Range"] is { } r ? " " + r : ""));
        if (!Routes.TryGetValue(path, out var route))
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
            return;
        }
        var from = 0L;
        if (ctx.Request.Headers["Range"] is { } range && range.StartsWith("bytes=") && long.TryParse(range[6..].TrimEnd('-'), out var start))
        {
            if (start >= route.Body.Length)
            {
                ctx.Response.StatusCode = 416;
                ctx.Response.AddHeader("Content-Range", $"bytes */{route.Body.Length}");
                ctx.Response.Close();
                return;
            }
            from = start;
            ctx.Response.StatusCode = 206;
            ctx.Response.AddHeader("Content-Range", $"bytes {start}-{route.Body.Length - 1}/{route.Body.Length}");
        }
        else
            ctx.Response.StatusCode = route.Status;
        ctx.Response.ContentType = route.Type;
        if (route.FileName is { } name)
            ctx.Response.AddHeader("Content-Disposition", $"attachment; filename=\"{name}\"");
        ctx.Response.ContentLength64 = route.Body.Length - from;
        if (CutFirstAfter is { } cut && from == 0 && route.Type == "application/octet-stream")
        {
            CutFirstAfter = null;
            ctx.Response.OutputStream.Write(route.Body, 0, (int)cut);
            ctx.Response.OutputStream.Flush();
            ctx.Response.Abort();
            return;
        }
        ctx.Response.OutputStream.Write(route.Body, (int)from, (int)(route.Body.Length - from));
        ctx.Response.Close();
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
        try { _loop.Wait(2000); } catch (AggregateException) { }
    }
}

public class ComfyHardwareTests
{
    private static GraphicsCard Card(string name, string vendor, double gb = 0) => new(name, vendor, (long)(gb * 1073741824));

    [Fact]
    public void Each_kind_of_graphics_card_gets_the_build_made_for_it_and_none_runs_on_the_cpu()
    {
        Assert.Equal(("ComfyUI_windows_portable_nvidia.7z", false), Pick(Card("NVIDIA GeForce RTX 3060", "10DE", 12)));
        Assert.Equal(("ComfyUI_windows_portable_nvidia.7z", false), Pick(Card("NVIDIA GeForce GTX 1650", "10DE", 4)));
        Assert.Equal(("ComfyUI_windows_portable_nvidia_cu126.7z", false), Pick(Card("NVIDIA GeForce GTX 1080 Ti", "10DE", 11)));
        Assert.Equal(("ComfyUI_windows_portable_amd.7z", false), Pick(Card("AMD Radeon RX 7900 XTX", "1002", 24), windows: 22631));
        Assert.Equal(("ComfyUI_windows_portable_intel.7z", false), Pick(Card("Intel(R) Arc(TM) A770 Graphics", "8086", 16)));
        // The laptop: Iris Xe is no Arc, so NVIDIA's build runs on the CPU, as ComfyUI's README says.
        Assert.Equal(("ComfyUI_windows_portable_nvidia.7z", true), Pick(Card("Intel(R) Iris(R) Xe Graphics", "8086", 1)));
        Assert.Equal(("ComfyUI_windows_portable_nvidia.7z", true), Pick());
        // A dedicated NVIDIA card wins over the integrated Intel one beside it.
        Assert.Equal(("ComfyUI_windows_portable_nvidia.7z", false), Pick(Card("Intel(R) UHD Graphics 770", "8086"), Card("NVIDIA GeForce RTX 4070", "10DE", 12)));
    }

    private static (string, bool) Pick(params GraphicsCard[] cards) => Pick(cards, 19044);

    private static (string, bool) Pick(GraphicsCard card, int windows) => Pick([card], windows);

    private static (string, bool) Pick(GraphicsCard[] cards, int windows)
    {
        var build = ComfyHardware.Pick(cards, windowsBuild: windows);
        return (build.Asset, build.Cpu);
    }

    [Fact]
    public void A_cpu_build_says_why_in_words()
    {
        var iris = ComfyHardware.Pick([Card("Intel(R) Iris(R) Xe Graphics", "8086", 1)], windowsBuild: 19044);
        Assert.Equal("the CPU (Intel(R) Iris(R) Xe Graphics isn't a graphics card ComfyUI can use)", iris.Device);
        var radeon = ComfyHardware.Pick([Card("AMD Radeon RX 6800", "1002", 16)], windowsBuild: 19044);
        Assert.True(radeon.Cpu);
        Assert.Contains("AMD's needs Windows 11", radeon.Device);
        Assert.Equal("NVIDIA GeForce RTX 3060 (12 GB)", ComfyHardware.Pick([Card("NVIDIA GeForce RTX 3060", "10DE", 12)]).Device);
    }

    /// <summary>A portable ComfyUI folder as the release unpacks, with a PyTorch built for <paramref name="torch"/>.</summary>
    private static ComfyInstall Portable(string torch)
    {
        var root = Directory.CreateTempSubdirectory("bb-portable-").FullName;
        var code = Path.Combine(root, "ComfyUI");
        Directory.CreateDirectory(Path.Combine(code, "comfy"));
        File.WriteAllText(Path.Combine(code, "main.py"), "");
        File.WriteAllText(Path.Combine(code, "nodes.py"), "");
        var site = Path.Combine(root, "python_embeded", "Lib", "site-packages", "torch");
        Directory.CreateDirectory(site);
        File.WriteAllText(Path.Combine(root, "python_embeded", "python.exe"), "");
        File.WriteAllText(Path.Combine(site, "version.py"), $$"""
            from typing import Optional
            __version__ = '2.14.0+{{torch}}'
            cuda: Optional[str] = {{(torch.StartsWith("cu") ? "'13.0'" : "None")}}
            hip: Optional[str] = {{(torch.StartsWith("rocm") ? "'7.1'" : "None")}}
            xpu: Optional[str] = {{(torch == "xpu" ? "'20250101'" : "None")}}
            """);
        return ComfyInstall.FromFolder(root)!;
    }

    [Fact]
    public void A_pytorch_built_for_a_card_this_pc_hasnt_got_runs_on_the_cpu()
    {
        var cuda = Portable("cu130");
        Assert.Equal(ComfyInstallKind.Portable, cuda.Kind);
        Assert.Equal("cuda", ComfyHardware.TorchBuild(cuda));
        Assert.True(ComfyHardware.NeedsCpu(cuda, [Card("Intel(R) Iris(R) Xe Graphics", "8086")]));
        Assert.False(ComfyHardware.NeedsCpu(cuda, [Card("NVIDIA GeForce RTX 3060", "10DE")]));
        var xpu = Portable("xpu");
        Assert.Equal("xpu", ComfyHardware.TorchBuild(xpu));
        Assert.False(ComfyHardware.NeedsCpu(xpu, [Card("Intel(R) Arc(TM) B580 Graphics", "8086")]));
        Assert.True(ComfyHardware.NeedsCpu(xpu, []));
        Assert.Equal("rocm", ComfyHardware.TorchBuild(Portable("rocm7.1")));
    }

    [Fact]
    public void A_server_blackbox_starts_never_opens_the_browser()
    {
        var args = ComfyProcess.DefaultArguments(Portable("cu130"), 8188);
        Assert.Contains("--windows-standalone-build", args);
        Assert.Contains("--disable-auto-launch", args);
        Assert.Equal(1, ComfyProcess.Quietly([.. args]).Count(a => a == "--disable-auto-launch"));
    }

    [Fact]
    public void What_the_server_runs_on_is_said_in_words()
    {
        var cpu = ComfyHardware.Device(JsonNode.Parse("""{"system": {"ram_total": 16865644544}, "devices": [{"name": "cpu", "type": "cpu"}]}""")!.AsObject());
        Assert.True(cpu.Cpu);
        Assert.Equal("this PC's CPU (it has no graphics card ComfyUI can use) with 16 GB of RAM", cpu.Text);
        var gpu = ComfyHardware.Device(JsonNode.Parse("""{"system": {"ram_total": 68000000000}, "devices": [{"name": "cuda:0 NVIDIA GeForce RTX 5070 Ti : cudaMallocAsync", "type": "cuda", "vram_total": 17094475776}]}""")!.AsObject());
        Assert.False(gpu.Cpu);
        Assert.Equal("NVIDIA GeForce RTX 5070 Ti with 16 GB, and 63 GB of RAM", gpu.Text);
    }
}

public class ComfyFindTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("bb-find-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>A ComfyUI folder (main.py, nodes.py, comfy\) at <paramref name="code"/>, and the Python beside it, if any.</summary>
    private string Code(string code, string? python = null)
    {
        var dir = Path.Combine(_root, code);
        Directory.CreateDirectory(Path.Combine(dir, "comfy"));
        File.WriteAllText(Path.Combine(dir, "main.py"), "");
        File.WriteAllText(Path.Combine(dir, "nodes.py"), "");
        if (python is not null)
        {
            var exe = Path.Combine(_root, python);
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            File.WriteAllText(exe, "");
        }
        return dir;
    }

    [Fact]
    public void Portable_builds_installs_with_a_venv_and_launchers_installs_are_found()
    {
        // A portable folder on the last level looked at: its ComfyUI folder is one deeper.
        var portable = Code(@"AI\Tools\ComfyUI_windows_portable\ComfyUI", @"AI\Tools\ComfyUI_windows_portable\python_embeded\python.exe");
        var venv = Code(@"Projects\ComfyUI", @"Projects\ComfyUI\venv\Scripts\python.exe");
        // Pinokio keeps it in api\comfy.git\app with an env, Stability Matrix in Packages\ComfyUI with a venv.
        var pinokio = Code(@"pinokio\api\comfy.git\app", @"pinokio\api\comfy.git\app\env\Scripts\python.exe");
        var plain = Code(@"src\ComfyUI");

        var found = ComfyInstall.Find(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken,
            firstLookIn: [Path.Combine(_root, "pinokio", "api")], scan: [_root]);

        Assert.Contains(found, f => f.CodeDir == portable && f.Kind == ComfyInstallKind.Portable && f.Program.EndsWith(@"python_embeded\python.exe"));
        Assert.Contains(found, f => f.CodeDir == venv && f.Kind == ComfyInstallKind.Venv && f.Program.EndsWith(@"venv\Scripts\python.exe"));
        Assert.Contains(found, f => f.CodeDir == pinokio && f.Kind == ComfyInstallKind.Venv && f.Program.EndsWith(@"env\Scripts\python.exe"));
        Assert.Contains(found, f => f.CodeDir == plain && f.Kind == ComfyInstallKind.SystemPython);
        Assert.Equal(4, found.Count);
    }

    [Fact]
    public void A_folder_named_by_the_user_is_any_of_its_levels()
    {
        var code = Code(@"ComfyUI_windows_portable\ComfyUI", @"ComfyUI_windows_portable\python_embeded\python.exe");
        Assert.Equal(code, ComfyInstall.FromFolder(Path.Combine(_root, "ComfyUI_windows_portable"))!.CodeDir);
        Assert.Equal(code, ComfyInstall.FromFolder(code)!.CodeDir);
        Assert.Null(ComfyInstall.FromFolder(_root));
    }
}

public class ComfyModelNameTests
{
    [Theory]
    [InlineData("split_files/diffusion_models/z_image_turbo_bf16.safetensors", "diffusion_models")]
    [InlineData("split_files/text_encoders/qwen_3_4b.safetensors", "text_encoders")]
    [InlineData("loras/watercolor.safetensors", "loras")]
    [InlineData("4x-UltraSharp.pth", "upscale_models")]
    [InlineData("RealESRGAN_x4plus.pth", "upscale_models")]
    [InlineData("sdxl_vae.safetensors", "vae")]
    [InlineData("vae-ft-mse-840000-ema-pruned.safetensors", "vae")]
    [InlineData("DreamShaper_4NoVae.safetensors", "checkpoints")]
    [InlineData("DreamShaperXL1.0Alpha2_fixedVae_half_00001_.safetensors", "checkpoints")]
    [InlineData("DreamShaper_3.3_baked_vae.safetensors", "checkpoints")]
    [InlineData("control_v11p_sd15_canny.pth", "controlnet")]
    [InlineData("pixel-art-xl-lora.safetensors", "loras")]
    public void A_file_goes_in_the_folder_its_name_or_path_says(string path, string folder) => Assert.Equal(folder, ComfyModelSearch.FolderFor(path, null));

    [Theory]
    [InlineData("v1-5-pruned-emaonly-fp16.safetensors", "SD 1.5")]
    [InlineData("DreamShaperXL1.0Alpha2_fixedVae_half", "SDXL")]
    [InlineData("sd_xl_turbo_1.0_fp16.safetensors", "SDXL Turbo")]
    [InlineData("flux1-dev-fp8.safetensors", "Flux.1 D")]
    [InlineData("stabilityai/sd-turbo", "SD 2.1 Turbo")]
    [InlineData("DreamShaper_8_pruned.safetensors", null)]
    public void A_name_tells_the_base_model_when_it_says_it(string name, string? expected) => Assert.Equal(expected, ComfyModelSearch.BaseFromName(name));

    [Fact]
    public void Base_models_and_kinds_are_matched_however_they_are_written()
    {
        Assert.True(ComfyModelSearch.SameBase("SD 1.5", "sd1.5"));
        Assert.True(ComfyModelSearch.SameBase("SD 1.5", "1.5"));
        Assert.True(ComfyModelSearch.SameBase("SDXL 1.0", "SDXL"));
        Assert.False(ComfyModelSearch.SameBase("SD 1.5", "SDXL"));
        Assert.Equal("loras", ComfyModelSearch.KindOf("LoRAs")!.Value.Folder);
        Assert.Equal("upscale_models", ComfyModelSearch.KindOf("upscale")!.Value.Folder);
        Assert.Equal("checkpoints", ComfyModelSearch.KindOf("checkpoint")!.Value.Folder);
        Assert.Null(ComfyModelSearch.KindOf("any"));
        Assert.Equal("checkpoints", ComfyModelFolders.Normalize("Stable-diffusion"));
        Assert.Equal("loras", ComfyModelFolders.Normalize("models/Lora"));
        Assert.Equal("upscale_models", ComfyModelFolders.Normalize("ESRGAN"));
        Assert.Equal("clip_vision", ComfyModelFolders.Normalize("clip_vision"));
    }
}

public class DownloaderTests : IDisposable
{
    private readonly FileServer _server = new();
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-download-").FullName;
    private static readonly byte[] Body = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7 % 251)).ToArray();
    private static readonly string Sha = Convert.ToHexStringLower(SHA256.HashData(Body));

    public void Dispose()
    {
        _server.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task A_file_arrives_whole_checked_and_named_only_at_the_end()
    {
        _server.File("/m.safetensors", Body);
        var progress = new List<string>();
        var target = Path.Combine(_dir, "models", "m.safetensors");
        var hash = await Downloader.DownloadAsync(new Uri(_server.Url, "m.safetensors"), target, Sha.ToUpperInvariant(), progress.Add, TestContext.Current.CancellationToken);
        Assert.Equal(Sha, hash);
        Assert.Equal(Body, File.ReadAllBytes(target));
        Assert.False(File.Exists(target + ".part"));
        Assert.Contains(progress, p => p.StartsWith("Checking m.safetensors"));
    }

    [Fact]
    public async Task A_download_cut_off_goes_on_where_it_stopped()
    {
        _server.File("/m.safetensors", Body);
        _server.CutFirstAfter = 100_000;
        var target = Path.Combine(_dir, "m.safetensors");
        var progress = new List<string>();
        await Downloader.DownloadAsync(new Uri(_server.Url, "m.safetensors"), target, Sha, progress.Add, TestContext.Current.CancellationToken);
        Assert.Equal(Body, File.ReadAllBytes(target));
        // The second request asks only for the rest.
        Assert.Contains(_server.Requests, r => r.Contains(" bytes=") && !r.EndsWith(" bytes=0-"));
        Assert.Contains(progress, p => p.Contains("going on where it stopped"));
    }

    [Fact]
    public async Task A_part_file_that_holds_the_whole_file_is_only_checked_and_named()
    {
        // Stopped after the last byte came, before the check: the server answers 416 to the range past the end.
        _server.File("/m.safetensors", Body);
        var target = Path.Combine(_dir, "m.safetensors");
        File.WriteAllBytes(target + ".part", Body);
        await Downloader.DownloadAsync(new Uri(_server.Url, "m.safetensors"), target, Sha, null, TestContext.Current.CancellationToken);
        Assert.Equal(Body, File.ReadAllBytes(target));
        Assert.Equal([$"/m.safetensors bytes={Body.Length}-"], _server.Requests);
    }

    [Fact]
    public async Task A_damaged_file_is_thrown_away_and_said_so()
    {
        _server.File("/m.safetensors", Body);
        var target = Path.Combine(_dir, "m.safetensors");
        var ex = await Assert.ThrowsAsync<DownloadException>(() => Downloader.DownloadAsync(new Uri(_server.Url, "m.safetensors"), target, new string('0', 64), null, TestContext.Current.CancellationToken));
        Assert.Contains("arrived damaged", ex.Message);
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".part"));
    }

    [Fact]
    public async Task A_web_page_or_a_refusal_where_a_file_was_expected_is_said_so()
    {
        _server.Page("/models/4384", "<html><body>DreamShaper</body></html>");
        _server.Page("/private.safetensors", "no", status: 401);
        var page = await Assert.ThrowsAsync<DownloadException>(() => Downloader.DownloadAsync(new Uri(_server.Url, "models/4384"), Path.Combine(_dir, "x"), null, null, TestContext.Current.CancellationToken));
        Assert.Contains("is a web page, not a file", page.Message);
        var refused = await Assert.ThrowsAsync<DownloadException>(() => Downloader.DownloadAsync(new Uri(_server.Url, "private.safetensors"), Path.Combine(_dir, "y"), null, null, TestContext.Current.CancellationToken));
        Assert.Contains("answered 401", refused.Message);
        Assert.Contains("needs a login", refused.Message);
    }

    [Fact]
    public async Task A_links_file_name_comes_from_the_server_when_it_gives_one()
    {
        _server.File("/api/download/models/128713", Body, fileName: "dreamshaper_8.safetensors");
        var (name, size, _) = await Downloader.ProbeAsync(new Uri(_server.Url, "api/download/models/128713"), TestContext.Current.CancellationToken);
        Assert.Equal("dreamshaper_8.safetensors", name);
        Assert.Equal(Body.Length, size);
    }

    [Fact]
    public void A_file_name_is_never_the_hash_a_storage_link_is_named_by()
    {
        // Hugging Face sends its files from a link named by their hash, with a header .NET refuses (it ends in ";").
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        response.Content.Headers.TryAddWithoutValidation("Content-Disposition", "inline; filename*=UTF-8''v1-5-pruned-emaonly-fp16.safetensors; filename=\"v1-5-pruned-emaonly-fp16.safetensors\";");
        var asked = new Uri("https://huggingface.co/Comfy-Org/stable-diffusion-v1-5-archive/resolve/main/v1-5-pruned-emaonly-fp16.safetensors");
        var final = new Uri("https://us.aws.cdn.hf.co/xet-bridge-us/66d0dccb/908c39bfdfec888e295ba04e974b6342f3c15776760edd46838240a8d455525d?xip=9a");
        Assert.Equal("v1-5-pruned-emaonly-fp16.safetensors", Downloader.NameOf(response, asked, final));
        using var bare = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        Assert.Equal("v1-5-pruned-emaonly-fp16.safetensors", Downloader.NameOf(bare, asked, final));
        Assert.Equal("dreamshaper_8.safetensors", Downloader.NameOf(bare, new Uri("https://civitai.com/api/download/models/128713"), new Uri("https://cdn.example/files/dreamshaper_8.safetensors?sig=1")));
    }
}

/// <summary>Opt-in checks against the real sites (BB_COMFY_ONLINE=1): GitHub's release of ComfyUI, and the model search on Civitai and Hugging Face.</summary>
public class ComfyOnlineTests
{
    private static readonly bool Online = Environment.GetEnvironmentVariable("BB_COMFY_ONLINE") == "1";

    [Fact]
    public async Task The_latest_portable_release_is_found_with_its_checksum()
    {
        Assert.SkipWhen(!Online, "Set BB_COMFY_ONLINE=1 to run.");
        var (url, sha256, tag) = await ComfyInstaller.LatestAsync("ComfyUI_windows_portable_nvidia.7z", TestContext.Current.CancellationToken);
        Assert.StartsWith("https://github.com/Comfy-Org/ComfyUI/releases/", url.ToString());
        Assert.Matches("^v\\d", tag);
        Assert.Matches("^[0-9a-f]{64}$", sha256 ?? "");
    }

    [Fact]
    public async Task Models_are_found_on_both_sites()
    {
        Assert.SkipWhen(!Online, "Set BB_COMFY_ONLINE=1 to run.");
        var problems = new List<string>();
        var found = await ComfyModelSearch.SearchAsync("dreamshaper", "checkpoint", "SD 1.5", 10, problems, TestContext.Current.CancellationToken);
        Assert.Empty(problems);
        Assert.Contains(found, m => m.Source == "Civitai" && m.File.EndsWith(".safetensors") && m.Sha256 is not null && m.Size > 1_000_000_000);
        Assert.Contains(found, m => m.Source == "Hugging Face" && m.Url.Contains("/resolve/main/") && m.Folder == "checkpoints");
        var page = await ComfyModelSearch.ResolveAsync(new Uri("https://civitai.com/models/4384/dreamshaper"), TestContext.Current.CancellationToken);
        Assert.Equal("checkpoints", page!.Folder);
        Assert.StartsWith("https://civitai.com/api/download/models/", page.Url);
    }
}

/// <summary>The model search, the download action and BlackBox's own templates, against stand-ins for Civitai, Hugging Face and ComfyUI.</summary>
public class ComfyModelToolTests : IDisposable
{
    private readonly FileServer _sites = new();
    private readonly FakeComfyServer _comfy = new();
    private readonly string _home = Directory.CreateTempSubdirectory("bb-models-home-").FullName;
    private readonly ToolContext _ctx;
    private readonly (string Civitai, string HuggingFace) _before = (ComfyModelSearch.CivitaiBase, ComfyModelSearch.HuggingFaceBase);
    private static readonly byte[] Model = Encoding.ASCII.GetBytes(new string('m', 50_000));

    public ComfyModelToolTests()
    {
        ComfyModelSearch.CivitaiBase = _sites.Base;
        ComfyModelSearch.HuggingFaceBase = _sites.Base;
        var paths = new AppPaths(_home);
        Directory.CreateDirectory(paths.ComfyDir);
        File.WriteAllText(Path.Combine(paths.ComfyDir, "state.json"), new JsonObject { ["url"] = _comfy.Url.ToString() }.ToJsonString());
        _ctx = new ToolContext
        {
            Platform = new FakePlatform(),
            Settings = new AgentSettings(),
            WorkingDirectory = _home,
            Comfy = ComfyHub.For(paths.ComfyDir),
            ScratchDirectory = paths.ScratchDir,
        };
        var sha = Convert.ToHexStringLower(SHA256.HashData(Model));
        _sites.Json("/api/v1/models", $$"""
            {"items": [
              {"id": 4384, "name": "DreamShaper", "type": "Checkpoint", "nsfw": false, "stats": {"downloadCount": 1691784},
               "modelVersions": [{"id": 128713, "name": "8", "baseModel": "SD 1.5", "files": [
                 {"name": "dreamshaper_8.safetensors", "sizeKB": 48.828125, "primary": true, "type": "Model", "metadata": {"fp": "fp16", "size": "pruned", "format": "SafeTensor"},
                  "hashes": {"SHA256": "{{sha.ToUpperInvariant()}}"}, "downloadUrl": "{{_sites.Base}}/api/download/models/128713"}]}]},
              {"id": 666, "name": "Not for here", "type": "Checkpoint", "nsfw": true, "stats": {"downloadCount": 9},
               "modelVersions": [{"id": 1, "baseModel": "SD 1.5", "files": [{"name": "x.safetensors", "primary": true, "downloadUrl": "{{_sites.Base}}/x"}]}]}
            ]}
            """);
        _sites.Json("/api/v1/model-versions/128713", $$"""
            {"id": 128713, "name": "8", "baseModel": "SD 1.5", "model": {"name": "DreamShaper", "type": "Checkpoint"},
             "files": [{"name": "dreamshaper_8.safetensors", "sizeKB": 48.828125, "primary": true, "hashes": {"SHA256": "{{sha}}"}, "downloadUrl": "{{_sites.Base}}/api/download/models/128713"}]}
            """);
        _sites.File("/api/download/models/128713", Model, fileName: "dreamshaper_8.safetensors");
        _sites.Json("/api/models", """
            [{"id": "Lykon/DreamShaper", "downloads": 215091, "pipeline_tag": "text-to-image", "tags": ["diffusers", "stable-diffusion", "text-to-image"]},
             {"id": "someone/chat-model", "downloads": 999999, "pipeline_tag": "text-generation", "tags": ["transformers"]}]
            """);
        _sites.Json("/api/models/Lykon/DreamShaper", """
            {"id": "Lykon/DreamShaper", "downloads": 215091, "gated": false, "tags": ["diffusers", "stable-diffusion", "text-to-image"], "siblings": [
              {"rfilename": "DreamShaper_7_pruned.safetensors", "size": 2132625894, "lfs": {"sha256": "ed98", "size": 2132625894}},
              {"rfilename": "DreamShaper_8_INPAINTING.inpainting.safetensors", "size": 2132626066},
              {"rfilename": "DreamShaper_8_pruned.safetensors", "size": 2132625894, "lfs": {"sha256": "879d", "size": 2132625894}},
              {"rfilename": "DreamShaper_8_pruned.ckpt", "size": 2132625894},
              {"rfilename": "unet/diffusion_pytorch_model.safetensors", "size": 3438167536},
              {"rfilename": "model_index.json", "size": 541}
            ]}
            """);
    }

    public void Dispose()
    {
        (ComfyModelSearch.CivitaiBase, ComfyModelSearch.HuggingFaceBase) = _before;
        _sites.Dispose();
        _comfy.Dispose();
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    private Task<ToolResult> Call(ComfyTool tool, string json) => tool.ExecuteAsync(JsonNode.Parse(json)!.AsObject(), _ctx, CancellationToken.None);

    [Fact]
    public async Task Models_online_come_from_both_sites_with_their_size_base_and_link()
    {
        var problems = new List<string>();
        var found = await ComfyModelSearch.SearchAsync("dreamshaper", null, null, 10, problems, TestContext.Current.CancellationToken);
        Assert.Empty(problems);
        var civitai = found[0];
        Assert.Equal(("DreamShaper (8)", "checkpoint", "SD 1.5", "checkpoints", "dreamshaper_8.safetensors"), (civitai.Title, civitai.Kind, civitai.Base, civitai.Folder, civitai.File));
        Assert.Equal($"{_sites.Base}/api/download/models/128713", civitai.Url);
        Assert.Equal("fp16, pruned", civitai.Note);
        Assert.DoesNotContain(found, m => m.Title == "Not for here");
        // Hugging Face: single files only, the newest first, not the inpainting one or a part of the diffusers model.
        var hf = found.Where(m => m.Source == "Hugging Face").ToList();
        Assert.Equal("DreamShaper_8_pruned.safetensors", hf[0].File);
        Assert.Equal($"{_sites.Base}/Lykon/DreamShaper/resolve/main/DreamShaper_8_pruned.safetensors", hf[0].Url);
        Assert.Equal("879d", hf[0].Sha256);
        Assert.DoesNotContain(hf, m => m.File.StartsWith("unet/"));
        Assert.DoesNotContain(found, m => m.Title.Contains("chat-model"));
    }

    [Fact]
    public async Task A_search_narrowed_to_a_kind_asks_civitai_for_it()
    {
        var problems = new List<string>();
        await ComfyModelSearch.SearchAsync("watercolor", "lora", null, 10, problems, TestContext.Current.CancellationToken);
        Assert.Contains(_sites.Requests, r => r.StartsWith("/api/v1/models?") && r.Contains("types=LORA") && r.Contains("types=LoCon"));
    }

    [Fact]
    public async Task Find_models_lists_them_with_what_to_pass_to_download_and_what_suits_the_pc()
    {
        _comfy.OnCpu = true;
        var result = await Call(new ComfyTool(), """{"action": "find_models", "search": "dreamshaper"}""");
        Assert.False(result.IsError, result.Text);
        Assert.Contains("ComfyUI runs on this PC's CPU", result.Text);
        Assert.Contains("use SD 1.5 checkpoints", result.Text);
        Assert.Contains($"1. DreamShaper (8): checkpoint for SD 1.5, 48 KB, {1691784:N0} downloads, fp16, pruned (Civitai,", result.Text);
        Assert.Contains($"dreamshaper_8.safetensors: url={_sites.Base}/api/download/models/128713 model_folder=checkpoints", result.Text);
    }

    [Fact]
    public async Task A_model_found_is_downloaded_into_comfyuis_folder_for_it_and_checked()
    {
        var tool = new ComfyTool();
        await Call(tool, """{"action": "find_models", "search": "dreamshaper"}""");
        var result = await Call(tool, $$"""{"action": "download", "url": "{{_sites.Base}}/api/download/models/128713"}""");
        Assert.False(result.IsError, result.Text);
        var file = Path.Combine(_comfy.CodeDir, "models", "checkpoints", "dreamshaper_8.safetensors");
        Assert.Equal(Model, File.ReadAllBytes(file));
        Assert.Contains("its SHA-256 checked", result.Text);
        Assert.Contains("CheckpointLoaderSimple (ckpt_name \"dreamshaper_8.safetensors\")", result.Text);

        var again = await Call(tool, $$"""{"action": "download", "url": "{{_sites.Base}}/api/download/models/128713"}""");
        Assert.Contains("is there already", again.Text);
    }

    [Fact]
    public async Task A_model_page_link_is_turned_into_its_files_link()
    {
        // A Civitai page with a version: its main file, named and checked as Civitai says.
        var result = await Call(new ComfyTool(), $$"""{"action": "download", "url": "{{_sites.Base}}/models/4384/dreamshaper?modelVersionId=128713"}""");
        Assert.False(result.IsError, result.Text);
        Assert.True(File.Exists(Path.Combine(_comfy.CodeDir, "models", "checkpoints", "dreamshaper_8.safetensors")));

        // A Hugging Face page: its download link.
        var hf = await ComfyModelSearch.ResolveAsync(new Uri($"{_sites.Base}/Lykon/DreamShaper/blob/main/DreamShaper_8_pruned.safetensors"), TestContext.Current.CancellationToken);
        Assert.Equal($"{_sites.Base}/Lykon/DreamShaper/resolve/main/DreamShaper_8_pruned.safetensors", hf!.Url);
        Assert.Equal("checkpoints", hf.Folder);
    }

    [Fact]
    public async Task Blackboxs_own_image_to_image_runs_a_picture_of_the_users_through_a_checkpoint()
    {
        _comfy.OnCpu = true;
        var tool = new ComfyTool();
        var list = await Call(tool, """{"action": "templates", "search": "a watercolor painting from a photo"}""");
        Assert.Contains("ComfyUI runs on this PC's CPU", list.Text);
        Assert.Contains("blackbox/image_to_image \"Image to image with a checkpoint\"", list.Text);
        Assert.Contains("[ready]", list.Text.Split('\n').First(l => l.Contains("blackbox/image_to_image")));

        var shown = await Call(tool, """{"action": "templates", "name": "blackbox/image_to_image"}""");
        Assert.Contains("7.denoise is how much changes", shown.Text);
        Assert.Contains("\"4\": {\"class_type\":\"LoadImage\"", shown.Text);

        var photo = Path.Combine(_home, "photo.png");
        File.WriteAllBytes(photo, [137, 80, 78, 71]);
        var run = await Call(tool, new JsonObject
        {
            ["action"] = "run",
            ["name"] = "blackbox/image_to_image",
            ["set"] = new JsonObject { ["4.image"] = photo, ["2.text"] = "a watercolor painting of a house", ["7.denoise"] = 0.55 },
        }.ToJsonString());
        Assert.False(run.IsError, run.Text);
        var prompt = Assert.Single(_comfy.Prompts)["prompt"]!;
        Assert.Equal("uploaded.png", (string?)prompt["4"]!["inputs"]!["image"]);
        Assert.Equal(0.55, (double?)prompt["7"]!["inputs"]!["denoise"]);
        Assert.Equal("v1-5-pruned-emaonly-fp16.safetensors", (string?)prompt["1"]!["inputs"]!["ckpt_name"]);
    }

    [Fact]
    public async Task A_comfyui_that_runs_already_is_used_as_it_is_and_none_is_installed_beside_it()
    {
        var install = await Call(new ComfyTool(), """{"action": "install"}""");
        Assert.False(install.IsError, install.Text);
        Assert.Contains($"A ComfyUI runs already, at {_comfy.Url}; nothing was downloaded", install.Text);
        Assert.Contains($"Using the ComfyUI that already runs at {_comfy.Url} (from {_comfy.CodeDir}); it keeps running after this task.", install.Text);
        Assert.False(Directory.Exists(Path.Combine(_home, "Data", "tools", "ComfyUI_windows_portable")));
    }

    [Fact]
    public async Task A_missing_checkpoint_comes_with_its_link_and_the_ones_that_could_take_its_place()
    {
        _comfy.Checkpoints.Remove("v1-5-pruned-emaonly-fp16.safetensors");
        var shown = await Call(new ComfyTool(), """{"action": "templates", "name": "blackbox/text_to_image"}""");
        Assert.Contains("download it with action=download url=https://huggingface.co/Comfy-Org/stable-diffusion-v1-5-archive/resolve/main/v1-5-pruned-emaonly-fp16.safetensors", shown.Text);
        Assert.Contains("Installed checkpoints that could take its place: sdxl.safetensors", shown.Text);
    }
}
