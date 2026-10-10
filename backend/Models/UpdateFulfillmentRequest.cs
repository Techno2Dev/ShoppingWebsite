namespace backend.Models;

public sealed class UpdateFulfillmentRequest
{
    public required string Status { get; set; }
    public string? TrackingId { get; set; }
}
