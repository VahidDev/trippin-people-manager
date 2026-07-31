using System.Globalization;
using System.Text;
using TripPin.Application.People.Models;
using TripPin.Domain.People;

namespace TripPin.Console.Rendering;

/// <summary>
/// Formats people for display. Presentation only, no domain logic.
/// </summary>
public sealed class PersonRenderer
{
    public string RenderTable(PagedResult<PersonSummary> page)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (page.Items.Count == 0)
        {
            return "  (no people matched)";
        }

        var builder = new StringBuilder();

        builder.AppendLine("  USER NAME             NAME                       GENDER");
        builder.AppendLine("  --------------------- -------------------------- -------");

        foreach (var person in page.Items)
        {
            builder.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {Fit(person.UserName.Value, 21)} {Fit(person.Name.ToString(), 26)} {person.Gender}"));
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Renders the position within the result set.
    /// </summary>
    /// <remarks>
    /// The total is trustworthy because Infrastructure fetches it with a
    /// dedicated request. This service computes @odata.count after applying
    /// $top, so a count taken from the page itself would make every result set
    /// read as a single page.
    /// </remarks>
    public string RenderPageFooter<T>(PagedResult<T> page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var totalPages = page.PageSize <= 0
            ? 1
            : Math.Max(1, (page.TotalCount + page.PageSize - 1) / page.PageSize);

        var noun = page.TotalCount == 1 ? "person" : "people";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"  Page {page.Page} of {totalPages}  ({page.TotalCount} {noun})");
    }

    public string RenderDetail(Person person)
    {
        ArgumentNullException.ThrowIfNull(person);

        var builder = new StringBuilder();

        builder.AppendLine($"  User name : {person.UserName.Value}");
        builder.AppendLine($"  Name      : {person.Name}");
        builder.AppendLine($"  Gender    : {person.Gender}");
        builder.AppendLine($"  Emails    : {(person.Emails.Count == 0 ? "(none)" : person.Emails[0].Value)}");

        for (var index = 1; index < person.Emails.Count; index++)
        {
            builder.AppendLine($"              {person.Emails[index].Value}");
        }

        // Labelled read-only, and no edit affordance is offered anywhere: a
        // partial address write is accepted with a 204 and then corrupts the
        // record irrecoverably. See docs/adr/ADR-004.
        builder.AppendLine($"  Addresses : {(person.Addresses.Count == 0 ? "(none)" : person.Addresses[0].ToString())}");

        for (var index = 1; index < person.Addresses.Count; index++)
        {
            builder.AppendLine($"              {person.Addresses[index]}");
        }

        builder.AppendLine("              (read-only)");
        builder.Append($"  Version   : {person.Concurrency.Value}");

        return builder.ToString();
    }

    private static string Fit(string value, int width) =>
        value.Length <= width
            ? value.PadRight(width)
            : string.Concat(value.AsSpan(0, width - 1), "…");
}
