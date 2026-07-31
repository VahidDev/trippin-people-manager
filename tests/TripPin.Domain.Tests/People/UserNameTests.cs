using FluentAssertions;
using TripPin.Domain.Common;
using TripPin.Domain.People;
using Xunit;

namespace TripPin.Domain.Tests.People;

public sealed class UserNameTests
{
    [Fact]
    public void From_accepts_a_valid_key()
    {
        UserName.From("russellwhyte").Value.Should().Be("russellwhyte");
    }

    [Fact]
    public void From_trims_surrounding_whitespace()
    {
        UserName.From("  russellwhyte  ").Value.Should().Be("russellwhyte");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void From_rejects_a_blank_key(string? value)
    {
        var act = () => UserName.From(value);

        act.Should().Throw<DomainException>().WithMessage("User name is required.");
    }

    [Fact]
    public void From_rejects_a_key_over_the_length_limit()
    {
        var act = () => UserName.From(new string('a', UserName.MaxLength + 1));

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void From_accepts_a_key_at_the_length_limit()
    {
        UserName.From(new string('a', UserName.MaxLength)).Value
            .Should().HaveLength(UserName.MaxLength);
    }

    [Fact]
    public void TryFrom_reports_the_reason_instead_of_throwing()
    {
        UserName.TryFrom("  ", out var result, out var error).Should().BeFalse();

        result.Should().BeNull();
        error.Should().Be("User name is required.");
    }

    [Fact]
    public void Equality_is_by_value()
    {
        UserName.From("russellwhyte").Should().Be(UserName.From("russellwhyte"));
        UserName.From("russellwhyte").Should().NotBe(UserName.From("scottketchum"));
    }

    /// <summary>
    /// The service treats keys as case-sensitive, so the value object must
    /// too. Folding case here would make two distinct people compare equal.
    /// </summary>
    [Fact]
    public void Equality_is_case_sensitive()
    {
        UserName.From("russellwhyte").Should().NotBe(UserName.From("RussellWhyte"));
    }
}
