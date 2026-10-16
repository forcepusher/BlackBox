using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace BlackBox.Core.Comfy;

/// <summary>A ComfyUI server's error answer to a request, with its text (a validation error is JSON).</summary>
public sealed class ComfyApiException(string message, HttpStatusCode? status = null, string? body = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
    public string? Body { get; } = body;
}

/// <summary>
/// ComfyUI's HTTP API, as its own web page uses it: node definitions, models, templates and saved
/// workflows, the queue, results and files. One instance per server address.
/// </summary>
public sealed class ComfyApi(Uri baseUrl)
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        AutomaticDecompression = DecompressionMethods.All,
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public Uri BaseUrl { get; } = baseUrl;

    /// <summary>Whether the server is on this PC, so its files can be read in place.</summary>
    public bool IsLocal => BaseUrl.IsLoopback || BaseUrl.Host is "localhost" or "127.0.0.1" or "[::1]" or "0.0.0.0";

    private Uri Url(string pathAndQuery) => new(BaseUrl, pathAndQuery.TrimStart('/'));

    /// <summary>GET returning the body as text; an HTTP error becomes a <see cref="ComfyApiException"/>.</summary>
    public async Task<string> GetTextAsync(string path, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));
        try
        {
            using var response = await Http.GetAsync(Url(path), HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new ComfyApiException($"ComfyUI answered {(int)response.StatusCode} {response.ReasonPhrase} to {path}.", response.StatusCode, text);
            return text;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ComfyApiException($"ComfyUI did not answer {path} in time.");
        }
        catch (HttpRequestException ex)
        {
            throw new ComfyApiException($"ComfyUI can't be reached at {BaseUrl}: {ex.Message}");
        }
    }

    public async Task<JsonNode?> GetJsonAsync(string path, CancellationToken ct, TimeSpan? timeout = null) =>
        JsonNode.Parse(await GetTextAsync(path, ct, timeout).ConfigureAwait(false));

    public async Task<byte[]> GetBytesAsync(string path, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromMinutes(5));
        try
        {
            using var response = await Http.GetAsync(Url(path), cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new ComfyApiException($"ComfyUI answered {(int)response.StatusCode} {response.ReasonPhrase} to {path}.", response.StatusCode);
            return await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ComfyApiException($"ComfyUI did not send {path} in time.");
        }
        catch (HttpRequestException ex)
        {
            throw new ComfyApiException($"ComfyUI can't be reached at {BaseUrl}: {ex.Message}");
        }
    }

    /// <summary>POST with a JSON body; returns the status and the answer's text (ComfyUI answers a bad workflow with 400 and JSON).</summary>
    public async Task<(HttpStatusCode Status, string Body)> PostJsonAsync(string path, JsonNode body, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));
        try
        {
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync(Url(path), content, cts.Token).ConfigureAwait(false);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ComfyApiException($"ComfyUI did not answer {path} in time.");
        }
        catch (HttpRequestException ex)
        {
            throw new ComfyApiException($"ComfyUI can't be reached at {BaseUrl}: {ex.Message}");
        }
    }

    /// <summary>The server's version, devices and arguments; null when nothing answers there.</summary>
    public async Task<JsonObject?> TrySystemStatsAsync(CancellationToken ct, TimeSpan? timeout = null)
    {
        try
        {
            return await GetJsonAsync("system_stats", ct, timeout ?? TimeSpan.FromSeconds(3)).ConfigureAwait(false) is JsonObject o && o["system"] is JsonObject ? o : null;
        }
        catch (Exception ex) when (ex is ComfyApiException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Puts a local file in the server's input folder (what LoadImage, LoadAudio and LoadVideo
    /// offer) and returns the name to give them. A file of the same name and size is not sent twice.
    /// </summary>
    public async Task<string> UploadAsync(string localPath, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        await using var stream = File.OpenRead(localPath);
        var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "image", Path.GetFileName(localPath));
        form.Add(new StringContent("input"), "type");
        form.Add(new StringContent("false"), "overwrite");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            using var response = await Http.PostAsync(Url("upload/image"), form, cts.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new ComfyApiException($"ComfyUI refused the upload of {Path.GetFileName(localPath)}: {(int)response.StatusCode} {text.Clip(300)}");
            var answer = JsonNode.Parse(text);
            var name = (string?)answer?["name"] ?? Path.GetFileName(localPath);
            var subfolder = (string?)answer?["subfolder"];
            return string.IsNullOrEmpty(subfolder) ? name : subfolder + "/" + name;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ComfyApiException($"The upload of {Path.GetFileName(localPath)} to ComfyUI took too long.");
        }
        catch (HttpRequestException ex)
        {
            throw new ComfyApiException($"ComfyUI can't be reached at {BaseUrl}: {ex.Message}");
        }
    }

    /// <summary>Asks the server to unload its models and free memory once it is idle.</summary>
    public Task FreeAsync(CancellationToken ct) =>
        PostJsonAsync("free", new JsonObject { ["unload_models"] = true, ["free_memory"] = true }, ct);

    /// <summary>Stops the job if it is the one running, and takes it out of the queue if it waits there.</summary>
    public async Task CancelAsync(string promptId, CancellationToken ct)
    {
        await PostJsonAsync("interrupt", new JsonObject { ["prompt_id"] = promptId }, ct).ConfigureAwait(false);
        await PostJsonAsync("queue", new JsonObject { ["delete"] = new JsonArray(promptId) }, ct).ConfigureAwait(false);
    }

    /// <summary>The web socket the server reports progress on, for this client.</summary>
    public Uri WebSocketUrl(string clientId)
    {
        var b = new UriBuilder(BaseUrl) { Scheme = BaseUrl.Scheme == "https" ? "wss" : "ws" };
        b.Path = b.Path.TrimEnd('/') + "/ws";
        b.Query = "clientId=" + Uri.EscapeDataString(clientId);
        return b.Uri;
    }

    /// <summary>The link to a file the server made or holds (output, input or temp).</summary>
    public string ViewPath(string filename, string? subfolder, string type) =>
        $"view?filename={Uri.EscapeDataString(filename)}&subfolder={Uri.EscapeDataString(subfolder ?? "")}&type={Uri.EscapeDataString(type)}";
}
