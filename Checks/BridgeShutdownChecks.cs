using System.Net.Http;
using System.Net;
using System.Net.Sockets;
using HytaleAccounts;

internal static class BridgeShutdownChecks
{
    public static async Task Run(Action<bool, string> check)
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
