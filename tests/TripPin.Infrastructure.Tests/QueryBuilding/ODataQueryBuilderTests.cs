using FluentAssertions;
using TripPin.Infrastructure.OData;
using Xunit;

namespace TripPin.Infrastructure.Tests.QueryBuilding;

/// <summary>
/// Golden-string tests. Escaping is unusually important here: a malformed
/// $filter returns 200 with zero rows rather than an error, so a bug in this
/// class is indistinguishable from a legitimate empty result at runtime.
/// </summary>
public sealed class ODataQueryBuilderTests
{
    [Fact]
    public void An_empty_builder_produces_no_query_string()
    {
        new ODataQueryBuilder().Build().Should().BeEmpty();
    }

    [Fact]
    public void Select_joins_properties_with_encoded_commas()
    {
        new ODataQueryBuilder().Select("UserName", "FirstName").Build()
            .Should().Be("?$select=UserName%2CFirstName");
    }

    [Fact]
    public void Paging_translates_to_top_and_skip()
    {
        new ODataQueryBuilder().Page(3, 8).Build().Should().Be("?$top=8&$skip=16");
    }

    [Fact]
    public void The_first_page_skips_nothing()
    {
        new ODataQueryBuilder().Page(1, 8).Build().Should().Be("?$top=8&$skip=0");
    }

    /// <summary>
    /// A defensive clamp. The handlers reject page zero, so this only matters
    /// if something bypasses them, and a negative $skip is a 400 from the
    /// service rather than an empty page.
    /// </summary>
    [Fact]
    public void A_page_below_one_never_produces_a_negative_skip()
    {
        new ODataQueryBuilder().Page(0, 8).Build().Should().Be("?$top=8&$skip=0");
    }

    [Fact]
    public void IncludeCount_requests_the_total()
    {
        new ODataQueryBuilder().IncludeCount().Build().Should().Be("?$count=true");
    }

    [Fact]
    public void OrderBy_appends_the_direction_only_when_descending()
    {
        new ODataQueryBuilder().OrderBy("LastName").Build()
            .Should().Be("?$orderby=LastName");

        new ODataQueryBuilder().OrderBy("LastName", descending: true).Build()
            .Should().Be("?$orderby=LastName%20desc");
    }

    /// <summary>
    /// Canonical ordering is what lets the built query double as a cache key:
    /// two callers who set the same options in different orders must produce
    /// the same string.
    /// </summary>
    [Fact]
    public void Options_are_emitted_in_canonical_order_regardless_of_call_order()
    {
        var forwards = new ODataQueryBuilder()
            .Select("UserName")
            .Filter("contains(FirstName,'R')")
            .Page(1, 8)
            .IncludeCount()
            .Build();

        var backwards = new ODataQueryBuilder()
            .IncludeCount()
            .Page(1, 8)
            .Filter("contains(FirstName,'R')")
            .Select("UserName")
            .Build();

        forwards.Should().Be(backwards);
        forwards.Should().Be(
            "?$select=UserName&$filter=contains%28FirstName%2C%27R%27%29&$top=8&$skip=0&$count=true");
    }

    [Fact]
    public void A_blank_filter_is_dropped_rather_than_emitted_empty()
    {
        new ODataQueryBuilder().Filter("").Build().Should().BeEmpty();
        new ODataQueryBuilder().Filter("   ").Build().Should().BeEmpty();
        new ODataQueryBuilder().Filter(null).Build().Should().BeEmpty();
    }

    // -----------------------------------------------------------------
    // Literal escaping
    // -----------------------------------------------------------------

    [Fact]
    public void EscapeLiteral_returns_a_quoted_literal()
    {
        ODataQueryBuilder.EscapeLiteral("Boise").Should().Be("'Boise'");
    }

    /// <summary>
    /// OData escapes a single quote by doubling it. Getting this wrong
    /// terminates the literal early and produces a silently empty result set.
    /// </summary>
    [Fact]
    public void EscapeLiteral_doubles_single_quotes()
    {
        ODataQueryBuilder.EscapeLiteral("O'Brien").Should().Be("'O''Brien'");
    }

    [Fact]
    public void EscapeLiteral_doubles_every_quote_including_adjacent_ones()
    {
        ODataQueryBuilder.EscapeLiteral("a'b'c").Should().Be("'a''b''c'");
        ODataQueryBuilder.EscapeLiteral("''").Should().Be("''''''");
    }

    /// <summary>
    /// The classic injection shape: without doubling, this would close the
    /// literal and append a clause of the caller's choosing.
    /// </summary>
    [Fact]
    public void EscapeLiteral_neutralizes_an_injection_attempt()
    {
        ODataQueryBuilder.EscapeLiteral("x' or UserName eq 'admin")
            .Should().Be("'x'' or UserName eq ''admin'");
    }

    [Fact]
    public void EscapeLiteral_handles_an_empty_value()
    {
        ODataQueryBuilder.EscapeLiteral("").Should().Be("''");
    }

    // -----------------------------------------------------------------
    // Key segments
    // -----------------------------------------------------------------

    [Fact]
    public void KeySegment_wraps_the_key_in_quoted_parentheses()
    {
        ODataQueryBuilder.KeySegment("russellwhyte").Should().Be("('russellwhyte')");
    }

    /// <summary>
    /// A key segment sits in the path, where Build never runs, so it must
    /// percent-encode itself.
    /// </summary>
    [Fact]
    public void KeySegment_percent_encodes_characters_that_are_illegal_in_a_path()
    {
        ODataQueryBuilder.KeySegment("russell whyte").Should().Be("('russell%20whyte')");
    }

    [Fact]
    public void KeySegment_doubles_quotes_before_encoding_them()
    {
        ODataQueryBuilder.KeySegment("O'Brien").Should().Be("('O%27%27Brien')");
    }

    [Fact]
    public void KeySegment_neutralizes_a_path_traversal_attempt()
    {
        ODataQueryBuilder.KeySegment("../Airlines").Should().Be("('..%2FAirlines')");
    }

    /// <summary>
    /// Numbers are formatted invariantly, so a comma decimal separator can
    /// never leak into $top or $skip.
    /// </summary>
    [Fact]
    public void Numeric_options_are_formatted_without_separators()
    {
        new ODataQueryBuilder().Page(1000, 1000).Build()
            .Should().Be("?$top=1000&$skip=999000");
    }
}
