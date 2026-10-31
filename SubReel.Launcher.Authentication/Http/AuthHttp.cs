using System.Net;
using System.Text;
using System.Text.Json;
using SubReel.Launcher.Application.Authentication;

namespace SubReel.Launcher.Authentication.Http;

/// <summary>Общие мелочи HTTP для всех шагов входа. Тела ответов и заголовки в сообщения об ошибках и в журнал не попадают.</summary>
internal static class AuthHttp
{
    public static HttpRequestMessage Form(string url, params (string Key, string Value)[] fields)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))),
        };
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    public static HttpRequestMessage Json(HttpMethod method, string url, object payload)
    {
        var request = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    public static HttpRequestMessage Bearer(string url, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    /// <summary>Отправляет запрос; сетевые сбои и тайм-ауты превращаются в <see cref="AuthFailureReason.Network"/>, отмена пользователем остаётся отменой.</summary>
    public static async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (response.StatusCode, body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AuthenticationException(AuthFailureReason.Network, "Сервер не ответил вовремя. Проверьте подключение к интернету и повторите попытку.");
        }
        catch (HttpRequestException ex)
        {
            throw new AuthenticationException(AuthFailureReason.Network, "Не удалось связаться с сервером. Проверьте подключение к интернету.", ex);
        }
        finally
        {
            request.Dispose();
        }
    }

    /// <summary>Сбои на стороне сервера и ограничение частоты — временные: токены не трогаем.</summary>
    public static void ThrowIfTemporary(HttpStatusCode status, string service)
    {
        if ((int)status >= 500 || status == HttpStatusCode.TooManyRequests)
            throw new AuthenticationException(AuthFailureReason.Network, $"{service} временно недоступен или перегружен (код {(int)status}). Повторите попытку позже.");
    }

    public static bool IsSuccess(HttpStatusCode status) => (int)status is >= 200 and < 300;

    public static JsonDocument? TryParse(string body)
    {
        try { return JsonDocument.Parse(body); }
        catch (JsonException) { return null; }
    }

    public static string? Str(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static long? Num(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
}
