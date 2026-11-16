using System.Net;
using System.Net.Http.Headers;

namespace AgenticSdr.Station;

/// <summary>
/// Forwards receive controls to the Host on port 5088. The desk does not open a second RadioSession;
/// when the receiver already has the radio, listen and decode run there.
/// </summary>
public sealed class StationReceiver : IDisposable
{
    readonly HttpClient http;
    readonly string receiverBase;

    public StationReceiver(string? receiverBase = "http://127.0.0.1:5088")
    {
        this.receiverBase = string.IsNullOrWhiteSpace(receiverBase)
            ? "http://127.0.0.1:5088"
            : receiverBase.TrimEnd('/');
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(2),
            AllowAutoRedirect = false
        };
        http = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public string ReceiverBase => receiverBase;

    public Task ProxyJsonAsync(HttpContext context, string pathAndQuery, HttpMethod method, CancellationToken ct) =>
        ProxyAsync(context, pathAndQuery, method, copyBody: method != HttpMethod.Get && method != HttpMethod.Head, ct);

    public async Task ProxyLiveAsync(HttpContext context, CancellationToken ct)
    {
        string query = context.Request.QueryString.HasValue ? context.Request.QueryString.Value! : "";
        await ProxyAsync(context, "/api/live" + query, HttpMethod.Get, copyBody: false, ct);
    }

    async Task ProxyAsync(HttpContext context, string pathAndQuery, HttpMethod method, bool copyBody, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, receiverBase + pathAndQuery);
        if (copyBody && context.Request.ContentLength is > 0 or null)
        {
            request.Content = new StreamContent(context.Request.Body);
            if (MediaTypeHeaderValue.TryParse(context.Request.ContentType, out MediaTypeHeaderValue? type))
                request.Content.Headers.ContentType = type;
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "The receiver on port 5088 is not reachable. Start it with make run, then open the radio there or from this panel."
                }, ct);
            }

            return;
        }

        using (response)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers)
            {
                if (IsHopByHop(header.Key))
                    continue;
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }

            if (response.Content is not null)
            {
                foreach (KeyValuePair<string, IEnumerable<string>> header in response.Content.Headers)
                {
                    if (IsHopByHop(header.Key) || header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                        continue;
                    context.Response.Headers[header.Key] = header.Value.ToArray();
                }

                // Live PCM uses these Host headers for Web Audio.
                CopyAudioHeader(response, context, "X-Audio-Rate");
                CopyAudioHeader(response, context, "X-Audio-Channels");
                CopyAudioHeader(response, context, "X-Tune-Hz");
                CopyAudioHeader(response, context, "X-Mode");
                CopyAudioHeader(response, context, "X-Source");
                CopyAudioHeader(response, context, "X-Wbfm-Program");

                await using Stream upstream = await response.Content.ReadAsStreamAsync(ct);
                await upstream.CopyToAsync(context.Response.Body, ct);
            }
        }
    }

    static void CopyAudioHeader(HttpResponseMessage response, HttpContext context, string name)
    {
        if (response.Headers.TryGetValues(name, out IEnumerable<string>? values) ||
            (response.Content is not null && response.Content.Headers.TryGetValues(name, out values)))
        {
            context.Response.Headers[name] = values.ToArray();
        }
    }

    static bool IsHopByHop(string name) =>
        name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
        || name.Equals("TE", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Trailer", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase);

    public void Dispose() => http.Dispose();
}
