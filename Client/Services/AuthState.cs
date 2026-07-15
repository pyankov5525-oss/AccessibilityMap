namespace AccessibilityMap.Client.Services;

public class AuthState
{
    public string Token { get; private set; } = "";
    public string UserName { get; private set; } = "";
    public string Role { get; private set; } = "";
    public bool IsAuthenticated => !string.IsNullOrEmpty(Token);

    public event Action? OnChange;

    public void Set(string token, string userName, string role)
    {
        Token = token;
        UserName = userName;
        Role = role;
        OnChange?.Invoke();
    }

    public void Clear()
    {
        Token = "";
        UserName = "";
        Role = "";
        OnChange?.Invoke();
    }
}
