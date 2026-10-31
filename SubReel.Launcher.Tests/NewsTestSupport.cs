using System.Net;
using SubReel.Launcher.Application.Abstractions;
using SubReel.Launcher.Application.News;
using SubReel.Launcher.Domain.News;

namespace SubReel.Launcher.Tests;

internal sealed class ManualTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class FakeNewsCache : INewsCache
{
    public NewsCacheSnapshot? Snapshot { get; set; }
    public int Writes { get; private set; }
    public Task<NewsCacheSnapshot?> ReadAsync(CancellationToken ct = default) => Task.FromResult(Snapshot);
    public Task WriteAsync(NewsCacheSnapshot snapshot, CancellationToken ct = default)
    {
        Snapshot = snapshot;
        Writes++;
        return Task.CompletedTask;
    }
}

internal sealed class FakeNewsSource : INewsSource
{
    public FakeNewsSource(string name, params NewsCategory[] categories)
    {
        Name = name;
        Categories = categories;
    }

    public string Name { get; }
    public bool IsConfigured { get; set; } = true;
    public IReadOnlyCollection<NewsCategory> Categories { get; }
    public List<NewsItem> Items { get; } = new();
    public Exception? Failure { get; set; }
    public int Calls { get; private set; }

    public Task<IReadOnlyList<NewsItem>> LoadAsync(CancellationToken ct)
    {
        Calls++;
        if (Failure is not null) throw Failure;
        return Task.FromResult<IReadOnlyList<NewsItem>>(Items.ToList());
    }
}

internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
    public List<string> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!.ToString());
        return Task.FromResult(_respond(request));
    }

    public static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
    public static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
}

internal sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;
    public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}
