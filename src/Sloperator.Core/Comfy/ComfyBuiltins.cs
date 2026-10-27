namespace Sloperator.Core.Comfy;

/// <summary>
/// Sloperator's own small workflows, listed with the templates as sloperator/…: the classic text to
/// image and image to image for any single-file checkpoint (SD 1.5, SD 2, SDXL). ComfyUI's own
/// templates are all for big new models now (Z-Image, Flux, Qwen-Image, 6 to 20 GB), and its
/// classic SD 1.5 ones are gone, so on a PC without a graphics card none of them was usable; these
/// run with a 2 GB SD 1.5 checkpoint on the CPU, and with any checkpoint on a graphics card. And foley
/// from a video (video to audio, MMAudio), which no template of ComfyUI's does.
/// </summary>
public static class ComfyBuiltins
{
    /// <param name="Workflow">The workflow in ComfyUI's API format.</param>
    /// <param name="Notes">What to change and how, shown with the template.</param>
    public sealed record Builtin(string Name, string Title, string Description, string Notes, string Workflow, IReadOnlyList<string> Tags);

    /// <summary>Model files workflows often name without a link, and where to download them.</summary>
    public static readonly IReadOnlyDictionary<string, (string Folder, string Url)> Known = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
    {
        ["v1-5-pruned-emaonly-fp16.safetensors"] = ("checkpoints", "https://huggingface.co/Comfy-Org/stable-diffusion-v1-5-archive/resolve/main/v1-5-pruned-emaonly-fp16.safetensors"),
        ["v1-5-pruned-emaonly.safetensors"] = ("checkpoints", "https://huggingface.co/Comfy-Org/stable-diffusion-v1-5-archive/resolve/main/v1-5-pruned-emaonly.safetensors"),
        ["sd_xl_base_1.0.safetensors"] = ("checkpoints", "https://huggingface.co/stabilityai/stable-diffusion-xl-base-1.0/resolve/main/sd_xl_base_1.0.safetensors"),
        ["sd_xl_turbo_1.0_fp16.safetensors"] = ("checkpoints", "https://huggingface.co/stabilityai/sdxl-turbo/resolve/main/sd_xl_turbo_1.0_fp16.safetensors"),
        ["4x-UltraSharp.pth"] = ("upscale_models", "https://huggingface.co/lokCX/4x-Ultrasharp/resolve/main/4x-UltraSharp.pth"),
        // MMAudio, for sloperator/video_to_audio: kijai's safetensors of it, in the folder his nodes add.
        ["mmaudio_large_44k_v2_fp16.safetensors"] = ("mmaudio", "https://huggingface.co/Kijai/MMAudio_safetensors/resolve/main/mmaudio_large_44k_v2_fp16.safetensors"),
        ["mmaudio_vae_44k_fp16.safetensors"] = ("mmaudio", "https://huggingface.co/Kijai/MMAudio_safetensors/resolve/main/mmaudio_vae_44k_fp16.safetensors"),
        ["mmaudio_synchformer_fp16.safetensors"] = ("mmaudio", "https://huggingface.co/Kijai/MMAudio_safetensors/resolve/main/mmaudio_synchformer_fp16.safetensors"),
        ["apple_DFN5B-CLIP-ViT-H-14-384_fp16.safetensors"] = ("mmaudio", "https://huggingface.co/Kijai/MMAudio_safetensors/resolve/main/apple_DFN5B-CLIP-ViT-H-14-384_fp16.safetensors"),
    };

    /// <summary>Where the custom nodes sloperator/video_to_audio runs come from.</summary>
    public const string MmAudioNodes = "https://github.com/kijai/ComfyUI-MMAudio";

    private const string Checkpoint = "v1-5-pruned-emaonly-fp16.safetensors";

    private const string CommonNotes =
        "Works with any single-file checkpoint in models/checkpoints: set 1.ckpt_name to another one (SD 1.5, SD 2.x or SDXL, a turbo or LCM one too; " +
        "action=models lists the installed ones, action=find_models looks for others online). 2.text is what to make, 3.text what to keep out. " +
        "For a turbo checkpoint use 4 to 8 steps and cfg 1.5 to 2; for an LCM one 6 steps, cfg 1.5 and sampler_name lcm. " +
        "A LoRA goes between the checkpoint and the rest: a LoraLoader node with model [\"1\", 0] and clip [\"1\", 1], whose outputs then take the place of 1's " +
        "in the sampler's model and the text nodes' clip; put its trigger words in 2.text.";

    public static readonly IReadOnlyList<Builtin> All =
    [
        new("text_to_image", "Text to image with a checkpoint",
            "A picture from a prompt with any SD 1.5, SD 2 or SDXL checkpoint; light enough for a PC without a graphics card (SD 1.5 on the CPU).",
            CommonNotes + " SD 1.5 makes 512 to 768 pixels well (4.width, 4.height), SDXL about 1024. " +
            "On the CPU, 20 steps of SD 1.5 at 512x512 take three to five minutes.",
            $$$"""
            {
              "1": {"class_type": "CheckpointLoaderSimple", "inputs": {"ckpt_name": "{{{Checkpoint}}}"}, "_meta": {"title": "Checkpoint"}},
              "2": {"class_type": "CLIPTextEncode", "inputs": {"text": "a lighthouse on a cliff at sunset, oil painting, detailed", "clip": ["1", 1]}, "_meta": {"title": "Prompt"}},
              "3": {"class_type": "CLIPTextEncode", "inputs": {"text": "blurry, low quality, deformed, watermark, text", "clip": ["1", 1]}, "_meta": {"title": "Negative prompt"}},
              "4": {"class_type": "EmptyLatentImage", "inputs": {"width": 512, "height": 512, "batch_size": 1}, "_meta": {"title": "Size"}},
              "5": {"class_type": "KSampler", "inputs": {"model": ["1", 0], "positive": ["2", 0], "negative": ["3", 0], "latent_image": ["4", 0],
                    "seed": 42, "steps": 20, "cfg": 7, "sampler_name": "dpmpp_2m", "scheduler": "karras", "denoise": 1}, "_meta": {"title": "Sampler"}},
              "6": {"class_type": "VAEDecode", "inputs": {"samples": ["5", 0], "vae": ["1", 2]}},
              "7": {"class_type": "SaveImage", "inputs": {"images": ["6", 0], "filename_prefix": "Sloperator"}}
            }
            """,
            ["image", "text to image", "SD 1.5", "SDXL", "checkpoint", "cpu"]),
        new("image_to_image", "Image to image with a checkpoint",
            "Repaints a picture from a prompt, keeping its layout: a photo as a watercolor or oil painting, a sketch made real, a new style or season. " +
            "Any SD 1.5, SD 2 or SDXL checkpoint; light enough for a PC without a graphics card (SD 1.5 on the CPU).",
            CommonNotes + " 4.image is the picture to start from (a full local path is uploaded). 7.denoise is how much changes: 0.3 keeps the picture " +
            "with a new touch, 0.5 to 0.65 repaints it in the new style keeping its layout, 0.8 keeps only its colours and rough shapes. " +
            "The picture is scaled to 5.megapixels first: 0.3 suits SD 1.5, 1.0 SDXL. On the CPU, 20 steps take three to five minutes.",
            $$$"""
            {
              "1": {"class_type": "CheckpointLoaderSimple", "inputs": {"ckpt_name": "{{{Checkpoint}}}"}, "_meta": {"title": "Checkpoint"}},
              "2": {"class_type": "CLIPTextEncode", "inputs": {"text": "a watercolor painting, soft colours, paper texture", "clip": ["1", 1]}, "_meta": {"title": "Prompt"}},
              "3": {"class_type": "CLIPTextEncode", "inputs": {"text": "blurry, low quality, deformed, watermark, text", "clip": ["1", 1]}, "_meta": {"title": "Negative prompt"}},
              "4": {"class_type": "LoadImage", "inputs": {"image": "example.png"}, "_meta": {"title": "Picture to start from"}},
              "5": {"class_type": "ImageScaleToTotalPixels", "inputs": {"image": ["4", 0], "upscale_method": "lanczos", "megapixels": 0.3}, "_meta": {"title": "Size to work at"}},
              "6": {"class_type": "VAEEncode", "inputs": {"pixels": ["5", 0], "vae": ["1", 2]}},
              "7": {"class_type": "KSampler", "inputs": {"model": ["1", 0], "positive": ["2", 0], "negative": ["3", 0], "latent_image": ["6", 0],
                    "seed": 42, "steps": 20, "cfg": 7, "sampler_name": "dpmpp_2m", "scheduler": "karras", "denoise": 0.6}, "_meta": {"title": "Sampler"}},
              "8": {"class_type": "VAEDecode", "inputs": {"samples": ["7", 0], "vae": ["1", 2]}},
              "9": {"class_type": "SaveImage", "inputs": {"images": ["8", 0], "filename_prefix": "Sloperator_img2img"}}
            }
            """,
            ["image", "image to image", "img2img", "edit", "style", "photo", "picture", "SD 1.5", "SDXL", "checkpoint", "cpu"]),
        new("video_to_audio", "Video to audio (foley) with MMAudio",
            "The sounds of a video, made to match what it shows and when (footsteps, a door, rain, an engine): MMAudio, from kijai's ComfyUI-MMAudio nodes.",
            "1.file is the video (a full local path is uploaded); 25 frames a second suit it, which is how the video tool hands its clips over. " +
            "5.duration is how many seconds to make (8 is what MMAudio learnt on; up to about 10 holds up), 5.prompt what is heard " +
            "(\"footsteps on gravel, wind\"; it can be left empty), 5.negative_prompt what to keep out. " +
            $"Its nodes are a custom node pack: clone {MmAudioNodes} into ComfyUI's custom_nodes folder, install its requirements.txt with ComfyUI's own Python " +
            "(the portable build: python_embeded\\python.exe -m pip install -r ComfyUI\\custom_nodes\\ComfyUI-MMAudio\\requirements.txt), then action=restart. " +
            "Its four models go into models/mmaudio (action=download with model_folder=mmaudio); the first run also fetches NVIDIA's BigVGAN vocoder by itself. " +
            "On a PC without a graphics card set 3.base_precision and 4.precision to fp32.",
            """
            {
              "1": {"class_type": "LoadVideo", "inputs": {"file": "example.mp4"}, "_meta": {"title": "Video"}},
              "2": {"class_type": "GetVideoComponents", "inputs": {"video": ["1", 0]}},
              "3": {"class_type": "MMAudioModelLoader", "inputs": {"mmaudio_model": "mmaudio_large_44k_v2_fp16.safetensors", "base_precision": "fp16"}},
              "4": {"class_type": "MMAudioFeatureUtilsLoader", "inputs": {"vae_model": "mmaudio_vae_44k_fp16.safetensors", "synchformer_model": "mmaudio_synchformer_fp16.safetensors",
                    "clip_model": "apple_DFN5B-CLIP-ViT-H-14-384_fp16.safetensors", "mode": "44k", "precision": "fp16"}},
              "5": {"class_type": "MMAudioSampler", "inputs": {"mmaudio_model": ["3", 0], "feature_utils": ["4", 0], "images": ["2", 0], "duration": 8, "steps": 25, "cfg": 4.5,
                    "seed": 42, "prompt": "", "negative_prompt": "music, speech, singing", "mask_away_clip": false, "force_offload": true}, "_meta": {"title": "Sampler"}},
              "6": {"class_type": "SaveAudioMP3", "inputs": {"audio": ["5", 0], "filename_prefix": "Sloperator_foley", "quality": "V0"}}
            }
            """,
            ["audio", "sound", "foley", "sfx", "sound effects", "video to audio", "mmaudio"]),
    ];

    private const string Prefix = "builtin:";

    /// <summary>The built-in workflows as templates, named sloperator/….</summary>
    public static IEnumerable<ComfyTemplate> Templates =>
        All.Select(b => new ComfyTemplate("sloperator/" + b.Name, b.Title, b.Description, Prefix + b.Name, "sloperator", b.Tags));

    /// <summary>The built-in workflow a template is, or null for one the server serves.</summary>
    public static Builtin? For(ComfyTemplate template) =>
        template.FetchPath.StartsWith(Prefix, StringComparison.Ordinal) ? All.FirstOrDefault(b => Prefix + b.Name == template.FetchPath) : null;
}
