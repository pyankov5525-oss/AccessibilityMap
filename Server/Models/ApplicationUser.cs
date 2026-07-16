using Microsoft.AspNetCore.Identity;

namespace AccessibilityMap.Server.Models;

public class ApplicationUser : IdentityUser
{
    // Профиль пользователя (все поля — строки, чтобы не зависеть от типов колонок БД)
    public string? FullName { get; set; }      // ФИО
    public string? DateOfBirth { get; set; }   // дата рождения в формате yyyy-MM-dd
    public string? Status { get; set; }        // active / blocked
    public string? About { get; set; }         // дополнительная информация
}
