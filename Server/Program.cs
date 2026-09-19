using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.HttpOverrides;
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
        var pgConnectionString = BuildPostgresConnectionString(connectionString!);
        options.UseNpgsql(pgConnectionString);
    }
    else
    {
        options.UseSqlite(connectionString);
    }
});

// Identity (роли: Developer / Manager / Volunteer)
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.Password.RequireDigit = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireUppercase = false;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequiredLength = 10;
    options.Password.RequiredUniqueChars = 4;
    options.User.RequireUniqueEmail = false;
    options.Lockout.AllowedForNewUsers = true;
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
})
.AddEntityFrameworkStores<AppDbContext>()
.AddDefaultTokenProviders();

// JWT. В production секрет обязателен и задаётся только через Jwt__Key/секреты хостинга.
// Локальный ключ разрешён исключительно в Development, чтобы проект можно было запустить без платных сервисов.
var jwtKey = builder.Configuration["Jwt:Key"];
if (string.IsNullOrWhiteSpace(jwtKey))
{
    if (!builder.Environment.IsDevelopment())
        throw new InvalidOperationException("Не задан Jwt__Key. Укажите случайный секрет длиной не менее 32 символов.");
    jwtKey = "development-only-accessibility-map-key-change-me";
}
if (Encoding.UTF8.GetByteCount(jwtKey) < 32)
    throw new InvalidOperationException("Jwt__Key должен содержать не менее 32 байт.");
builder.Configuration["Jwt:Key"] = jwtKey;

var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "AccessibilityMap";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "AccessibilityMap.Client";
builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultSignInScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
    });

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();
builder.Services.AddHealthChecks();
builder.Services.AddResponseCompression(options => options.EnableForHttps = true);
builder.Services.AddHostedService<PlacemarkCleanupService>();

// Hosted WASM обращается к API с того же origin. Дополнительные origin разрешаются
// явным списком CORS_ORIGINS (через запятую), а не небезопасным AllowAnyOrigin.
var corsOrigins = (Environment.GetEnvironmentVariable("CORS_ORIGINS") ?? string.Empty)
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (corsOrigins.Length > 0)
            policy.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod();
    });
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Заголовки принимаются только от прокси, добавленных хостингом в KnownProxies/KnownNetworks.
    options.ForwardLimit = 1;
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

app.UseForwardedHeaders();
app.UseResponseCompression();

// Базовые защитные заголовки. unsafe-inline/unsafe-eval пока нужны Яндекс.Картам 2.1
// и существующему JS-интеропу; после перехода на Maps 3.0 политику можно ужесточить.
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), payment=(), usb=()";
        headers["X-Frame-Options"] = "SAMEORIGIN";
        headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'unsafe-inline' 'unsafe-eval' https://api-maps.yandex.ru https://*.maps.yandex.ru https://yastatic.net; style-src 'self' 'unsafe-inline' https://*.maps.yandex.ru https://yastatic.net; img-src 'self' data: blob: https://*.maps.yandex.ru https://yastatic.net https://*.yandex.net; connect-src 'self' https://*.yandex.ru https://*.yandex.net; font-src 'self' data: https://yastatic.net; object-src 'none'; base-uri 'self'; frame-ancestors 'self'; form-action 'self'";
        return Task.CompletedTask;
    });
    await next();
});

app.UseCors();
app.UseMiddleware<RateLimitMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
if (!app.Environment.IsDevelopment())
    app.UseHsts();
app.UseBlazorFrameworkFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        var path = context.Context.Request.Path.Value ?? string.Empty;
        if (path.Contains("/_framework/") || path.EndsWith(".css") || path.EndsWith(".js") || path.EndsWith(".svg") || path.EndsWith(".png"))
            context.Context.Response.Headers["Cache-Control"] = "public,max-age=604800";
    }
});
app.MapHealthChecks("/health");
app.MapControllers();
app.MapFallbackToFile("index.html");

// Папка для загруженных фотографий (вне wwwroot, отдаётся через API)
Directory.CreateDirectory(Path.Combine(app.Environment.ContentRootPath, "uploads"));

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var db = services.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    await EnsureUserSchemaAsync(db, usePostgres);
    await DbInitializer.SeedAsync(db);
    await SeedRolesAndAdminAsync(services);
    // Известный аккаунт разработчика через переменные окружения (восстановление доступа)
    await EnsureEnvAdminAsync(services);
}

app.Run();


static string BuildPostgresConnectionString(string rawConnectionString)
{
    // Render/Supabase часто дают строку в URI-формате:
    // postgresql://postgres:password@db.xxxxx.supabase.co:5432/postgres
    // NpgsqlConnectionStringBuilder НЕ понимает такой формат напрямую,
    // поэтому аккуратно переводим URI в обычный формат Host=...;Username=...
    if (rawConnectionString.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
        rawConnectionString.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
    {
        var uri = new Uri(rawConnectionString);
        var userInfo = uri.UserInfo.Split(':', 2);
        var username = Uri.UnescapeDataString(userInfo.ElementAtOrDefault(0) ?? string.Empty);
        var password = Uri.UnescapeDataString(userInfo.ElementAtOrDefault(1) ?? string.Empty);
        var database = uri.AbsolutePath.TrimStart('/');

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = string.IsNullOrWhiteSpace(database) ? "postgres" : Uri.UnescapeDataString(database),
            Username = username,
            Password = password,
            SslMode = SslMode.Require,
            Pooling = true
        };

        return builder.ConnectionString;
    }

    // Если строка уже в формате Npgsql: Host=...;Database=...;Username=...
    var pgBuilder = new NpgsqlConnectionStringBuilder(rawConnectionString);
    if (pgBuilder.SslMode == SslMode.Prefer || pgBuilder.SslMode == SslMode.Disable)
    {
        pgBuilder.SslMode = SslMode.Require;
    }

    return pgBuilder.ConnectionString;
}

static async Task SeedRolesAndAdminAsync(IServiceProvider services)
{
    var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();

    foreach (var role in new[] { "Developer", "Manager", "Volunteer" })
    {
        if (!await roleManager.RoleExistsAsync(role))
            await roleManager.CreateAsync(new IdentityRole(role));
    }
}

// Если заданы SEED_LOGIN и SEED_PASSWORD — гарантируем существование
// разработчика с этими данными (создаём или сбрасываем пароль). Это позволяет
// восстановить доступ на хостинге (Render и т.п.), где логи недоступны/эфемерны.
static async Task EnsureUserSchemaAsync(AppDbContext db, bool usePostgres)
{
    // EnsureCreated не добавляет колонки в уже существующие таблицы,
    // поэтому добавляем поля профиля явно (идемпотентно).
    try
    {
        if (usePostgres)
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"AspNetUsers\" ADD COLUMN IF NOT EXISTS \"FullName\" text;");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"AspNetUsers\" ADD COLUMN IF NOT EXISTS \"DateOfBirth\" text;");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"AspNetUsers\" ADD COLUMN IF NOT EXISTS \"Status\" text;");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"AspNetUsers\" ADD COLUMN IF NOT EXISTS \"About\" text;");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Placemarks\" ADD COLUMN IF NOT EXISTS \"PhotoPaths\" text;");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Placemarks\" ADD COLUMN IF NOT EXISTS \"CreatedByUserId\" text;");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Placemarks\" ADD COLUMN IF NOT EXISTS \"CreatedByFullName\" text;");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Placemarks\" ADD COLUMN IF NOT EXISTS \"Likes\" integer NOT NULL DEFAULT 0;");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Placemarks\" ADD COLUMN IF NOT EXISTS \"Dislikes\" integer NOT NULL DEFAULT 0;");
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS \"Photos\" (\"Id\" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, \"FileName\" text NOT NULL UNIQUE, \"ContentType\" text NOT NULL, \"Data\" bytea NOT NULL, \"UploadedAt\" timestamp with time zone NOT NULL);");
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS \"PlacemarkVotes\" (\"Id\" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, \"PlacemarkId\" integer NOT NULL, \"VoterKey\" text NOT NULL, \"Value\" integer NOT NULL, \"CreatedAt\" timestamp with time zone NOT NULL, \"UpdatedAt\" timestamp with time zone NOT NULL);");
            await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_PlacemarkVotes_PlacemarkId_VoterKey\" ON \"PlacemarkVotes\" (\"PlacemarkId\", \"VoterKey\");");
            await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_Placemarks_VerificationStatus\" ON \"Placemarks\" (\"VerificationStatus\");");
            await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_Placemarks_Category\" ON \"Placemarks\" (\"Category\");");
            await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_Placemarks_Latitude_Longitude\" ON \"Placemarks\" (\"Latitude\", \"Longitude\");");
            await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_ActivityLogs_Timestamp\" ON \"ActivityLogs\" (\"Timestamp\");");
        }
        else
        {
            TryAddColumnSqlite(db, "FullName");
            TryAddColumnSqlite(db, "DateOfBirth");
            TryAddColumnSqlite(db, "Status");
            TryAddColumnSqlite(db, "About");
            TryAddColumnSqlite(db, "PhotoPaths", "Placemarks");
            TryAddColumnSqlite(db, "CreatedByUserId", "Placemarks");
            TryAddColumnSqlite(db, "CreatedByFullName", "Placemarks");
            TryAddColumnSqlite(db, "Likes", "Placemarks", "integer NOT NULL DEFAULT 0");
            TryAddColumnSqlite(db, "Dislikes", "Placemarks", "integer NOT NULL DEFAULT 0");
            db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS Photos (Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, FileName TEXT NOT NULL UNIQUE, ContentType TEXT NOT NULL, Data BLOB NOT NULL, UploadedAt TEXT NOT NULL);");
            db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS PlacemarkVotes (Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, PlacemarkId INTEGER NOT NULL, VoterKey TEXT NOT NULL, Value INTEGER NOT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL);");
            db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_PlacemarkVotes_PlacemarkId_VoterKey ON PlacemarkVotes (PlacemarkId, VoterKey);");
            db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_Placemarks_VerificationStatus ON Placemarks (VerificationStatus);");
            db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_Placemarks_Category ON Placemarks (Category);");
            db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_Placemarks_Latitude_Longitude ON Placemarks (Latitude, Longitude);");
            db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_ActivityLogs_UserName ON ActivityLogs (UserName);");
            db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_ActivityLogs_Timestamp ON ActivityLogs (Timestamp);");
        }
    }
    catch { }
}

static void TryAddColumnSqlite(AppDbContext db, string column, string table = "AspNetUsers", string type = "text")
{
    try
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            connection.Open();

        using (var check = connection.CreateCommand())
        {
            check.CommandText = $"PRAGMA table_info({table});";
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    return;
            }
        }

        var allowedTables = new[] { "AspNetUsers", "Placemarks" };
        var allowedColumns = new[] { "FullName", "DateOfBirth", "Status", "About", "PhotoPaths", "CreatedByUserId", "CreatedByFullName", "Likes", "Dislikes" };
        if (!allowedTables.Contains(table) || !allowedColumns.Contains(column)) return;
        db.Database.ExecuteSqlRaw("ALTER TABLE " + table + " ADD COLUMN " + column + " " + type + ";");
    }
    catch { }
}

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
        user = new ApplicationUser { UserName = login, EmailConfirmed = true, Status = "active", FullName = "Разработчик" };
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


