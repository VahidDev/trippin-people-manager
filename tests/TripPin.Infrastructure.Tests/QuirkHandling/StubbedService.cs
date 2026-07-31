using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RichardSzalay.MockHttp;
using TripPin.Infrastructure.Configuration;
using TripPin.Infrastructure.OData;

namespace TripPin.Infrastructure.Tests.QuirkHandling;

/// <summary>
/// Builds a real <see cref="ODataPeopleRepository"/> over a stubbed
/// <see cref="HttpMessageHandler"/>, so quirk behaviour is exercised with no
/// network and no shared sandbox to corrupt.
/// </summary>
internal static class StubbedService
{
    public const string BaseUrl = "https://example.test/svc/";
    public const string DetailUrl = "https://example.test/svc/People('russellwhyte')";
    public const string CollectionUrl = "https://example.test/svc/People";

    public const string PersonJson = """
        {
          "@odata.etag": "W/\"08DEEEEB83CE374D\"",
          "UserName": "russellwhyte",
          "FirstName": "Russell",
          "LastName": "Whyte",
          "Emails": ["Russell@example.com", "Russell@contoso.com"],
          "AddressInfo": [
            {
              "Address": "187 Suffolk Ln.",
              "City": { "CountryRegion": "United States", "Name": "Boise", "Region": "ID" }
            }
          ],
          "Gender": "Male",
          "Concurrency": 639210892429244237
        }
        """;

    public const string CollectionJson = """
        {
          "@odata.count": 20,
          "value": [
            { "UserName": "russellwhyte", "FirstName": "Russell", "LastName": "Whyte", "Gender": "Male" },
            { "UserName": "scottketchum", "FirstName": "Scott", "LastName": "Ketchum", "Gender": "Male" }
          ]
        }
        """;

    /// <summary>A page that also carries the server's continuation link.</summary>
    public const string TruncatedCollectionJson = """
        {
          "@odata.count": 20,
          "@odata.nextLink": "https://example.test/svc/People?%24skiptoken=8",
          "value": [
            { "UserName": "russellwhyte", "FirstName": "Russell", "LastName": "Whyte", "Gender": "Male" }
          ]
        }
        """;

    public static ODataPeopleRepository Repository(MockHttpMessageHandler mockHttp)
    {
        var client = mockHttp.ToHttpClient();
        client.BaseAddress = new Uri(BaseUrl);

        return new ODataPeopleRepository(
            client,
            new ODataFilterTranslator(),
            new PersonMapper(NullLogger<PersonMapper>.Instance),
            new ODataStatusInterpreter(),
            Options.Create(new TripPinOptions()),
            NullLogger<ODataPeopleRepository>.Instance);
    }

    public static HttpResponseMessage Json(string body, string? etag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };

        if (etag is not null)
        {
            response.Headers.ETag = new EntityTagHeaderValue($"\"{etag}\"", isWeak: true);
        }

        return response;
    }

    public static HttpResponseMessage Status(HttpStatusCode statusCode, string? etag = null)
    {
        var response = new HttpResponseMessage(statusCode);

        if (etag is not null)
        {
            response.Headers.ETag = new EntityTagHeaderValue($"\"{etag}\"", isWeak: true);
        }

        return response;
    }
}
