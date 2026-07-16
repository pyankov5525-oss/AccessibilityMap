using System.Net.Http;
using System.Net.Http.Headers;

namespace AccessibilityMap.Services;

public class AuthHandler : DelegatingHandler
{
    private readonly AuthState _auth;

    public AuthHandler(HttpMessageHandler innerHandler, AuthState auth) : base(innerHandler)
    {
        _auth = auth;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_auth.Token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _auth.Token);
        }

        var response = await base.SendAsync(request, cancellationToken);

        // Если после смены БД/деплоя старый JWT из localStorage больше не подходит,
        // очищаем сессию. Иначе пользователь визуально «вошёл», но профиль/API дают 401.
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && !string.IsNullOrEmpty(_auth.Token))
        {
            await _auth.ClearAsync();
        }

        return response;
    }
}
