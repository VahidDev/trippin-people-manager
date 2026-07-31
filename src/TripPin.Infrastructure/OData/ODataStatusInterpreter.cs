using System.Net;
using TripPin.Application.Abstractions;

namespace TripPin.Infrastructure.OData;

/// <summary>
/// Normalises the service's HTTP behaviour into <see cref="ResultStatus"/>
/// values, so no status code leaks past Infrastructure.
/// </summary>
/// <remarks>
/// The mapping is unusual because the service is unusual:
/// <list type="bullet">
///   <item>
///     204 on a single-entity GET means not found. The service does not
///     return 404, so a naive success check passes and then parses an empty
///     body.
///   </item>
///   <item>412 means the ETag was stale: a real concurrency conflict.</item>
///   <item>
///     428 means no If-Match was sent. Unreachable by construction, since the
///     only write path always sends one, so treat it as a bug: log at error
///     and fail rather than retry.
///   </item>
///   <item>
///     400 means the request was malformed, which is a validation failure
///     rather than a transport one.
///   </item>
/// </list>
/// <para>
/// One caveat measured on this service: it answers most malformed queries with
/// 500 and <c>"code":"InternalServerError"</c> rather than 400, including a
/// negative <c>$top</c> and unparseable <c>$filter</c> syntax. Those are still
/// treated as transient, because a 500 is genuinely ambiguous and retrying is
/// the safe reading. Genuine 400s do occur and carry the standard error
/// envelope, so the mapping is worth having and correct per the specification.
/// </para>
/// </remarks>
public sealed class ODataStatusInterpreter
{
    /// <summary>Sent when no If-Match header accompanies an update.</summary>
    public const HttpStatusCode PreconditionRequired = (HttpStatusCode)428;

    public ResultStatus Interpret(HttpStatusCode statusCode, bool isSingleEntityRead)
    {
        // Checked before the switch: 204 is success on a write and "no such
        // entity" on a read, and only the caller knows which this was.
        if (isSingleEntityRead && statusCode == HttpStatusCode.NoContent)
        {
            return ResultStatus.NotFound;
        }

        return statusCode switch
        {
            HttpStatusCode.OK or HttpStatusCode.NoContent => ResultStatus.Success,
            HttpStatusCode.NotFound => ResultStatus.NotFound,
            HttpStatusCode.PreconditionFailed => ResultStatus.ConcurrencyConflict,

            // A 400 says the request itself was wrong: a malformed query or an
            // unacceptable payload. Reporting that as a transport failure would
            // tell the user the service is unreachable and send whoever
            // investigates looking at the network instead of the request.
            HttpStatusCode.BadRequest => ResultStatus.ValidationFailed,

            _ => ResultStatus.TransportFailure,
        };
    }

    /// <summary>True for status codes worth retrying: 5xx, 408 and 429.</summary>
    /// <remarks>
    /// Status codes only. Exceptions (network faults, timeouts, caller
    /// cancellation) are classified separately in the resilience pipeline,
    /// which needs the caller's token to tell a timeout apart from an
    /// abandoned request.
    /// <para>
    /// 400, 412 and 428 fall outside this set deliberately. All three are
    /// semantic outcomes: a malformed request stays malformed, retrying a 412
    /// resends the same stale ETag and fails identically, and a 428 means our
    /// own code omitted the header. Because the circuit breaker counts only
    /// what this predicate handles, none of them can push the breaker open
    /// against a healthy service.
    /// </para>
    /// </remarks>
    public static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        || (int)statusCode >= 500;
}
