using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Collections.Concurrent;
using AccessibilityMap.Server.Data;
using AccessibilityMap.Server.Models;

namespace AccessibilityMap.Server.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly IConfiguration _config;
    private readonly AppDbContext _db;

    public AuthController(UserManager<ApplicationUser> userManager,
                          SignInManager<ApplicationUser> signInManager,
                          RoleManager<IdentityRole> roleManager,
                          IConfiguration config,
                          AppDbContext db)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _roleManager = roleManager;
        _config = config;
        _db = db;
    }

    public class LoginModel
    {
        public string Login { get; set; } = "";
        public string Password { get; set; } = "";
        public string CaptchaToken { get; set; } = "";
        public string CaptchaAnswer { get; set; } = "";
    }

    public class CreateModel
    {
        public string Role { get; set; } = "Volunteer";
        public string FullName { get; set; } = "";
        public string DateOfBirth { get; set; } = "";
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginModel model)
    {
        var user = await _userManager.FindByNameAsync(model.Login);
        if (user == null)
            return Unauthorized(new { error = "Неверный логин или пароль" });

        // Защита: заблокированные пользователи не могут войти в систему.
        if (user.Status == "blocked")
            return Unauthorized(new { error = "Ваш аккаунт заблокирован администратором" });

        // Капча: защита формы входа от автоматического подбора пароля ботами.
        if (string.IsNullOrEmpty(model.CaptchaToken) || string.IsNullOrEmpty(model.CaptchaAnswer) ||
            !_captchas.TryRemove(model.CaptchaToken, out var cap) || cap.Expiry < DateTime.UtcNow ||
            cap.Answer != model.CaptchaAnswer.Trim())
        {
            return Unauthorized(new { error = "Неверный ответ на проверку (капча)" });
        }

        var result = await _signInManager.CheckPasswordSignInAsync(user, model.Password, false);
        if (!result.Succeeded)
            return Unauthorized(new { error = "Неверный логин или пароль" });

        var roles = await _userManager.GetRolesAsync(user);
        var token = GenerateToken(user, roles);
        try
        {
            _db.ActivityLogs.Add(new ActivityLog
            {
                Type = "login",
                UserName = user.UserName,
                Description = $"Вход в систему: {user.UserName}",
                IpAddress = HttpContext?.Connection?.RemoteIpAddress?.ToString()
            });
            await _db.SaveChangesAsync();
        }
        catch { }
        return Ok(new { token, userName = user.UserName, role = roles.FirstOrDefault(), profileComplete = IsProfileComplete(user) });
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();
        var roles = await _userManager.GetRolesAsync(user);
        return Ok(new
        {
            userName = user.UserName,
            role = roles.FirstOrDefault(),
            isAuthenticated = true,
            fullName = user.FullName,
            dateOfBirth = user.DateOfBirth,
            status = user.Status,
            about = user.About,
            profileComplete = IsProfileComplete(user)
        });
    }

    [HttpPost("logout")]
    [Authorize]
    public IActionResult Logout() => Ok(new { ok = true });

    // Авто-создание аккаунта со случайным логином/паролем.
    // Developer выбирает Manager/Volunteer; Manager может создать только Volunteer.
    [HttpPost("create")]
    [Authorize(Roles = "Developer")]
    public async Task<IActionResult> CreateManagerOrDev([FromBody] CreateModel model)
    {
        if (model.Role != "Developer" && model.Role != "Manager" && model.Role != "Volunteer")
            return BadRequest(new { error = "Можно создать только разработчика, управляющего или волонтёра" });
        return await CreateUser(model.Role, model.FullName, model.DateOfBirth);
    }

    [HttpPost("create-volunteer")]
    [Authorize(Roles = "Manager,Developer")]
    public async Task<IActionResult> CreateVolunteer([FromBody] CreateModel model)
    {
        return await CreateUser("Volunteer", model.FullName, model.DateOfBirth);
    }

    private async Task<IActionResult> CreateUser(string role, string fullName, string dateOfBirth)
    {
        var login = GenerateLogin();
        var password = GeneratePassword();
        var user = new ApplicationUser
        {
            UserName = login,
            EmailConfirmed = true,
            Status = "active",
            FullName = fullName?.Trim(),
            DateOfBirth = dateOfBirth?.Trim()
        };
        var result = await _userManager.CreateAsync(user, password);
        if (!result.Succeeded)
            return BadRequest(new { errors = result.Errors.Select(e => e.Description) });

        await _userManager.AddToRoleAsync(user, role);
        try
        {
            _db.ActivityLogs.Add(new ActivityLog
            {
                Type = "action",
                UserName = User.Identity?.Name,
                Description = $"Создан пользователь {login} (роль {role})"
            });
            await _db.SaveChangesAsync();
        }
        catch { }
        return Ok(new { login, password, role, fullName = user.FullName, dateOfBirth = user.DateOfBirth });
    }

    // Список пользователей. Developer видит всех, Manager — только волонтёров.
    [HttpGet("users")]
    [Authorize(Roles = "Manager,Developer")]
    public async Task<IActionResult> ListUsers()
    {
        try
        {
            var currentRole = await GetCurrentRoleAsync();
            var result = new List<object>();
            foreach (var u in _db.Users.ToList())
            {
                var roles = await _userManager.GetRolesAsync(u);
                var role = roles.FirstOrDefault() ?? "";
                if (currentRole == "Manager" && role != "Volunteer")
                    continue;
                result.Add(new { id = u.Id, userName = u.UserName, role, fullName = u.FullName, dateOfBirth = u.DateOfBirth, status = u.Status });
            }
            return Ok(result);
        }
        catch
        {
            return Ok(new List<object>());
        }
    }

    // Смена роли пользователя (только Developer)
    [HttpPost("{id}/role")]
    [Authorize(Roles = "Developer")]
    public async Task<IActionResult> ChangeRole(string id, [FromBody] RoleModel model)
    {
        if (model.Role != "Developer" && model.Role != "Manager" && model.Role != "Volunteer")
            return BadRequest(new { error = "Недопустимая роль" });

        var user = await _userManager.FindByIdAsync(id);
        if (user == null) return NotFound();

        var self = await _userManager.GetUserAsync(User);
        if (self != null && self.Id == user.Id)
            return BadRequest(new { error = "Нельзя менять собственную роль" });

        var current = await _userManager.GetRolesAsync(user);
        if (current.Contains("Developer") && model.Role != "Developer")
        {
            var devs = await _userManager.GetUsersInRoleAsync("Developer");
            if (devs.Count <= 1)
                return BadRequest(new { error = "Нельзя убрать роль у последнего разработчика" });
        }

        foreach (var r in current)
            await _userManager.RemoveFromRoleAsync(user, r);
        await _userManager.AddToRoleAsync(user, model.Role);

        try
        {
            _db.ActivityLogs.Add(new ActivityLog
            {
                Type = "action",
                UserName = User.Identity?.Name,
                Description = $"Смена роли пользователя {user.UserName} → {model.Role}"
            });
            await _db.SaveChangesAsync();
        }
        catch { }
        return Ok(new { id = user.Id, userName = user.UserName, role = model.Role });
    }

    // Удаление пользователя (Manager/Developer; нельзя удалить себя и последнего разработчика)
    [HttpDelete("{id}")]
    [Authorize(Roles = "Manager,Developer")]
    public async Task<IActionResult> DeleteUser(string id)
    {
        var self = await _userManager.GetUserAsync(User);
        if (self != null && self.Id == id)
            return BadRequest(new { error = "Нельзя удалить самого себя" });

        var user = await _userManager.FindByIdAsync(id);
        if (user == null) return NotFound();

        var roles = await _userManager.GetRolesAsync(user);
        var currentRole = await GetCurrentRoleAsync();
        if (roles.Contains("Developer"))
        {
            if (currentRole != "Developer") return Forbid();
            var devs = await _userManager.GetUsersInRoleAsync("Developer");
            if (devs.Count <= 1)
                return BadRequest(new { error = "Нельзя удалить последнего разработчика" });
        }

        if (currentRole == "Manager" && !roles.Contains("Volunteer"))
            return Forbid();

        // Вместо удаления блокируем/разблокируем аккаунт, чтобы не терять историю и логи.
        user.Status = user.Status == "blocked" ? "active" : "blocked";
        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
            return BadRequest(new { errors = result.Errors.Select(e => e.Description) });
        try
        {
            _db.ActivityLogs.Add(new ActivityLog
            {
                Type = "action",
                UserName = User.Identity?.Name,
                Description = user.Status == "blocked" ? $"Заблокирован пользователь {user.UserName}" : $"Разблокирован пользователь {user.UserName}"
            });
            await _db.SaveChangesAsync();
        }
        catch { }
        return Ok(new { ok = true, status = user.Status });
    }

    [HttpGet("profile")]
    [Authorize]
    public async Task<IActionResult> GetProfile([FromQuery] string? id)
    {
        var current = await _userManager.GetUserAsync(User);
        if (current == null) return Unauthorized();
        ApplicationUser? target = current;
        if (!string.IsNullOrEmpty(id) && id != current.Id)
        {
            // просмотр чужого профиля — только Developer/Manager
            var currentRoles = await _userManager.GetRolesAsync(current);
            if (!currentRoles.Contains("Developer") && !currentRoles.Contains("Manager"))
                return Forbid();
            target = await _userManager.FindByIdAsync(id);
            if (target == null) return NotFound();
        }
        var roles = await _userManager.GetRolesAsync(target);
        return Ok(new ProfileDto
        {
            Id = target.Id,
            UserName = target.UserName ?? string.Empty,
            FullName = target.FullName,
            DateOfBirth = target.DateOfBirth,
            Status = target.Status,
            About = target.About,
            Role = roles.FirstOrDefault() ?? ""
        });
    }

    [HttpPut("profile")]
    [Authorize]
    public async Task<IActionResult> UpdateProfile([FromBody] ProfileUpdateModel model)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();
        user.FullName = model.FullName;
        user.DateOfBirth = model.DateOfBirth;
        user.About = model.About;
        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
            return BadRequest(new { errors = result.Errors.Select(e => e.Description) });
        await UpdatePlacemarkAuthorNameAsync(user);
        return Ok(new { ok = true });
    }

    [HttpPut("{id}/profile")]
    [Authorize(Roles = "Developer")]
    public async Task<IActionResult> UpdateProfileForUser(string id, [FromBody] ProfileUpdateModel model)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user == null) return NotFound();
        if (!await CanCurrentUserManageTargetAsync(user)) return Forbid();
        user.FullName = model.FullName;
        user.DateOfBirth = model.DateOfBirth;
        user.About = model.About;
        if (!string.IsNullOrEmpty(model.Status)) user.Status = model.Status;
        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
            return BadRequest(new { errors = result.Errors.Select(e => e.Description) });
        await UpdatePlacemarkAuthorNameAsync(user);
        return Ok(new { ok = true });
    }


    private async Task UpdatePlacemarkAuthorNameAsync(ApplicationUser user)
    {
        try
        {
            var marks = _db.Placemarks.Where(p => p.CreatedByUserId == user.Id).ToList();
            foreach (var m in marks)
                m.CreatedByFullName = user.FullName;
            if (marks.Count > 0)
                await _db.SaveChangesAsync();
        }
        catch { }
    }

    public class RoleModel
    {
        public string Role { get; set; } = "";
    }

    public class ProfileDto
    {
        public string Id { get; set; } = "";
        public string UserName { get; set; } = "";
        public string? FullName { get; set; }
        public string? DateOfBirth { get; set; }
        public string? Status { get; set; }
        public string? About { get; set; }
        public string Role { get; set; } = "";
    }

    public class ProfileUpdateModel
    {
        public string? FullName { get; set; }
        public string? DateOfBirth { get; set; }
        public string? About { get; set; }
        public string? Status { get; set; }
    }


    [HttpGet("db-status")]
    [Authorize(Roles = "Developer")]
    public IActionResult DbStatus()
    {
        var provider = _db.Database.ProviderName ?? "unknown";
        var databaseUrlSet = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DATABASE_URL"));
        var isPersistent = provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) || provider.Contains("PostgreSQL", StringComparison.OrdinalIgnoreCase);
        return Ok(new
        {
            provider,
            databaseUrlSet,
            isPersistent,
            storage = isPersistent ? "PostgreSQL/Supabase — постоянная база" : "SQLite — временная база Render, данные могут пропадать после деплоя"
        });
    }


    private bool IsProfileComplete(ApplicationUser user) =>
        !string.IsNullOrWhiteSpace(user.FullName) && !string.IsNullOrWhiteSpace(user.DateOfBirth);

    private BadRequestObjectResult? ValidateProfileRequired(string fullName, string dateOfBirth)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            return BadRequest(new { error = "Укажите ФИО пользователя" });
        if (string.IsNullOrWhiteSpace(dateOfBirth))
            return BadRequest(new { error = "Укажите дату рождения пользователя" });
        if (!DateTime.TryParse(dateOfBirth, out var dob))
            return BadRequest(new { error = "Дата рождения указана неверно" });
        if (dob.Year < 1900 || dob.Date > DateTime.UtcNow.Date)
            return BadRequest(new { error = "Проверьте год рождения" });
        return null;
    }

    // ===== Капча (защита входа от ботов) =====
    private static readonly ConcurrentDictionary<string, (string Answer, DateTime Expiry)> _captchas = new();

    [HttpGet("captcha")]
    [AllowAnonymous]
    public IActionResult GetCaptcha()
    {
        var rnd = new Random();
        var a = rnd.Next(1, 10);
        var b = rnd.Next(1, 10);
        var token = Guid.NewGuid().ToString("N");
        _captchas[token] = ((a + b).ToString(), DateTime.UtcNow.AddMinutes(5));
        return Ok(new { token, question = $"Сколько будет {a} + {b}?" });
    }

    // ===== Ручное создание пользователя (Dev/Manager) =====
    [HttpPost("register")]
    [Authorize]
    public async Task<IActionResult> Register([FromBody] RegisterModel model)
    {
        if (string.IsNullOrWhiteSpace(model.Login) || string.IsNullOrWhiteSpace(model.Password))
            return BadRequest(new { error = "Укажите логин и пароль" });
        if (model.Password.Length < 6)
            return BadRequest(new { error = "Пароль должен быть не короче 6 символов" });
        var validation = ValidateProfileRequired(model.FullName, model.DateOfBirth);
        if (validation != null) return validation;

        var self = await _userManager.GetUserAsync(User);
        var selfRoles = self == null ? new List<string>() : await _userManager.GetRolesAsync(self);
        bool allowed;
        if (selfRoles.Contains("Developer"))
            allowed = model.Role == "Developer" || model.Role == "Manager" || model.Role == "Volunteer"; // разработчик создаёт любые роли
        else if (selfRoles.Contains("Manager"))
            allowed = model.Role == "Volunteer"; // управляющий — только волонтёра
        else
            allowed = false;
        if (!allowed) return Forbid();

        if (await _userManager.FindByNameAsync(model.Login) != null)
            return BadRequest(new { error = "Такой логин уже занят" });

        var user = new ApplicationUser
        {
            UserName = model.Login.Trim(),
            EmailConfirmed = true,
            Status = "active",
            FullName = model.FullName.Trim(),
            DateOfBirth = model.DateOfBirth.Trim()
        };
        var result = await _userManager.CreateAsync(user, model.Password);
        if (!result.Succeeded)
            return BadRequest(new { errors = result.Errors.Select(e => e.Description) });
        await _userManager.AddToRoleAsync(user, model.Role);
        try
        {
            _db.ActivityLogs.Add(new ActivityLog
            {
                Type = "action",
                UserName = User.Identity?.Name,
                Description = $"Создан пользователь {model.Login} (роль {model.Role})"
            });
            await _db.SaveChangesAsync();
        }
        catch { }
        return Ok(new { login = model.Login, password = model.Password, role = model.Role, fullName = user.FullName, dateOfBirth = user.DateOfBirth });
    }

    // ===== Смена пароля самому пользователю =====
    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordModel model)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();
        var result = await _userManager.ChangePasswordAsync(user, model.CurrentPassword, model.NewPassword);
        if (!result.Succeeded)
            return BadRequest(new { errors = result.Errors.Select(e => e.Description) });
        try
        {
            _db.ActivityLogs.Add(new ActivityLog { Type = "action", UserName = user.UserName, Description = "Смена пароля" });
            await _db.SaveChangesAsync();
        }
        catch { }
        return Ok(new { ok = true });
    }

    // ===== Сброс пароля администратором (Dev/Manager) =====
    [HttpPost("{id}/reset-password")]
    [Authorize(Roles = "Developer")]
    public async Task<IActionResult> ResetPassword(string id, [FromBody] ResetPasswordModel model)
    {
        var user = await _userManager.FindByIdAsync(id);
        if (user == null) return NotFound();
        if (!await CanCurrentUserManageTargetAsync(user)) return Forbid();
        var token = await _userManager.GeneratePasswordResetTokenAsync(user);
        var result = await _userManager.ResetPasswordAsync(user, token, model.NewPassword);
        if (!result.Succeeded)
            return BadRequest(new { errors = result.Errors.Select(e => e.Description) });
        return Ok(new { ok = true });
    }

    public class RegisterModel
    {
        public string Login { get; set; } = "";
        public string Password { get; set; } = "";
        public string Role { get; set; } = "Volunteer";
        public string FullName { get; set; } = "";
        public string DateOfBirth { get; set; } = "";
    }

    public class ChangePasswordModel
    {
        public string CurrentPassword { get; set; } = "";
        public string NewPassword { get; set; } = "";
    }

    public class ResetPasswordModel
    {
        public string NewPassword { get; set; } = "";
    }


    private async Task<string> GetCurrentRoleAsync()
    {
        var current = await _userManager.GetUserAsync(User);
        if (current == null) return string.Empty;
        var roles = await _userManager.GetRolesAsync(current);
        return roles.FirstOrDefault() ?? string.Empty;
    }

    private async Task<bool> CanCurrentUserManageTargetAsync(ApplicationUser target)
    {
        var currentRole = await GetCurrentRoleAsync();
        var targetRoles = await _userManager.GetRolesAsync(target);
        if (currentRole == "Developer")
            return true;
        return false;
    }

    private string GenerateToken(ApplicationUser user, IList<string> roles)
    {
        var key = _config["Jwt:Key"] ?? "accessibility-map-dev-secret-key-1234567890";
        var claims = new List<Claim>
        {
            // NameIdentifier обязателен: UserManager.GetUserAsync(User) ищет
            // пользователя именно по этому claim. Без него все эндпоинты профиля
            // возвращали 401 и клиент считал вход неуспешным.
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(ClaimTypes.Name, user.UserName ?? "")
        };
        foreach (var role in roles)
            claims.Add(new Claim(ClaimTypes.Role, role));

        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            claims: claims,
            expires: DateTime.UtcNow.AddDays(30),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string GenerateLogin()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        var rnd = new Random();
        return new string(Enumerable.Repeat(chars, 5).Select(c => c[rnd.Next(c.Length)]).ToArray());
    }

    private static string GeneratePassword()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        var rnd = new Random();
        return new string(Enumerable.Repeat(chars, 10).Select(c => c[rnd.Next(c.Length)]).ToArray());
    }
}
