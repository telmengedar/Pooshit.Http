using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Http.Tests.TestSupport;
using Pooshit.Http;

namespace Http.Tests;

[TestFixture, Parallelizable]
public class HttpServiceHeaderRedactionTests {

    static HttpResponseMessage ErrorResponse() =>
        new(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"code\":\"denied\"}") };

    static HttpHeader Header(string key, string value) => new() { Key = key, Value = value };

    static HttpOptions Options(HeaderDumpMode? mode, params HttpHeader[] headers) =>
        new() { HeaderDumpMode = mode, Headers = headers };

    static HttpServiceException Capture(HttpService service, HttpOptions? options) =>
        Assert.ThrowsAsync<HttpServiceException>(() => service.Get<string>("https://example.test/probe", options))!;

    [Test, Parallelizable]
    public void DefaultMode_RequestAuthorizationHeader_KeepsNameDropsValue() {
        using HttpResponseMessage response = ErrorResponse();
        HttpService service = new(new SequenceHandler(response));

        HttpServiceException exception = Capture(service, Options(null, Header("Authorization", "Bearer eyJsecret-token-value")));

        Assert.That(exception.Message, Does.Contain("Authorization"));
        Assert.That(exception.Message, Does.Contain("<redacted>"));
        Assert.That(exception.Message, Does.Not.Contain("eyJsecret-token-value"));
    }

    [Test, Parallelizable]
    public void DefaultMode_ResponseSetCookieHeader_KeepsNameDropsValue() {
        using HttpResponseMessage response = ErrorResponse();
        response.Headers.TryAddWithoutValidation("Set-Cookie", "session=topsecretsessionvalue");
        HttpService service = new(new SequenceHandler(response));

        HttpServiceException exception = Capture(service, null);

        Assert.That(exception.Message, Does.Contain("Set-Cookie"));
        Assert.That(exception.Message, Does.Contain("<redacted>"));
        Assert.That(exception.Message, Does.Not.Contain("topsecretsessionvalue"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9117: a filter that redacts every header passes every token-is-gone assertion, so the non-sensitive direction is pinned too")]
    public void DefaultMode_NonSensitiveHeaderAlongsideSensitiveOne_DumpedVerbatim() {
        using HttpResponseMessage response = ErrorResponse();
        HttpService service = new(new SequenceHandler(response));

        HttpServiceException exception = Capture(service, Options(null,
                                                                 Header("Authorization", "Bearer eyJsecret-token-value"),
                                                                 Header("X-Correlation-Id", "correlation-4711")));

        Assert.That(exception.Message, Does.Contain("X-Correlation-Id: correlation-4711"));
        Assert.That(exception.Message, Does.Not.Contain("eyJsecret-token-value"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9117: that an auth header was set is diagnostically valuable and is not itself a secret, so the name must outlive its value")]
    public void RedactedMode_SensitiveHeaderName_SurvivesWithoutItsValue() {
        using HttpResponseMessage response = ErrorResponse();
        HttpService service = new(new SequenceHandler(response));

        HttpServiceException exception = Capture(service, Options(null, Header("Proxy-Authorization", "Basic cHJveHktdG9wc2VjcmV0")));

        Assert.That(exception.Message, Does.Contain("Proxy-Authorization"));
        Assert.That(exception.Message, Does.Not.Contain("cHJveHktdG9wc2VjcmV0"));
    }

    [Test, Parallelizable]
    public void FullMode_AuthorizationHeader_DumpedVerbatim() {
        using HttpResponseMessage response = ErrorResponse();
        HttpService service = new(new SequenceHandler(response));

        HttpServiceException exception = Capture(service, Options(HeaderDumpMode.Full, Header("Authorization", "Bearer eyJsecret-token-value")));

        Assert.That(exception.Message, Does.Contain("Authorization: Bearer eyJsecret-token-value"));
        Assert.That(exception.Message, Does.Not.Contain("<redacted>"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9117: turning the dump off must not cost the diagnostics that are not secret")]
    public void OmittedMode_HeadersGone_UrlStatusAndBodyRemain() {
        using HttpResponseMessage response = ErrorResponse();
        HttpService service = new(new SequenceHandler(response));

        HttpServiceException exception = Capture(service, Options(HeaderDumpMode.Omitted, Header("Authorization", "Bearer eyJsecret-token-value")));

        Assert.That(exception.Response.RequestMessage!.Headers.Contains("Authorization"), Is.True);
        Assert.That(exception.Message, Does.Not.Contain("Authorization"));
        Assert.That(exception.Message, Does.Not.Contain("eyJsecret-token-value"));
        Assert.That(exception.Message, Does.Contain("https://example.test/probe"));
        Assert.That(exception.Message, Does.Contain("Unauthorized"));
        Assert.That(exception.Body, Is.EqualTo("{\"code\":\"denied\"}"));
    }

    [Test, Parallelizable]
    public void ServiceSet_AddedHeaderName_KeepsNameDropsValue() {
        using HttpResponseMessage response = ErrorResponse();
        HttpService service = new(new SequenceHandler(response));
        service.SensitiveHeaders.Add("X-Vendor-Signature");

        HttpServiceException exception = Capture(service, Options(null, Header("X-Vendor-Signature", "signature-topsecret")));

        Assert.That(exception.Message, Does.Contain("X-Vendor-Signature"));
        Assert.That(exception.Message, Does.Contain("<redacted>"));
        Assert.That(exception.Message, Does.Not.Contain("signature-topsecret"));
    }

    [Test, Parallelizable]
    public void ServiceSet_RemovedHeaderName_DumpedVerbatim() {
        using HttpResponseMessage response = ErrorResponse();
        HttpService service = new(new SequenceHandler(response));
        service.SensitiveHeaders.Remove("Cookie");

        HttpServiceException exception = Capture(service, Options(null, Header("Cookie", "session=nolongerredacted")));

        Assert.That(exception.Message, Does.Contain("Cookie: session=nolongerredacted"));
    }

    [Test, Parallelizable]
    [Description("names configured in one casing must match headers arriving in another")]
    public void ServiceSet_NameCasingDiffersFromHeaderCasing_KeepsNameDropsValue() {
        using HttpResponseMessage response = ErrorResponse();
        HttpService service = new(new SequenceHandler(response));
        service.SensitiveHeaders.Add("X-VENDOR-KEY");

        HttpServiceException exception = Capture(service, Options(null, Header("x-vendor-key", "vendorkey-topsecret")));

        Assert.That(exception.Message, Does.Contain("x-vendor-key"));
        Assert.That(exception.Message, Does.Contain("<redacted>"));
        Assert.That(exception.Message, Does.Not.Contain("vendorkey-topsecret"));
    }

    [Test, Parallelizable]
    public void CallMode_OverridesServiceMode_RedactedWinsOverFull() {
        using HttpResponseMessage response = ErrorResponse();
        HttpService service = new(new SequenceHandler(response)) { HeaderDumpMode = HeaderDumpMode.Full };

        HttpServiceException exception = Capture(service, Options(HeaderDumpMode.Redacted, Header("Authorization", "Bearer eyJsecret-token-value")));

        Assert.That(exception.Message, Does.Contain("Authorization"));
        Assert.That(exception.Message, Does.Contain("<redacted>"));
        Assert.That(exception.Message, Does.Not.Contain("eyJsecret-token-value"));
    }

    [Test, Parallelizable]
    public void ServiceMode_AppliesWhenCallLeavesModeUnset() {
        using HttpResponseMessage response = ErrorResponse();
        HttpService service = new(new SequenceHandler(response)) { HeaderDumpMode = HeaderDumpMode.Full };

        HttpServiceException exception = Capture(service, Options(null, Header("Authorization", "Bearer eyJsecret-token-value")));

        Assert.That(exception.Message, Does.Contain("Authorization: Bearer eyJsecret-token-value"));
    }

    [Test, Parallelizable]
    public void ServiceMode_AppliesWhenOptionsAreNull() {
        using HttpResponseMessage response = ErrorResponse();
        response.Headers.TryAddWithoutValidation("Set-Cookie", "session=topsecretsessionvalue");
        HttpService service = new(new SequenceHandler(response)) { HeaderDumpMode = HeaderDumpMode.Full };

        HttpServiceException exception = Capture(service, null);

        Assert.That(exception.Message, Does.Contain("Set-Cookie: session=topsecretsessionvalue"));
    }

    [Test, Parallelizable]
    public void DefaultService_UnconfiguredMode_RedactsByDefault() {
        using HttpResponseMessage response = ErrorResponse();
        HttpService service = new(new SequenceHandler(response));

        HttpServiceException exception = Capture(service, Options(null, Header("X-Api-Key", "apikey-topsecret")));

        Assert.That(exception.Message, Does.Contain("X-Api-Key"));
        Assert.That(exception.Message, Does.Contain("<redacted>"));
        Assert.That(exception.Message, Does.Not.Contain("apikey-topsecret"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9702 leg 1: BynerApplicationService sends the vendor key under the unhyphenated name 'apiKey'")]
    public void DefaultMode_ApiKeyHeaderUnhyphenated_KeepsNameDropsValue() {
        using HttpResponseMessage response = ErrorResponse();
        HttpService service = new(new SequenceHandler(response));

        HttpServiceException exception = Capture(service, Options(null, Header("apiKey", "byner-vendor-key-topsecret")));

        Assert.That(exception.Message, Does.Contain("apiKey"));
        Assert.That(exception.Message, Does.Contain("<redacted>"));
        Assert.That(exception.Message, Does.Not.Contain("byner-vendor-key-topsecret"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9702 leg 1: ZvooveApi and ProsoftRecruitingApi send the vendor key under the unhyphenated name 'X-ApiKey'")]
    public void DefaultMode_XApiKeyHeaderUnhyphenated_KeepsNameDropsValue() {
        using HttpResponseMessage response = ErrorResponse();
        HttpService service = new(new SequenceHandler(response));

        HttpServiceException exception = Capture(service, Options(null, Header("X-ApiKey", "zvoove-vendor-key-topsecret")));

        Assert.That(exception.Message, Does.Contain("X-ApiKey"));
        Assert.That(exception.Message, Does.Contain("<redacted>"));
        Assert.That(exception.Message, Does.Not.Contain("zvoove-vendor-key-topsecret"));
    }

    [Test, Parallelizable]
    [Description("the fix stays an exact-name match: a header that merely contains 'apikey' as a substring is not swept in")]
    public void DefaultMode_HeaderNameContainingApiKeyAsSubstring_DumpedVerbatim() {
        using HttpResponseMessage response = ErrorResponse();
        HttpService service = new(new SequenceHandler(response));

        HttpServiceException exception = Capture(service, Options(null, Header("X-ApiKeyId", "not-a-secret-id")));

        Assert.That(exception.Message, Does.Contain("X-ApiKeyId: not-a-secret-id"));
    }

    static string HttpServiceSourcePath([CallerFilePath] string testFilePath = "") {
        string testDir = Path.GetDirectoryName(testFilePath)!;
        return Path.GetFullPath(Path.Combine(testDir, "..", "Pooshit.Http", "HttpService.cs"));
    }

    static HttpRequestMessage MarkedRequest() {
        HttpRequestMessage request = new(HttpMethod.Get, "https://example.test/probe");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer fanned-marker-token");
        return request;
    }

    static IEnumerable<TestCaseData> StatusValidatingOverloads() {
        const string url = "https://example.test/probe";
        yield return new TestCaseData(new Func<HttpService, HttpOptions, Task>(
            (s, o) => s.Post<string, string>(url, "body", o))).SetName("Post_WithBodyAndResult");
        yield return new TestCaseData(new Func<HttpService, HttpOptions, Task>(
            (s, o) => s.Post<string>(url, o))).SetName("Post_NoBodyWithResult");
        yield return new TestCaseData(new Func<HttpService, HttpOptions, Task>(
            (s, o) => s.Post(url, o))).SetName("Post_NoBodyNoResult");
        yield return new TestCaseData(new Func<HttpService, HttpOptions, Task>(
            (s, o) => s.Post(url, "body", o))).SetName("Post_WithBodyNoResult");
        yield return new TestCaseData(new Func<HttpService, HttpOptions, Task>(
            (s, o) => s.Put<string, string>(url, "body", o))).SetName("Put_WithResult");
        yield return new TestCaseData(new Func<HttpService, HttpOptions, Task>(
            (s, o) => s.Put(url, "body", o))).SetName("Put_NoResult");
        yield return new TestCaseData(new Func<HttpService, HttpOptions, Task>(
            (s, o) => s.Patch<string, string>(url, "body", o))).SetName("Patch_WithResult");
        yield return new TestCaseData(new Func<HttpService, HttpOptions, Task>(
            (s, o) => s.Patch(url, "body", o))).SetName("Patch_NoResult");
        yield return new TestCaseData(new Func<HttpService, HttpOptions, Task>(
            (s, o) => s.Get(url, o))).SetName("Get_NoResult");
        yield return new TestCaseData(new Func<HttpService, HttpOptions, Task>(
            (s, o) => s.Get<string>(url, o))).SetName("Get_WithResult");
        yield return new TestCaseData(new Func<HttpService, HttpOptions, Task>(
            (s, o) => s.Delete(url, o))).SetName("Delete_NoResult");
        yield return new TestCaseData(new Func<HttpService, HttpOptions, Task>(
            (s, o) => s.Delete<string>(url, o))).SetName("Delete_WithResult");
        yield return new TestCaseData(new Func<HttpService, HttpOptions, Task>(
            (s, o) => s.Request("PUT", url, "body", o))).SetName("Request_NoResult");
        yield return new TestCaseData(new Func<HttpService, HttpOptions, Task>(
            (s, o) => s.Request<string, string>("PUT", url, "body", o))).SetName("Request_WithResult");
        yield return new TestCaseData(new Func<HttpService, HttpOptions, Task>(
            (s, o) => s.Send(MarkedRequest(), o))).SetName("Send_NoResult");
        yield return new TestCaseData(new Func<HttpService, HttpOptions, Task>(
            (s, o) => s.Send<string>(MarkedRequest(), o))).SetName("Send_WithResult");
    }

    [TestCaseSource(nameof(StatusValidatingOverloads)), Parallelizable]
    [Description("DiVoid #9117: CheckHttpResponse takes the options bag at nine call sites, so the per-call mode has to be pinned on every overload that can reach it")]
    public void CallMode_ReachesEveryOverloadThatValidatesStatus(Func<HttpService, HttpOptions, Task> invoke) {
        using HttpResponseMessage response = ErrorResponse();
        HttpService service = new(new SequenceHandler(response));
        HttpOptions options = Options(HeaderDumpMode.Full, Header("Authorization", "Bearer fanned-marker-token"));

        HttpServiceException exception = Assert.ThrowsAsync<HttpServiceException>(() => invoke(service, options))!;

        Assert.That(exception.Message, Does.Contain("Authorization: Bearer fanned-marker-token"));
        Assert.That(exception.Message, Does.Not.Contain("<redacted>"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #9117: an overload added later must not reach the status check without the options bag, which the overload fan cannot see")]
    public void EveryStatusCheckCallSiteNamesTheOptions() {
        string source = File.ReadAllText(HttpServiceSourcePath());
        int callSites = Regex.Matches(source, @"await CheckHttpResponse\([^)]*\)").Count;
        int callSitesNamingOptions = Regex.Matches(source, @"await CheckHttpResponse\([^)]*options[^)]*\)").Count;

        Assert.That(callSites, Is.GreaterThanOrEqualTo(9));
        Assert.That(callSitesNamingOptions, Is.EqualTo(callSites));
    }
}
