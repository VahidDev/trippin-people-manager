using System.Net;
using FluentAssertions;
using RichardSzalay.MockHttp;
using TripPin.Application.Abstractions;
using TripPin.Application.People.Models;
using TripPin.Domain.Common;
using TripPin.Domain.People;
using Xunit;

namespace TripPin.Infrastructure.Tests.QuirkHandling;

/// <summary>
/// Regression tests for the service behaviours that would otherwise be
/// rediscovered painfully. All run against a stubbed HttpMessageHandler, so
/// no network is involved and no shared sandbox is mutated.
/// </summary>
public sealed class ServiceQuirkTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static PersonUpdate Update(string token = "W/\"caller\"") => new()
    {
        UserName = UserName.From("russellwhyte"),
        Concurrency = ConcurrencyToken.From(token),
        FirstName = "Russ",
    };

    // -----------------------------------------------------------------
    // 204 instead of 404
    // -----------------------------------------------------------------

    /// <summary>
    /// The single most dangerous quirk: a naive <c>IsSuccessStatusCode</c>
    /// check passes on a 204 and then parses an empty body.
    /// </summary>
    [Fact]
    public async Task A_204_on_a_single_entity_read_is_reported_as_NotFound()
    {
        using var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Get, StubbedService.DetailUrl)
            .Respond(HttpStatusCode.NoContent);

        var result = await StubbedService.Repository(mockHttp)
            .GetByIdAsync(UserName.From("russellwhyte"), Token);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.NotFound);
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task A_204_never_throws_while_trying_to_parse_an_empty_body()
    {
        using var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Get, StubbedService.DetailUrl)
            .Respond(HttpStatusCode.NoContent);

        var act = async () => await StubbedService.Repository(mockHttp)
            .GetByIdAsync(UserName.From("russellwhyte"), Token);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task GetForUpdateAsync_maps_a_204_the_same_way()
    {
        using var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Get, StubbedService.DetailUrl)
            .Respond(HttpStatusCode.NoContent);

        var result = await StubbedService.Repository(mockHttp)
            .GetForUpdateAsync(UserName.From("russellwhyte"), Token);

        result.Status.Should().Be(ResultStatus.NotFound);
    }

    /// <summary>
    /// A 204 on a write means success, so the read-versus-write distinction has
    /// to be made by the caller rather than by the status alone.
    /// </summary>
    [Fact]
    public async Task A_204_on_a_write_is_success_not_NotFound()
    {
        using var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Patch, StubbedService.DetailUrl)
            .Respond(_ => StubbedService.Status(HttpStatusCode.NoContent, "after"));

        var result = await StubbedService.Repository(mockHttp).UpdateAsync(Update(), Token);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_genuine_404_is_also_NotFound()
    {
        using var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Get, StubbedService.DetailUrl)
            .Respond(HttpStatusCode.NotFound);

        var result = await StubbedService.Repository(mockHttp)
            .GetByIdAsync(UserName.From("russellwhyte"), Token);

        result.Status.Should().Be(ResultStatus.NotFound);
    }

    // -----------------------------------------------------------------
    // Concurrency
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_412_is_reported_as_a_concurrency_conflict()
    {
        using var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Patch, StubbedService.DetailUrl)
            .Respond(HttpStatusCode.PreconditionFailed);

        var result = await StubbedService.Repository(mockHttp).UpdateAsync(Update(), Token);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.ConcurrencyConflict);
    }

    /// <summary>
    /// The 412 response body is empty, so the status code is the only signal
    /// available and the message has to be supplied by us.
    /// </summary>
    [Fact]
    public async Task A_412_still_yields_a_usable_message_despite_an_empty_body()
    {
        using var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Patch, StubbedService.DetailUrl)
            .Respond(HttpStatusCode.PreconditionFailed);

        var result = await StubbedService.Repository(mockHttp).UpdateAsync(Update(), Token);

        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// 428 means our own code omitted If-Match. It is a defect, so it must
    /// fail immediately rather than be retried, and it must not be reported as
    /// a conflict, which would invite the user to pointlessly retry.
    /// </summary>
    [Fact]
    public async Task A_428_fails_fast_and_is_not_reported_as_a_conflict()
    {
        using var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Patch, StubbedService.DetailUrl)
            .Respond(_ => StubbedService.Status((HttpStatusCode)428));

        var result = await StubbedService.Repository(mockHttp).UpdateAsync(Update(), Token);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().NotBe(ResultStatus.ConcurrencyConflict);
        result.Error.Should().Contain("defect");
    }

    [Fact]
    public async Task A_428_is_attempted_exactly_once()
    {
        using var mockHttp = new MockHttpMessageHandler();
        var attempts = 0;

        mockHttp.When(HttpMethod.Patch, StubbedService.DetailUrl)
            .Respond(_ =>
            {
                attempts++;
                return StubbedService.Status((HttpStatusCode)428);
            });

        await StubbedService.Repository(mockHttp).UpdateAsync(Update(), Token);

        attempts.Should().Be(1);
    }

    /// <summary>
    /// The write path always sends If-Match, which is what makes a 428
    /// unreachable in the first place.
    /// </summary>
    [Fact]
    public async Task Every_update_carries_the_callers_If_Match_header()
    {
        using var mockHttp = new MockHttpMessageHandler();
        string? observed = null;

        mockHttp.When(HttpMethod.Patch, StubbedService.DetailUrl)
            .Respond(request =>
            {
                observed = request.Headers.TryGetValues("If-Match", out var values)
                    ? string.Join(",", values)
                    : null;

                return StubbedService.Status(HttpStatusCode.NoContent, "after");
            });

        await StubbedService.Repository(mockHttp)
            .UpdateAsync(Update("W/\"from-caller\""), Token);

        observed.Should().Be("W/\"from-caller\"");
    }

    [Fact]
    public async Task A_successful_update_returns_the_token_from_the_response_header()
    {
        using var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Patch, StubbedService.DetailUrl)
            .Respond(_ => StubbedService.Status(HttpStatusCode.NoContent, "08DEEF12D898537B"));

        var result = await StubbedService.Repository(mockHttp).UpdateAsync(Update(), Token);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Value.Should().Be("W/\"08DEEF12D898537B\"");
    }

    /// <summary>
    /// The live service does send an ETag on a PATCH, but a fallback keeps the
    /// contract intact if that ever changes.
    /// </summary>
    [Fact]
    public async Task An_update_without_a_response_etag_falls_back_to_a_projected_re_read()
    {
        using var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Patch, StubbedService.DetailUrl)
            .Respond(HttpStatusCode.NoContent);
        mockHttp.When(HttpMethod.Get, StubbedService.DetailUrl)
            .Respond(_ => StubbedService.Json(
                """{ "@odata.etag": "W/\"re-read\"", "UserName": "russellwhyte" }"""));

        var result = await StubbedService.Repository(mockHttp).UpdateAsync(Update(), Token);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Value.Should().Be("W/\"re-read\"");
    }

    // -----------------------------------------------------------------
    // Reads and paging
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_successful_read_maps_the_body_etag_onto_the_domain_token()
    {
        using var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Get, StubbedService.DetailUrl)
            .Respond(_ => StubbedService.Json(StubbedService.PersonJson));

        var result = await StubbedService.Repository(mockHttp)
            .GetByIdAsync(UserName.From("russellwhyte"), Token);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Concurrency.Value.Should().Be("W/\"08DEEEEB83CE374D\"");
        result.Value.Name.ToString().Should().Be("Russell Whyte");
        result.Value.Emails.Should().HaveCount(2);
        result.Value.Addresses.Should().ContainSingle()
            .Which.City.Should().Be("Boise");
    }

    [Fact]
    public async Task A_read_falls_back_to_the_header_etag_when_the_body_omits_it()
    {
        using var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Get, StubbedService.DetailUrl)
            .Respond(_ => StubbedService.Json(
                """
                {
                  "UserName": "russellwhyte",
                  "FirstName": "Russell",
                  "LastName": "Whyte",
                  "Gender": "Male"
                }
                """,
                etag: "from-header"));

        var result = await StubbedService.Repository(mockHttp)
            .GetByIdAsync(UserName.From("russellwhyte"), Token);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Concurrency.Value.Should().Be("W/\"from-header\"");
    }

    [Fact]
    public async Task ListAsync_reports_the_service_total_rather_than_the_page_length()
    {
        using var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Get, StubbedService.CollectionUrl)
            .Respond(_ => StubbedService.Json(StubbedService.CollectionJson));

        var result = await StubbedService.Repository(mockHttp).ListAsync(1, 8, Token);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Should().HaveCount(2);
        result.Value.TotalCount.Should().Be(20);
        result.Value.HasMore.Should().BeTrue();
    }

    /// <summary>
    /// The service pages at 8 rows whatever $top asks for and supplies a
    /// continuation link. A short page must not be mistaken for the end of the
    /// data, which is why the total comes from $count and not from the page.
    /// </summary>
    [Fact]
    public async Task A_page_carrying_a_nextLink_is_not_mistaken_for_the_last_page()
    {
        using var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Get, StubbedService.CollectionUrl)
            .Respond(_ => StubbedService.Json(StubbedService.TruncatedCollectionJson));

        var result = await StubbedService.Repository(mockHttp).ListAsync(1, 8, Token);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Should().ContainSingle();
        result.Value.TotalCount.Should().Be(20);
        result.Value.HasMore.Should().BeTrue();
    }

    /// <summary>
    /// The total is fetched separately because this service applies $top and
    /// $skip before computing @odata.count, making the count in a paged
    /// response merely the length of that page.
    /// </summary>
    [Fact]
    public async Task ListAsync_asks_for_the_total_without_paging_and_the_page_without_a_count()
    {
        using var mockHttp = new MockHttpMessageHandler();
        var queries = new List<string>();

        mockHttp.When(HttpMethod.Get, StubbedService.CollectionUrl)
            .Respond(request =>
            {
                queries.Add(request.RequestUri!.Query);
                return StubbedService.Json(StubbedService.CollectionJson);
            });

        await StubbedService.Repository(mockHttp).ListAsync(2, 8, Token);

        queries.Should().HaveCount(2);

        var totalQuery = queries.Should().ContainSingle(query => query.Contains("$count=true"))
            .Subject;
        totalQuery.Should().NotContain("$top").And.NotContain("$skip");

        var pageQuery = queries.Should().ContainSingle(query => query.Contains("$top=8")).Subject;
        pageQuery.Should().Contain("$skip=8").And.Contain("$select=");
    }

    /// <summary>
    /// Guards against the regression this quirk invites: taking the count from
    /// the page response would report a total of 2 here, not 20, and every
    /// query would look like a single page.
    /// </summary>
    [Fact]
    public async Task The_total_is_never_taken_from_the_paged_response()
    {
        using var mockHttp = new MockHttpMessageHandler();

        mockHttp.When(HttpMethod.Get, StubbedService.CollectionUrl)
            .Respond(request => StubbedService.Json(
                request.RequestUri!.Query.Contains("$count=true", StringComparison.Ordinal)
                    ? StubbedService.CollectionJson
                    : """{ "@odata.count": 2, "value": [] }"""));

        var result = await StubbedService.Repository(mockHttp).ListAsync(1, 8, Token);

        result.Value!.TotalCount.Should().Be(20);
    }

    [Fact]
    public async Task A_failed_total_request_fails_the_whole_read()
    {
        using var mockHttp = new MockHttpMessageHandler();

        mockHttp.When(HttpMethod.Get, StubbedService.CollectionUrl)
            .Respond(request =>
                request.RequestUri!.Query.Contains("$count=true", StringComparison.Ordinal)
                    ? StubbedService.Status(HttpStatusCode.InternalServerError)
                    : StubbedService.Json(StubbedService.CollectionJson));

        var result = await StubbedService.Repository(mockHttp).ListAsync(1, 8, Token);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.TransportFailure);
    }

    /// <summary>
    /// A structured filter must reach the wire as an escaped $filter, never as
    /// concatenated user text.
    /// </summary>
    [Fact]
    public async Task SearchAsync_sends_an_escaped_filter()
    {
        using var mockHttp = new MockHttpMessageHandler();
        string? query = null;

        mockHttp.When(HttpMethod.Get, StubbedService.CollectionUrl)
            .Respond(request =>
            {
                query = Uri.UnescapeDataString(request.RequestUri!.Query);
                return StubbedService.Json(StubbedService.CollectionJson);
            });

        await StubbedService.Repository(mockHttp).SearchAsync(
            new PersonFilter { NameContains = "O'Brien", Gender = Gender.Female },
            1,
            8,
            Token);

        query.Should().Contain("'O''Brien'");
        query.Should().Contain("PersonGender'Female'");
    }

    [Fact]
    public async Task An_unfiltered_list_sends_no_filter_at_all()
    {
        using var mockHttp = new MockHttpMessageHandler();
        string? query = null;

        mockHttp.When(HttpMethod.Get, StubbedService.CollectionUrl)
            .Respond(request =>
            {
                query = request.RequestUri!.Query;
                return StubbedService.Json(StubbedService.CollectionJson);
            });

        await StubbedService.Repository(mockHttp).ListAsync(1, 8, Token);

        query.Should().NotContain("$filter");
    }

    // -----------------------------------------------------------------
    // Transport faults
    // -----------------------------------------------------------------

    [Fact]
    public async Task A_500_becomes_a_transport_failure_rather_than_an_exception()
    {
        using var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Get, StubbedService.CollectionUrl)
            .Respond(HttpStatusCode.InternalServerError);

        var result = await StubbedService.Repository(mockHttp).ListAsync(1, 8, Token);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.TransportFailure);
    }

    [Fact]
    public async Task A_network_fault_becomes_a_transport_failure()
    {
        using var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Get, StubbedService.CollectionUrl)
            .Throw(new HttpRequestException("connection reset"));

        var result = await StubbedService.Repository(mockHttp).ListAsync(1, 8, Token);

        result.Status.Should().Be(ResultStatus.TransportFailure);
        result.Error.Should().Contain("connection reset");
    }

    /// <summary>
    /// Malformed JSON is the service's problem, not a crash: the console still
    /// gets a result it can render.
    /// </summary>
    [Fact]
    public async Task A_malformed_body_becomes_a_transport_failure()
    {
        using var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Get, StubbedService.CollectionUrl)
            .Respond(_ => StubbedService.Json("{ this is not json"));

        var result = await StubbedService.Repository(mockHttp).ListAsync(1, 8, Token);

        result.Status.Should().Be(ResultStatus.TransportFailure);
    }

    /// <summary>
    /// Caller-requested cancellation is not a transport failure and propagates
    /// as an exception, which is what callers expect from the framework.
    /// </summary>
    [Fact]
    public async Task Caller_cancellation_propagates_rather_than_becoming_a_result()
    {
        using var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Get, StubbedService.CollectionUrl)
            .Respond(_ => StubbedService.Json(StubbedService.CollectionJson));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await StubbedService.Repository(mockHttp).ListAsync(1, 8, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// The service accepts "not-an-email" with a 204, so such a value can
    /// genuinely be stored. Failing the read would make the person impossible
    /// to load and therefore impossible to repair through the UI.
    /// </summary>
    [Fact]
    public async Task A_stored_invalid_email_is_dropped_rather_than_failing_the_read()
    {
        using var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Get, StubbedService.DetailUrl)
            .Respond(_ => StubbedService.Json(
                """
                {
                  "@odata.etag": "W/\"1\"",
                  "UserName": "russellwhyte",
                  "FirstName": "Russell",
                  "LastName": "Whyte",
                  "Emails": ["not-an-email", "good@example.com"],
                  "Gender": "Male"
                }
                """));

        var result = await StubbedService.Repository(mockHttp)
            .GetByIdAsync(UserName.From("russellwhyte"), Token);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Emails.Should().ContainSingle()
            .Which.Value.Should().Be("good@example.com");
    }

    /// <summary>
    /// Gender is nullable on the wire. Unknown is the honest rendering, and
    /// unlike null it is a value the service can actually store.
    /// </summary>
    [Fact]
    public async Task A_missing_gender_reads_as_Unknown_rather_than_defaulting_to_Male()
    {
        using var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Get, StubbedService.DetailUrl)
            .Respond(_ => StubbedService.Json(
                """
                {
                  "@odata.etag": "W/\"1\"",
                  "UserName": "russellwhyte",
                  "FirstName": "Russell",
                  "LastName": "Whyte"
                }
                """));

        var result = await StubbedService.Repository(mockHttp)
            .GetByIdAsync(UserName.From("russellwhyte"), Token);

        result.Value!.Gender.Should().Be(Gender.Unknown);
    }
}
