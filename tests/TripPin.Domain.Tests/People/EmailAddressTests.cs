using FluentAssertions;
using TripPin.Domain.Common;
using TripPin.Domain.People;
using Xunit;

namespace TripPin.Domain.Tests.People;

/// <summary>
/// The service accepts "not-an-email" with a 204 and performs no element
/// validation at all, so every meaningful check happens here.
/// </summary>
public sealed class EmailAddressTests
{
    [Theory]
    [InlineData("Russell@example.com")]
    [InlineData("Russell@contoso.com")]
    [InlineData("first.last@sub.domain.co.uk")]
    [InlineData("user+tag@example.com")]
    [InlineData("user_name@example.com")]
    public void From_accepts_a_valid_address(string value)
    {
        EmailAddress.From(value).Value.Should().Be(value);
    }

    [Fact]
    public void From_trims_surrounding_whitespace()
    {
        EmailAddress.From("  a@example.com  ").Value.Should().Be("a@example.com");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void From_rejects_a_blank_address(string? value)
    {
        var act = () => EmailAddress.From(value);

        act.Should().Throw<DomainException>().WithMessage("Email address is required.");
    }

    /// <summary>
    /// The exact value the live service accepted with a 204, which is the
    /// reason this type exists.
    /// </summary>
    [Fact]
    public void From_rejects_the_value_the_service_accepts()
    {
        var act = () => EmailAddress.From("not-an-email");

        act.Should().Throw<DomainException>();
    }

    [Theory]
    [InlineData("no-at-sign")]
    [InlineData("@example.com")]
    [InlineData("user@")]
    [InlineData("user @example.com")]
    [InlineData("user@exam ple.com")]
    public void From_rejects_a_malformed_address(string value)
    {
        var act = () => EmailAddress.From(value);

        act.Should().Throw<DomainException>();
    }

    /// <summary>
    /// MailAddress parses the display-name form happily, so this is one of the
    /// two checks layered on top of it. We want a bare address, not a header.
    /// </summary>
    [Fact]
    public void From_rejects_the_display_name_form()
    {
        var act = () => EmailAddress.From("Russell Whyte <russell@example.com>");

        act.Should().Throw<DomainException>();
    }

    /// <summary>The other layered check: MailAddress alone accepts "a@b".</summary>
    [Theory]
    [InlineData("user@localhost")]
    [InlineData("user@example")]
    public void From_rejects_an_undotted_host(string value)
    {
        var act = () => EmailAddress.From(value);

        act.Should().Throw<DomainException>();
    }

    [Theory]
    [InlineData("user@example.com.")]
    [InlineData("user@.example.com")]
    public void From_rejects_a_host_with_a_leading_or_trailing_dot(string value)
    {
        var act = () => EmailAddress.From(value);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void From_rejects_an_address_over_the_length_limit()
    {
        var local = new string('a', EmailAddress.MaxLength);

        var act = () => EmailAddress.From($"{local}@example.com");

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void TryFrom_reports_the_reason_instead_of_throwing()
    {
        EmailAddress.TryFrom("not-an-email", out var result, out var error).Should().BeFalse();

        result.Should().BeNull();
        error.Should().Be("'not-an-email' is not a valid email address.");
    }

    [Fact]
    public void Equality_is_by_value()
    {
        EmailAddress.From("a@example.com").Should().Be(EmailAddress.From("a@example.com"));
        EmailAddress.From("a@example.com").Should().NotBe(EmailAddress.From("b@example.com"));
    }
}
