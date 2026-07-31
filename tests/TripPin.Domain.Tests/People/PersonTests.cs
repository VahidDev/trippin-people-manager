using FluentAssertions;
using TripPin.Domain.Common;
using TripPin.Domain.People;
using Xunit;

namespace TripPin.Domain.Tests.People;

public sealed class PersonTests
{
    private static Person Russell(
        IReadOnlyList<EmailAddress>? emails = null,
        IReadOnlyList<Address>? addresses = null,
        Gender gender = Gender.Male) =>
        Person.Create(
            UserName.From("russellwhyte"),
            PersonName.From("Russell", "Whyte"),
            emails ?? [EmailAddress.From("russell@example.com")],
            addresses ?? [Address.From("187 Suffolk Ln.", "Boise", "ID", "United States")],
            gender,
            ConcurrencyToken.From("W/\"1\""));

    [Fact]
    public void Create_populates_every_member()
    {
        var person = Russell();

        person.UserName.Value.Should().Be("russellwhyte");
        person.Name.First.Should().Be("Russell");
        person.Emails.Should().HaveCount(1);
        person.Addresses.Should().HaveCount(1);
        person.Gender.Should().Be(Gender.Male);
        person.Concurrency.Value.Should().Be("W/\"1\"");
    }

    [Fact]
    public void Create_accepts_empty_collections()
    {
        var person = Russell(emails: [], addresses: []);

        person.Emails.Should().BeEmpty();
        person.Addresses.Should().BeEmpty();
    }

    [Fact]
    public void Create_rejects_an_undefined_gender()
    {
        var act = () => Russell(gender: (Gender)99);

        act.Should().Throw<DomainException>().WithMessage("'99' is not a valid gender.");
    }

    [Fact]
    public void Create_rejects_a_null_entry_in_a_collection()
    {
        var emails = new EmailAddress[] { null! };

        var act = () => Russell(emails: emails);

        act.Should().Throw<DomainException>().WithMessage("*must not contain null entries*");
    }

    [Fact]
    public void Create_rejects_a_missing_required_member()
    {
        var act = () => Person.Create(
            UserName.From("russellwhyte"),
            PersonName.From("Russell", "Whyte"),
            [],
            [],
            Gender.Male,
            null!);

        act.Should().Throw<DomainException>().WithMessage("concurrency is required.");
    }

    /// <summary>
    /// Without a defensive copy the aggregate would be immutable in name only:
    /// the caller keeps a live reference to the backing list.
    /// </summary>
    [Fact]
    public void Create_copies_the_collections_it_is_given()
    {
        var emails = new List<EmailAddress> { EmailAddress.From("a@example.com") };

        var person = Russell(emails: emails);
        emails.Add(EmailAddress.From("b@example.com"));

        person.Emails.Should().HaveCount(1);
    }

    [Fact]
    public void WithEmails_replaces_wholesale_and_leaves_the_original_untouched()
    {
        var person = Russell(emails: [EmailAddress.From("a@example.com")]);

        var updated = person.WithEmails([EmailAddress.From("b@example.com")]);

        updated.Emails.Should().ContainSingle().Which.Value.Should().Be("b@example.com");
        person.Emails.Should().ContainSingle().Which.Value.Should().Be("a@example.com");
        updated.Should().NotBeSameAs(person);
    }

    [Fact]
    public void WithEmails_can_clear_the_collection()
    {
        Russell().WithEmails([]).Emails.Should().BeEmpty();
    }

    [Fact]
    public void WithName_preserves_every_other_member()
    {
        var person = Russell();

        var updated = person.WithName(PersonName.From("Russ", "White"));

        updated.Name.ToString().Should().Be("Russ White");
        updated.UserName.Should().Be(person.UserName);
        updated.Emails.Should().BeEquivalentTo(person.Emails);
        updated.Addresses.Should().BeEquivalentTo(person.Addresses);
        updated.Gender.Should().Be(person.Gender);
        updated.Concurrency.Should().Be(person.Concurrency);
    }

    [Fact]
    public void WithGender_preserves_every_other_member()
    {
        var person = Russell();

        var updated = person.WithGender(Gender.Unknown);

        updated.Gender.Should().Be(Gender.Unknown);
        updated.Name.Should().Be(person.Name);
        updated.Concurrency.Should().Be(person.Concurrency);
    }

    [Fact]
    public void WithGender_rejects_an_undefined_value()
    {
        var act = () => Russell().WithGender((Gender)42);

        act.Should().Throw<DomainException>();
    }

    /// <summary>
    /// The token is not something the aggregate may invent. It comes from the
    /// service on read and is carried back on write, so no With* member
    /// changes it.
    /// </summary>
    [Fact]
    public void No_mutating_member_alters_the_concurrency_token()
    {
        var person = Russell();

        person.WithName(PersonName.From("A", "B")).Concurrency.Should().Be(person.Concurrency);
        person.WithEmails([]).Concurrency.Should().Be(person.Concurrency);
        person.WithGender(Gender.Female).Concurrency.Should().Be(person.Concurrency);
    }
}
