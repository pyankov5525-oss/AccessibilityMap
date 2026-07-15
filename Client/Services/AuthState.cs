using Microsoft.JSInterop;
using System.Text.Json;

namespace AccessibilityMap.Services;

public class AuthState
{
    private readonly IJSRuntime _js;
    public AuthState(IJSRuntime js) { _js = js; }

    public string Token { get; private set; } = "";
    public string UserName { get; private set; } = "";
    public string Role { get; private set; } = "";
    public bool IsAuthenticated => !string.IsNullOrEmpty(Token);

    public event Action? OnChange;

    // Восстановить сессию из localStorage (чтобы при перезагрузке страницы не выкидывало)
    public async Task RestoreAsync()
    {
        try
        {
            var json = await _js.InvokeAsync<string>("localStorage.getItem", "am_auth");
            if (!string.IsNullOrEmpty(json) && json != "null")
            {
                var data = JsonSerializer.Deserialize<AuthData>(json);
                if (data is not null && !string.IsNullOrEmpty(data.Token))
                {
                    Token = data.Token;
                    UserName = data.UserName;
                    Role = data.Role;
                    OnChange?.Invoke();
                }
            }
        }
        catch { }
    }

    public async Task SetAsync(string token, string userName, string role)
    {
        Token = token;
        UserName = userName;
        Role = role;
        try
        {
            var json = JsonSerializer.Serialize(new AuthData { Token = token, UserName = userName, Role = role });
            await _js.InvokeVoidAsync("localStorage.setItem", "am_auth", json);
        }
        catch { }
        OnChange?.Invoke();
    }

    public async Task ClearAsync()
    {
        Token = "";
        UserName = "";
        Role = "";
        try { await _js.InvokeVoidAsync("localStorage.removeItem", "am_auth"); } catch { }
        OnChange?.Invoke();
    }

    private class AuthData
    {
        public string Token { get; set; } = "";
        public string UserName { get; set; } = "";
        public string Role { get; set; } = "";
    }
}
