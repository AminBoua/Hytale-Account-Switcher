using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HytaleAccounts;

public sealed class LocalAccountBridge : IAsyncDisposable
{
    public const string Origin = "http://wails.localhost";
    private readonly HttpListener listener = new();
    private readonly string capability;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Func<JsonElement, Task<bool>> command;
    private readonly Task serving;
    private readonly List<Task> pending = [];
    private bool disposed;
    private int scriptLoads, stateReads;
    public object Evidence => new { scriptLoads = Volatile.Read(ref scriptLoads), authenticatedStateReads = Volatile.Read(ref stateReads) };
    private byte[] state = JsonSerializer.SerializeToUtf8Bytes(new { profiles = Array.Empty<object>(), busy = false, message = "Connecting account backend…" });
    public string Endpoint { get; }
    public int Port => new Uri(Endpoint).Port;
    public string Capability => capability;
    // Only the ephemeral bridge capability crosses into the private frontend.
    public string Bootstrap => $"<script src=\"{Endpoint}{capability}/ui.js\"></script>";

    // Integrated mode serves UI only. Legacy companion commands cannot reach credentials.
    public static LocalAccountBridge ForNativeUi(int? port = null, string? capability = null) =>
        new(_ => Task.FromResult(false), port, capability);

    public LocalAccountBridge(Func<JsonElement, Task<bool>> command, int? installedPort = null, string? installedCapability = null)
    {
        this.command = command;
        capability = installedCapability ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        for (var attempt = 0; ; attempt++)
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = installedPort ?? ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            Endpoint = $"http://127.0.0.1:{port}/";
            listener.Prefixes.Clear(); listener.Prefixes.Add(Endpoint);
            try { listener.Start(); break; }
            catch (HttpListenerException) when (installedPort is null && attempt < 9) { }
        }
        serving = Serve();
    }
    public Task Update(object data) { Volatile.Write(ref state, JsonSerializer.SerializeToUtf8Bytes(data)); return Task.CompletedTask; }
    private bool Authorized(string? value) => value is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes("Bearer " + capability), Encoding.UTF8.GetBytes(value));
    private async Task Serve()
    {
        while (!lifetime.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { break; }
            pending.RemoveAll(task => task.IsCompleted);
            if (pending.Count >= 16) { context.Response.StatusCode = 503; CloseResponse(context.Response); continue; }
            // Bound concurrent work as well as request size and duration.
            pending.Add(Respond(context));
        }
    }
    private async Task Respond(HttpListenerContext context)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var request = context.Request; var response = context.Response;
        try
        {
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            if (request.Headers["Host"] != new Uri(Endpoint).Authority || request.Url?.Query != "" ||
                request.RemoteEndPoint is null || !IPAddress.IsLoopback(request.RemoteEndPoint.Address))
            { response.StatusCode = 403; return; }
            var origin = request.Headers["Origin"];
            var path = request.Url!.AbsolutePath;
            if (path == "/" + capability + "/ui.js" && request.HttpMethod == "GET")
            {
                // Script requests may omit Origin; require the exact referring Wails origin.
                if ((origin is not null && origin != Origin) || !Uri.TryCreate(request.Headers["Referer"], UriKind.Absolute, out var referer) || referer.GetLeftPart(UriPartial.Authority) != Origin)
                { response.StatusCode = 403; return; }
                using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("HytaleAccounts.NativeDropdown.js") ?? throw new IOException();
                using var reader = new StreamReader(resource);
                var script = await reader.ReadToEndAsync(timeout.Token);
                response.ContentType = "application/javascript; charset=utf-8";
                await Write(response, Encoding.UTF8.GetBytes(script), timeout.Token);
                Interlocked.Increment(ref scriptLoads); return;
            }
            if (origin != Origin) { response.StatusCode = 403; return; }
            response.Headers["Access-Control-Allow-Origin"] = Origin;
            response.Headers["Vary"] = "Origin";
            if (request.HttpMethod == "OPTIONS")
            {
                if (path is not ("/state" or "/command") || request.Headers["Access-Control-Request-Method"] != (path == "/state" ? "GET" : "POST") ||
                    request.Headers["Access-Control-Request-Headers"]?.Split(',').Any(h => h.Trim().ToLowerInvariant() is not ("authorization" or "content-type")) != false)
                { response.StatusCode = 403; return; }
                response.Headers["Access-Control-Allow-Methods"] = "GET, POST";
                response.Headers["Access-Control-Allow-Headers"] = "Authorization, Content-Type";
                response.StatusCode = 204; return;
            }
            if (!Authorized(request.Headers["Authorization"])) { response.StatusCode = 401; return; }
            if (path == "/state" && request.HttpMethod == "GET")
            { response.ContentType = "application/json"; await Write(response, Volatile.Read(ref state), timeout.Token); Interlocked.Increment(ref stateReads); return; }
            if (path != "/command") { response.StatusCode = 404; return; }
            if (request.HttpMethod != "POST") { response.StatusCode = 405; return; }
            if (!string.Equals(request.ContentType?.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase)) { response.StatusCode = 415; return; }
            if (request.ContentLength64 is < 1 or > 4096) { response.StatusCode = 413; return; }
            var payload = new byte[(int)request.ContentLength64];
            await request.InputStream.ReadExactlyAsync(payload, timeout.Token);
            using var json = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 8 });
            response.StatusCode = await command(json.RootElement.Clone()) ? 204 : 400;
        }
        catch (Exception e)
        {
            try { response.StatusCode = e is JsonException ? 400 : e is OperationCanceledException ? 408 : 500; }
            catch (ObjectDisposedException) { }
        }
        finally { CloseResponse(response); }
    }
    internal static void CloseResponse(HttpListenerResponse response)
    {
        try { response.Close(); }
        catch (Exception e) when (e is HttpListenerException or InvalidOperationException or IOException)
        {
            // A closing launcher can disconnect before the advertised response body finishes.
            try { response.Abort(); }
            catch (Exception aborted) when (aborted is HttpListenerException or ObjectDisposedException) { }
        }
    }
    private static async Task Write(HttpListenerResponse response, byte[] data, CancellationToken ct)
    { response.ContentLength64 = data.Length; await response.OutputStream.WriteAsync(data, ct); }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel(); listener.Close(); await serving;
        await Task.WhenAll(pending); lifetime.Dispose();
    }
}
