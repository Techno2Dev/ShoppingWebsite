using backend.Data;
using backend.Models;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace backend.Mongo;

// Only one set of routes is published. PostgreSQL controllers and their EF/SQL code are never used in Mongo mode.
public sealed class StoreControllerConvention(bool mongo) : IApplicationModelConvention
{
    public void Apply(ApplicationModel model)
    {
        foreach (var controller in model.Controllers.ToArray())
            if ((controller.ControllerType.Namespace == "backend.Mongo") != mongo)
                model.Controllers.Remove(controller);
    }
}

public sealed class MongoEntity<T>
{
    [BsonId] public int Id { get; set; }
    public required T Value { get; set; }
    public long LoginSequence { get; set; }
    // This lease prevents cancellation or a second checkout while a provider order is being created.
    public DateTime? CheckoutLeaseUntil { get; set; }
    public string? CheckoutLeaseId { get; set; }
}

public sealed class MongoSession
{
    [BsonId] public required string TokenHash { get; set; }
    public int UserId { get; set; }
    public DateTime ExpiresAt { get; set; }
}

public sealed class MongoCounter
{
    [BsonId] public required string Name { get; set; }
    public int Next { get; set; }
}

public sealed class MongoConflictException : Exception;

public sealed class MongoStore
{
    private readonly IMongoClient client;
    private readonly IMongoDatabase database;
    public IMongoCollection<MongoEntity<Product>> Products { get; }
    public IMongoCollection<MongoEntity<Order>> Orders { get; }
    public IMongoCollection<MongoEntity<AdminUser>> AdminUsers { get; }
    public IMongoCollection<MongoEntity<CustomerUser>> CustomerUsers { get; }
    public IMongoCollection<MongoSession> AdminSessions { get; }
    public IMongoCollection<MongoSession> CustomerSessions { get; }
    private readonly IMongoCollection<MongoCounter> counters;

    public MongoStore(string uri)
    {
        var settings = MongoClientSettings.FromConnectionString(uri);
        var databaseName = new MongoUrl(uri).DatabaseName;
        if (string.IsNullOrWhiteSpace(databaseName))
            throw new InvalidOperationException("The MongoDB URI must include a database path (mongodb://host/database).");
        client = new MongoClient(settings);
        database = client.GetDatabase(databaseName);
        Products = database.GetCollection<MongoEntity<Product>>("products");
        Orders = database.GetCollection<MongoEntity<Order>>("orders");
        AdminUsers = database.GetCollection<MongoEntity<AdminUser>>("adminUsers");
        CustomerUsers = database.GetCollection<MongoEntity<CustomerUser>>("customerUsers");
        AdminSessions = database.GetCollection<MongoSession>("adminSessions");
        CustomerSessions = database.GetCollection<MongoSession>("customerSessions");
        counters = database.GetCollection<MongoCounter>("counters");
    }

    public async Task InitializeAsync(IConfiguration configuration, bool development)
    {
        var hello = await database.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1));
        if (!hello.Contains("logicalSessionTimeoutMinutes") ||
            !(hello.Contains("setName") || hello.GetValue("msg", "").AsString == "isdbgrid"))
            throw new InvalidOperationException("MongoDB requires a transaction-capable replica set or sharded cluster; standalone MongoDB is not supported.");

        await AdminUsers.Indexes.CreateOneAsync(new CreateIndexModel<MongoEntity<AdminUser>>(
            Builders<MongoEntity<AdminUser>>.IndexKeys.Ascending(x => x.Value.NormalizedUserName),
            new CreateIndexOptions { Unique = true }));
        await CustomerUsers.Indexes.CreateOneAsync(new CreateIndexModel<MongoEntity<CustomerUser>>(
            Builders<MongoEntity<CustomerUser>>.IndexKeys.Ascending(x => x.Value.GoogleSubject),
            new CreateIndexOptions { Unique = true }));
        foreach (var field in new[] { "Value.RazorpayOrderId", "Value.RazorpayPaymentId" })
            await Orders.Indexes.CreateOneAsync(new CreateIndexModel<MongoEntity<Order>>(
                Builders<MongoEntity<Order>>.IndexKeys.Ascending(field), new CreateIndexOptions<MongoEntity<Order>>
                {
                    Unique = true, PartialFilterExpression = new BsonDocument(field, new BsonDocument("$type", "string"))
                }));
        await Orders.Indexes.CreateOneAsync(new CreateIndexModel<MongoEntity<Order>>(
            Builders<MongoEntity<Order>>.IndexKeys.Ascending(x => x.Value.CustomerUserId)));
        await Orders.Indexes.CreateOneAsync(new CreateIndexModel<MongoEntity<Order>>(
            Builders<MongoEntity<Order>>.IndexKeys.Descending(x => x.Value.CreatedAt).Descending(x => x.Id)));
        await Orders.Indexes.CreateOneAsync(new CreateIndexModel<MongoEntity<Order>>(
            Builders<MongoEntity<Order>>.IndexKeys.Ascending(x => x.Value.PaymentStatus)));
        await Products.Indexes.CreateOneAsync(new CreateIndexModel<MongoEntity<Product>>(
            Builders<MongoEntity<Product>>.IndexKeys.Ascending(x => x.Value.Category)));
        await counters.Indexes.CreateOneAsync(new CreateIndexModel<MongoCounter>(
            Builders<MongoCounter>.IndexKeys.Ascending(x => x.Next)));
        await AdminSessions.Indexes.CreateOneAsync(new CreateIndexModel<MongoSession>(
            Builders<MongoSession>.IndexKeys.Ascending(x => x.ExpiresAt), new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }));
        await CustomerSessions.Indexes.CreateOneAsync(new CreateIndexModel<MongoSession>(
            Builders<MongoSession>.IndexKeys.Ascending(x => x.ExpiresAt), new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }));

        var userName = configuration["Admin:UserName"] ?? (development ? "admin" : null);
        var password = configuration["Admin:Password"] ?? (development ? "KitchenKatta@123" : null);
        if (!string.IsNullOrWhiteSpace(userName) && !string.IsNullOrWhiteSpace(password))
            await Transact(async (session, ct) =>
            {
                if (await AdminUsers.CountDocumentsAsync(session, FilterDefinition<MongoEntity<AdminUser>>.Empty, cancellationToken: ct) == 0)
                {
                    var id = await NextId("admin", session, ct);
                    await AdminUsers.InsertOneAsync(session, new MongoEntity<AdminUser> { Id = id, Value = new AdminUser
                    {
                        Id = id, UserName = userName.Trim(), NormalizedUserName = userName.Trim().ToUpperInvariant(),
                        PasswordHash = PasswordHashing.Hash(password)
                    } }, cancellationToken: ct);
                }
                return true;
            });
        await Transact(async (session, ct) =>
        {
            if (await Products.CountDocumentsAsync(session, FilterDefinition<MongoEntity<Product>>.Empty, cancellationToken: ct) == 0)
            {
                var samples = new[]
                {
                    new Product { Name = "Everyday Backpack", Description = "A lightweight everyday carry with room for all the essentials.", Price = 64, Image = "🎒", Category = "Accessories", Stock = 18 },
                    new Product { Name = "Ceramic Pour-over Set", Description = "Slow mornings start with a better cup. Includes dripper and server.", Price = 42, Image = "☕", Category = "Home", Stock = 12 },
                    new Product { Name = "Studio Headphones", Description = "Rich, balanced sound in a comfortable over-ear fit.", Price = 129, Image = "🎧", Category = "Tech", Stock = 9 },
                    new Product { Name = "Linen Market Tote", Description = "A sturdy, reusable tote made for market runs and weekends away.", Price = 28, Image = "👜", Category = "Accessories", Stock = 24 },
                    new Product { Name = "Desk Plant Kit", Description = "A low-maintenance green companion, pot included.", Price = 35, Image = "🪴", Category = "Home", Stock = 15 },
                    new Product { Name = "Pocket Notebook Set", Description = "Three thoughtfully designed notebooks for ideas on the go.", Price = 18, Image = "📓", Category = "Stationery", Stock = 30 }
                };
                foreach (var product in samples)
                {
                    product.Id = await NextId("product", session, ct);
                    await Products.InsertOneAsync(session, new MongoEntity<Product> { Id = product.Id, Value = product }, cancellationToken: ct);
                }
            }
            return true;
        });
    }

    public async Task<int> NextId(string name, IClientSessionHandle session, CancellationToken ct)
    {
        var counter = await counters.FindOneAndUpdateAsync<MongoCounter, MongoCounter>(session, x => x.Name == name,
            Builders<MongoCounter>.Update.Inc(x => x.Next, 1),
            new FindOneAndUpdateOptions<MongoCounter> { IsUpsert = true, ReturnDocument = ReturnDocument.After }, ct);
        return counter.Next;
    }

    public async Task<T> Transact<T>(Func<IClientSessionHandle, CancellationToken, Task<T>> work, CancellationToken ct = default)
    {
        using var session = await client.StartSessionAsync(cancellationToken: ct);
        return await session.WithTransactionAsync(work, new TransactionOptions(
            readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority), ct);
    }

    public async Task<AdminUser?> AuthenticateAdminAsync(string hash, CancellationToken ct)
    {
        var session = await AdminSessions.Find(x => x.TokenHash == hash && x.ExpiresAt > DateTime.UtcNow).FirstOrDefaultAsync(ct);
        return session is null ? null : (await AdminUsers.Find(x => x.Id == session.UserId).FirstOrDefaultAsync(ct))?.Value;
    }

    public async Task<CustomerUser?> AuthenticateCustomerAsync(string hash, CancellationToken ct)
    {
        var session = await CustomerSessions.Find(x => x.TokenHash == hash && x.ExpiresAt > DateTime.UtcNow).FirstOrDefaultAsync(ct);
        return session is null ? null : (await CustomerUsers.Find(x => x.Id == session.UserId).FirstOrDefaultAsync(ct))?.Value;
    }
}