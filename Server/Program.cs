using Microsoft.EntityFrameworkCore;
using AccessibilityMap.Server.Data;
using System.IO;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite("Data Source=accessibility.db"));

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

// На хостинге (Render/Azure) порт задаётся переменной окружения PORT (Render) / WEBSITES_PORT (Azure).
var port = Environment.GetEnvironmentVariable("PORT") ?? Environment.GetEnvironmentVariable("WEBSITES_PORT");
if (!string.IsNullOrEmpty(port))
{
    Environment.SetEnvironmentVariable("ASPNETCORE_URLS", $"http://*:{port}");
}

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
//app.UseHttpsRedirection();
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.MapControllers();
app.MapFallbackToFile("index.html");

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    await DbInitializer.SeedAsync(db);
}

// Папка для загруженных фотографий объектов (вне wwwroot, отдаётся через API)
Directory.CreateDirectory(Path.Combine(app.Environment.ContentRootPath, "uploads"));

app.Run();
