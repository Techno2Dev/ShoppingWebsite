namespace backend.Models;

public sealed class CreateProductRequest
{
    public required string Name { get; set; }
    public required string Description { get; set; }
    public decimal Price { get; set; }
    public string? Image { get; set; }
    public IFormFile? ImageFile { get; set; }
    public required string Category { get; set; }
    public int Stock { get; set; }
}
