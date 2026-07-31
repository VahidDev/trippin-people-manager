using System.Text.Json.Serialization;

namespace TripPin.Infrastructure.OData.Dtos;

/// <summary>
/// The standard OData error envelope: <c>{"error":{"code":..,"message":..}}</c>.
/// </summary>
/// <remarks>
/// Only <c>code</c> and <c>message</c> are read. The <c>innererror</c> this
/// service also sends carries a full server stack trace, which is neither
/// useful to a caller nor something to put in front of a user.
/// </remarks>
public sealed class ODataErrorResponse
{
    [JsonPropertyName("error")]
    public ODataErrorDetail? Error { get; set; }
}

public sealed class ODataErrorDetail
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
