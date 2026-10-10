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
    public DbSet<CustomerUser> CustomerUsers => Set<CustomerUser>();
    public DbSet<CustomerSession> CustomerSessions => Set<CustomerSession>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>().Property(product => product.Price).HasPrecision(10, 2);
        modelBuilder.Entity<Order>().Property(order => order.Total).HasPrecision(10, 2);
        modelBuilder.Entity<Order>().Property(order => order.PaymentReference).HasMaxLength(100);
        modelBuilder.Entity<Order>().Property(order => order.MobileNumber).HasMaxLength(10);
        modelBuilder.Entity<CustomerUser>().Property(user => user.MobileNumber).HasMaxLength(10);
        modelBuilder.Entity<Order>().Property(order => order.TrackingId).HasMaxLength(100);
        modelBuilder.Entity<Order>().Property(order => order.PaymentVerifiedBy).HasMaxLength(100);
        modelBuilder.Entity<OrderItem>().Property(item => item.UnitPrice).HasPrecision(10, 2);
        modelBuilder.Entity<Order>().HasMany(order => order.Items).WithOne().HasForeignKey(item => item.OrderId);
        modelBuilder.Entity<AdminUser>().HasIndex(user => user.NormalizedUserName).IsUnique();
        modelBuilder.Entity<AdminSession>().HasKey(session => session.TokenHash);
        modelBuilder.Entity<AdminSession>().HasOne(session => session.AdminUser).WithMany()
            .HasForeignKey(session => session.AdminUserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<CustomerUser>().HasIndex(user => user.GoogleSubject).IsUnique();
        modelBuilder.Entity<CustomerUser>().Property(user => user.GoogleSubject).HasMaxLength(255);
        modelBuilder.Entity<CustomerUser>().Property(user => user.Email).HasMaxLength(254);
        modelBuilder.Entity<CustomerUser>().Property(user => user.Name).HasMaxLength(120);
        modelBuilder.Entity<CustomerUser>().Property(user => user.PhotoUrl).HasMaxLength(1000);
        modelBuilder.Entity<CustomerUser>().Property(user => user.DeliveryAddress).HasMaxLength(500);
        modelBuilder.Entity<CustomerSession>().HasKey(session => session.TokenHash);
        modelBuilder.Entity<CustomerSession>().HasOne(session => session.CustomerUser).WithMany()
            .HasForeignKey(session => session.CustomerUserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Order>().HasOne<CustomerUser>().WithMany()
            .HasForeignKey(order => order.CustomerUserId).OnDelete(DeleteBehavior.SetNull);
    }
}
