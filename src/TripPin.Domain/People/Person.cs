using TripPin.Domain.Common;

namespace TripPin.Domain.People;

/// <summary>
/// A person, and the aggregate root of this model.
/// </summary>
/// <remarks>
/// Enforces the invariants the service claims but does not check. Instances
/// are immutable; the <c>With*</c> members return a modified copy.
/// <para>
/// Collections are defensively copied on the way in and exposed as
/// <see cref="IReadOnlyList{T}"/>. Without the copy a caller could hand in a
/// <c>List&lt;T&gt;</c> and keep mutating it, which would make the aggregate
/// immutable in name only.
/// </para>
/// </remarks>
public sealed class Person
{
    private Person(
        UserName userName,
        PersonName name,
        IReadOnlyList<EmailAddress> emails,
        IReadOnlyList<Address> addresses,
        Gender gender,
        ConcurrencyToken concurrency)
    {
        UserName = userName;
        Name = name;
        Emails = emails;
        Addresses = addresses;
        Gender = gender;
        Concurrency = concurrency;
    }

    public UserName UserName { get; }

    public PersonName Name { get; }

    /// <summary>Writable. Replaced wholesale on update, never merged.</summary>
    public IReadOnlyList<EmailAddress> Emails { get; }

    /// <summary>Read-only projection. See <see cref="Address"/>.</summary>
    public IReadOnlyList<Address> Addresses { get; }

    public Gender Gender { get; }

    /// <summary>Required for any update; the service rejects writes without it.</summary>
    public ConcurrencyToken Concurrency { get; }

    public static Person Create(
        UserName userName,
        PersonName name,
        IReadOnlyList<EmailAddress> emails,
        IReadOnlyList<Address> addresses,
        Gender gender,
        ConcurrencyToken concurrency)
    {
        Require(userName, nameof(userName));
        Require(name, nameof(name));
        Require(emails, nameof(emails));
        Require(addresses, nameof(addresses));
        Require(concurrency, nameof(concurrency));
        RequireDefinedGender(gender);

        return new Person(
            userName,
            name,
            CopyOf(emails, nameof(emails)),
            CopyOf(addresses, nameof(addresses)),
            gender,
            concurrency);
    }

    public Person WithName(PersonName name)
    {
        Require(name, nameof(name));

        return new Person(UserName, name, Emails, Addresses, Gender, Concurrency);
    }

    public Person WithEmails(IReadOnlyList<EmailAddress> emails)
    {
        Require(emails, nameof(emails));

        return new Person(
            UserName,
            Name,
            CopyOf(emails, nameof(emails)),
            Addresses,
            Gender,
            Concurrency);
    }

    public Person WithGender(Gender gender)
    {
        RequireDefinedGender(gender);

        return new Person(UserName, Name, Emails, Addresses, gender, Concurrency);
    }

    private static void Require(object? value, string parameterName)
    {
        if (value is null)
        {
            throw new DomainException($"{parameterName} is required.");
        }
    }

    private static void RequireDefinedGender(Gender gender)
    {
        if (!Enum.IsDefined(gender))
        {
            throw new DomainException($"'{(int)gender}' is not a valid gender.");
        }
    }

    private static T[] CopyOf<T>(IReadOnlyList<T> source, string parameterName)
        where T : class
    {
        var copy = source.ToArray();

        if (Array.IndexOf(copy, null) >= 0)
        {
            throw new DomainException($"{parameterName} must not contain null entries.");
        }

        return copy;
    }
}
