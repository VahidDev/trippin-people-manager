using TripPin.Application.Abstractions;

namespace TripPin.Console.Rendering;

/// <summary>
/// Turns a <see cref="ResultStatus"/> into a message for the user.
/// </summary>
/// <remarks>
/// The interesting case is <see cref="ResultStatus.ConcurrencyConflict"/>,
/// which should read as "this record changed while you were editing, reload?"
/// rather than as an error. It is the expected outcome of two people editing
/// at once, not a failure.
/// </remarks>
public sealed class ResultPresenter
{
    /// <summary>
    /// Shown for an unexpected exception. The detail goes to the log, never to
    /// the screen: a stack trace tells the user nothing and may disclose more
    /// than it should.
    /// </summary>
    public const string UnexpectedFailure =
        "Something went wrong. The details have been written to the log.";

    public string Describe<T>(Result<T> result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Status switch
        {
            ResultStatus.Success => "Done.",

            ResultStatus.NotFound =>
                result.Error ?? "That person could not be found.",

            // Validation messages are written for a person to read, so they
            // are passed through rather than replaced.
            ResultStatus.ValidationFailed =>
                $"That is not quite right:{Environment.NewLine}"
                + string.Join(
                    Environment.NewLine,
                    result.Errors.Select(error => $"  - {error}")),

            ResultStatus.ConcurrencyConflict =>
                "This person changed since you loaded them, so nothing was saved.",

            ResultStatus.TransportFailure =>
                "The TripPin service could not be reached. Please try again shortly.",

            ResultStatus.Cancelled => "That operation was cancelled.",

            _ => UnexpectedFailure,
        };
    }
}
