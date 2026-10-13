using System.Net;
using System.Net.Http;
using System.Text;
using HytaleAccounts;

internal static class BridgeAuthenticationChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var commands = 0;
        await using var bridge = new LocalAccountBridge(_ => { Interlocked.Increment(ref commands); return Task.FromResult(true); });
        await using var other = LocalAccountBridge.ForNativeUi();
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
        check(bridge.Capability.Length == 43 && bridge.Capability != other.Capability &&
            bridge.Capability.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'), "bridges use distinct per-launch 256-bit capabilities");
        async Task Expect(string name, HttpStatusCode status, string path = "command", string? token = null,
            string? origin = LocalAccountBridge.Origin, string method = "POST", string? body = "{}",
            string contentType = "application/json", string? referer = null, string? host = null,
            string? preflightMethod = null, string? preflightHeaders = null, string? endpoint = null)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), (endpoint ?? bridge.Endpoint) + path);
            if (token != null) request.Headers.TryAddWithoutValidation("Authorization", token);
            if (origin != null) request.Headers.TryAddWithoutValidation("Origin", origin);
            if (referer != null) request.Headers.Referrer = new Uri(referer);
            if (host != null) request.Headers.Host = host;
            if (preflightMethod != null) request.Headers.Add("Access-Control-Request-Method", preflightMethod);
            if (preflightHeaders != null) request.Headers.Add("Access-Control-Request-Headers", preflightHeaders);
            if (body != null) request.Content = new StringContent(body, Encoding.UTF8, contentType);
            using var response = await http.SendAsync(request);
            check(response.StatusCode == status, name + $" ({(int)response.StatusCode})");
            if (status == HttpStatusCode.OK && path.EndsWith("/ui.js"))
                check((await response.Content.ReadAsStringAsync()).Contains("data-native-store"), "authenticated bootstrap serves the embedded dropdown");
        }
        var bearer = "Bearer " + bridge.Capability;
        foreach (var path in new[] { "state", "command" })
        {
            var method = path == "state" ? "GET" : "POST";
            await Expect("missing capability rejected for " + path, HttpStatusCode.Unauthorized, path, method: method);
            await Expect("wrong capability rejected for " + path, HttpStatusCode.Unauthorized, path, "Bearer " + other.Capability, method: method);
            await Expect("missing origin rejected for " + path, HttpStatusCode.Forbidden, path, bearer, origin: null, method: method);
            await Expect("foreign origin rejected for " + path, HttpStatusCode.Forbidden, path, bearer, "https://example.invalid", method);
            await Expect("query-bearing request rejected for " + path, HttpStatusCode.Forbidden, path + "?x=1", bearer, method: method);
        }
        await Expect("bearer scheme mismatch rejected", HttpStatusCode.Unauthorized, token: "Basic " + bridge.Capability);
        await Expect("local Host mismatch rejected", HttpStatusCode.Forbidden, token: bearer, host: "127.0.0.1:" + (bridge.Port == 65535 ? 65534 : bridge.Port + 1));
        var script = bridge.Capability + "/ui.js";
        await Expect("script requires native referrer", HttpStatusCode.Forbidden, script, method: "GET", body: null);
        await Expect("script rejects lookalike referrer", HttpStatusCode.Forbidden, script, method: "GET", body: null,
            referer: "http://wails.localhost.example.invalid/");
        await Expect("script rejects hostile origin even with native referrer", HttpStatusCode.Forbidden, script,
            origin: "https://example.invalid", method: "GET", body: null, referer: LocalAccountBridge.Origin + "/");
        await Expect("script URL requires matching capability", HttpStatusCode.Unauthorized, other.Capability + "/ui.js",
            method: "GET", body: null, referer: LocalAccountBridge.Origin + "/");
        await Expect("script accepts native referrer without Origin", HttpStatusCode.OK, script,
            origin: null, method: "GET", body: null, referer: LocalAccountBridge.Origin + "/launch-game");
        await Expect("valid preflight needs no capability", HttpStatusCode.NoContent, method: "OPTIONS", body: null,
            preflightMethod: "POST", preflightHeaders: "authorization, content-type");
        await Expect("preflight rejects unexpected headers", HttpStatusCode.Forbidden, method: "OPTIONS", body: null,
            preflightMethod: "POST", preflightHeaders: "authorization, x-unsafe");
        await Expect("preflight rejects wrong method", HttpStatusCode.Forbidden, method: "OPTIONS", body: null,
            preflightMethod: "DELETE", preflightHeaders: "authorization");
        await Expect("command rejects GET", HttpStatusCode.MethodNotAllowed, token: bearer, method: "GET", body: null);
        await Expect("command rejects non-JSON", HttpStatusCode.UnsupportedMediaType, token: bearer, contentType: "text/plain");
        await Expect("command rejects oversized JSON", HttpStatusCode.RequestEntityTooLarge, token: bearer, body: new string(' ', 4097));
        await Expect("command rejects empty body", HttpStatusCode.RequestEntityTooLarge, token: bearer, body: "");
        await Expect("command rejects malformed JSON", HttpStatusCode.BadRequest, token: bearer, body: "{");
        await Expect("command rejects excessive nesting", HttpStatusCode.BadRequest, token: bearer, body: new string('[', 10) + "0" + new string(']', 10));
        check(commands == 0, "rejected requests and preflight never invoke the command handler");
        await Expect("authorized state read succeeds", HttpStatusCode.OK, "state", bearer, method: "GET", body: null);
        await Expect("authorized command invokes handler", HttpStatusCode.NoContent, token: bearer);
        check(commands == 1, "only the authorized valid command reached the handler");
        await Expect("capability from another launch cannot be replayed", HttpStatusCode.Unauthorized, token: bearer, endpoint: other.Endpoint);
        await Expect("UI-only bridge refuses legacy commands", HttpStatusCode.BadRequest, token: "Bearer " + other.Capability, endpoint: other.Endpoint);
    }
}
