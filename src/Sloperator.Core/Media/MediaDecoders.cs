using Sloperator.Core.Platform;

namespace Sloperator.Core.Media;

public static class MediaDecoders
{
    /// <summary>
    /// The OS's own decoders, and ffmpeg for what they can't read: on the PATH, among the agent's
    /// portable tools, or the copy that comes with ComfyUI.
    /// </summary>
    public static IMediaDecoder Create(IPlatformServices platform, Settings.AppPaths paths, Comfy.ComfyHub? comfy)
    {
        var ffmpeg = new FfmpegDecoder(() => FfmpegFolders(paths, comfy));
        return platform.Media is { } os ? new FallbackDecoder(os, ffmpeg) : ffmpeg;
    }

    private static IEnumerable<string> FfmpegFolders(Settings.AppPaths paths, Comfy.ComfyHub? comfy)
    {
        yield return paths.ToolsDir;
        if (comfy?.SavedInstall is { } install)
        {
            yield return Path.Combine(install.Root, "python_embeded", "Lib", "site-packages", "imageio_ffmpeg", "binaries");
            yield return Path.Combine(install.Root, "venv", "Lib", "site-packages", "imageio_ffmpeg", "binaries");
            yield return Path.Combine(install.Root, ".venv", "Lib", "site-packages", "imageio_ffmpeg", "binaries");
        }
    }
}
