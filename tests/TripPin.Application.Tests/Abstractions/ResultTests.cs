using FluentAssertions;
using TripPin.Application.Abstractions;
using Xunit;

namespace TripPin.Application.Tests.Abstractions;

public sealed class ResultTests
{
    [Fact]
    public void Success_carries_the_value_and_no_errors()
    {
        var result = Result<int>.Success(42);

        result.IsSuccess.Should().BeTrue();
        result.Status.Should().Be(ResultStatus.Success);
        result.Value.Should().Be(42);
        result.Errors.Should().BeEmpty();
        result.Error.Should().BeNull();
    }

    [Theory]
    [InlineData(ResultStatus.NotFound)]
    [InlineData(ResultStatus.ValidationFailed)]
    [InlineData(ResultStatus.ConcurrencyConflict)]
    [InlineData(ResultStatus.TransportFailure)]
    [InlineData(ResultStatus.Cancelled)]
    public void Failure_is_not_success(ResultStatus status)
    {
        var result = Result<int>.Failure(status, "boom");

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(status);
        result.Value.Should().Be(default);
        result.Error.Should().Be("boom");
    }

    /// <summary>
    /// Validation reports every problem at once rather than making the user
    /// fix them one round trip at a time.
    /// </summary>
    [Fact]
    public void ValidationFailed_keeps_all_the_errors()
    {
        var result = Result<int>.ValidationFailed(["first", "second"]);

        result.Status.Should().Be(ResultStatus.ValidationFailed);
        result.Errors.Should().Equal("first", "second");
        result.Error.Should().Be("first");
    }

    [Fact]
    public void NotFound_and_Conflict_set_the_matching_status()
    {
        Result<int>.NotFound("gone").Status.Should().Be(ResultStatus.NotFound);
        Result<int>.Conflict("stale").Status.Should().Be(ResultStatus.ConcurrencyConflict);
    }

    /// <summary>
    /// The error list is copied in, so a caller mutating its own list
    /// afterwards cannot alter a result that has already been returned.
    /// </summary>
    [Fact]
    public void Failure_copies_the_error_list()
    {
        var errors = new List<string> { "first" };

        var result = Result<int>.ValidationFailed(errors);
        errors.Add("second");

        result.Errors.Should().Equal("first");
    }
}
