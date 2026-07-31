using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RichardSzalay.MockHttp;
using TripPin.Infrastructure.Configuration;
using TripPin.Infrastructure.Session;
using Xunit;

namespace TripPin.Infrastructure.Tests.QuirkHandling;

/// <summary>
/// The session base address is the one piece of long-lived mutable state in
/// the application, and getting it wrong loses writes silently: each fresh hit
/// on the bare service root mints a new session with pristine data.
/// </summary>
/// <remarks>
/// Redirect <em>following</em> is a primary-handler feature that a stubbed
/// handler cannot emulate, so it is verified against the live service in the
/// Integration folder. What is covered here is everything else: that one
/// resolution is shared, that failures are not cached, and that the handler
/// rewrites URIs correctly.
/// </remarks>
public sealed class SessionUriProviderTests
{
    private const string Configured = "https://services.odata.test/v4/TripPinServiceRW/";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static SessionUriProvider Provider(MockHttpMessageHandler mockHttp)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(SessionUriProvider.ResolverClientName)
            .Returns(_ => mockHttp.ToHttpClient());

        return new SessionUriProvider(
            factory,
            Options.Create(new TripPinOptions { BaseAddress = new Uri(Configured) }),
            NullLogger<SessionUriProvider>.Instance);
    }

    [Fact]
    public async Task The_resolved_address_always_ends_with_a_slash()
    {
        using var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(HttpMethod.Get, Configured).Respond(HttpStatusCode.OK);

        var resolved = await Provider(mockHttp).GetBaseAddressAsync(Token);

        resolved.AbsoluteUri.Should().EndWith("/");
    }

    /// <summary>
    /// The core requirement. Every extra resolution is a new session with its
    /// own pristine copy of the data, so N concurrent callers must cause one
    /// request, not N.
    /// </summary>
    [Fact]
    public async Task Concurrent_callers_share_a_single_resolution()
    {
        using var mockHttp = new MockHttpMessageHandler();
        var resolutions = 0;

        mockHttp.When(HttpMethod.Get, Configured).Respond(_ =>
        {
            Interlocked.Increment(ref resolutions);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var provider = Provider(mockHttp);

        var callers = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => provider.GetBaseAddressAsync(Token), Token));

        var resolved = await Task.WhenAll(callers);

        resolutions.Should().Be(1);
        resolved.Should().AllBeEquivalentTo(resolved[0]);
    }

    [Fact]
    public async Task A_repeat_call_reuses_the_cached_address()
    {
        using var mockHttp = new MockHttpMessageHandler();
        var resolutions = 0;

        mockHttp.When(HttpMethod.Get, Configured).Respond(_ =>
        {
            resolutions++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var provider = Provider(mockHttp);

        await provider.GetBaseAddressAsync(Token);
        await provider.GetBaseAddressAsync(Token);

        resolutions.Should().Be(1);
    }

    [Fact]
    public async Task Invalidate_forces_the_next_call_to_resolve_again()
    {
        using var mockHttp = new MockHttpMessageHandler();
        var resolutions = 0;

        mockHttp.When(HttpMethod.Get, Configured).Respond(_ =>
        {
            resolutions++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var provider = Provider(mockHttp);

        await provider.GetBaseAddressAsync(Token);
        provider.Invalidate();
        await provider.GetBaseAddressAsync(Token);

        resolutions.Should().Be(2);
    }

    /// <summary>
    /// A blip at startup must not disable the application for its whole
    /// lifetime, which is what caching the faulted task would do.
    /// </summary>
    [Fact]
    public async Task A_failed_resolution_is_not_cached_permanently()
    {
        using var mockHttp = new MockHttpMessageHandler();
        var attempts = 0;

        mockHttp.When(HttpMethod.Get, Configured).Respond(_ =>
        {
            attempts++;
            return new HttpResponseMessage(
                attempts == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        });

        var provider = Provider(mockHttp);

        var first = async () => await provider.GetBaseAddressAsync(Token);
        await first.Should().ThrowAsync<HttpRequestException>();

        var resolved = await provider.GetBaseAddressAsync(Token);

        resolved.Should().NotBeNull();
        attempts.Should().Be(2);
    }
}

public sealed class SessionUriHandlerTests
{
    private static readonly Uri ConfiguredBase = new("https://services.odata.test/v4/TripPinServiceRW/");
    private static readonly Uri SessionBase = new("https://services.odata.test/v4/(S(abc123))/TripPinServiceRW/");

    [Fact]
    public void A_collection_request_is_rebased_onto_the_session()
    {
        var rewritten = SessionUriHandler.Rebase(
            new Uri(ConfiguredBase, "People"),
            ConfiguredBase,
            SessionBase);

        rewritten.AbsoluteUri.Should()
            .Be("https://services.odata.test/v4/(S(abc123))/TripPinServiceRW/People");
    }

    /// <summary>
    /// The quoted key segment must survive rebasing intact, or every
    /// single-person read addresses the wrong resource.
    /// </summary>
    [Fact]
    public void A_quoted_key_segment_survives_rebasing()
    {
        var rewritten = SessionUriHandler.Rebase(
            new Uri(ConfiguredBase, "People('russellwhyte')"),
            ConfiguredBase,
            SessionBase);

        rewritten.AbsoluteUri.Should().EndWith("/TripPinServiceRW/People('russellwhyte')");
        rewritten.AbsoluteUri.Should().Contain("(S(abc123))");
    }

    [Fact]
    public void A_query_string_survives_rebasing()
    {
        var rewritten = SessionUriHandler.Rebase(
            new Uri(ConfiguredBase, "People?$select=UserName&$top=8&$count=true"),
            ConfiguredBase,
            SessionBase);

        rewritten.Query.Should().Be("?$select=UserName&$top=8&$count=true");
        rewritten.AbsoluteUri.Should().Contain("(S(abc123))");
    }

    [Fact]
    public void A_relative_uri_is_resolved_against_the_session_base()
    {
        var rewritten = SessionUriHandler.Rebase(
            new Uri("People('x')", UriKind.Relative),
            ConfiguredBase,
            SessionBase);

        rewritten.AbsoluteUri.Should().Be($"{SessionBase.AbsoluteUri}People('x')");
    }

    /// <summary>
    /// A retry re-enters this handler, so rebasing must be idempotent rather
    /// than nesting one session inside another.
    /// </summary>
    [Fact]
    public void Rebasing_an_already_rebased_uri_changes_nothing()
    {
        var once = SessionUriHandler.Rebase(
            new Uri(ConfiguredBase, "People"), ConfiguredBase, SessionBase);

        var twice = SessionUriHandler.Rebase(once, ConfiguredBase, SessionBase);

        twice.Should().Be(once);
    }

    [Fact]
    public void A_uri_outside_the_configured_service_is_left_alone()
    {
        var unrelated = new Uri("https://example.test/somewhere/else");

        SessionUriHandler.Rebase(unrelated, ConfiguredBase, SessionBase).Should().Be(unrelated);
    }
}
