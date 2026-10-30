using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sloperator.Core.Web;

namespace Sloperator.Core.Comfy;

/// <summary>A graphics card as Windows lists it: its name, maker (PCI vendor id) and memory.</summary>
public sealed record GraphicsCard(string Name, string Vendor, long Memory)
{
    public bool IsNvidia => Vendor == "10DE" || Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
    public bool IsAmd => Vendor == "1002" || Name.Contains("Radeon", StringComparison.OrdinalIgnoreCase);
    public bool IsIntel => Vendor == "8086" || Name.StartsWith("Intel", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One of ComfyUI's portable builds for Windows, and what it will run on here.</summary>
/// <param name="Asset">The release file: ComfyUI_windows_portable_nvidia.7z, …_amd.7z, …_intel.7z.</param>
/// <param name="Device">What it runs on, in words: "NVIDIA GeForce RTX 3060 (12 GB)", or the CPU and why.</param>
/// <param name="Cpu">No graphics card it can use: it runs on the CPU (started with --cpu).</param>
public sealed record ComfyBuild(string Asset, string Device, bool Cpu);

/// <summary>Which graphics cards this PC has, and which ComfyUI build and settings suit them.</summary>
public static partial class ComfyHardware
{
    /// <summary>The PC's graphics cards, from the registry's display adapter class (no WMI, no extra process).</summary>
    public static IReadOnlyList<GraphicsCard> Cards()
    {
        var cards = new List<GraphicsCard>();
        if (!OperatingSystem.IsWindows())
            return cards;
        try
        {
            using var display = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            foreach (var sub in display?.GetSubKeyNames() ?? [])
            {
                if (!sub.All(char.IsDigit))
                    continue;
                using var key = display!.OpenSubKey(sub);
                if (key?.GetValue("DriverDesc") is not string name || name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Remote", StringComparison.OrdinalIgnoreCase) || name.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                    continue;
                var vendor = Regex.Match(key.GetValue("MatchingDeviceId") as string ?? "", @"VEN_([0-9A-F]{4})", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value.ToUpperInvariant() : "";
                var memory = key.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long q => q,
                    byte[] { Length: 8 } b => BitConverter.ToInt64(b),
                    _ => key.GetValue("HardwareInformation.MemorySize") switch
                    {
                        int d => (uint)d,
                        byte[] { Length: >= 4 } b => BitConverter.ToUInt32(b),
                        _ => 0L,
                    },
                };
                cards.Add(new GraphicsCard(name.Trim(), vendor, memory));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return cards;
    }

    /// <summary>
    /// The portable build for these cards: NVIDIA's (the CUDA 12.6 one for a GTX 10 series or older),
    /// AMD's for a Radeon RX 6000 or newer on Windows 11, Intel's for an Arc, else NVIDIA's run on
    /// the CPU, which ComfyUI's README names for PCs without a graphics card it can use.
    /// </summary>
    public static ComfyBuild Pick(IReadOnlyList<GraphicsCard> cards, bool arm64 = false, int windowsBuild = 0)
    {
        if (windowsBuild == 0 && OperatingSystem.IsWindows())
            windowsBuild = Environment.OSVersion.Version.Build;
        if (cards.FirstOrDefault(c => c.IsNvidia) is { } nvidia)
        {
            var asset = arm64 ? "ComfyUI_windows_portable_nvidia_arm64.7z" : OldNvidia().IsMatch(nvidia.Name) ? "ComfyUI_windows_portable_nvidia_cu126.7z" : "ComfyUI_windows_portable_nvidia.7z";
            return new ComfyBuild(asset, Describe(nvidia), Cpu: false);
        }
        if (!arm64 && cards.FirstOrDefault(c => c.IsAmd && NewRadeon().IsMatch(c.Name)) is { } amd && windowsBuild >= 22000)
            return new ComfyBuild("ComfyUI_windows_portable_amd.7z", Describe(amd), Cpu: false);
        if (!arm64 && cards.FirstOrDefault(c => c.IsIntel && c.Name.Contains("Arc", StringComparison.OrdinalIgnoreCase)) is { } arc)
            return new ComfyBuild("ComfyUI_windows_portable_intel.7z", Describe(arc), Cpu: false);
        var why = cards.Count == 0 ? "no graphics card was found" :
            string.Join(", ", cards.Select(c => c.Name)) + (cards.Count == 1 ? " isn't" : " aren't") + " a graphics card ComfyUI can use" +
            (cards.Any(c => c.IsAmd && NewRadeon().IsMatch(c.Name)) ? " on this Windows (AMD's needs Windows 11)" : "");
        return new ComfyBuild(arm64 ? "ComfyUI_windows_portable_nvidia_arm64.7z" : "ComfyUI_windows_portable_nvidia.7z", $"the CPU ({why})", Cpu: true);
    }

    private static string Describe(GraphicsCard card) => card.Memory >= 1L << 30 ? $"{card.Name} ({card.Memory / 1073741824.0:0} GB)" : card.Name;

    /// <summary>Pascal and older, which PyTorch's CUDA 13 builds no longer run on.</summary>
    [GeneratedRegex(@"GTX\s*(?:10|9|7|6)\d{2}\b|\bGT\s*(?:10|7)\d{2}\b|Quadro\s*[PMK]\d|TITAN\s*X\b|TITAN\s*Xp|Tesla\s*[PMK]\d", RegexOptions.IgnoreCase)]
    private static partial Regex OldNvidia();

    /// <summary>The Radeons AMD's PyTorch for Windows runs on: RDNA 2 and newer, and the newest Ryzen AI graphics.</summary>
    [GeneratedRegex(@"\bRX\s*[679]\d{3}|Radeon\s*(?:AI\s*)?PRO\s*[WR]?[679]\d{3}|Radeon\s*(?:8060S|8050S|890M|880M|860M)", RegexOptions.IgnoreCase)]
    private static partial Regex NewRadeon();

    /// <summary>
    /// What the PyTorch of an installation was built for: "cuda", "rocm", "xpu" or "cpu", from its
    /// torch/version.py; null when it can't be told (the Desktop app, or the system's Python).
    /// </summary>
    public static string? TorchBuild(ComfyInstall install)
    {
        var version = install.SitePackages is { } sitePackages ? Path.Combine(sitePackages, "torch", "version.py") : null;
        if (version is null || !File.Exists(version))
            return null;
        try
        {
            var text = File.ReadAllText(version);
            static bool Set(string text, string name) => Regex.IsMatch(text, $@"^{name}\s*(?::[^=]*)?=\s*['""][^'""]+['""]", RegexOptions.Multiline);
            return Set(text, "cuda") ? "cuda" : Set(text, "hip") || Set(text, "rocm") ? "rocm" : Set(text, "xpu") ? "xpu" : "cpu";
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether an installation has to run on the CPU here: its PyTorch is built for a kind of
    /// graphics card this PC doesn't have. Started without --cpu, the CUDA build crashed on the
    /// laptop's Iris Xe with an access violation in torch.cuda.
    /// </summary>
    public static bool NeedsCpu(ComfyInstall install, IReadOnlyList<GraphicsCard>? cards = null)
    {
        var torch = TorchBuild(install);
        if (torch is null or "cpu")
            return false;
        cards ??= Cards();
        return torch switch
        {
            "cuda" => !cards.Any(c => c.IsNvidia),
            "rocm" => !cards.Any(c => c.IsAmd),
            "xpu" => !cards.Any(c => c.IsIntel),
            _ => false,
        };
    }

    /// <summary>What the server runs on, from its system_stats: "an NVIDIA GeForce RTX 5070 Ti with 16 GB", or the CPU with its RAM.</summary>
    public static (string Text, bool Cpu) Device(JsonObject? stats)
    {
        var devices = (stats?["devices"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var ram = (long?)stats?["system"]?["ram_total"];
        var gb = ram is { } r ? $"{r / 1073741824.0:0} GB of RAM" : "its RAM";
        if (devices.Count == 0 || devices.All(d => (string?)d["type"] == "cpu" || (string?)d["name"] == "cpu"))
            return ($"this PC's CPU (it has no graphics card ComfyUI can use) with {gb}", true);
        var gpu = devices[0];
        var name = ((string?)gpu["name"] ?? "a graphics card").Split(" : ")[0];
        name = Regex.Replace(name, @"^(cuda|xpu|hip):\d+\s*", "");
        return ($"{name} with {(long?)gpu["vram_total"] / 1073741824.0:0} GB, and {gb}", false);
    }

    /// <summary>What to use on the CPU: told once ComfyUI is there, and with templates and model searches.</summary>
    public const string CpuAdvice =
        "On the CPU each step of an SD 1.5 checkpoint (about 2 GB) at about 512x512 takes 5 to 15 seconds, so a picture takes " +
        "two to five minutes; larger models (SDXL, Flux, Qwen-Image, Z-Image, video models) take very long or don't fit in memory. " +
        "So use SD 1.5 checkpoints with the sloperator/ templates, at about 512 pixels and 15 to 25 steps, and allow for the time.";
}

/// <summary>
/// Installs ComfyUI's portable build for this PC into Sloperator's Data/tools: the right archive
/// from its GitHub release (checked against the SHA-256 GitHub gives), unpacked with 7-Zip (the
/// PC's own, or 7-Zip's small console 7zr.exe fetched into Data/tools). Windows' own tar can't
/// unpack it: the archive is LZMA2, which Windows 10's tar refuses.
/// </summary>
public static partial class ComfyInstaller
{
    private const string Releases = "https://github.com/Comfy-Org/ComfyUI/releases";

    /// <summary>7-Zip's console program, version 26.04, from its author's GitHub release (the same file 7-zip.org serves).</summary>
    private const string SevenZipUrl = "https://github.com/ip7z/7zip/releases/download/26.04/7zr.exe",
        SevenZipSha256 = "256feca8e274e5da655e2a284fabafd9f554365eb164862089dacd4e8276d282",
        SevenZipSite = "https://www.7-zip.org/a/7zr.exe";

    /// <summary>The folder the portable archive unpacks to.</summary>
    public const string PortableFolder = "ComfyUI_windows_portable";

    /// <summary>
    /// Downloads and unpacks <paramref name="build"/> into <paramref name="toolsDir"/> and returns
    /// the installation. The download is kept as a .part file if it is stopped, and picked up
    /// again next time; the archive is deleted once it is unpacked.
    /// </summary>
    public static async Task<ComfyInstall> InstallAsync(string toolsDir, ComfyBuild build, Action<string>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(toolsDir);
        var target = Path.Combine(toolsDir, PortableFolder);
        if (ComfyInstall.FromFolder(target) is { } already)
            return already;
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            throw new ComfyApiException($"{target} is there but holds no working ComfyUI (no ComfyUI\\main.py): move it away or install into another folder with folder=.");

        progress?.Invoke("Looking up ComfyUI's latest release…");
        var (url, sha256, tag) = await LatestAsync(build.Asset, ct).ConfigureAwait(false);
        var archive = Path.Combine(toolsDir, "downloads", build.Asset);
        await Downloader.DownloadAsync(url, archive, sha256, progress, ct).ConfigureAwait(false);

        var sevenZip = await SevenZipAsync(toolsDir, progress, ct).ConfigureAwait(false);
        var unpacking = Path.Combine(toolsDir, ".unpacking-comfyui");
        if (Directory.Exists(unpacking))
            Directory.Delete(unpacking, recursive: true);
        await ExtractAsync(sevenZip, archive, unpacking, progress, ct).ConfigureAwait(false);
        var unpacked = Directory.Exists(Path.Combine(unpacking, PortableFolder)) ? Path.Combine(unpacking, PortableFolder)
            : Directory.EnumerateDirectories(unpacking).FirstOrDefault(d => ComfyInstall.FromFolder(d) is not null)
              ?? throw new ComfyApiException($"{build.Asset} ({tag}) unpacked into {unpacking} but holds no ComfyUI folder.");
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true); // an empty leftover
        Directory.Move(unpacked, target);
        try
        {
            Directory.Delete(unpacking, recursive: true);
            File.Delete(archive);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return ComfyInstall.FromFolder(target) ?? throw new ComfyApiException($"{target} holds no ComfyUI after unpacking {build.Asset}.");
    }

    /// <summary>
    /// The latest release's link for <paramref name="asset"/>, with its SHA-256 when GitHub says it
    /// (see <see cref="GitHubReleases"/>); when GitHub can't be asked, the link GitHub keeps to every
    /// release's newest file, without a checksum.
    /// </summary>
    internal static async Task<(Uri Url, string? Sha256, string Tag)> LatestAsync(string asset, CancellationToken ct) =>
        await GitHubReleases.LatestAsync("Comfy-Org/ComfyUI", asset, ct).ConfigureAwait(false) is { Asset: { } url } release
            ? (url, release.Sha256, release.Tag)
            : (new Uri($"{Releases}/latest/download/{asset}"), null, "latest");

    /// <summary>A 7-Zip that unpacks .7z: the PC's own, one fetched for an earlier task, or 7zr.exe fetched now into Data/tools/7-Zip.</summary>
    internal static async Task<string> SevenZipAsync(string toolsDir, Action<string>? progress, CancellationToken ct)
    {
        var programFiles = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) };
        var candidates = programFiles.Where(p => p.Length > 0).Select(p => Path.Combine(p, "7-Zip", "7z.exe"))
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(p => p.Length > 0)
                .SelectMany(p => new[] { "7z.exe", "7za.exe", "7zr.exe" }.Select(n => Path.Combine(p.Trim('"'), n))))
            .Append(Path.Combine(toolsDir, "7-Zip", "7zr.exe"));
        foreach (var exe in candidates)
        {
            try
            {
                if (File.Exists(exe))
                    return exe;
            }
            catch (ArgumentException) { }
        }
        var fetched = Path.Combine(toolsDir, "7-Zip", "7zr.exe");
        progress?.Invoke("Fetching 7-Zip to unpack ComfyUI…");
        try
        {
            await Downloader.DownloadAsync(new Uri(SevenZipUrl), fetched, SevenZipSha256, progress, ct).ConfigureAwait(false);
        }
        catch (DownloadException)
        {
            // A release taken down or a newer one moved in: the author's own site serves the current one.
            await Downloader.DownloadAsync(new Uri(SevenZipSite), fetched, null, progress, ct).ConfigureAwait(false);
        }
        return fetched;
    }

    /// <summary>Unpacks a .7z with 7-Zip into <paramref name="folder"/>, saying how far it got.</summary>
    internal static async Task ExtractAsync(string sevenZip, string archive, string folder, Action<string>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var psi = new ProcessStartInfo(sevenZip)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "x", "-y", "-bsp1", "-bso0", "-bse2", "-o" + folder, archive })
            psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new ComfyApiException($"{sevenZip} didn't start.");
        var errors = process.StandardError.ReadToEndAsync(ct);
        var name = Path.GetFileName(archive);
        var buffer = new char[4096];
        var last = -1;
        int read;
        try
        {
            while ((read = await process.StandardOutput.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                // 7-Zip redraws one line with backspaces: " 42% 1234 - path".
                foreach (Match m in Percent().Matches(new string(buffer, 0, read)))
                    if (int.Parse(m.Groups[1].Value) is var p && p != last)
                    {
                        last = p;
                        progress?.Invoke($"Unpacking {name}… {p}%");
                    }
            }
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            throw;
        }
        if (process.ExitCode != 0)
            throw new ComfyApiException($"7-Zip couldn't unpack {name} (exit code {process.ExitCode}): {(await errors.ConfigureAwait(false)).Trim().Clip(600)}");
    }

    [GeneratedRegex(@"(\d{1,3})%")]
    private static partial Regex Percent();

    /// <summary>Whether this PC can take the portable build: 64-bit Windows.</summary>
    public static bool Supported => OperatingSystem.IsWindows() && Environment.Is64BitOperatingSystem;

    public static bool Arm64 => RuntimeInformation.OSArchitecture == Architecture.Arm64;
}
