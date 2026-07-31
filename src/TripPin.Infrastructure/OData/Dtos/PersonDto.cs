using System.Text.Json.Serialization;

namespace TripPin.Infrastructure.OData.Dtos;

public sealed class PersonDto
{
    [JsonPropertyName("@odata.etag")]
    public string? ETag { get; set; }

    [JsonPropertyName("UserName")]
    public string? UserName { get; set; }

    [JsonPropertyName("FirstName")]
    public string? FirstName { get; set; }

    [JsonPropertyName("LastName")]
    public string? LastName { get; set; }

    [JsonPropertyName("Emails")]
    public IReadOnlyList<string>? Emails { get; set; }

    [JsonPropertyName("AddressInfo")]
    public IReadOnlyList<LocationDto>? AddressInfo { get; set; }

    /// <summary>Bare enum name on the wire, for example "Female".</summary>
    [JsonPropertyName("Gender")]
    public string? Gender { get; set; }

    [JsonPropertyName("Concurrency")]
    public long Concurrency { get; set; }
}

/// <summary>Wire shape of a Location entry. Read-only; never sent back.</summary>
public sealed class LocationDto
{
    [JsonPropertyName("Address")]
    public string? Address { get; set; }

    [JsonPropertyName("City")]
    public CityDto? City { get; set; }
}

public sealed class CityDto
{
    [JsonPropertyName("CountryRegion")]
    public string? CountryRegion { get; set; }

    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("Region")]
    public string? Region { get; set; }
}
