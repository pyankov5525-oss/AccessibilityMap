using Microsoft.EntityFrameworkCore;
using AccessibilityMap.Server.Models;

namespace AccessibilityMap.Server.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<PlacemarkModel> Placemarks { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PlacemarkModel>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired();
            entity.Property(e => e.Address).IsRequired();
            entity.Property(e => e.Category).IsRequired();
            entity.Ignore(e => e.TotalScore);
            entity.Ignore(e => e.Level);
            entity.Ignore(e => e.LevelText);
        });
    }
}