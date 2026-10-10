using backend.Models;
using Microsoft.EntityFrameworkCore;

namespace backend.Data;

public sealed class StoreDbContext(DbContextOptions<StoreDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<AdminSession> AdminSessions => Set<AdminSession>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>().Property(product => product.Price).HasPrecision(10, 2);
        modelBuilder.Entity<Order>().Property(order => order.Total).HasPrecision(10, 2);
        modelBuilder.Entity<Order>().Property(order => order.PaymentReference).HasMaxLength(100);
        modelBuilder.Entity<Order>().Property(order => order.PaymentVerifiedBy).HasMaxLength(100);
        modelBuilder.Entity<OrderItem>().Property(item => item.UnitPrice).HasPrecision(10, 2);
        modelBuilder.Entity<Order>().HasMany(order => order.Items).WithOne().HasForeignKey(item => item.OrderId);
        modelBuilder.Entity<AdminUser>().HasIndex(user => user.NormalizedUserName).IsUnique();
        modelBuilder.Entity<AdminSession>().HasKey(session => session.TokenHash);
        modelBuilder.Entity<AdminSession>().HasOne(session => session.AdminUser).WithMany()
            .HasForeignKey(session => session.AdminUserId).OnDelete(DeleteBehavior.Cascade);
    }
}
