using System.Globalization;

namespace TripPin.Infrastructure.OData;

/// <summary>
/// Builds and escapes OData query strings.
/// </summary>
/// <remarks>
/// The single place raw OData syntax is constructed. Escaping matters more
/// than usual here: a malformed or unrecognised $filter does not fail, it
/// returns 200 with zero rows, so an escaping bug looks exactly like a
/// legitimate empty result.
/// <para>
/// <see cref="Build"/> emits options in a fixed canonical order regardless of
/// the order they were added. That keeps the golden-string tests stable and,
/// more usefully, makes the resulting query usable as a cache key.
/// </para>
/// </remarks>
public sealed class ODataQueryBuilder
{
    private string? _select;
    private string? _filter;
    private string? _orderBy;
    private int? _top;
    private int? _skip;
    private bool _count;

    public ODataQueryBuilder Select(params string[] properties)
    {
        ArgumentNullException.ThrowIfNull(properties);

        if (properties.Length > 0)
        {
            _select = string.Join(",", properties);
        }

        return this;
    }

    public ODataQueryBuilder Filter(string? filterExpression)
    {
        if (!string.IsNullOrWhiteSpace(filterExpression))
        {
            _filter = filterExpression;
        }

        return this;
    }

    public ODataQueryBuilder OrderBy(string property, bool descending = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(property);

        _orderBy = descending ? $"{property} desc" : property;
        return this;
    }

    public ODataQueryBuilder Page(int page, int pageSize)
    {
        _top = pageSize;
        _skip = Math.Max(0, (page - 1) * pageSize);
        return this;
    }

    public ODataQueryBuilder IncludeCount()
    {
        _count = true;
        return this;
    }

    /// <summary>
    /// Renders a value as a complete OData string literal, quotes included:
    /// <c>O'Brien</c> becomes <c>'O''Brien'</c>.
    /// </summary>
    /// <remarks>
    /// Returns the quoted form rather than just the inner text so a caller
    /// cannot forget the quotes. Percent-encoding is not applied here because
    /// <see cref="Build"/> encodes the whole option value.
    /// </remarks>
    public static string EscapeLiteral(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
    }

    /// <summary>
    /// Renders an entity key as a URL path segment, parentheses included:
    /// <c>russellwhyte</c> becomes <c>('russellwhyte')</c>.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="EscapeLiteral"/> this percent-encodes, because a key
    /// segment sits in the path where <see cref="Build"/> never reaches it.
    /// Quote doubling happens first, so the doubled quotes survive encoding.
    /// </remarks>
    public static string KeySegment(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var doubled = value.Replace("'", "''", StringComparison.Ordinal);
        return $"('{Uri.EscapeDataString(doubled)}')";
    }

    public string Build()
    {
        var parts = new List<string>(6);

        if (_select is not null)
        {
            parts.Add($"$select={Uri.EscapeDataString(_select)}");
        }

        if (_filter is not null)
        {
            parts.Add($"$filter={Uri.EscapeDataString(_filter)}");
        }

        if (_orderBy is not null)
        {
            parts.Add($"$orderby={Uri.EscapeDataString(_orderBy)}");
        }

        if (_top is not null)
        {
            parts.Add($"$top={_top.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        if (_skip is not null)
        {
            parts.Add($"$skip={_skip.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        if (_count)
        {
            parts.Add("$count=true");
        }

        return parts.Count == 0 ? string.Empty : $"?{string.Join("&", parts)}";
    }
}
