using SystemConsole = System.Console;

namespace TripPin.Console.Input;

/// <summary>
/// Cancellable console input. Pure I/O, no domain or validation logic.
/// </summary>
/// <remarks>
/// A blocking <c>Console.ReadLine</c> cannot be interrupted mid-read, so these
/// methods check for cancellation on entry rather than pretending otherwise.
/// That is sufficient in practice: the tokens exist to abort network calls, and
/// a pending prompt is not one. Ctrl+C unblocks the read by returning null,
/// which every caller treats as "leave this screen", and the same is true at
/// end of input when stdin is redirected.
/// </remarks>
public sealed class ConsoleReader
{
    public Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(SystemConsole.ReadLine());
    }

    public Task<string?> PromptAsync(string label, CancellationToken cancellationToken)
    {
        SystemConsole.Write(label);

        return ReadLineAsync(cancellationToken);
    }

    /// <summary>Prompts with an existing value, where empty input means "leave unchanged".</summary>
    public Task<string?> PromptOptionalAsync(
        string label,
        string currentValue,
        CancellationToken cancellationToken) =>
        PromptAsync($"{label} [{currentValue}]: ", cancellationToken);

    /// <summary>Requires an explicit "y"; anything else, including null, is no.</summary>
    public async Task<bool> ConfirmAsync(string question, CancellationToken cancellationToken)
    {
        var answer = await PromptAsync($"{question} (y/N): ", cancellationToken)
            .ConfigureAwait(false);

        return string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase);
    }
}
