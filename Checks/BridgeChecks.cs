using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace HytaleAccounts.Checks;

internal static class BridgeChecks
{
    internal static async Task Run()
    {
        await Authentication(Check);
        await Limits(Check);
        await Shutdown(Check);
    }

    private static void Check(bool condition, string detail)
    {
        if (!condition) throw new Exception(detail);
    }

    private static async Task Authentication(Action<bool, string> check)
    {
        var commands = 0;
        await using var bridge = new LocalAccountBridge(_ => { Interlocked.Increment(ref commands); return Task.FromResult(true); });
        await using var other = new LocalAccountBridge(_ => Task.FromResult(false));
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
        await Expect("authenticated command refusal is returned to caller", HttpStatusCode.BadRequest, token: "Bearer " + other.Capability, endpoint: other.Endpoint);
    }

    private static async Task Limits(Action<bool, string> check)
    {
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false, MaxConnectionsPerServer = 64 })
            { Timeout = TimeSpan.FromSeconds(12) };
        var entered = new SemaphoreSlim(0);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var bridge = new LocalAccountBridge(_ => { Interlocked.Increment(ref calls); entered.Release(); return release.Task; });
        async Task<HttpStatusCode> Send(string? token = null, string path = "command")
        {
            using var request = new HttpRequestMessage(path == "state" ? HttpMethod.Get : HttpMethod.Post, bridge.Endpoint + path);
            request.Headers.Add("Origin", LocalAccountBridge.Origin);
            request.Headers.Add("Authorization", token ?? "Bearer " + bridge.Capability);
            if (path == "command") request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request);
            return response.StatusCode;
        }
        try
        {
            // Hold the body open after valid headers; no real browser or remote endpoint is involved.
            using (var slow = new TcpClient())
            {
                await slow.ConnectAsync(IPAddress.Loopback, bridge.Port);
                var bytes = Encoding.ASCII.GetBytes($"POST /command HTTP/1.1\r\nHost: 127.0.0.1:{bridge.Port}\r\nOrigin: {LocalAccountBridge.Origin}\r\nAuthorization: Bearer {bridge.Capability}\r\nContent-Type: application/json\r\nContent-Length: 2\r\n\r\n{{");
                var clock = Stopwatch.StartNew();
                await slow.GetStream().WriteAsync(bytes);
                using var reader = new StreamReader(slow.GetStream());
                var status = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(9));
                check(status?.Contains(" 408 ") == true && clock.Elapsed.TotalSeconds is >= 4 and < 9 && calls == 0,
                    "incomplete authorized body times out in five seconds without invoking handler");
            }
            check(await Send("Bearer invalid") == HttpStatusCode.Unauthorized && calls == 0,
                "unauthorized requests never enter handler after body timeout");
            var requests = new List<Task<HttpStatusCode>>();
            for (var i = 0; i < 16; i++)
            {
                requests.Add(Send());
                check(await entered.WaitAsync(TimeSpan.FromSeconds(2)), "handler admitted within concurrency budget " + (i + 1));
            }
            check(await Send() == HttpStatusCode.ServiceUnavailable && calls == 16,
                "seventeenth request is rejected without invoking handler");
            check(await Send("Bearer invalid") == HttpStatusCode.ServiceUnavailable && calls == 16,
                "unauthorized request at saturation never invokes handler");
            var statuses = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(9));
            check(statuses.All(s => s == HttpStatusCode.RequestTimeout), "stalled handlers return timeout responses");
            check(await Send() == HttpStatusCode.ServiceUnavailable && calls == 16,
                "timed-out handlers retain their slots until actual completion");
            check(await Send("Bearer invalid") == HttpStatusCode.Unauthorized && calls == 16,
                "unauthorized request after response timeout never invokes handler");
            check(await Send(path: "state") == HttpStatusCode.OK, "state reads recover while timed-out handlers are outstanding");
            release.SetResult(true);
            // Completion continuations release the handler slots; retry only a bounded 503 response.
            var deadline = Stopwatch.StartNew();
            HttpStatusCode recovered;
            do { recovered = await Send(); if (recovered == HttpStatusCode.ServiceUnavailable) await Task.Delay(10); }
            while (recovered == HttpStatusCode.ServiceUnavailable && deadline.Elapsed < TimeSpan.FromSeconds(2));
            check(recovered == HttpStatusCode.NoContent && calls == 17, "command service recovers after saturation clears");
        }
        finally { release.TrySetResult(true); await bridge.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)); entered.Dispose(); }

        var shutdownEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdownRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopping = new LocalAccountBridge(_ => { shutdownEntered.TrySetResult(); return shutdownRelease.Task; });
        using var requestAtShutdown = new HttpRequestMessage(HttpMethod.Post, stopping.Endpoint + "command");
        requestAtShutdown.Headers.Add("Origin", LocalAccountBridge.Origin);
        requestAtShutdown.Headers.Add("Authorization", "Bearer " + stopping.Capability);
        requestAtShutdown.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        var responseAtShutdown = http.SendAsync(requestAtShutdown);
        try
        {
            await shutdownEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await stopping.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            check(true, "shutdown completes with an uncooperative asynchronous handler");
        }
        finally
        {
            shutdownRelease.TrySetResult(true);
            await stopping.DisposeAsync();
            try { using var response = await responseAtShutdown; } catch (HttpRequestException) { }
        }

        using var unblock = new ManualResetEventSlim();
        var synchronousEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var synchronous = new LocalAccountBridge(_ =>
        { synchronousEntered.TrySetResult(); unblock.Wait(); return Task.FromResult(true); });
        using var blockingRequest = new HttpRequestMessage(HttpMethod.Post, synchronous.Endpoint + "command");
        blockingRequest.Headers.Add("Origin", LocalAccountBridge.Origin);
        blockingRequest.Headers.Add("Authorization", "Bearer " + synchronous.Capability);
        blockingRequest.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        var blockedResponse = http.SendAsync(blockingRequest);
        try
        {
            await synchronousEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            using var stateRequest = new HttpRequestMessage(HttpMethod.Get, synchronous.Endpoint + "state");
            stateRequest.Headers.Add("Origin", LocalAccountBridge.Origin);
            stateRequest.Headers.Add("Authorization", "Bearer " + synchronous.Capability);
            using var stateResponse = await http.SendAsync(stateRequest).WaitAsync(TimeSpan.FromSeconds(2));
            check(stateResponse.StatusCode == HttpStatusCode.OK, "synchronously blocked handler does not block the accept loop");
            using var timedOut = await blockedResponse.WaitAsync(TimeSpan.FromSeconds(9));
            check(timedOut.StatusCode == HttpStatusCode.RequestTimeout, "synchronously blocked handler also receives a timeout response");
            await synchronous.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            unblock.Set(); await synchronous.DisposeAsync();
            try { using var response = await blockedResponse; } catch (HttpRequestException) { }
        }
    }

    private static async Task Shutdown(Action<bool, string> check)
    {
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var listener = new HttpListener();
        var endpoint = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(endpoint); listener.Start();
        var incoming = listener.GetContextAsync();
        var reading = http.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead);
        var context = await incoming.WaitAsync(TimeSpan.FromSeconds(5));
        context.Response.ContentLength64 = 2;
        await context.Response.OutputStream.WriteAsync(new byte[] { 1 });
        using var response = await reading.WaitAsync(TimeSpan.FromSeconds(5));
        // Match the logged failure exactly: close after writing fewer bytes than promised.
        LocalAccountBridge.CloseResponse(context.Response);
        check(true, "bridge shutdown tolerates an unfinished response during launcher restart");
        listener.Close();
        LocalAccountBridge.CloseResponse(context.Response);
        check(true, "response cleanup also tolerates an already-disposed HTTP request queue");
        await using var replacement = new LocalAccountBridge(_ => Task.FromResult(true));
        using var next = new HttpRequestMessage(HttpMethod.Get, replacement.Endpoint + "state");
        next.Headers.Add("Origin", LocalAccountBridge.Origin);
        next.Headers.Add("Authorization", "Bearer " + replacement.Capability);
        using var ready = await http.SendAsync(next);
        check(ready.IsSuccessStatusCode, "replacement launcher bridge responds after interrupted shutdown");
    }
}
