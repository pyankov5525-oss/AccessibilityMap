using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using System.IO;
using System.Linq;
using System.Text;
using AccessibilityMap.Server;
using AccessibilityMap.Server.Data;
using AccessibilityMap.Server.Models;

var builder = WebApplication.CreateBuilder(args);

// База: PostgreSQL (переменная DATABASE_URL от Supabase/Neon) либо локальный SQLite для разработки
var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
                       ?? builder.Configuration.GetConnectionString("Default");
var usePostgres = !string.IsNullOrEmpty(connectionString)
                  && connectionString.Contains("postgres", StringComparison.OrdinalIgnoreCase);
if (!usePostgres)
{
    connectionString = "Data Source=accessibility.db";
}

builder.Services.AddDbContext<AppDbContext>(options =>
{
    if (usePostgres)
    {
        // Neon/Supabase выдают postgres:// — Npgsql нужен префикс postgresql://
        var pg = connectionString.Replace("postgres://", "postgresql://", StringComparison.OrdinalIgnoreCase);

        // Облачные базы (Supabase/Neon) требуют SSL. По умолчанию Npgsql стоит
        // SslMode=Prefer — принудительно включаем Require, если пользователь
        // не задал режим SSL сам. Для локального Postgres без SSL это не сработает,
        // но для Supabase/Neon обязательно.
        var pgBuilder = new NpgsqlConnectionStringBuilder(pg);
        if (pgBuilder.SslMode == SslMode.Prefer || pgBuilder.SslMode == SslMode.Disable)
        {
            pgBuilder.SslMode = SslMode.Require;
        }

        options.UseNpgsql(pgBuilder.ConnectionString);
    }
    else
    {
        options.UseSqlite(connectionString);
    }
});

// Identity (роли: Developer / Manager / Volunteer)
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.Password.RequireDigit = false;
    options.Password.RequireLowercase = false;
    options.Password.RequireUppercase = false;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequiredLength = 6;
    options.User.RequireUniqueEmail = false;
})
.AddEntityFrameworkStores<AppDbContext>()
.AddDefaultTokenProviders();

// JWT (логин/пароль генерируются, пароли хешируются)
var jwtKey = builder.Configuration["Jwt:Key"] ?? "accessibility-map-dev-secret-key-1234567890";
builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultSignInScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
    });

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();
builder.Services.AddHostedService<PlacemarkCleanupService>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    });
});

// На хостинге (Render/Azure) порт задаётся переменной окружения PORT / WEBSITES_PORT
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
app.UseAuthentication();
app.UseAuthorization();
//app.UseHttpsRedirection();
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.MapControllers();
app.MapFallbackToFile("index.html");

// Папка для загруженных фотографий (вне wwwroot, отдаётся через API)
Directory.CreateDirectory(Path.Combine(app.Environment.ContentRootPath, "uploads"));

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var db = services.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    await DbInitializer.SeedAsync(db);
    await SeedRolesAndAdminAsync(services);
    // Известный аккаунт разработчика через переменные окружения (восстановление доступа)
    await EnsureEnvAdminAsync(services);
}

app.Run();

static async Task SeedRolesAndAdminAsync(IServiceProvider services)
{
    var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
    var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();

    foreach (var role in new[] { "Developer", "Manager", "Volunteer" })
    {
        if (!await roleManager.RoleExistsAsync(role))
            await roleManager.CreateAsync(new IdentityRole(role));
    }

    if (!await userManager.Users.AnyAsync())
    {
        var login = GenerateLogin();
        var password = GeneratePassword();
        var admin = new ApplicationUser { UserName = login, EmailConfirmed = true };
        var result = await userManager.CreateAsync(admin, password);
        if (result.Succeeded)
        {
            await userManager.AddToRoleAsync(admin, "Developer");
            Console.WriteLine("==================================================");
            Console.WriteLine("  СОЗДАН АККАУНТ РАЗРАБОТЧИКА (первый запуск):");
            Console.WriteLine($"  Логин:  {login}");
            Console.WriteLine($"  Пароль: {password}");
            Console.WriteLine("  Сохраните их! Потом создавайте остальных через интерфейс.");
            Console.WriteLine("==================================================");
        }
    }
}

// Если заданы SEED_LOGIN и SEED_PASSWORD — гарантируем существование
// разработчика с этими данными (создаём или сбрасываем пароль). Это позволяет
// восстановить доступ на хостинге (Render и т.п.), где логи недоступны/эфемерны.
static async Task EnsureEnvAdminAsync(IServiceProvider services)
{
    var login = Environment.GetEnvironmentVariable("SEED_LOGIN");
    var password = Environment.GetEnvironmentVariable("SEED_PASSWORD");
    if (string.IsNullOrEmpty(login) || string.IsNullOrEmpty(password))
        return;

    var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
    var user = await userManager.FindByNameAsync(login);
    if (user == null)
    {
        user = new ApplicationUser { UserName = login, EmailConfirmed = true };
        var result = await userManager.CreateAsync(user, password);
        if (result.Succeeded)
        {
            await userManager.AddToRoleAsync(user, "Developer");
            Console.WriteLine($"Создан резервный разработчик из SEED_LOGIN/SEED_PASSWORD: {login}");
        }
    }
    else
    {
        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        await userManager.ResetPasswordAsync(user, token, password);
        if (!await userManager.IsInRoleAsync(user, "Developer"))
            await userManager.AddToRoleAsync(user, "Developer");
    }
}

static string GenerateLogin()
{
    const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
    var rnd = new Random();
    return new string(Enumerable.Repeat(chars, 5).Select(c => c[rnd.Next(c.Length)]).ToArray());
}

static string GeneratePassword()
{
    const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
    var rnd = new Random();
    return new string(Enumerable.Repeat(chars, 10).Select(c => c[rnd.Next(c.Length)]).ToArray());
}
