using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using HytaleAccounts;

internal static class BridgeLimitChecks
{
    public static async Task Run(Action<bool, string> check)
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
}
