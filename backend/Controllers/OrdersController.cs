using backend.Data;
using backend.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class OrdersController(StoreDbContext db) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<IEnumerable<Order>>> GetAll(CancellationToken cancellationToken) =>
        Ok(await db.Orders.AsNoTracking().Include(order => order.Items)
            .OrderByDescending(order => order.CreatedAt).ToListAsync(cancellationToken));

    [HttpGet("{id:int}")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<Order>> GetById(int id, CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking().Include(item => item.Items)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        return order is null ? NotFound() : Ok(order);
    }

    [HttpPost]
    public async Task<ActionResult<Order>> Create(CreateOrderRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CustomerName) || string.IsNullOrWhiteSpace(request.Email) ||
            string.IsNullOrWhiteSpace(request.Address) || request.Items.Count == 0 ||
            request.Items.Any(item => item.Quantity < 1 || item.Quantity > 99))
        {
            return BadRequest(new { message = "Enter customer details and at least one valid item." });
        }

        var requestedItems = request.Items
            .GroupBy(item => item.ProductId)
            .Select(group => new CreateOrderItemRequest { ProductId = group.Key, Quantity = group.Sum(item => item.Quantity) })
            .ToList();
        if (requestedItems.Any(item => item.Quantity > 99))
        {
            return BadRequest(new { message = "The total quantity per product cannot exceed 99." });
        }

        var ids = requestedItems.Select(item => item.ProductId).ToArray();
        var products = await db.Products.Where(product => ids.Contains(product.Id)).ToDictionaryAsync(product => product.Id, cancellationToken);
        if (products.Count != ids.Length || requestedItems.Any(item => products[item.ProductId].Stock < item.Quantity))
        {
            return Conflict(new { message = "One or more products are unavailable in the requested quantity." });
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var order = new Order
        {
            CustomerName = request.CustomerName.Trim(),
            Email = request.Email.Trim(),
            Address = request.Address.Trim(),
            PaymentStatus = "Pending",
            Items = requestedItems.Select(item => new OrderItem
            {
                ProductId = item.ProductId,
                ProductName = products[item.ProductId].Name,
                Quantity = item.Quantity,
                UnitPrice = products[item.ProductId].Price
            }).ToList()
        };
        order.Total = order.Items.Sum(item => item.UnitPrice * item.Quantity);

        foreach (var item in requestedItems)
        {
            products[item.ProductId].Stock -= item.Quantity;
        }

        db.Orders.Add(order);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = order.Id }, order);
    }
}
