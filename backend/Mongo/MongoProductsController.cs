using backend.Models;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;

namespace backend.Mongo;

[ApiController]
[Route("api/products")]
public sealed class MongoProductsController(MongoStore db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var products = await db.Products.Find(FilterDefinition<MongoEntity<Product>>.Empty).SortBy(x => x.Id).ToListAsync(ct);
        return Ok(products.Select(item => new
        {
            item.Value.Id, item.Value.Name, item.Value.Description, item.Value.Price, item.Value.Image,
            imageUrl = item.Value.ImageData is null ? null : $"{Request.Scheme}://{Request.Host}/api/products/{item.Id}/image",
            item.Value.Category, item.Value.Stock
        }));
    }

    [HttpGet("{id:int}/image")]
    public async Task<IActionResult> GetImage(int id, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var product = (await db.Products.Find(x => x.Id == id).FirstOrDefaultAsync(ct))?.Value;
        return product?.ImageData is null || product.ImageContentType is null
            ? NotFound() : File(product.ImageData, product.ImageContentType);
    }
}