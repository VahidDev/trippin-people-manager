using FluentAssertions;
using TripPin.Domain.Common;
using Xunit;

namespace TripPin.Domain.Tests.Common;

public sealed class ConcurrencyTokenTests
{
    [Fact]
    public void Any_is_the_wildcard_the_service_accepts()
    {
        ConcurrencyToken.Any.Value.Should().Be("*");
    }

    [Fact]
    public void From_preserves_an_etag_verbatim()
    {
        const string etag = "W/\"08DEEEEB83CE374D\"";

        ConcurrencyToken.From(etag).Value.Should().Be(etag);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void From_rejects_an_empty_token(string? value)
    {
        // A blank token would be sent as If-Match: and rejected with a 428,
        // which must surface as a bug rather than as a concurrency conflict.
        var act = () => ConcurrencyToken.From(value);

        act.Should().Throw<DomainException>().WithMessage("Concurrency token is required.");
    }

    /// <summary>
    /// The format is deliberately not validated. It is the service's to
    /// choose, and a change to it should not break reads.
    /// </summary>
    [Fact]
    public void From_does_not_police_the_etag_format()
    {
        ConcurrencyToken.From("anything-at-all").Value.Should().Be("anything-at-all");
    }

    [Fact]
    public void Equality_is_by_value()
    {
        ConcurrencyToken.From("W/\"1\"").Should().Be(ConcurrencyToken.From("W/\"1\""));
        ConcurrencyToken.From("W/\"1\"").Should().NotBe(ConcurrencyToken.From("W/\"2\""));
    }
}
