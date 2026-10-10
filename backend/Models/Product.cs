namespace backend.Models;

public sealed class Product
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Description { get; set; }
    public decimal Price { get; set; }
    public required string Image { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public byte[]? ImageData { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ImageContentType { get; set; }
    public required string Category { get; set; }
    public int Stock { get; set; }
}
