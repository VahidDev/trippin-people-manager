using FluentAssertions;
using TripPin.Application.Abstractions;
using TripPin.Application.People.UpdatePerson;
using TripPin.Domain.Common;
using TripPin.Domain.People;
using Xunit;

namespace TripPin.Application.Tests.People;

/// <summary>
/// Edge cases around the validator, complementing
/// <see cref="UpdatePersonValidatorTests"/>, which covers the mainline rules.
/// </summary>
/// <remarks>
/// Two themes. First, that Domain rules genuinely reach the caller through the
/// validator rather than being enforced only where they are declared. Second,
/// the clear-versus-untouched question asked of <em>every</em> optional field,
/// not just emails.
/// </remarks>
public sealed class UpdatePersonValidatorEdgeCaseTests
{
    private static UpdatePersonCommand Command(
        string? firstName = null,
        string? lastName = null,
        IReadOnlyList<string>? emails = null,
        Gender? gender = null) =>
        new()
        {
            UserName = UserName.From("russellwhyte"),
            Concurrency = ConcurrencyToken.From("W/\"1\""),
            FirstName = firstName,
            LastName = lastName,
            Emails = emails,
            Gender = gender,
        };

    // -----------------------------------------------------------------
    // Whitespace that is not simply a space
    // -----------------------------------------------------------------

    /// <summary>
    /// Tabs, newlines and non-breaking spaces all reach a console prompt from
    /// a paste, and none of them make a valid name.
    /// </summary>
    [Theory]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("  \t  ")]
    [InlineData(" ")]
    [InlineData(" ")]
    public void Exotic_whitespace_is_still_a_blank_name(string value)
    {
        var result = UpdatePersonValidator.Validate(Command(firstName: value));

        result.Status.Should().Be(ResultStatus.ValidationFailed);
        result.Errors.Should().Equal("First name is required.");
    }

    [Theory]
    [InlineData("\t")]
    [InlineData("   \r\n  ")]
    [InlineData(" ")]
    public void Exotic_whitespace_is_still_a_blank_email(string value)
    {
        var result = UpdatePersonValidator.Validate(Command(emails: [value]));

        result.Status.Should().Be(ResultStatus.ValidationFailed);
        result.Errors.Should().ContainSingle().Which.Should().StartWith("Emails[0]:");
    }

    /// <summary>
    /// A name surrounded by tabs is a real name, so it is trimmed rather than
    /// rejected.
    /// </summary>
    [Fact]
    public void Surrounding_whitespace_is_stripped_from_an_otherwise_valid_name()
    {
        var result = UpdatePersonValidator.Validate(Command(firstName: "\tRuss\r\n"));

        result.IsSuccess.Should().BeTrue();
        result.Value!.FirstName.Should().Be("Russ");
    }

    // -----------------------------------------------------------------
    // Malformed emails beyond the obvious
    // -----------------------------------------------------------------

    /// <summary>
    /// The service validates none of these: it accepts every one with a 204.
    /// This asserts the Domain rules actually surface through the validator,
    /// with the offending position attached.
    /// </summary>
    [Theory]
    [InlineData("not-an-email")]
    [InlineData("no-at-sign")]
    [InlineData("@example.com")]
    [InlineData("user@")]
    [InlineData("user @example.com")]
    [InlineData("user@exam ple.com")]
    [InlineData("user@localhost")]
    [InlineData("user@example")]
    [InlineData("user@example.com.")]
    [InlineData("user@.example.com")]
    [InlineData("Russell Whyte <russell@example.com>")]
    public void Every_malformed_email_shape_is_rejected(string value)
    {
        var result = UpdatePersonValidator.Validate(Command(emails: [value]));

        result.Status.Should().Be(ResultStatus.ValidationFailed);
        result.Errors.Should().ContainSingle().Which.Should().StartWith("Emails[0]:");
    }

    [Theory]
    [InlineData("first.last@sub.domain.co.uk")]
    [InlineData("user+tag@example.com")]
    [InlineData("user_name@example.com")]
    [InlineData("UPPER@EXAMPLE.COM")]
    public void Unusual_but_valid_addresses_are_accepted(string value)
    {
        UpdatePersonValidator.Validate(Command(emails: [value])).IsSuccess.Should().BeTrue();
    }

    /// <summary>
    /// The index has to survive a mixed list, or the message points at the
    /// wrong row in the console.
    /// </summary>
    [Fact]
    public void The_reported_position_matches_the_offending_entry()
    {
        var result = UpdatePersonValidator.Validate(Command(emails:
        [
            "good@example.com",
            "also.good@example.com",
            "bad",
            "fine@example.com",
            "worse",
        ]));

        result.Errors.Should().HaveCount(2);
        result.Errors[0].Should().StartWith("Emails[2]:");
        result.Errors[1].Should().StartWith("Emails[4]:");
    }

    [Fact]
    public void A_duplicate_address_is_accepted_because_the_service_permits_it()
    {
        var result = UpdatePersonValidator.Validate(
            Command(emails: ["a@example.com", "a@example.com"]));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Emails.Should().HaveCount(2);
    }

    // -----------------------------------------------------------------
    // Length limits reaching the caller
    // -----------------------------------------------------------------

    [Fact]
    public void An_over_long_first_name_is_rejected_through_the_validator()
    {
        var result = UpdatePersonValidator.Validate(
            Command(firstName: new string('a', PersonName.MaxPartLength + 1)));

        result.Status.Should().Be(ResultStatus.ValidationFailed);
        result.Errors.Should().ContainSingle().Which.Should().Contain("100 characters or fewer");
    }

    [Fact]
    public void A_name_at_the_limit_is_accepted()
    {
        UpdatePersonValidator.Validate(
            Command(firstName: new string('a', PersonName.MaxPartLength)))
            .IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void An_over_long_email_is_rejected_through_the_validator()
    {
        var result = UpdatePersonValidator.Validate(
            Command(emails: [$"{new string('a', EmailAddress.MaxLength)}@example.com"]));

        result.Status.Should().Be(ResultStatus.ValidationFailed);
        result.Errors.Should().ContainSingle().Which.Should().StartWith("Emails[0]:");
    }

    // -----------------------------------------------------------------
    // Untouched versus cleared, for every optional field
    // -----------------------------------------------------------------

    /// <summary>
    /// The asymmetry stated outright: emails are the only field with a clear
    /// operation, and that is a property of the service, not an oversight.
    /// </summary>
    /// <remarks>
    /// Names are required, so a blank is a validation error rather than an
    /// erasure. Gender cannot hold "no value" either: a null write is accepted
    /// with a 204 and silently coerced to Male, so <c>Unknown</c> carries that
    /// meaning instead. Only the email collection has a representable empty
    /// state, which is why only it distinguishes null from empty.
    /// </remarks>
    [Fact]
    public void Emails_is_the_only_field_with_a_clear_operation()
    {
        // Emails: null and empty are both valid and mean different things.
        UpdatePersonValidator.Validate(Command(emails: null, firstName: "Russ"))
            .Value!.Emails.Should().BeNull();
        UpdatePersonValidator.Validate(Command(emails: []))
            .Value!.Emails.Should().NotBeNull().And.BeEmpty();

        // Names: blank is an error, never an erasure.
        UpdatePersonValidator.Validate(Command(firstName: ""))
            .Status.Should().Be(ResultStatus.ValidationFailed);
        UpdatePersonValidator.Validate(Command(lastName: ""))
            .Status.Should().Be(ResultStatus.ValidationFailed);

        // Gender: no clear operation exists; Unknown is the substitute.
        UpdatePersonValidator.Validate(Command(gender: Gender.Unknown))
            .Value!.Gender.Should().Be(Gender.Unknown);
    }

    [Fact]
    public void Untouched_means_null_for_every_optional_field()
    {
        var result = UpdatePersonValidator.Validate(Command(firstName: "Russ"));

        result.IsSuccess.Should().BeTrue();
        result.Value!.LastName.Should().BeNull();
        result.Value.Emails.Should().BeNull();
        result.Value.Gender.Should().BeNull();
    }

    /// <summary>
    /// Each field must be independently settable, since the payload is built
    /// from exactly the ones that are non-null.
    /// </summary>
    [Fact]
    public void Each_optional_field_can_be_the_only_one_supplied()
    {
        UpdatePersonValidator.Validate(Command(firstName: "Russ")).Value!.HasChanges
            .Should().BeTrue();
        UpdatePersonValidator.Validate(Command(lastName: "White")).Value!.HasChanges
            .Should().BeTrue();
        UpdatePersonValidator.Validate(Command(gender: Gender.Female)).Value!.HasChanges
            .Should().BeTrue();
        UpdatePersonValidator.Validate(Command(emails: [])).Value!.HasChanges
            .Should().BeTrue();
    }

    /// <summary>
    /// An empty list is a change in its own right, even though it looks like
    /// absence. Treating it as "nothing supplied" would silently discard the
    /// only instruction the user gave.
    /// </summary>
    [Fact]
    public void Clearing_emails_alone_is_a_change()
    {
        var result = UpdatePersonValidator.Validate(Command(emails: []));

        result.IsSuccess.Should().BeTrue();
        result.Errors.Should().NotContain("No changes were supplied.");
    }

    // -----------------------------------------------------------------
    // Required members
    // -----------------------------------------------------------------

    /// <summary>
    /// A blank token would be sent as an empty If-Match and answered with a
    /// 428, so it is stopped where it is constructed.
    /// </summary>
    [Fact]
    public void A_command_cannot_be_built_without_a_concurrency_token()
    {
        var act = () => ConcurrencyToken.From("  ");

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void The_gender_boundary_values_are_handled()
    {
        UpdatePersonValidator.Validate(Command(gender: (Gender)(-1)))
            .Status.Should().Be(ResultStatus.ValidationFailed);
        UpdatePersonValidator.Validate(Command(gender: (Gender)3))
            .Status.Should().Be(ResultStatus.ValidationFailed);
        UpdatePersonValidator.Validate(Command(gender: (Gender)2))
            .IsSuccess.Should().BeTrue();
    }
}
