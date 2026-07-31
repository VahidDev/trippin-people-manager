using FluentAssertions;
using TripPin.Application.People.Models;
using TripPin.Domain.People;
using TripPin.Infrastructure.OData;
using Xunit;

namespace TripPin.Infrastructure.Tests.QueryBuilding;

/// <summary>
/// The read and write encodings of the gender enum are inverted, which is why
/// ODataFilterTranslator and PersonMapper are separate types. A single shared
/// helper would be wrong on one side by construction, so these two tests are
/// deliberately a matched pair.
/// </summary>
public sealed class EnumEncodingTests
{
    private const string QualifiedPrefix =
        "Microsoft.OData.SampleService.Models.TripPin.PersonGender";

    /// <summary>
    /// Measured: the bare form returns 500 from the live service.
    /// </summary>
    [Theory]
    [InlineData(Gender.Male, $"{QualifiedPrefix}'Male'")]
    [InlineData(Gender.Female, $"{QualifiedPrefix}'Female'")]
    [InlineData(Gender.Unknown, $"{QualifiedPrefix}'Unknown'")]
    public void Filter_literals_are_fully_qualified(Gender gender, string expected)
    {
        ODataFilterTranslator.ToFilterLiteral(gender).Should().Be(expected);
    }

    /// <summary>
    /// Measured: the qualified form returns 500 on a PATCH.
    /// </summary>
    [Theory]
    [InlineData(Gender.Male, "Male")]
    [InlineData(Gender.Female, "Female")]
    [InlineData(Gender.Unknown, "Unknown")]
    public void Patch_literals_are_bare(Gender gender, string expected)
    {
        PersonMapper.ToPatchLiteral(gender).Should().Be(expected);
    }

    /// <summary>
    /// States the inversion directly, so anyone tempted to unify the two
    /// helpers sees why that cannot work.
    /// </summary>
    [Fact]
    public void The_two_encodings_are_deliberately_different()
    {
        ODataFilterTranslator.ToFilterLiteral(Gender.Female)
            .Should().NotBe(PersonMapper.ToPatchLiteral(Gender.Female));

        ODataFilterTranslator.ToFilterLiteral(Gender.Female).Should().StartWith(QualifiedPrefix);
        PersonMapper.ToPatchLiteral(Gender.Female).Should().NotContain(".");
    }
}

public sealed class ODataFilterTranslatorTests
{
    private readonly ODataFilterTranslator _translator = new();

    [Fact]
    public void An_empty_filter_translates_to_nothing()
    {
        _translator.Translate(PersonFilter.Empty).Should().BeEmpty();
    }

    [Fact]
    public void A_name_fragment_searches_all_three_name_columns()
    {
        _translator.Translate(new PersonFilter { NameContains = "russ" })
            .Should().Be(
                "(contains(FirstName,'russ')"
                + " or contains(LastName,'russ')"
                + " or contains(UserName,'russ'))");
    }

    [Fact]
    public void A_gender_filter_uses_the_qualified_enum_literal()
    {
        _translator.Translate(new PersonFilter { Gender = Gender.Female })
            .Should().Be($"Gender eq {ODataFilterTranslator.GenderEnumTypeName}'Female'");
    }

    [Fact]
    public void An_email_fragment_uses_a_lambda_over_the_collection()
    {
        _translator.Translate(new PersonFilter { EmailContains = "contoso" })
            .Should().Be("Emails/any(e: contains(e,'contoso'))");
    }

    [Fact]
    public void Multiple_criteria_are_joined_with_and()
    {
        _translator.Translate(new PersonFilter
        {
            NameContains = "russ",
            Gender = Gender.Male,
            EmailContains = "example",
        })
            .Should().Be(
                "(contains(FirstName,'russ')"
                + " or contains(LastName,'russ')"
                + " or contains(UserName,'russ'))"
                + $" and Gender eq {ODataFilterTranslator.GenderEnumTypeName}'Male'"
                + " and Emails/any(e: contains(e,'example'))");
    }

    /// <summary>
    /// The whole reason PersonFilter is structured: user text reaches the
    /// query only through the escaper.
    /// </summary>
    [Fact]
    public void User_text_is_escaped_rather_than_concatenated()
    {
        _translator.Translate(new PersonFilter { NameContains = "O'Brien" })
            .Should().Contain("'O''Brien'").And.NotContain("'O'Brien'");
    }

    [Fact]
    public void An_injection_attempt_stays_inside_the_literal()
    {
        var filter = _translator.Translate(
            new PersonFilter { NameContains = "x' or UserName eq 'admin" });

        filter.Should().Contain("'x'' or UserName eq ''admin'");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_fragment_is_ignored(string value)
    {
        _translator.Translate(new PersonFilter { NameContains = value }).Should().BeEmpty();
    }

    [Fact]
    public void Fragments_are_trimmed()
    {
        _translator.Translate(new PersonFilter { NameContains = "  russ  " })
            .Should().Contain("'russ'");
    }
}
