using System.Text.Json.Serialization;

namespace TripPin.Infrastructure.OData.Dtos;

/// <summary>
/// Envelope for a collection response.
/// </summary>
/// <remarks>
/// <see cref="NextLink"/> is captured even though paging is driven with
/// $top/$skip: the service pages at 8 rows regardless, so the envelope will
/// carry one and silently ignoring it would hide truncation.
/// </remarks>
public sealed class ODataCollectionResponse<T>
{
    [JsonPropertyName("value")]
    public IReadOnlyList<T> Value { get; set; } = [];

    [JsonPropertyName("@odata.count")]
    public int? Count { get; set; }

    [JsonPropertyName("@odata.nextLink")]
    public string? NextLink { get; set; }
}
