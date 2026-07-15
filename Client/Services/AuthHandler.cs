using System.Net.Http;
using System.Net.Http.Headers;

namespace AccessibilityMap.Client.Services;

public class AuthHandler : DelegatingHandler
{
    private readonly AuthState _auth;

    public AuthHandler(HttpMessageHandler innerHandler, AuthState auth) : base(innerHandler)
    {
        _auth = auth;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_auth.Token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _auth.Token);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
