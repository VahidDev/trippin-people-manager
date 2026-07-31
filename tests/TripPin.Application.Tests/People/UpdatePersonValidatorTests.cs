using FluentAssertions;
using TripPin.Application.Abstractions;
using TripPin.Application.People.UpdatePerson;
using TripPin.Domain.Common;
using TripPin.Domain.People;
using Xunit;

namespace TripPin.Application.Tests.People;

public sealed class UpdatePersonValidatorTests
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

    // ---------------------------------------------------------------------
    // Sparseness
    // ---------------------------------------------------------------------

    [Fact]
    public void An_untouched_field_stays_null_on_the_update()
    {
        var result = UpdatePersonValidator.Validate(Command(firstName: "Russ"));

        result.IsSuccess.Should().BeTrue();
        result.Value!.FirstName.Should().Be("Russ");
        result.Value.LastName.Should().BeNull();
        result.Value.Emails.Should().BeNull();
        result.Value.Gender.Should().BeNull();
    }

    [Fact]
    public void The_key_and_token_are_carried_through_unchanged()
    {
        var result = UpdatePersonValidator.Validate(Command(gender: Gender.Female));

        result.Value!.UserName.Value.Should().Be("russellwhyte");
        result.Value.Concurrency.Value.Should().Be("W/\"1\"");
    }

    [Fact]
    public void A_command_that_changes_nothing_is_rejected()
    {
        var result = UpdatePersonValidator.Validate(Command());

        result.Status.Should().Be(ResultStatus.ValidationFailed);
        result.Errors.Should().Equal("No changes were supplied.");
    }

    /// <summary>
    /// A command that is both empty and malformed should report the malformed
    /// field, which is actionable, rather than "no changes", which is not.
    /// </summary>
    [Fact]
    public void A_malformed_field_is_reported_ahead_of_no_changes()
    {
        var result = UpdatePersonValidator.Validate(Command(firstName: "   "));

        result.Errors.Should().Equal("First name is required.");
        result.Errors.Should().NotContain("No changes were supplied.");
    }

    [Fact]
    public void HasChanges_is_false_only_when_every_field_is_untouched()
    {
        UpdatePersonValidator.Validate(Command(gender: Gender.Unknown))
            .Value!.HasChanges.Should().BeTrue();

        UpdatePersonValidator.Validate(Command(emails: []))
            .Value!.HasChanges.Should().BeTrue();
    }

    // ---------------------------------------------------------------------
    // Emails: the null / empty distinction
    // ---------------------------------------------------------------------

    /// <summary>
    /// Null means "leave the collection alone" and must not become an empty
    /// list, which would silently clear the person's emails.
    /// </summary>
    [Fact]
    public void Null_emails_means_untouched_and_stays_null()
    {
        var result = UpdatePersonValidator.Validate(Command(emails: null, gender: Gender.Male));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Emails.Should().BeNull();
    }

    /// <summary>
    /// An empty list means "clear them" and is a valid instruction on its own.
    /// It must survive as an empty list, because Infrastructure has to
    /// serialise it as [] and the service answers null with a 500.
    /// </summary>
    [Fact]
    public void Empty_emails_means_clear_and_stays_an_empty_list()
    {
        var result = UpdatePersonValidator.Validate(Command(emails: []));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Emails.Should().NotBeNull();
        result.Value.Emails.Should().BeEmpty();
    }

    [Fact]
    public void The_two_empty_states_are_distinguishable_on_the_update()
    {
        var untouched = UpdatePersonValidator.Validate(Command(gender: Gender.Male)).Value!;
        var cleared = UpdatePersonValidator.Validate(Command(emails: [])).Value!;

        untouched.Emails.Should().BeNull();
        cleared.Emails.Should().BeEmpty();
        cleared.Emails.Should().NotBeSameAs(untouched.Emails);
    }

    [Fact]
    public void Valid_emails_are_converted_to_value_objects()
    {
        var result = UpdatePersonValidator.Validate(
            Command(emails: ["a@example.com", "  b@example.com  "]));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Emails!.Select(email => email.Value)
            .Should().Equal("a@example.com", "b@example.com");
    }

    /// <summary>
    /// The service accepts this with a 204, so rejecting it is entirely our
    /// responsibility.
    /// </summary>
    [Fact]
    public void An_invalid_email_is_rejected_with_its_position()
    {
        var result = UpdatePersonValidator.Validate(
            Command(emails: ["a@example.com", "not-an-email"]));

        result.Status.Should().Be(ResultStatus.ValidationFailed);
        result.Errors.Should().ContainSingle()
            .Which.Should().Be("Emails[1]: 'not-an-email' is not a valid email address.");
    }

    [Fact]
    public void Every_invalid_email_is_reported_not_just_the_first()
    {
        var result = UpdatePersonValidator.Validate(
            Command(emails: ["bad", "also-bad", "c@example.com"]));

        result.Errors.Should().HaveCount(2);
        result.Errors[0].Should().StartWith("Emails[0]:");
        result.Errors[1].Should().StartWith("Emails[1]:");
    }

    // ---------------------------------------------------------------------
    // Names
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_supplied_but_blank_first_name_is_rejected(string value)
    {
        var result = UpdatePersonValidator.Validate(Command(firstName: value));

        result.Errors.Should().Equal("First name is required.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_supplied_but_blank_last_name_is_rejected(string value)
    {
        var result = UpdatePersonValidator.Validate(Command(lastName: value));

        result.Errors.Should().Equal("Last name is required.");
    }

    [Fact]
    public void Names_are_trimmed()
    {
        var result = UpdatePersonValidator.Validate(
            Command(firstName: "  Russ  ", lastName: "  White  "));

        result.Value!.FirstName.Should().Be("Russ");
        result.Value.LastName.Should().Be("White");
    }

    /// <summary>
    /// Only one name may change, because the wire protocol patches per field.
    /// Requiring both would make every rename send data it did not need to.
    /// </summary>
    [Fact]
    public void One_name_part_may_change_on_its_own()
    {
        var result = UpdatePersonValidator.Validate(Command(lastName: "White"));

        result.IsSuccess.Should().BeTrue();
        result.Value!.LastName.Should().Be("White");
        result.Value.FirstName.Should().BeNull();
    }

    [Fact]
    public void Both_name_errors_are_reported_together()
    {
        var result = UpdatePersonValidator.Validate(Command(firstName: " ", lastName: " "));

        result.Errors.Should().Equal("First name is required.", "Last name is required.");
    }

    // ---------------------------------------------------------------------
    // Gender
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(Gender.Male)]
    [InlineData(Gender.Female)]
    [InlineData(Gender.Unknown)]
    public void Every_defined_gender_is_accepted(Gender gender)
    {
        var result = UpdatePersonValidator.Validate(Command(gender: gender));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Gender.Should().Be(gender);
    }

    /// <summary>
    /// Guards the cast boundary. The service returns 500 for an out-of-range
    /// value, so catching it here saves a round trip and gives a better message.
    /// </summary>
    [Fact]
    public void An_undefined_gender_is_rejected()
    {
        var result = UpdatePersonValidator.Validate(Command(gender: (Gender)99));

        result.Status.Should().Be(ResultStatus.ValidationFailed);
        result.Errors.Should().Equal("'99' is not a valid gender.");
    }

    /// <summary>
    /// A null gender means untouched, never "set the gender to null". The
    /// service accepts a null write with a 204 and silently coerces it to
    /// Male, so the field is simply never emitted.
    /// </summary>
    [Fact]
    public void A_null_gender_means_untouched_and_never_becomes_Male()
    {
        var result = UpdatePersonValidator.Validate(Command(gender: null, firstName: "Russ"));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Gender.Should().BeNull();
        result.Value.Gender.Should().NotBe(Gender.Male);
    }

    /// <summary>
    /// Clearing a gender is not expressible, by design. Unknown is the way to
    /// say "not stated", and it is a real value the service can store.
    /// </summary>
    [Fact]
    public void Unknown_is_how_an_unstated_gender_is_expressed()
    {
        var result = UpdatePersonValidator.Validate(Command(gender: Gender.Unknown));

        result.Value!.Gender.Should().Be(Gender.Unknown);
    }

    // ---------------------------------------------------------------------
    // Contract
    // ---------------------------------------------------------------------

    [Fact]
    public void Validate_rejects_a_null_command()
    {
        var act = () => UpdatePersonValidator.Validate(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Errors_across_several_fields_are_all_reported()
    {
        var result = UpdatePersonValidator.Validate(
            Command(firstName: " ", emails: ["bad"], gender: (Gender)99));

        result.Errors.Should().HaveCount(3);
    }
}
