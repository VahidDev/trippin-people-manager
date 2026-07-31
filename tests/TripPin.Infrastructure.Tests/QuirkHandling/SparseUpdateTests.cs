using System.Net;
using System.Text.Json;
using FluentAssertions;
using RichardSzalay.MockHttp;
using TripPin.Application.People.Models;
using TripPin.Domain.Common;
using TripPin.Domain.People;
using Xunit;

namespace TripPin.Infrastructure.Tests.QuirkHandling;

/// <summary>
/// Guards the payload-shaping rules. The address case is the important one:
/// a PATCH omitting the required City returns 204 and then permanently
/// corrupts the record, breaking reads of the whole People collection, not
/// just that person.
/// </summary>
public sealed class SparseUpdateTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static PersonUpdate Update(
        string? firstName = null,
        string? lastName = null,
        IReadOnlyList<EmailAddress>? emails = null,
        Gender? gender = null) =>
        new()
        {
            UserName = UserName.From("russellwhyte"),
            Concurrency = ConcurrencyToken.From("W/\"caller\""),
            FirstName = firstName,
            LastName = lastName,
            Emails = emails,
            Gender = gender,
        };

    /// <summary>Sends the update and hands back the raw request body.</summary>
    private static async Task<string> CaptureBodyAsync(PersonUpdate update)
    {
        using var mockHttp = new MockHttpMessageHandler();
        var body = string.Empty;

        mockHttp.When(HttpMethod.Patch, StubbedService.DetailUrl)
            .Respond(async request =>
            {
                body = await request.Content!.ReadAsStringAsync(Token);
                return StubbedService.Status(HttpStatusCode.NoContent, "after");
            });

        var result = await StubbedService.Repository(mockHttp).UpdateAsync(update, Token);

        result.IsSuccess.Should().BeTrue();
        return body;
    }

    // -----------------------------------------------------------------
    // Sparseness
    // -----------------------------------------------------------------

    [Fact]
    public async Task Only_changed_fields_appear_in_the_payload()
    {
        var body = await CaptureBodyAsync(Update(firstName: "Russ"));

        using var document = JsonDocument.Parse(body);
        document.RootElement.EnumerateObject().Select(property => property.Name)
            .Should().Equal("FirstName");
    }

    [Fact]
    public async Task Several_changed_fields_all_appear()
    {
        var body = await CaptureBodyAsync(
            Update(firstName: "Russ", lastName: "White", gender: Gender.Female));

        using var document = JsonDocument.Parse(body);
        document.RootElement.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo("FirstName", "LastName", "Gender");
    }

    [Fact]
    public async Task An_untouched_field_is_absent_rather_than_null()
    {
        var body = await CaptureBodyAsync(Update(firstName: "Russ"));

        body.Should().NotContain("LastName");
        body.Should().NotContain("Emails");
        body.Should().NotContain("Gender");
        body.Should().NotContain("null");
    }

    /// <summary>
    /// The key is immutable: the service accepts a change to it, returns 204
    /// and silently ignores it, so sending it is pure noise.
    /// </summary>
    [Fact]
    public async Task The_key_is_never_part_of_the_payload()
    {
        var body = await CaptureBodyAsync(Update(firstName: "Russ"));

        body.Should().NotContain("UserName");
    }

    [Fact]
    public async Task The_server_managed_concurrency_property_is_never_written()
    {
        var body = await CaptureBodyAsync(Update(firstName: "Russ"));

        body.Should().NotContain("Concurrency");
    }

    // -----------------------------------------------------------------
    // AddressInfo
    // -----------------------------------------------------------------

    [Fact]
    public async Task An_update_payload_never_contains_AddressInfo()
    {
        var body = await CaptureBodyAsync(
            Update(firstName: "Russ", lastName: "White", gender: Gender.Male,
                emails: [EmailAddress.From("a@example.com")]));

        body.Should().NotContain("AddressInfo");
        body.Should().NotContain("City");
    }

    /// <summary>
    /// The regression that matters most.
    /// </summary>
    /// <remarks>
    /// The stub models the live service's actual failure mode: a Location
    /// written without its required City is accepted with a 204, after which
    /// every read of that person <em>and of the whole People collection</em>
    /// fails with a 500, recoverable only by a blind write or ResetDataSource.
    /// <para>
    /// So this test does not merely assert on the payload. It lets the stub
    /// poison itself if AddressInfo ever appears, then proves both a detail
    /// read and a list read still succeed afterwards. If the mapper regresses
    /// to sending whole entities, the reads below start failing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_sparse_update_leaves_both_the_person_and_the_collection_readable()
    {
        using var mockHttp = new MockHttpMessageHandler();
        var poisoned = false;

        mockHttp.When(HttpMethod.Patch, StubbedService.DetailUrl)
            .Respond(async request =>
            {
                var body = await request.Content!.ReadAsStringAsync(Token);

                if (body.Contains("AddressInfo", StringComparison.Ordinal))
                {
                    poisoned = true;
                }

                return StubbedService.Status(HttpStatusCode.NoContent, "after");
            });

        mockHttp.When(HttpMethod.Get, StubbedService.DetailUrl)
            .Respond(_ => poisoned
                ? StubbedService.Status(HttpStatusCode.InternalServerError)
                : StubbedService.Json(StubbedService.PersonJson));

        mockHttp.When(HttpMethod.Get, StubbedService.CollectionUrl)
            .Respond(_ => poisoned
                ? StubbedService.Status(HttpStatusCode.InternalServerError)
                : StubbedService.Json(StubbedService.CollectionJson));

        var repository = StubbedService.Repository(mockHttp);

        var updated = await repository.UpdateAsync(Update(firstName: "Russ"), Token);
        updated.IsSuccess.Should().BeTrue();

        poisoned.Should().BeFalse("the payload must not carry AddressInfo");

        var detail = await repository.GetByIdAsync(UserName.From("russellwhyte"), Token);
        detail.IsSuccess.Should().BeTrue("the person must remain readable after an update");

        var list = await repository.ListAsync(1, 8, Token);
        list.IsSuccess.Should().BeTrue("the collection must remain readable after an update");
    }

    /// <summary>
    /// The inverse, proving the stub above would actually catch a regression
    /// rather than passing vacuously.
    /// </summary>
    [Fact]
    public async Task The_corruption_model_does_fail_when_AddressInfo_is_sent()
    {
        using var mockHttp = new MockHttpMessageHandler();
        var poisoned = false;

        mockHttp.When(HttpMethod.Patch, StubbedService.DetailUrl)
            .Respond(async request =>
            {
                var body = await request.Content!.ReadAsStringAsync(Token);

                if (body.Contains("AddressInfo", StringComparison.Ordinal))
                {
                    poisoned = true;
                }

                return StubbedService.Status(HttpStatusCode.NoContent, "after");
            });

        mockHttp.When(HttpMethod.Get, StubbedService.CollectionUrl)
            .Respond(_ => poisoned
                ? StubbedService.Status(HttpStatusCode.InternalServerError)
                : StubbedService.Json(StubbedService.CollectionJson));

        var client = mockHttp.ToHttpClient();
        client.BaseAddress = new Uri(StubbedService.BaseUrl);

        // Bypasses the mapper on purpose, standing in for a regression that
        // reintroduces the full-entity payload.
        using var request = new HttpRequestMessage(HttpMethod.Patch, "People('russellwhyte')")
        {
            Content = new StringContent(
                """{"AddressInfo":[{"Address":"2 Partial St"}]}""",
                System.Text.Encoding.UTF8,
                "application/json"),
        };

        using var response = await client.SendAsync(request, Token);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var list = await StubbedService.Repository(mockHttp).ListAsync(1, 8, Token);
        list.IsSuccess.Should().BeFalse("a partial address write poisons the collection");
    }

    // -----------------------------------------------------------------
    // Emails
    // -----------------------------------------------------------------

    /// <summary>Null is a 500 from the service, so an empty list must stay a list.</summary>
    [Fact]
    public async Task Clearing_emails_emits_an_empty_array_not_null()
    {
        var body = await CaptureBodyAsync(Update(emails: []));

        using var document = JsonDocument.Parse(body);
        var emails = document.RootElement.GetProperty("Emails");

        emails.ValueKind.Should().Be(JsonValueKind.Array);
        emails.GetArrayLength().Should().Be(0);
        body.Should().Contain("[]").And.NotContain("null");
    }

    [Fact]
    public async Task Replacing_emails_sends_the_whole_new_collection()
    {
        var body = await CaptureBodyAsync(Update(emails:
        [
            EmailAddress.From("a@example.com"),
            EmailAddress.From("b@example.com"),
        ]));

        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("Emails")
            .EnumerateArray().Select(element => element.GetString())
            .Should().Equal("a@example.com", "b@example.com");
    }

    [Fact]
    public async Task Untouched_emails_are_absent_entirely()
    {
        var body = await CaptureBodyAsync(Update(firstName: "Russ"));

        using var document = JsonDocument.Parse(body);
        document.RootElement.TryGetProperty("Emails", out _).Should().BeFalse();
    }

    // -----------------------------------------------------------------
    // Gender
    // -----------------------------------------------------------------

    /// <summary>
    /// Bare, not the fully-qualified form that $filter requires. The qualified
    /// spelling is a 500 on a PATCH.
    /// </summary>
    [Fact]
    public async Task Gender_is_written_as_a_bare_enum_literal()
    {
        var body = await CaptureBodyAsync(Update(gender: Gender.Female));

        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("Gender").GetString().Should().Be("Female");

        body.Should().NotContain("Microsoft.OData.SampleService");
    }

    [Fact]
    public async Task Gender_is_never_emitted_as_null()
    {
        var body = await CaptureBodyAsync(Update(firstName: "Russ", gender: null));

        using var document = JsonDocument.Parse(body);
        document.RootElement.TryGetProperty("Gender", out _).Should().BeFalse();
    }

    /// <summary>
    /// Unknown is a real value the service stores, unlike null which it
    /// silently coerces to Male.
    /// </summary>
    [Fact]
    public async Task An_unstated_gender_is_written_as_Unknown()
    {
        var body = await CaptureBodyAsync(Update(gender: Gender.Unknown));

        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("Gender").GetString().Should().Be("Unknown");
    }
}
