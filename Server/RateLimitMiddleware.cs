using Microsoft.AspNetCore.Http;
using System.Collections.Concurrent;

namespace AccessibilityMap.Server;

// Простое in-memory ограничение частоты запросов по IP (скользящее окно).
// Защищает от спама/брутфорса эндпоинты входа, геокодера и добавления меток.
public class RateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private static readonly ConcurrentDictionary<string, (int Count, DateTime WindowStart)> _hits = new();
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    // лимиты: путь -> макс. запросов в минуту
    private static readonly Dictionary<string, int> Limits = new()
    {
        { "/api/auth/login", 10 },
        { "/api/placemarks/geocode", 30 },
        { "/api/placemarks/suggest", 60 },
        { "/api/placemarks", 20 }, // POST — добавление метки
    };

    public RateLimitMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        // Эндпоинт добавления метки ограничиваем только для POST (GET-список не трогаем)
        if (path == "/api/placemarks" && context.Request.Method != HttpMethods.Post)
        {
            await _next(context);
            return;
        }

        if (!Limits.TryGetValue(path, out int limit) || context.Request.Method == HttpMethods.Options)
        {
            await _next(context);
            return;
        }

        var ip = GetClientIp(context);
        var key = ip + "|" + path;
        var now = DateTime.UtcNow;

        if (_hits.Count > 5000) _hits.Clear(); // профилактика роста словаря

        _hits.AddOrUpdate(key,
            _ => (1, now),
            (_, v) => (now - v.WindowStart > Window) ? (1, now) : (v.Count + 1, v.WindowStart));

        if (_hits.TryGetValue(key, out var entry) && entry.Count > limit)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers["Retry-After"] = "60";
            await context.Response.WriteAsJsonAsync(new { error = "Слишком много запросов. Попробуйте позже." });
            return;
        }

        await _next(context);
    }

    private static string GetClientIp(HttpContext context)
    {
        var fwd = context.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrEmpty(fwd))
        {
            var first = fwd.Split(',')[0].Trim();
            if (!string.IsNullOrEmpty(first)) return first;
        }
        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
