using backend.Data;
using backend.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
var storeConnectionString = builder.Configuration.GetConnectionString("Store");
if (string.IsNullOrWhiteSpace(storeConnectionString))
{
    throw new InvalidOperationException("Set ConnectionStrings__Store to your Neon PostgreSQL connection string.");
}
storeConnectionString = PostgresConnectionString.Normalize(storeConnectionString);

builder.Services.AddDbContext<StoreDbContext>(options =>
    options.UseNpgsql(storeConnectionString));
var frontendOrigins = builder.Configuration.GetSection("FrontendOrigins").Get<string[]>() ??
    (builder.Configuration["FrontendOrigin"] is { Length: > 0 } configuredOrigin
        ? configuredOrigin.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        : Array.Empty<string>());
frontendOrigins = frontendOrigins
    .Append("http://localhost:4200")
    .Append("https://shoppingwebsite-1-9ytf.onrender.com")
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();

builder.Services.AddCors(options => options.AddPolicy("frontend", policy =>
    policy.WithOrigins(frontendOrigins).AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddAuthentication("AdminBearer")
    .AddScheme<AuthenticationSchemeOptions, AdminBearerAuthenticationHandler>("AdminBearer", _ => { });
builder.Services.AddAuthorization(options => options.AddPolicy("AdminOnly", policy =>
    policy.RequireAuthenticatedUser().RequireRole("Admin")));

var app = builder.Build();

app.UseCors("frontend");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
    db.Database.EnsureCreated();
    // EnsureCreated does not add new tables to an existing database.
    db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS \"AdminSessions\" (\"TokenHash\" text PRIMARY KEY, \"AdminUserId\" integer NOT NULL REFERENCES \"AdminUsers\"(\"Id\") ON DELETE CASCADE, \"ExpiresAt\" timestamp with time zone NOT NULL);");
    db.Database.ExecuteSqlRaw("ALTER TABLE \"Orders\" ADD COLUMN IF NOT EXISTS \"PaymentReference\" character varying(100) NULL;");
    db.Database.ExecuteSqlRaw("ALTER TABLE \"Orders\" ADD COLUMN IF NOT EXISTS \"PaymentReportedAt\" timestamp with time zone NULL;");
    db.Database.ExecuteSqlRaw("ALTER TABLE \"Orders\" ADD COLUMN IF NOT EXISTS \"PaymentVerifiedAt\" timestamp with time zone NULL;");
    db.Database.ExecuteSqlRaw("ALTER TABLE \"Orders\" ADD COLUMN IF NOT EXISTS \"PaymentVerifiedBy\" character varying(100) NULL;");
    db.Database.ExecuteSqlRaw("ALTER TABLE \"Orders\" ADD COLUMN IF NOT EXISTS \"PaymentAccessTokenHash\" text NULL;");
    db.Database.ExecuteSqlRaw("UPDATE \"Orders\" SET \"PaymentStatus\" = 'Pending' WHERE \"PaymentStatus\" = 'PaymentReported';");

    var configuredAdminUserName = builder.Configuration["Admin:UserName"];
    var configuredAdminPassword = builder.Configuration["Admin:Password"];
    if (app.Environment.IsDevelopment())
    {
        configuredAdminUserName ??= "admin";
        configuredAdminPassword ??= "KitchenKatta@123";
    }

    if (!string.IsNullOrWhiteSpace(configuredAdminUserName) &&
        !string.IsNullOrWhiteSpace(configuredAdminPassword) &&
        !db.AdminUsers.Any())
    {
        db.AdminUsers.Add(new AdminUser
        {
            UserName = configuredAdminUserName.Trim(),
            NormalizedUserName = configuredAdminUserName.Trim().ToUpperInvariant(),
            PasswordHash = PasswordHashing.Hash(configuredAdminPassword)
        });
        db.SaveChanges();
    }

    if (!db.Products.Any())
    {
        db.Products.AddRange(
            new Product { Name = "Everyday Backpack", Description = "A lightweight everyday carry with room for all the essentials.", Price = 64.00m, Image = "🎒", Category = "Accessories", Stock = 18 },
            new Product { Name = "Ceramic Pour-over Set", Description = "Slow mornings start with a better cup. Includes dripper and server.", Price = 42.00m, Image = "☕", Category = "Home", Stock = 12 },
            new Product { Name = "Studio Headphones", Description = "Rich, balanced sound in a comfortable over-ear fit.", Price = 129.00m, Image = "🎧", Category = "Tech", Stock = 9 },
            new Product { Name = "Linen Market Tote", Description = "A sturdy, reusable tote made for market runs and weekends away.", Price = 28.00m, Image = "👜", Category = "Accessories", Stock = 24 },
            new Product { Name = "Desk Plant Kit", Description = "A low-maintenance green companion, pot included.", Price = 35.00m, Image = "🪴", Category = "Home", Stock = 15 },
            new Product { Name = "Pocket Notebook Set", Description = "Three thoughtfully designed notebooks for ideas on the go.", Price = 18.00m, Image = "📓", Category = "Stationery", Stock = 30 }
        );
        db.SaveChanges();
    }
}

app.Run();
