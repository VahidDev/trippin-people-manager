using FluentAssertions;
using TripPin.Domain.Common;
using TripPin.Domain.People;
using Xunit;

namespace TripPin.Domain.Tests.People;

/// <summary>
/// These invariants exist precisely because the service does not enforce them:
/// an update omitting LastName is accepted with a 204.
/// </summary>
public sealed class PersonNameTests
{
    [Fact]
    public void From_accepts_a_complete_name()
    {
        var name = PersonName.From("Russell", "Whyte");

        name.First.Should().Be("Russell");
        name.Last.Should().Be("Whyte");
    }

    [Fact]
    public void From_trims_both_parts()
    {
        var name = PersonName.From("  Russell  ", "  Whyte  ");

        name.First.Should().Be("Russell");
        name.Last.Should().Be("Whyte");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void From_rejects_a_missing_first_name(string? first)
    {
        var act = () => PersonName.From(first, "Whyte");

        act.Should().Throw<DomainException>().WithMessage("First name is required.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void From_rejects_a_missing_last_name(string? last)
    {
        var act = () => PersonName.From("Russell", last);

        act.Should().Throw<DomainException>().WithMessage("Last name is required.");
    }

    [Fact]
    public void From_rejects_a_part_over_the_length_limit()
    {
        var act = () => PersonName.From(new string('a', PersonName.MaxPartLength + 1), "Whyte");

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void TryNormalizePart_names_the_offending_field()
    {
        PersonName.TryNormalizePart(null, "Last name", out var normalized, out var error)
            .Should().BeFalse();

        normalized.Should().BeNull();
        error.Should().Be("Last name is required.");
    }

    [Fact]
    public void TryNormalizePart_trims_a_valid_value()
    {
        PersonName.TryNormalizePart("  Russell ", "First name", out var normalized, out var error)
            .Should().BeTrue();

        normalized.Should().Be("Russell");
        error.Should().BeNull();
    }

    [Fact]
    public void ToString_joins_the_parts()
    {
        PersonName.From("Russell", "Whyte").ToString().Should().Be("Russell Whyte");
    }
}
