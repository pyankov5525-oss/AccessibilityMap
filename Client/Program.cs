using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using System.Net.Http;
using AccessibilityMap;
using AccessibilityMap.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Авторизация: состояние + handler, который подставляет JWT-токен во все запросы
builder.Services.AddScoped<AuthState>();
builder.Services.AddScoped<AuthHandler>();
builder.Services.AddScoped(sp =>
{
    var handler = new AuthHandler(new HttpClientHandler(), sp.GetRequiredService<AuthState>());
    return new HttpClient(handler) { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) };
});

await builder.Build().RunAsync();
