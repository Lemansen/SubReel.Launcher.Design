using System.Net;
using System.Text;

namespace SubReel.Launcher.Tests;

/// <summary>
/// HTTP-заглушка: ответы заранее выстроены в очередь по части адреса. Настоящей сети нет.
/// Лишний или непредусмотренный запрос — ошибка теста.
/// </summary>
internal sealed class ScriptedHandler : HttpMessageHandler
{
    internal sealed record Recorded(string Method, string Url, string Body, string? Authorization);

    private readonly List<(string Key, Queue<Func<Recorded, HttpResponseMessage>> Queue)> _routes = new();

    public List<Recorded> Requests { get; } = new();

    public void Respond(string urlPart, HttpStatusCode status, string json) =>
        Enqueue(urlPart, _ => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

    public void Fail(string urlPart, Exception exception) => Enqueue(urlPart, _ => throw exception);

    public int Count(string urlPart) => Requests.Count(r => r.Url.Contains(urlPart, StringComparison.Ordinal));

    private void Enqueue(string urlPart, Func<Recorded, HttpResponseMessage> responder)
    {
        var route = _routes.FirstOrDefault(r => r.Key == urlPart);
        if (route.Queue is null)
        {
            route = (urlPart, new Queue<Func<Recorded, HttpResponseMessage>>());
            _routes.Add(route);
        }
        route.Queue.Enqueue(responder);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new Recorded(request.Method.Method, request.RequestUri!.ToString(), body, request.Headers.Authorization?.ToString());
        Requests.Add(recorded);

        foreach (var (key, queue) in _routes)
        {
            if (!recorded.Url.Contains(key, StringComparison.Ordinal)) continue;
            if (queue.Count == 0) throw new InvalidOperationException($"Лишний запрос: {recorded.Method} {recorded.Url}");
            return queue.Dequeue()(recorded);
        }
        throw new InvalidOperationException($"Нет заглушки для запроса: {recorded.Method} {recorded.Url}");
    }
}
