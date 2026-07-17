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
app.UseMiddleware<RateLimitMiddleware>();
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
    var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();

    foreach (var role in new[] { "Developer", "Manager", "Volunteer" })
    {
        if (!await roleManager.RoleExistsAsync(role))
            await roleManager.CreateAsync(new IdentityRole(role));
    }

    const string defaultLogin = "K1ng152";
    const string defaultPassword = "Text-700";

    // Миграция старого аккаунта «admin» → новый логин (смена логина на уже
    // развёрнутых базах, где admin уже создан). Выполняется однократно.
    if (defaultLogin != "admin")
    {
        var oldAdmin = await userManager.FindByNameAsync("admin");
        var newAccount = await userManager.FindByNameAsync(defaultLogin);
        if (oldAdmin != null && newAccount == null)
        {
            oldAdmin.UserName = defaultLogin;
            oldAdmin.NormalizedUserName = defaultLogin.ToUpperInvariant();
            if ((await userManager.UpdateAsync(oldAdmin)).Succeeded)
            {
                var tok = await userManager.GeneratePasswordResetTokenAsync(oldAdmin);
                await userManager.ResetPasswordAsync(oldAdmin, tok, defaultPassword);
                if (!await userManager.IsInRoleAsync(oldAdmin, "Developer"))
                    await userManager.AddToRoleAsync(oldAdmin, "Developer");
            }
        }
    }

    // Гарантируем постоянный аккаунт разработчика с ФИКСИРОВАННЫМИ данными,
    // чтобы не приходилось каждый запуск угадывать случайный логин/пароль.
    // (Если заданы SEED_LOGIN/SEED_PASSWORD — создаётся/сбрасывается они, см. EnsureEnvAdminAsync.)
    var admin = await userManager.FindByNameAsync(defaultLogin);
    if (admin == null)
    {
        admin = new ApplicationUser
        {
            UserName = defaultLogin,
            EmailConfirmed = true,
            Status = "active",
            FullName = "Администратор"
        };
        var result = await userManager.CreateAsync(admin, defaultPassword);
        if (result.Succeeded)
        {
            await userManager.AddToRoleAsync(admin, "Developer");
            Console.WriteLine("==================================================");
            Console.WriteLine("  АККАУНТ РАЗРАБОТЧИКА (по умолчанию):");
            Console.WriteLine("  Логин:    K1ng152");
            Console.WriteLine("  Пароль:   Text-700");
            Console.WriteLine("  (можно переопределить через SEED_LOGIN/SEED_PASSWORD)");
            Console.WriteLine("==================================================");
        }
    }
    else if (!await userManager.IsInRoleAsync(admin, "Developer"))
    {
        await userManager.AddToRoleAsync(admin, "Developer");
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


