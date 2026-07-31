namespace TripPin.Domain.People;

/// <summary>
/// An entry in a person's address list. Read-only in this version.
/// </summary>
/// <remarks>
/// Not writable by deliberate decision (see docs/adr/ADR-004). An update that
/// omits the required City is accepted with a 204 and then permanently
/// corrupts the record: every later read of that person, and of the whole
/// People collection, fails with a 500. There is no safe partial write, so
/// this type is projected outward for display and never sent back.
/// <para>
/// Consequently this is the one value object that does not reject blank
/// input. It is a projection of data the service owns, and a strict rule here
/// would fail a <em>read</em> over content we neither control nor write back.
/// Missing parts normalise to empty rather than throwing.
/// </para>
/// </remarks>
public sealed record Address
{
    private Address(string street, string city, string region, string countryRegion)
    {
        Street = street;
        City = city;
        Region = region;
        CountryRegion = countryRegion;
    }

    public string Street { get; }

    public string City { get; }

    public string Region { get; }

    public string CountryRegion { get; }

    public static Address From(string? street, string? city, string? region, string? countryRegion) =>
        new(
            Normalize(street),
            Normalize(city),
            Normalize(region),
            Normalize(countryRegion));

    public override string ToString()
    {
        var parts = new[] { Street, City, Region, CountryRegion }
            .Where(part => part.Length > 0);

        return string.Join(", ", parts);
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
