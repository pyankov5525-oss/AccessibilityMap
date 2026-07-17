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
    public bool ProfileComplete { get; private set; } = true;
    public bool IsAuthenticated => !string.IsNullOrEmpty(Token);
    public bool CanWork => !IsAuthenticated || ProfileComplete;

    public event Action? OnChange;

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
                    ProfileComplete = data.ProfileComplete;
                    OnChange?.Invoke();
                }
            }
        }
        catch { }
    }

    public async Task SetAsync(string token, string userName, string role, bool profileComplete = true)
    {
        Token = token;
        UserName = userName;
        Role = role;
        ProfileComplete = profileComplete;
        try
        {
            var json = JsonSerializer.Serialize(new AuthData { Token = token, UserName = userName, Role = role, ProfileComplete = profileComplete });
            await _js.InvokeVoidAsync("localStorage.setItem", "am_auth", json);
        }
        catch { }
        OnChange?.Invoke();
    }

    public async Task SetProfileCompleteAsync(bool complete)
    {
        ProfileComplete = complete;
        if (IsAuthenticated)
            await SetAsync(Token, UserName, Role, complete);
        else
            OnChange?.Invoke();
    }

    public async Task ClearAsync()
    {
        Token = "";
        UserName = "";
        Role = "";
        ProfileComplete = true;
        try { await _js.InvokeVoidAsync("localStorage.removeItem", "am_auth"); } catch { }
        OnChange?.Invoke();
    }

    private class AuthData
    {
        public string Token { get; set; } = "";
        public string UserName { get; set; } = "";
        public string Role { get; set; } = "";
        public bool ProfileComplete { get; set; } = true;
    }
}
