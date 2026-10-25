using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using BlackBox.Core.Comfy;

namespace BlackBox.Core.Video;

/// <summary>How a song sounds to SongEval, 1 to 5 each (a real, finished song scores about 4).</summary>
public sealed record SongScore(double Coherence, double Musicality, double Memorability, double Clarity, double Naturalness)
{
    /// <summary>Below this, a song sounds bad to people: the user called two videos at 2.4 and 2.7 "garbage".</summary>
    public const double Poor = 3.3;

    /// <summary>From here on it sounds like a real song: excerpts of a released one scored 3.9 to 4.1.</summary>
    public const double Good = 3.7;

    public string Describe()
    {
        var verdict = Musicality < Poor ? "it sounds bad (a released song scores about 4)" : Musicality < Good ? "passable, not good (a released song scores about 4)" : "it sounds like a real song";
        return string.Create(CultureInfo.InvariantCulture,
            $"musicality {Musicality:0.0} of 5: {verdict}; coherence {Coherence:0.0}, memorability {Memorability:0.0}, structure {Clarity:0.0}, natural vocals {Naturalness:0.0}");
    }
}

/// <summary>
/// Listens to a song and says how good it sounds, so a bad one fails like a warped face does: no one
/// has to hear every song made. It is SongEval (ASLP-lab), a scorer trained on 2,400 songs rated by
/// musicians, over MuQ's music features, run by ComfyUI's own Python (which has PyTorch). Meta's
/// Audiobox Aesthetics was tried first: it rated the two test videos' songs 8.2 of 10 for production
/// quality, the songs the user called garbage, as it hears how clean a recording is rather than
/// whether it is music; SongEval gave them 2.4 and 2.7 of 5, and a released song about 4.
/// </summary>
/// <remarks>
/// Set up once in BlackBox's Data\tools\songeval: MuQ and nnAudio by pip (no dependencies, into
/// that folder: ComfyUI's own packages are left alone), SongEval's 100 MB head from GitHub (its
/// SHA-256 checked), MuQ's 1.3 GB model from Hugging Face on first use. MuQ's weights are CC BY-NC
/// 4.0: for the user's own videos.
/// </remarks>
public sealed class SongJudge(string folder)
{
    private const string HeadUrl = "https://raw.githubusercontent.com/ASLP-lab/SongEval/848fb2ff3a2a9d64dcb20d46f07238c28abd7add/ckpt/model.safetensors";
    private const string HeadSha256 = "0cb1eb8be02b96e9949ca3a2b66997c5ac7d9da3567240df4dbfc2048eb98efd";

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public string Folder { get; } = folder;

    /// <summary>The Python that runs the judge: ComfyUI's own (a portable build's or a venv's), which has PyTorch.</summary>
    public static string? PythonOf(ComfyHub hub) =>
        hub.SavedInstall is { Kind: ComfyInstallKind.Portable or ComfyInstallKind.Venv } install && File.Exists(install.Program) ? install.Program : null;

    /// <summary>
    /// Scores each sound file, its first <paramref name="seconds"/> (the part a video uses; 0: all of
    /// it); a file it couldn't score is missing from the answer. Throws <see cref="VideoException"/>
    /// when the judge can't run at all (no ComfyUI Python, no network the first time).
    /// </summary>
    public async Task<Dictionary<string, SongScore>> ScoreAsync(IReadOnlyList<string> files, string python, string ffmpeg, double seconds, Action<string>? progress, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SetUpAsync(python, progress, ct).ConfigureAwait(false);
            progress?.Invoke(files.Count == 1 ? "Listening to the song…" : $"Listening to the {files.Count} songs…");
            var psi = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-s", Path.Combine(Folder, "songeval.py"), Path.Combine(Folder, "head.safetensors"), ffmpeg,
                                      Math.Max(0, seconds).ToString("0.###", CultureInfo.InvariantCulture) }.Concat(files))
                psi.ArgumentList.Add(a);
            // MuQ's model is downloaded here, not into the user's own Hugging Face cache.
            psi.Environment["HF_HOME"] = Path.Combine(Folder, "hf");
            psi.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            using var process = Process.Start(psi) ?? throw new VideoException("The song judge didn't start.");
            var errors = process.StandardError.ReadToEndAsync(ct);
            var scores = new Dictionary<string, SongScore>(StringComparer.OrdinalIgnoreCase);
            while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (!line.StartsWith('{'))
                    continue;
                try
                {
                    if (JsonNode.Parse(line) is JsonObject o && (string?)o["path"] is { } path)
                        scores[path] = new SongScore((double)o["Coherence"]!, (double)o["Musicality"]!, (double)o["Memorability"]!, (double)o["Clarity"]!, (double)o["Naturalness"]!);
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException) { }
            }
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (scores.Count == 0)
            {
                var said = (await errors.ConfigureAwait(false)).Split('\n').Where(l => l.Trim().Length > 0).TakeLast(6);
                throw new VideoException("The song judge couldn't listen to it:\n" + string.Join("\n", said));
            }
            return scores;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new VideoException("The song judge took too long.");
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Puts the judge's script, packages and head in its folder, the first time.</summary>
    private async Task SetUpAsync(string python, Action<string>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        await File.WriteAllTextAsync(Path.Combine(Folder, "songeval.py"), Script, ct).ConfigureAwait(false);
        var packages = Path.Combine(Folder, "pkg");
        if (!Directory.Exists(Path.Combine(packages, "muq")) || !Directory.Exists(Path.Combine(packages, "nnAudio")) || !Directory.Exists(Path.Combine(packages, "easydict")))
        {
            progress?.Invoke("Setting up the song judge (once): MuQ and nnAudio…");
            var psi = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-m", "pip", "install", "--no-deps", "--disable-pip-version-check", "-q", "--target", packages, "muq==0.1.0", "nnAudio==0.3.4", "easydict==1.13" })
                psi.ArgumentList.Add(a);
            using var pip = Process.Start(psi) ?? throw new VideoException("pip didn't start to set up the song judge.");
            var said = pip.StandardError.ReadToEndAsync(ct);
            await pip.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await pip.WaitForExitAsync(ct).ConfigureAwait(false);
            if (pip.ExitCode != 0)
                throw new VideoException($"The song judge's packages couldn't be installed (pip exit code {pip.ExitCode}): " + (await said.ConfigureAwait(false)).Trim().Clip(600));
        }
        var head = Path.Combine(Folder, "head.safetensors");
        if (!File.Exists(head))
        {
            progress?.Invoke("Setting up the song judge (once): SongEval's model…");
            await Web.Downloader.DownloadAsync(new Uri(HeadUrl), head, HeadSha256, progress, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The scorer, run by ComfyUI's Python: songeval.py &lt;head&gt; &lt;ffmpeg&gt; &lt;file&gt;…, a JSON line per file.</summary>
    private const string Script = """
        # SongEval (ASLP-lab, Apache-2.0) over MuQ (Tencent, model CC BY-NC 4.0), written by BlackBox.
        # songeval.py <head.safetensors> <ffmpeg> <seconds> <file>... prints a JSON line per file, each score 1 to 5,
        # for its first <seconds> (0: all of it).
        import json, os, sys, types, subprocess, tempfile, wave
        sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "pkg"))
        # MuQ's package also loads MuQ-MuLan (music and text), which needs x_clip: only the music model is used.
        for name in ["x_clip", "x_clip.tokenizer"]:
            stub = types.ModuleType(name)
            stub.tokenizer = types.SimpleNamespace(vocab_size=49408)
            sys.modules[name] = stub
        import numpy as np
        import torch
        import torch.nn as nn
        from safetensors.torch import load_file
        from muq import MuQ


        class Generator(nn.Module):
            def __init__(self, in_features=1024, ffd_hidden_size=4096, num_classes=5, attn_layer_num=4):
                super().__init__()
                self.attn = nn.ModuleList([nn.MultiheadAttention(embed_dim=in_features, num_heads=8, dropout=0.2, batch_first=True) for _ in range(attn_layer_num)])
                self.ffd = nn.Sequential(nn.Linear(in_features, ffd_hidden_size), nn.ReLU(), nn.Linear(ffd_hidden_size, in_features))
                self.dropout = nn.Dropout(0.2)
                self.fc = nn.Linear(in_features * 2, num_classes)
                self.proj = nn.Tanh()

            def forward(self, ssl_feature):
                ssl_feature = self.ffd(ssl_feature)
                tmp = ssl_feature
                for attn in self.attn:
                    tmp, _ = attn(tmp, tmp, tmp)
                x = self.dropout(torch.concat([torch.mean(tmp, dim=1), torch.max(ssl_feature, dim=1)[0]], dim=1))
                return self.proj(self.fc(x)) * 2.0 + 3


        device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
        head = Generator().to(device).eval()
        head.load_state_dict(load_file(sys.argv[1], device="cpu"), strict=False)
        muq = MuQ.from_pretrained("OpenMuQ/MuQ-large-msd-iter").to(device).eval()
        names = ["Coherence", "Musicality", "Memorability", "Clarity", "Naturalness"]
        tmp = os.path.join(tempfile.gettempdir(), "blackbox-songeval-%d.wav" % os.getpid())
        seconds = float(sys.argv[3]) if float(sys.argv[3]) > 0 else 300
        with torch.no_grad():
            for path in sys.argv[4:]:
                try:
                    subprocess.run([sys.argv[2], "-hide_banner", "-loglevel", "error", "-y", "-i", path, "-vn", "-ac", "1", "-ar", "24000", "-t", str(seconds), "-c:a", "pcm_s16le", tmp], check=True)
                    with wave.open(tmp, "rb") as w:
                        pcm = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(np.float32) / 32768.0
                    hidden = muq(torch.tensor(pcm).unsqueeze(0).to(device), output_hidden_states=True)["hidden_states"][6]
                    s = head(hidden).squeeze(0)
                    print(json.dumps({"path": path, **{n: round(s[i].item(), 3) for i, n in enumerate(names)}}), flush=True)
                except Exception as e:
                    print("could not score %s: %s" % (path, e), file=sys.stderr, flush=True)
        try:
            os.remove(tmp)
        except OSError:
            pass
        """;
}
