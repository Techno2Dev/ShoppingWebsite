using System.Text.Json;
using System.Reflection;
using backend.Models;
using backend.Mongo;
using backend.Controllers;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Xunit;

namespace backend.Tests;

public sealed class MongoDocumentTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnlySelectedBackendPublishesRoutes(bool mongo)
    {
        var model = new ApplicationModel();
        Type[] postgres = [typeof(OrdersController), typeof(ProductsController), typeof(AdminController),
            typeof(AdminAuthController), typeof(CustomerAuthController), typeof(RazorpayController)];
        Type[] mongoControllers = [typeof(MongoOrdersController), typeof(MongoProductsController), typeof(MongoAdminController),
            typeof(MongoAdminAuthController), typeof(MongoCustomerAuthController), typeof(MongoRazorpayController)];
        foreach (var controller in postgres.Concat(mongoControllers))
            model.Controllers.Add(new ControllerModel(controller.GetTypeInfo(), []));
        new StoreControllerConvention(mongo).Apply(model);
        Assert.Equal(mongo ? mongoControllers : postgres, model.Controllers.Select(x => x.ControllerType.AsType()));
    }

    [Fact]
    public void MoneyAndImagesRoundTripWithoutExposingPaymentToken()
    {
        var order = new Order
        {
            Id = 23, CustomerName = "Buyer", Email = "buyer@example.org", Address = "Address",
            Total = 105.25m, PaymentAccessTokenHash = "private-hash",
            Items = [new OrderItem { Id = 9, OrderId = 23, ProductId = 4, ProductName = "Item", Quantity = 2, UnitPrice = 52.625m }]
        };
        var doc = new MongoEntity<Order> { Id = 23, Value = order };
        var bson = doc.ToBsonDocument();
        Assert.Equal(BsonType.Decimal128, bson["Value"]["Total"].BsonType);
        Assert.Equal(BsonType.Decimal128, bson["Value"]["Items"][0]["UnitPrice"].BsonType);
        var decoded = BsonSerializer.Deserialize<MongoEntity<Order>>(bson);
        Assert.Equal(order.Total, decoded.Value.Total);
        Assert.Equal(order.Items[0].UnitPrice, decoded.Value.Items[0].UnitPrice);
        Assert.Equal(23, decoded.Value.Items[0].OrderId);
        Assert.Equal("private-hash", decoded.Value.PaymentAccessTokenHash);
        Assert.DoesNotContain("private-hash", JsonSerializer.Serialize(decoded.Value));

        var product = new MongoEntity<Product> { Id = 4, Value = new Product
        {
            Id = 4, Name = "Item", Description = "Description", Price = 52.63m,
            Image = "", ImageData = [0, 1, 2, 255], ImageContentType = "image/png", Category = "Home", Stock = 5
        } };
        var productBson = product.ToBsonDocument();
        Assert.Equal(BsonType.Decimal128, productBson["Value"]["Price"].BsonType);
        Assert.Equal(product.Value.ImageData, BsonSerializer.Deserialize<MongoEntity<Product>>(productBson).Value.ImageData);
    }

    [Fact]
    public void MongoUriMustHaveDatabasePath()
    {
        Assert.Throws<InvalidOperationException>(() => new MongoStore("mongodb://localhost:27017"));
        // MongoClient creation does not contact the server; no database is accessed by this test.
        Assert.NotNull(new MongoStore("mongodb://localhost:27017/store"));
    }
}
