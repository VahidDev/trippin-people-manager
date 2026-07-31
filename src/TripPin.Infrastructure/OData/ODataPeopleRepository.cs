using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TripPin.Application.Abstractions;
using TripPin.Application.People.Models;
using TripPin.Application.People.Ports;
using TripPin.Domain.Common;
using TripPin.Domain.People;
using TripPin.Infrastructure.Configuration;
using TripPin.Infrastructure.OData.Dtos;

namespace TripPin.Infrastructure.OData;

/// <summary>
/// Typed <see cref="HttpClient"/> implementation of <see cref="IPeopleRepository"/>.
/// </summary>
/// <remarks>
/// Stateless by design, and therefore safe under concurrent use: the only
/// shared state in the request path is the session provider, which is
/// immutable once resolved. Chosen over Microsoft.OData.Client because
/// DataServiceContext is a stateful, non-thread-safe unit of work and does
/// not integrate with IHttpClientFactory. See docs/adr/ADR-001.
/// <para>
/// Knows nothing about caching. The decorator that adds it is composed at the
/// DI root, which is why <see cref="GetForUpdateAsync"/> here is identical to
/// <see cref="GetByIdAsync"/> apart from logging.
/// </para>
/// </remarks>
public sealed class ODataPeopleRepository(
    HttpClient httpClient,
    ODataFilterTranslator filterTranslator,
    PersonMapper mapper,
    ODataStatusInterpreter statusInterpreter,
    IOptions<TripPinOptions> options,
    ILogger<ODataPeopleRepository> logger) : IPeopleRepository
{
    public const string HttpClientName = "TripPin.OData";

    private const string PeopleSet = "People";

    private static readonly string[] SummaryProperties =
        ["UserName", "FirstName", "LastName", "Gender"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient = httpClient;
    private readonly ODataFilterTranslator _filterTranslator = filterTranslator;
    private readonly PersonMapper _mapper = mapper;
    private readonly ODataStatusInterpreter _statusInterpreter = statusInterpreter;
    private readonly TripPinOptions _options = options.Value;
    private readonly ILogger<ODataPeopleRepository> _logger = logger;

    public Task<Result<PagedResult<PersonSummary>>> ListAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken) =>
        QueryAsync(PersonFilter.Empty, page, pageSize, cancellationToken);

    public Task<Result<PagedResult<PersonSummary>>> SearchAsync(
        PersonFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        return QueryAsync(filter, page, pageSize, cancellationToken);
    }

    public Task<Result<Person>> GetByIdAsync(
        UserName userName,
        CancellationToken cancellationToken) =>
        ReadPersonAsync(userName, forUpdate: false, cancellationToken);

    public Task<Result<Person>> GetForUpdateAsync(
        UserName userName,
        CancellationToken cancellationToken) =>
        ReadPersonAsync(userName, forUpdate: true, cancellationToken);

    public async Task<Result<ConcurrencyToken>> UpdateAsync(
        PersonUpdate update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);

        var body = _mapper.ToSparsePatchBody(update);

        if (body.Count == 0)
        {
            return Result<ConcurrencyToken>.Failure(
                ResultStatus.ValidationFailed,
                "The update contained no changes.");
        }

        var requestUri = PeopleSet + ODataQueryBuilder.KeySegment(update.UserName.Value);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Patch, requestUri)
            {
                Content = JsonContent.Create(body, options: JsonOptions),
            };

            // TryAddWithoutValidation rather than Headers.IfMatch, which
            // rejects both the "*" wildcard and some weak-ETag spellings.
            request.Headers.TryAddWithoutValidation("If-Match", update.Concurrency.Value);

            // Guarded by hand: string.Join allocates, and this is the one
            // logging call in the class whose arguments are not free.
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "PATCH {RequestUri} with fields {Fields} and If-Match {ConcurrencyToken}.",
                    requestUri,
                    string.Join(",", body.Keys),
                    update.Concurrency.Value);
            }

            using var response = await _httpClient
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == ODataStatusInterpreter.PreconditionRequired)
            {
                // Unreachable by construction: the only write path always
                // sends If-Match. Logged as an error and failed immediately
                // rather than retried, because a replay cannot fix a bug here.
                _logger.LogError(
                    "The service rejected an update to {UserName} with 428 Precondition Required, "
                    + "meaning no If-Match header arrived. This is a defect in the write path, "
                    + "not a transient fault, and was not retried.",
                    update.UserName.Value);

                return Result<ConcurrencyToken>.Failure(
                    ResultStatus.TransportFailure,
                    "The update was sent without a concurrency token. This is a defect.");
            }

            var status = _statusInterpreter.Interpret(response.StatusCode, isSingleEntityRead: false);

            if (status == ResultStatus.ConcurrencyConflict)
            {
                _logger.LogInformation(
                    "Update to {UserName} conflicted: the record changed since it was read.",
                    update.UserName.Value);

                return Result<ConcurrencyToken>.Conflict(
                    "This person changed since you loaded them. Reload and try again.");
            }

            if (status != ResultStatus.Success)
            {
                return await FailureAsync<ConcurrencyToken>(
                    status, response, requestUri, cancellationToken).ConfigureAwait(false);
            }

            var token = await ReadUpdatedTokenAsync(update.UserName, response, cancellationToken)
                .ConfigureAwait(false);

            if (token is null)
            {
                return Result<ConcurrencyToken>.Failure(
                    ResultStatus.TransportFailure,
                    "The update succeeded but no new concurrency token could be obtained.");
            }

            _logger.LogInformation("Updated {UserName}.", update.UserName.Value);

            return Result<ConcurrencyToken>.Success(token);
        }
        catch (Exception exception) when (IsRecoverable(exception, cancellationToken))
        {
            return Recovered<ConcurrencyToken>(exception, requestUri);
        }
    }

    /// <summary>
    /// Reads one page, plus the total, as two requests.
    /// </summary>
    /// <remarks>
    /// The two requests are not an oversight. This service applies $top and
    /// $skip <em>before</em> computing @odata.count, which the specification
    /// says it must not: <c>$count=true</c> alone reports 20, while
    /// <c>$count=true&amp;$top=8</c> reports 8. The count in a paged response
    /// is therefore just the length of the page and useless as a total, so the
    /// total is fetched by a separate request that omits $top and $skip.
    /// <para>
    /// Reading the count from the page would silently make "page 1 of 1" the
    /// answer for every query, hiding most of the data with no visible error.
    /// The cost is one extra round trip per page, which the caching decorator
    /// absorbs for repeat views.
    /// </para>
    /// </remarks>
    private async Task<Result<PagedResult<PersonSummary>>> QueryAsync(
        PersonFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        // The filter only ever exists as a string from here inward.
        var expression = _filterTranslator.Translate(filter);

        var totalUri = PeopleSet + new ODataQueryBuilder()
            .Select("UserName")
            .Filter(expression)
            .IncludeCount()
            .Build();

        var pageUri = PeopleSet + new ODataQueryBuilder()
            .Select(SummaryProperties)
            .Filter(expression)
            .Page(page, pageSize)
            .Build();

        try
        {
            var total = await ReadPageAsync(totalUri, cancellationToken).ConfigureAwait(false);

            if (!total.IsSuccess)
            {
                return Result<PagedResult<PersonSummary>>.Failure(total.Status, total.Errors);
            }

            var body = await ReadPageAsync(pageUri, cancellationToken).ConfigureAwait(false);

            if (!body.IsSuccess)
            {
                return Result<PagedResult<PersonSummary>>.Failure(body.Status, body.Errors);
            }

            var items = body.Value.Value.Select(_mapper.ToSummary).ToArray();
            var totalCount = total.Value.Count ?? items.Length;

            if (body.Value.NextLink is not null)
            {
                // Expected: the service pages at 8 rows whatever $top asks
                // for. Recorded so a short page is never mistaken for the end
                // of the data.
                _logger.LogDebug(
                    "The service truncated the page and supplied a continuation link.");
            }

            _logger.LogDebug("Read {ItemCount} of {TotalCount} people.", items.Length, totalCount);

            return Result<PagedResult<PersonSummary>>.Success(
                new PagedResult<PersonSummary>(items, totalCount, page, pageSize));
        }
        catch (Exception exception) when (IsRecoverable(exception, cancellationToken))
        {
            return Recovered<PagedResult<PersonSummary>>(exception, pageUri);
        }
    }

    private async Task<Result<ODataCollectionResponse<PersonDto>>> ReadPageAsync(
        string requestUri,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("GET {RequestUri}.", requestUri);

        using var response = await _httpClient
            .GetAsync(requestUri, cancellationToken)
            .ConfigureAwait(false);

        var status = _statusInterpreter.Interpret(response.StatusCode, isSingleEntityRead: false);

        if (status != ResultStatus.Success)
        {
            return await FailureAsync<ODataCollectionResponse<PersonDto>>(
                status, response, requestUri, cancellationToken).ConfigureAwait(false);
        }

        var payload = await response.Content
            .ReadFromJsonAsync<ODataCollectionResponse<PersonDto>>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        return payload is null
            ? Result<ODataCollectionResponse<PersonDto>>.Failure(
                ResultStatus.TransportFailure,
                "The service returned an empty collection response.")
            : Result<ODataCollectionResponse<PersonDto>>.Success(payload);
    }

    private async Task<Result<Person>> ReadPersonAsync(
        UserName userName,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userName);

        var requestUri = PeopleSet + ODataQueryBuilder.KeySegment(userName.Value);

        try
        {
            _logger.LogDebug(
                "GET {RequestUri} ({ReadPurpose}).",
                requestUri,
                forUpdate ? "fresh read for update" : "read for display");

            using var response = await _httpClient
                .GetAsync(requestUri, cancellationToken)
                .ConfigureAwait(false);

            var status = _statusInterpreter.Interpret(response.StatusCode, isSingleEntityRead: true);

            // The service answers 204 rather than 404 for a person that does
            // not exist, so this path is reached with an empty body and a
            // status a naive success check would accept.
            if (status == ResultStatus.NotFound)
            {
                _logger.LogDebug("No person named {UserName} exists.", userName.Value);

                return Result<Person>.NotFound($"No person named '{userName.Value}' exists.");
            }

            if (status != ResultStatus.Success)
            {
                return await FailureAsync<Person>(status, response, requestUri, cancellationToken)
                    .ConfigureAwait(false);
            }

            var dto = await response.Content
                .ReadFromJsonAsync<PersonDto>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (dto is null)
            {
                return Result<Person>.Failure(
                    ResultStatus.TransportFailure,
                    "The service returned an empty entity response.");
            }

            dto.ETag ??= response.Headers.ETag?.ToString();

            return Result<Person>.Success(_mapper.ToDomain(dto));
        }
        catch (Exception exception) when (IsRecoverable(exception, cancellationToken))
        {
            return Recovered<Person>(exception, requestUri);
        }
    }

    /// <summary>
    /// Takes the new token from the response header, falling back to a cheap
    /// projected re-read if the service ever stops sending one.
    /// </summary>
    private async Task<ConcurrencyToken?> ReadUpdatedTokenAsync(
        UserName userName,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var headerTag = response.Headers.ETag?.ToString();

        if (!string.IsNullOrWhiteSpace(headerTag))
        {
            return ConcurrencyToken.From(headerTag);
        }

        var requestUri = PeopleSet
            + ODataQueryBuilder.KeySegment(userName.Value)
            + new ODataQueryBuilder().Select("UserName").Build();

        _logger.LogDebug("The update response carried no ETag; re-reading {RequestUri}.", requestUri);

        using var reread = await _httpClient
            .GetAsync(requestUri, cancellationToken)
            .ConfigureAwait(false);

        if (!reread.IsSuccessStatusCode)
        {
            return null;
        }

        var dto = await reread.Content
            .ReadFromJsonAsync<PersonDto>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        var tag = dto?.ETag ?? reread.Headers.ETag?.ToString();

        return string.IsNullOrWhiteSpace(tag) ? null : ConcurrencyToken.From(tag);
    }

    /// <summary>
    /// Faults that belong in a result rather than propagating. Cancellation
    /// requested by the caller is excluded on purpose: that propagates as
    /// <see cref="OperationCanceledException"/>, which is what callers expect.
    /// </summary>
    private static bool IsRecoverable(Exception exception, CancellationToken cancellationToken) =>
        exception switch
        {
            OperationCanceledException => !cancellationToken.IsCancellationRequested,
            HttpRequestException or JsonException or DomainException or InvalidOperationException =>
                true,
            _ => false,
        };

    private Result<T> Recovered<T>(Exception exception, string requestUri)
    {
        _logger.LogError(
            exception,
            "The OData request to {RequestUri} failed.",
            requestUri);

        return Result<T>.Failure(ResultStatus.TransportFailure, exception.Message);
    }

    /// <summary>
    /// Builds a failure, preferring the service's own error message.
    /// </summary>
    /// <remarks>
    /// A 400 carries an OData error envelope naming what was wrong with the
    /// request, which is far more actionable than the status code alone. Only
    /// <c>message</c> is surfaced: this service also returns an
    /// <c>innererror</c> containing a server stack trace, which belongs
    /// nowhere near a user.
    /// </remarks>
    private async Task<Result<T>> FailureAsync<T>(
        ResultStatus status,
        HttpResponseMessage response,
        string requestUri,
        CancellationToken cancellationToken)
    {
        var detail = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);

        _logger.LogError(
            "The OData request to {RequestUri} returned {StatusCode}. Service message: {ServiceMessage}",
            requestUri,
            (int)response.StatusCode,
            detail ?? "(none)");

        return Result<T>.Failure(
            status,
            detail ?? $"The service returned {(int)response.StatusCode} for '{requestUri}'.");
    }

    private static async Task<string?> ReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = await response.Content
                .ReadFromJsonAsync<ODataErrorResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            var message = payload?.Error?.Message;

            return string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            // An error body that is not the standard envelope is no reason to
            // lose the status code we already have.
            return null;
        }
    }
}
