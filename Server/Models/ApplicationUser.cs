using Microsoft.AspNetCore.Identity;
using System.ComponentModel.DataAnnotations;

namespace AccessibilityMap.Server.Models;

public class ApplicationUser : IdentityUser
{
    // Профиль пользователя (все поля — строки, чтобы не зависеть от типов колонок БД)
    [MaxLength(200)]
    public string? FullName { get; set; }      // ФИО
    [MaxLength(10)]
    public string? DateOfBirth { get; set; }   // дата рождения в формате yyyy-MM-dd
    [MaxLength(20)]
    public string? Status { get; set; }        // active / blocked
    [MaxLength(2000)]
    public string? About { get; set; }         // дополнительная информация
}
