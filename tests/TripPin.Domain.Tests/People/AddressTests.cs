using FluentAssertions;
using TripPin.Domain.People;
using Xunit;

namespace TripPin.Domain.Tests.People;

/// <summary>
/// Address is the one value object that tolerates blank input, because it is
/// a read projection of data the service owns and never receives back. A
/// strict rule here would fail a read rather than prevent a bad write.
/// </summary>
public sealed class AddressTests
{
    [Fact]
    public void From_keeps_a_complete_address()
    {
        var address = Address.From("187 Suffolk Ln.", "Boise", "ID", "United States");

        address.Street.Should().Be("187 Suffolk Ln.");
        address.City.Should().Be("Boise");
        address.Region.Should().Be("ID");
        address.CountryRegion.Should().Be("United States");
    }

    [Fact]
    public void From_normalizes_missing_parts_to_empty_rather_than_throwing()
    {
        var address = Address.From(null, null, null, null);

        address.Street.Should().BeEmpty();
        address.City.Should().BeEmpty();
        address.Region.Should().BeEmpty();
        address.CountryRegion.Should().BeEmpty();
    }

    [Fact]
    public void From_trims_each_part()
    {
        Address.From("  187 Suffolk Ln.  ", " Boise ", " ID ", " United States ")
            .Street.Should().Be("187 Suffolk Ln.");
    }

    [Fact]
    public void ToString_omits_empty_parts()
    {
        Address.From("187 Suffolk Ln.", "Boise", null, null)
            .ToString().Should().Be("187 Suffolk Ln., Boise");
    }

    [Fact]
    public void Equality_is_by_value()
    {
        Address.From("a", "b", "c", "d").Should().Be(Address.From("a", "b", "c", "d"));
        Address.From("a", "b", "c", "d").Should().NotBe(Address.From("a", "b", "c", "e"));
    }
}
