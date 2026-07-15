using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
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
    }

    public class CreateModel
    {
        public string Role { get; set; } = "";
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginModel model)
    {
        var user = await _userManager.FindByNameAsync(model.Login);
        if (user == null)
            return Unauthorized(new { error = "Неверный логин или пароль" });

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
        return Ok(new { token, userName = user.UserName, role = roles.FirstOrDefault() });
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();
        var roles = await _userManager.GetRolesAsync(user);
        return Ok(new { userName = user.UserName, role = roles.FirstOrDefault(), isAuthenticated = true });
    }

    [HttpPost("logout")]
    [Authorize]
    public IActionResult Logout() => Ok(new { ok = true });

    // Разработчик создаёт управляющего или разработчика
    [HttpPost("create")]
    [Authorize(Roles = "Developer")]
    public async Task<IActionResult> CreateManagerOrDev([FromBody] CreateModel model)
    {
        if (model.Role != "Manager" && model.Role != "Developer")
            return BadRequest(new { error = "Можно создавать только Manager или Developer" });
        return await CreateUser(model.Role);
    }

    // Управляющий создаёт волонтёра
    [HttpPost("create-volunteer")]
    [Authorize(Roles = "Manager,Developer")]
    public async Task<IActionResult> CreateVolunteer()
    {
        return await CreateUser("Volunteer");
    }

    private async Task<IActionResult> CreateUser(string role)
    {
        var login = GenerateLogin();
        var password = GeneratePassword();
        var user = new ApplicationUser { UserName = login, EmailConfirmed = true };
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
        return Ok(new { login, password, role });
    }

    // Список всех пользователей с их ролью (Manager/Developer)
    [HttpGet("users")]
    [Authorize(Roles = "Manager,Developer")]
    public async Task<IActionResult> ListUsers()
    {
        try
        {
            var result = new List<object>();
            foreach (var u in _db.Users.ToList())
            {
                var roles = await _userManager.GetRolesAsync(u);
                result.Add(new { id = u.Id, userName = u.UserName, role = roles.FirstOrDefault() ?? "" });
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

        var current = await _userManager.GetRolesAsync(user);
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
        if (roles.Contains("Developer"))
        {
            var devs = await _userManager.GetUsersInRoleAsync("Developer");
            if (devs.Count <= 1)
                return BadRequest(new { error = "Нельзя удалить последнего разработчика" });
        }

        await _userManager.DeleteAsync(user);
        try
        {
            _db.ActivityLogs.Add(new ActivityLog
            {
                Type = "action",
                UserName = User.Identity?.Name,
                Description = $"Удалён пользователь {user.UserName}"
            });
            await _db.SaveChangesAsync();
        }
        catch { }
        return Ok(new { ok = true });
    }

    public class RoleModel
    {
        public string Role { get; set; } = "";
    }

    private string GenerateToken(ApplicationUser user, IList<string> roles)
    {
        var key = _config["Jwt:Key"] ?? "accessibility-map-dev-secret-key-1234567890";
        var claims = new List<Claim> { new Claim(ClaimTypes.Name, user.UserName ?? "") };
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
