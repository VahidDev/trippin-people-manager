using System.Net;
using FluentAssertions;
using RichardSzalay.MockHttp;
using TripPin.Application.Abstractions;
using TripPin.Application.People.Models;
using TripPin.Domain.Common;
using TripPin.Domain.People;
using TripPin.Infrastructure.OData;
using Xunit;

namespace TripPin.Infrastructure.Tests.QuirkHandling;

/// <summary>
/// How the OData error envelope is read, and where a 400 lands.
/// </summary>
/// <remarks>
/// A 400 says the request was wrong, not that the service was unreachable.
/// Reporting it as a transport failure would tell the user to try again later
/// and send whoever investigates looking at the network rather than the query.
/// </remarks>
public sealed class ErrorPayloadTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The real envelope shape, taken from the live service.</summary>
    private const string BadRequestJson = """
        {
          "error": {
            "code": "BadRequest",
            "message": "The query option '$filter' can only be applied to collection resouces"
          }
        }
        """;

    /// <summary>
    /// This service also returns an innererror carrying a full server stack
    /// trace, which must never reach a caller.
    /// </summary>
    private const string BadRequestWithInnerErrorJson = """
        {
          "error": {
            "code": "BadRequest",
            "message": "Invalid value '-1' for $top query option found.",
            "innererror": {
              "message": "Invalid value '-1' for $top query option found.",
              "type": "Microsoft.OData.Core.ODataException",
              "stacktrace": "   at Microsoft.OData.Core.UriParser.Parsers.FunctionCallParser..."
            }
          }
        }
        """;

    private static HttpResponseMessage ErrorResponse(HttpStatusCode statusCode, string body) =>
        new(statusCode)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };

    private static PersonUpdate Update() => new()
    {
        UserName = UserName.From("russellwhyte"),
        Concurrency = ConcurrencyToken.From("W/\"caller\""),
        FirstName = "Russ",
    };

    // -----------------------------------------------------------------
    // Status mapping
    // -----------------------------------------------------------------

    [Fact]
    public void The_interpreter_maps_400_to_a_validation_failure()
    {
        new ODataStatusInterpreter()
            .Interpret(HttpStatusCode.BadRequest, isSingleEntityRead: false)
            .Should().Be(ResultStatus.ValidationFailed);
    }

    [Fact]
    public void A_400_is_a_validation_failure_on_a_single_entity_read_too()
    {
        new ODataStatusInterpreter()
            .Interpret(HttpStatusCode.BadRequest, isSingleEntityRead: true)
            .Should().Be(ResultStatus.ValidationFailed);
    }

    /// <summary>
    /// A malformed request is not worth repeating, so it must stay outside the
    /// retry predicate and the breaker's failure count.
    /// </summary>
    [Fact]
    public void A_400_is_not_transient()
    {
        ODataStatusInterpreter.IsTransient(HttpStatusCode.BadRequest).Should().BeFalse();
    }

    // -----------------------------------------------------------------
    // Reaching the caller
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_400_on_a_list_read_becomes_a_validation_failure()
    {
        using var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Get, StubbedService.CollectionUrl)
            .Respond(_ => ErrorResponse(HttpStatusCode.BadRequest, BadRequestJson));

        var result = await StubbedService.Repository(mockHttp).ListAsync(1, 8, Token);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.ValidationFailed);
        result.Status.Should().NotBe(ResultStatus.TransportFailure);
    }

    /// <summary>
    /// The service's own message names what was wrong, which is far more
    /// actionable than the status code by itself.
    /// </summary>
    [Fact]
    public async Task The_service_message_is_surfaced_rather_than_a_generic_one()
    {
        using var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Get, StubbedService.CollectionUrl)
            .Respond(_ => ErrorResponse(HttpStatusCode.BadRequest, BadRequestJson));

        var result = await StubbedService.Repository(mockHttp).ListAsync(1, 8, Token);

        result.Error.Should().Be(
            "The query option '$filter' can only be applied to collection resouces");
    }

    /// <summary>
    /// The stack trace in innererror belongs in a log at most, never in a
    /// message handed to a caller.
    /// </summary>
    [Fact]
    public async Task The_inner_error_stack_trace_is_never_surfaced()
    {
        using var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Get, StubbedService.CollectionUrl)
            .Respond(_ => ErrorResponse(HttpStatusCode.BadRequest, BadRequestWithInnerErrorJson));

        var result = await StubbedService.Repository(mockHttp).ListAsync(1, 8, Token);

        result.Error.Should().Be("Invalid value '-1' for $top query option found.");
        result.Error.Should().NotContain("stacktrace");
        result.Error.Should().NotContain("Microsoft.OData.Core");
    }

    [Fact]
    public async Task A_400_on_a_detail_read_becomes_a_validation_failure()
    {
        using var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Get, StubbedService.DetailUrl)
            .Respond(_ => ErrorResponse(HttpStatusCode.BadRequest, BadRequestJson));

        var result = await StubbedService.Repository(mockHttp)
            .GetByIdAsync(UserName.From("russellwhyte"), Token);

        result.Status.Should().Be(ResultStatus.ValidationFailed);
        result.Error.Should().Contain("can only be applied to collection");
    }

    [Fact]
    public async Task A_400_on_an_update_becomes_a_validation_failure()
    {
        using var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Patch, StubbedService.DetailUrl)
            .Respond(_ => ErrorResponse(HttpStatusCode.BadRequest, BadRequestJson));

        var result = await StubbedService.Repository(mockHttp).UpdateAsync(Update(), Token);

        result.Status.Should().Be(ResultStatus.ValidationFailed);
        result.Status.Should().NotBe(ResultStatus.ConcurrencyConflict);
    }

    // -----------------------------------------------------------------
    // Robustness of the parsing
    // -----------------------------------------------------------------

    /// <summary>
    /// A 500 carrying the same envelope stays transient. This service labels
    /// most malformed queries as 500, and a 500 is genuinely ambiguous, so
    /// retrying remains the safe reading.
    /// </summary>
    [Fact]
    public async Task A_500_with_an_error_payload_is_still_a_transport_failure()
    {
        using var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Get, StubbedService.CollectionUrl)
            .Respond(_ => ErrorResponse(
                HttpStatusCode.InternalServerError,
                """{ "error": { "code": "InternalServerError", "message": "Value cannot be null." } }"""));

        var result = await StubbedService.Repository(mockHttp).ListAsync(1, 8, Token);

        result.Status.Should().Be(ResultStatus.TransportFailure);
        result.Error.Should().Be("Value cannot be null.");
    }

    /// <summary>
    /// An error body that is not the standard envelope is no reason to lose
    /// the status code we already have.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{ "error": {} }""")]
    [InlineData("""{ "error": { "code": "BadRequest" } }""")]
    [InlineData("""{ "error": { "code": "BadRequest", "message": "   " } }""")]
    [InlineData("<html><body>502 Bad Gateway</body></html>")]
    public async Task An_unparseable_error_body_still_yields_a_usable_message(string body)
    {
        using var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Get, StubbedService.CollectionUrl)
            .Respond(_ => ErrorResponse(HttpStatusCode.BadRequest, body));

        var result = await StubbedService.Repository(mockHttp).ListAsync(1, 8, Token);

        result.Status.Should().Be(ResultStatus.ValidationFailed);
        result.Error.Should().NotBeNullOrWhiteSpace();
        result.Error.Should().Contain("400");
    }

    [Fact]
    public async Task A_message_with_surrounding_whitespace_is_trimmed()
    {
        using var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Get, StubbedService.CollectionUrl)
            .Respond(_ => ErrorResponse(
                HttpStatusCode.BadRequest,
                """{ "error": { "code": "BadRequest", "message": "  spaced out  " } }"""));

        var result = await StubbedService.Repository(mockHttp).ListAsync(1, 8, Token);

        result.Error.Should().Be("spaced out");
    }

    /// <summary>
    /// A 412 keeps its own conflict wording; the service sends an empty body
    /// for it in any case.
    /// </summary>
    [Fact]
    public async Task A_412_keeps_its_conflict_message_rather_than_an_error_payload()
    {
        using var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Patch, StubbedService.DetailUrl)
            .Respond(_ => ErrorResponse(
                HttpStatusCode.PreconditionFailed,
                """{ "error": { "code": "PreconditionFailed", "message": "etag mismatch" } }"""));

        var result = await StubbedService.Repository(mockHttp).UpdateAsync(Update(), Token);

        result.Status.Should().Be(ResultStatus.ConcurrencyConflict);
        result.Error.Should().Contain("changed since you loaded");
    }
}
