using backend.Data;
using backend.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class ProductsController(StoreDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<object>>> GetAll(CancellationToken cancellationToken)
    {
        var products = await db.Products.AsNoTracking().OrderBy(product => product.Id).ToListAsync(cancellationToken);
        return Ok(products.Select(product => new
        {
            product.Id,
            product.Name,
            product.Description,
            product.Price,
            product.Image,
            imageUrl = product.ImageData == null ? null : $"{Request.Scheme}://{Request.Host}/api/products/{product.Id}/image",
            product.Category,
            product.Stock
        }));
    }

    [HttpGet("{id:int}/image")]
    public async Task<IActionResult> GetImage(int id, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var image = await db.Products.AsNoTracking()
            .Where(product => product.Id == id)
            .Select(product => new { product.ImageData, product.ImageContentType })
            .SingleOrDefaultAsync(cancellationToken);

        return image?.ImageData is null || image.ImageContentType is null
            ? NotFound()
            : File(image.ImageData, image.ImageContentType);
    }
}
