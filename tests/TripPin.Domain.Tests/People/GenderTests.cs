using FluentAssertions;
using TripPin.Domain.People;
using Xunit;

namespace TripPin.Domain.Tests.People;

public sealed class GenderTests
{
    /// <summary>
    /// The ordinals are not arbitrary: they mirror the service's PersonGender
    /// enumeration. Male being zero is load-bearing, because writing null to
    /// Gender is accepted with a 204 and silently coerced to ordinal zero.
    /// If these drift, that coercion becomes silently wrong.
    /// </summary>
    [Fact]
    public void Ordinals_match_the_service_enumeration()
    {
        ((int)Gender.Male).Should().Be(0);
        ((int)Gender.Female).Should().Be(1);
        ((int)Gender.Unknown).Should().Be(2);
    }

    /// <summary>
    /// The service's null-coercion trap, stated as an assertion: because Male
    /// is the zero value, <c>default(Gender)</c> is Male. Any code path that
    /// forgets to set a gender silently produces Male rather than something
    /// obviously wrong, which is why the field is never sent as null.
    /// </summary>
    [Fact]
    public void Default_is_Male_which_is_exactly_what_a_null_write_coerces_to()
    {
        default(Gender).Should().Be(Gender.Male);
    }

    /// <summary>
    /// Unknown must be a real member rather than an absence. The service
    /// declares the property nullable but cannot actually store "no value",
    /// so a nullable domain type would promise something unrepresentable.
    /// </summary>
    [Fact]
    public void Unknown_is_an_explicit_third_state()
    {
        Enum.IsDefined(Gender.Unknown).Should().BeTrue();
        Gender.Unknown.Should().NotBe(Gender.Male);
        Gender.Unknown.Should().NotBe(Gender.Female);
    }

    [Fact]
    public void The_enumeration_has_exactly_three_members()
    {
        Enum.GetValues<Gender>().Should().HaveCount(3);
    }

    [Fact]
    public void An_out_of_range_value_is_not_defined()
    {
        Enum.IsDefined((Gender)99).Should().BeFalse();
    }
}
