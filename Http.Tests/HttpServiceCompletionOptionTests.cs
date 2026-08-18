using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Http.Tests.TestSupport;
using Pooshit.Http;

namespace Http.Tests;

[TestFixture, Parallelizable]
public class HttpServiceCompletionOptionTests {

    static string HttpServiceSourcePath([CallerFilePath] string testFilePath = "") {
        string testSupportDir = Path.GetDirectoryName(testFilePath)!;
        return Path.GetFullPath(Path.Combine(testSupportDir, "..", "Pooshit.Http", "HttpService.cs"));
    }

    static IEnumerable<TestCaseData> SendSites() {
        const string url = "https://example.test/probe";
        yield return new TestCaseData(new Func<HttpService, HttpOptions, Task>(
            (s, o) => s.Post<string, HttpResponseMessage>(url, "body", o))).SetName("Post_WithBodyAndResponse");
        yield return new TestCaseData(new Func<HttpService, HttpOptions, Task>(
            (s, o) => s.Post(url, o))).SetName("Post_NoBodyNoResult");
        yield return new TestCaseData(new Func<HttpService, HttpOptions, Task>(
            (s, o) => s.Post<HttpResponseMessage>(url, o))).SetName("Post_NoBodyWithResult");
        yield return new TestCaseData(new Func<HttpService, HttpOptions, Task>(
            (s, o) => s.Post(url, "body", o))).SetName("Post_WithBodyNoResult");
        yield return new TestCaseData(new Func<HttpService, HttpOptions, Task>(
            (s, o) => s.Put<string, HttpResponseMessage>(url, "body", o))).SetName("Put_WithResult");
        yield return new TestCaseData(new Func<HttpService, HttpOptions, Task>(
            (s, o) => s.Put(url, "body", o))).SetName("Put_NoResult");
        yield return new TestCaseData(new Func<HttpService, HttpOptions, Task>(
            (s, o) => s.Patch<string, HttpResponseMessage>(url, "body", o))).SetName("Patch_WithResult");
        yield return new TestCaseData(new Func<HttpService, HttpOptions, Task>(
            (s, o) => s.Patch(url, "body", o))).SetName("Patch_NoResult");
        yield return new TestCaseData(new Func<HttpService, HttpOptions, Task>(
            (s, o) => s.Get(url, o))).SetName("Get_NoResult");
        yield return new TestCaseData(new Func<HttpService, HttpOptions, Task>(
            (s, o) => s.Get<HttpResponseMessage>(url, o))).SetName("Get_WithResult");
        yield return new TestCaseData(new Func<HttpService, HttpOptions, Task>(
            (s, o) => s.Delete(url, o))).SetName("Delete_NoResult");
        yield return new TestCaseData(new Func<HttpService, HttpOptions, Task>(
            (s, o) => s.Delete<HttpResponseMessage>(url, o))).SetName("Delete_WithResult");
        yield return new TestCaseData(new Func<HttpService, HttpOptions, Task>(
            (s, o) => s.Request("PUT", url, "body", o))).SetName("Request_NoResult");
        yield return new TestCaseData(new Func<HttpService, HttpOptions, Task>(
            (s, o) => s.Request<string, HttpResponseMessage>("PUT", url, "body", o))).SetName("Request_WithResult");
        yield return new TestCaseData(new Func<HttpService, HttpOptions, Task>(
            (s, o) => s.Send<HttpResponseMessage>(new HttpRequestMessage(HttpMethod.Get, url), o))).SetName("Send_WithResult");
        yield return new TestCaseData(new Func<HttpService, HttpOptions, Task>(
            (s, o) => s.Send(new HttpRequestMessage(HttpMethod.Get, url), o))).SetName("Send_NoResult");
    }

    [TestCaseSource(nameof(SendSites)), Parallelizable]
    public async Task StreamingOption_ReachesEverySendSite(Func<HttpService, HttpOptions, Task> invoke) {
        ProbeContent content = new("payload"u8.ToArray());
        using HttpResponseMessage response = new(HttpStatusCode.OK) { Content = content };
        SequenceHandler handler = new(response);
        HttpService service = new(handler);

        await invoke(service, new HttpOptions { CompletionOption = HttpCompletionOption.ResponseHeadersRead });

        Assert.That(content.BytesRead, Is.Zero);
    }

    [Test, Parallelizable]
    public void ExactlyOneCallSiteNamesACompletionOption() {
        string source = File.ReadAllText(HttpServiceSourcePath());
        int totalSendSites = Regex.Matches(source, @"client\.SendAsync\(").Count;
        int sitesNamingCompletionOption = Regex.Matches(source, @"client\.SendAsync\([^)]*CompletionOption[^)]*\)").Count;

        Assert.That(totalSendSites, Is.EqualTo(1));
        Assert.That(sitesNamingCompletionOption, Is.EqualTo(1));
    }

    static IEnumerable<HttpOptions?> BufferingOptions() {
        yield return null;
        yield return new HttpOptions();
        yield return new HttpOptions { CompletionOption = HttpCompletionOption.ResponseContentRead };
    }

    [TestCaseSource(nameof(BufferingOptions)), Parallelizable]
    public async Task DefaultCompletionOption_BlocksOnGatedBody(HttpOptions? options) {
        ProbeContent content = new("payload"u8.ToArray(), gated: true);
        using HttpResponseMessage response = new(HttpStatusCode.OK) { Content = content };
        SequenceHandler handler = new(response);
        HttpService service = new(handler);

        Task<HttpResponseMessage> sendTask = service.Get<HttpResponseMessage>("https://example.test/probe", options);
        Task completed = await Task.WhenAny(sendTask, Task.Delay(TimeSpan.FromMilliseconds(200)));

        Assert.That(completed, Is.Not.SameAs(sendTask));

        content.Release();
        using HttpResponseMessage result = await sendTask;
        Assert.That(content.BytesRead, Is.EqualTo(7));
    }

    [Test, Parallelizable]
    public async Task StreamingCompletionOption_ReturnsBeforeGatedBodyReleases() {
        ProbeContent content = new("payload"u8.ToArray(), gated: true);
        using HttpResponseMessage response = new(HttpStatusCode.OK) { Content = content };
        SequenceHandler handler = new(response);
        HttpService service = new(handler);

        Task<HttpResponseMessage> sendTask = service.Get<HttpResponseMessage>("https://example.test/probe",
            new HttpOptions { CompletionOption = HttpCompletionOption.ResponseHeadersRead });
        Task completed = await Task.WhenAny(sendTask, Task.Delay(TimeSpan.FromMilliseconds(200)));

        Assert.That(completed, Is.SameAs(sendTask));
        Assert.That(content.BytesRead, Is.Zero);

        content.Release();
        using HttpResponseMessage result = await sendTask;
    }
}
