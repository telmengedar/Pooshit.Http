using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Http.Tests.TestSupport;
using Pooshit.Http;
using Pooshit.Http.Encodings;
using Pooshit.Json;

namespace Http.Tests;

[TestFixture, Parallelizable]
public class JsonEncoderTests {
    const int overCapItems = 2000;

    static List<ProbeDto> Document(int items) {
        List<ProbeDto> document = new();
        for (int index = 0; index < items; ++index)
            document.Add(new() { Value = $"item-{index:D6}-padding-padding-padding" });
        return document;
    }

    static byte[] Serialized(object document) => Encoding.UTF8.GetBytes(Json.WriteString(document, JsonOptions.RestApi));

    static async Task<byte[]> Drain(HttpContent content) {
        MemoryStream sink = new();
        await content.CopyToAsync(sink);
        return sink.ToArray();
    }


    [Test, Parallelizable]
    public void Encode_SmallBody_ReportsContentLength() {
        ProbeDto document = new() { Value = "42" };

        using HttpContent content = new JsonEncoder().Encode(document);

        Assert.That(content.Headers.ContentLength, Is.EqualTo(14));
    }


    [Test, Parallelizable]
    public async Task Encode_SmallBody_BytesMatchWriteString() {
        ProbeDto document = new() { Value = "42" };

        using HttpContent content = new JsonEncoder().Encode(document);
        byte[] drained = await Drain(content);

        Assert.That(Encoding.UTF8.GetString(drained), Is.EqualTo("{\"value\":\"42\"}"));
        Assert.That(drained, Is.EqualTo(Serialized(document)));
    }


    [Test, Parallelizable]
    public async Task Encode_SmallBodyWithCustomOptions_UsesThem() {
        ProbeDto document = new() { Value = "42" };

        using HttpContent content = new JsonEncoder(JsonOptions.Default).Encode(document);
        byte[] drained = await Drain(content);

        Assert.That(Encoding.UTF8.GetString(drained), Is.EqualTo("{\"Value\":\"42\"}"));
    }


    [Test, Parallelizable]
    public async Task Encode_LargeBodyWithCustomOptions_UsesThem() {
        List<ProbeDto> document = Document(overCapItems);

        using HttpContent content = new JsonEncoder(JsonOptions.Default).Encode(document);
        byte[] drained = await Drain(content);

        Assert.That(drained, Is.EqualTo(Encoding.UTF8.GetBytes(Json.WriteString(document, JsonOptions.Default))));
        Assert.That(drained, Is.Not.EqualTo(Serialized(document)));
    }


    [Test, Parallelizable]
    [Description("pins the cap itself: a document of exactly the buffered size is still framed with a content length")]
    public void Encode_BodyAtTheCap_ReportsContentLength() {
        ProbeDto document = new() { Value = new string('x', 84988) };

        using HttpContent content = new JsonEncoder().Encode(document);

        Assert.That(Serialized(document), Has.Length.EqualTo(85000));
        Assert.That(content.Headers.ContentLength, Is.EqualTo(85000));
    }


    [Test, Parallelizable]
    [Description("pins the cap itself: one byte past the buffered size the body is framed without a content length")]
    public void Encode_BodyOneByteOverTheCap_ReportsNoContentLength() {
        ProbeDto document = new() { Value = new string('x', 84989) };

        using HttpContent content = new JsonEncoder().Encode(document);

        Assert.That(Serialized(document), Has.Length.EqualTo(85001));
        Assert.That(content.Headers.ContentLength, Is.Null);
    }


    [Test, Parallelizable]
    public void Encode_LargeBody_ReportsNoContentLength() {
        List<ProbeDto> document = Document(overCapItems);

        using HttpContent content = new JsonEncoder().Encode(document);

        Assert.That(Serialized(document), Has.Length.GreaterThan(85000));
        Assert.That(content.Headers.ContentLength, Is.Null);
    }


    [Test, Parallelizable]
    public async Task Encode_LargeBody_BytesMatchWriteString() {
        List<ProbeDto> document = Document(overCapItems);

        using HttpContent content = new JsonEncoder().Encode(document);
        byte[] drained = await Drain(content);

        Assert.That(drained, Has.Length.GreaterThan(85000));
        Assert.That(drained, Is.EqualTo(Serialized(document)));
    }


    [Test, Parallelizable]
    [Description("pins that an over-cap document reaches the transport as a sequence of writes none of which is a whole-document block")]
    public async Task Encode_LargeBody_ReachesTheTransportInChunksBelowTheCap() {
        List<ProbeDto> document = Document(overCapItems);
        int documentBytes = Serialized(document).Length;

        using HttpContent content = new JsonEncoder().Encode(document);
        WriteRecordingSink sink = new();
        await content.CopyToAsync(sink);

        Assert.That(sink.BytesWritten, Is.EqualTo(documentBytes));
        Assert.That(sink.LargestWrite, Is.LessThan(85000));
    }


    [Test, Parallelizable]
    [Description("pins that an over-cap document is read from the object graph while its bytes are already on the wire, which no implementation serializing the whole document before its first write can satisfy")]
    public async Task Encode_LargeBody_ReadsTheDocumentWhileWritingIt() {
        WriteRecordingSink sink = new();
        List<long> reads = new();
        List<ReadProgressDto> document = new();
        for (int index = 0; index < overCapItems; ++index)
            document.Add(new(sink, reads, $"item-{index:D6}-padding-padding-padding"));

        using HttpContent content = new JsonEncoder().Encode(document);
        int documentBytes = Serialized(document).Length;
        reads.Clear();
        await content.CopyToAsync(sink);

        Assert.That(sink.BytesWritten, Is.EqualTo(documentBytes));
        Assert.That(reads, Has.Count.EqualTo(overCapItems));
        Assert.That(reads[^1], Is.GreaterThan(documentBytes / 2));
    }


    [TestCase(1)]
    [TestCase(overCapItems)]
    [Parallelizable]
    public void Encode_ContentTypeIsApplicationJson(int items) {
        List<ProbeDto> document = Document(items);

        using HttpContent content = new JsonEncoder().Encode(document);

        Assert.That(content.Headers.ContentType?.ToString(), Is.EqualTo("application/json"));
    }


    [Test, Parallelizable]
    [Description("pins that the streaming branch can be sent more than once, which is what a verb preserving redirect hop requires of it")]
    public async Task Encode_LargeBody_SurvivesFiveSends_WithIdenticalBytes() {
        List<ProbeDto> document = Document(overCapItems);
        byte[] expected = Serialized(document);

        using HttpContent content = new JsonEncoder().Encode(document);

        for (int send = 0; send < 5; ++send)
            Assert.That(await Drain(content), Is.EqualTo(expected), $"send {send}");
    }


    [Test, Parallelizable]
    public async Task Encode_SmallBody_MutatedAfterEncode_CarriesTheEncodedDocument() {
        ProbeDto document = new() { Value = "42" };

        using HttpContent content = new JsonEncoder().Encode(document);
        document.Value = "mutated";
        byte[] drained = await Drain(content);

        Assert.That(Encoding.UTF8.GetString(drained), Is.EqualTo("{\"value\":\"42\"}"));
    }


    [Test, Parallelizable]
    [Description("pins that the streaming branch serializes at send time, so a document mutated after encoding travels in its mutated form")]
    public async Task Encode_LargeBody_MutatedAfterEncode_CarriesTheMutation() {
        List<ProbeDto> document = Document(overCapItems);
        byte[] encoded = Serialized(document);

        using HttpContent content = new JsonEncoder().Encode(document);
        document[0].Value = "mutated";
        byte[] drained = await Drain(content);

        Assert.That(drained, Is.EqualTo(Serialized(document)));
        Assert.That(drained, Is.Not.EqualTo(encoded));
    }


    [Test, Parallelizable]
    public async Task Post308_LargeJsonBody_HopRepeatsIdenticalBytes() {
        using HttpResponseMessage redirect = new(HttpStatusCode.PermanentRedirect);
        redirect.Headers.Location = new Uri("https://other-host.example/target");

        using HttpResponseMessage final = new(HttpStatusCode.OK) { Content = new StringContent("done") };

        SequenceHandler handler = new(redirect, final);
        HttpService service = new(handler);

        string result = await service.Post<List<ProbeDto>, string>("https://original-host.example/start",
                                                                   Document(overCapItems),
                                                                   new HttpOptions { FollowRedirects = true });

        Assert.That(result, Is.EqualTo("done"));
        Assert.That(handler.RequestBodies, Has.Count.EqualTo(2));
        Assert.That(handler.RequestBodies[0], Has.Length.GreaterThan(85000));
        Assert.That(handler.RequestBodies[1], Is.EqualTo(handler.RequestBodies[0]));
    }
}
