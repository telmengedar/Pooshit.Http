# Architectural Document: the default JSON encoder stops materialising the document

> **Repo path:** `docs/architecture/json-encoder-streaming.md` (repository `telmengedar/Pooshit.Http`).
> **Node:** **#14635** — this document's own DiVoid node, maintained as a byte copy of this file.
> **DiVoid:** source task **#10071** (severity 2, measured in production) · the replay constraint **#14516** D2 · .NET Framework replay **#14607** · project **#2281** · repo map root **#8292** · `Encodings/` **#8294** · `JsonEncoder` **#8306** · `IResponseEncoder` **#8304** · `HttpService` **#8297**.
> **Predecessors, none superseded:** **#14516** (whose D2 and D3 this design has to survive) · **#8328** (the completion option, whose buffering axis this touches).
> **Contracts cited as load-bearing:** Design Contracts **#1136** (§1 KISS/DRY/YAGNI, §2 existing systems first, §3 configurability is not free, §4 less is better, §5 checklist walked as §9, §6 anti-patterns) · Code Contracts **#114** §0 · YAGNI **#1184** · falsifiable universals **#9951** and the discriminator rule **#14516** §D7.2.
> **Baseline:** branch `fix/json-encoder-streaming` off `origin/master` @ **`1457df8`**, tree clean. Version on master: `0.15.0-preview`. Every figure below was re-derived at that commit.

---

## TL;DR

**The brief's central worry does not exist, and the real cost is somewhere else.**

It asks whether streaming the JSON body would move every JSON payload out of the replayable column and break the 307/308 hop. **Measured: it does not.** A streaming content backed by *the object* re-serialises on every send, so it replays **five hops out of five with byte-identical output**. #14516 D2's non-replayable case is a `StreamContent` over a **consumed stream** — a different thing, and one this change does not create.

**The real cost is `Content-Length`.** A content that has not been materialised cannot report its length, so the request goes out `Transfer-Encoding: chunked`. Measured on a loopback server, both ways.

**Re-derived at `1457df8`, peak managed heap for one request:**

| doc | **A** current (`WriteString` → `StringContent`) | **B** buffer to bytes | **C** stream from the object |
|---|---|---|---|
| 2.3 MB | 11 M | 6 M | **0 M** |
| 5.9 MB | 11 M | 12 M | **1 M** |
| 15.4 MB | **61 M ≈ 4× doc** | 29 M ≈ 2× | **2 M** |

**The task's "~4×" reproduces** — as *peak*, not as total allocation, and that distinction matters (§3.1). **A is O(4n), B is O(2n), C is O(1).** Only C removes the failure class; B defers it.

**Decision: C, but gated — the hybrid.** Serialise into a bounded buffer while the document stays under the **large-object-heap threshold (85,000 bytes)**; if it fits, send it with a `Content-Length` exactly as today; if it overflows, discard and stream. **So the wire changes only for payloads at or above the size where the current code starts allocating on the LOH — that is, only for the payloads that were the problem.** A 59-byte body still goes out with `Content-Length: 59` (measured).

**Should it ship? Yes** — §10.1, and the hybrid is what makes that answer easy, because always-stream would have changed the wire for `{"id":1}`.

---

## 1. Problem

#10071, from production:

```
System.OutOfMemoryException
  at System.Text.StringBuilder.ToString()
  at Pooshit.Json.Json.WriteString(Object data, JsonOptions options)
  at Pooshit.Http.HttpService.CreateRequest[T](...)
```

A 770-job feed, **125 consecutive failures over six months** before anyone noticed. The mechanism is generic: `JsonEncoder.Encode` calls `Json.WriteString`, which must return the whole document as **one contiguous `string`** (UTF-16, so ≈2× the UTF-8 byte count), and `StringContent` then re-encodes that to a second full-size `byte[]`.

The encoder, in full, with the `// TODO` it has carried since it was written:

```
public HttpContent Encode(object data) {
    // TODO: WriteAsync Stream?
    StringContent content = new(Json.Json.WriteString(data, options));
    content.Headers.ContentType = new("application/json");
    return content;
}
```

`Pooshit.Json` already ships `Json.WriteAsync(object, Stream, JsonOptions)` — confirmed by reflecting the 0.3.40-preview assembly, alongside `Write(object, Stream, …)`. **Nothing needs to be built in the JSON library.**

---

## 2. Scope

**In:** what `JsonEncoder.Encode` returns.

**Out, explicitly:**

| | Not done here |
|---|---|
| `JsonDecoder` | the same double-allocation may exist on the **response** side. #10071 names it as uninvestigated and out of scope; it stays out, and §10.3 says why it is not a free rider. |
| `IResponseEncoder` | the seam is unchanged. It already takes the object and returns `HttpContent`, which is exactly what this needs (§4.1). |
| `HttpService.cs` | untouched. The change is entirely inside `Encodings/`. |
| the caller's own `Stream` body strategy | a caller who passes a `Stream` still gets `StreamContent`, still non-replayable. Their choice, unchanged. |

**No public API change.** No new option, no new type in the public surface beyond the content class itself.

### 2.1 The outcome that must be true when this ships

> A caller posting a document larger than memory can hold twice does not crash, and a caller posting a small one sees no change on the wire.

§8 states what would break each half.

---

## 3. What was measured

All at `1457df8`, .NET 8, against the real `Pooshit.Json` 0.3.40-preview.

### 3.1 Allocation — and the metric the task's figure is in

**The first thing I measured was wrong, and it is worth recording which.** Measuring *total bytes allocated* (`GC.GetTotalAllocatedBytes`) showed only a ~2× improvement, because it counts every short-lived gen0 byte the serialiser produces either way. **The defect is not total allocation — it is one contiguous allocation**, which is why the stack trace ends in `StringBuilder.ToString()`. The right metric is **peak managed heap**, and under it the task's figure reproduces.

Sampled peak heap growth for one request, body drained to a **non-retaining sink** (the task's own methodology — an earlier run buffered the sink into a `MemoryStream`, which inflated B and C equally and hid the difference):

| jobs | doc | **A** current | **B** buffer to bytes | **C** stream from object |
|---|---|---|---|---|
| 300 | 2.3 MB | 11 M | 6 M | **0 M** |
| 770 | 5.9 MB | 11 M | 12 M | **1 M** |
| 2000 | 15.4 MB | **61 M** | 29 M | **2 M** |

**The shape matters more than any single row: A ≈ 4n, B ≈ 2n, C ≈ constant.** C's peak does not grow with the document. That is the property that removes the OOM rather than postponing it — B would fail on a document twice as large.

### 3.2 Byte-identity

`Json.WriteString` and `Json.WriteAsync` produce **identical bytes** — verified by comparing the two outputs directly (53,240 B each, `SequenceEqual` true). #10071 claimed this; it holds at this commit.

### 3.3 Replay — the brief's concern, falsified

A `StreamingJsonContent` holding the **object** and calling `Json.WriteAsync` in `SerializeToStreamAsync`, re-sent across five hops through a draining transport:

```
streaming JSON content, 5 hops: survived all
serialisations = 5   bytes per hop = 9290, 9290, 9290, 9290, 9290   identical: True
```

**Five sends, five serialisations, identical bytes.** A source-backed content is not merely as replayable as a buffered one — it is replayable *arbitrarily*, because it regenerates from the object graph rather than from a consumed stream.

**#14516 D2's non-replayable case is a `StreamContent` over a non-seekable stream**, where the bytes are gone after the first read. That is a genuinely different thing, and this change does not put JSON bodies into it. **The trade the brief asked me to design around is not there.**

### 3.4 The wire — where the cost actually is

Against a loopback `HttpListener`, reading the real request headers:

```
buffered    Content-Length=9290      Transfer-Encoding=<none>
streaming   Content-Length=<none>    Transfer-Encoding=chunked
```

**That is the whole cost of this change**, and it is a change to every JSON request if streaming is unconditional.

### 3.5 The hybrid, measured

Bounded buffer capped at the LOH threshold, overflow falling back to streaming:

| payload | doc | streamed? | on the wire |
|---|---|---|---|
| tiny | 59 B | no | `Content-Length: 59` |
| small | 4,810 B | no | `Content-Length: 4810` |
| at-cap | 96,990 B | **yes** | `chunked` |
| large | 12,132,790 B | **yes** | `chunked` |

Peak heap on the streaming branch stays flat — 0 M at a 1.2 MB document, 2 M at 7.7 MB. **And replay survives on both branches**, five hops each.

### 3.6 Inferred, not measured

- **Which servers reject a chunked request body.** Real — some gateways and pre-signed object-store `PUT` endpoints require `Content-Length` — but I have measured no specific consumer, and §8.1 refuses to claim otherwise.
- **.NET Framework.** #14607 measured that `HttpClient` disposes request content after a send there, so *nothing* replays. That is a platform property, independent of which strategy ships: today's `StringContent` and this design's content both fail on hop 2 in the same place, and both surface as `ObjectDisposedException`, which #14516 D3's net already catches (it derives from `InvalidOperationException`). **No regression, no improvement.** Inferred by reading #14607 plus the exception hierarchy, not re-run.

---

## 4. Decisions

### D1 — the default encoder returns a content that serialises from the object

Not from a string, not from a pre-filled buffer. This is the shape #10071 suggests, the shape mamgo-backend already ships as a call-site workaround, and the shape §3.3 shows keeps replay.

### D2 — gated by size: buffer under the LOH threshold, stream above it

`TryComputeLength` attempts a bounded serialisation into a pooled buffer of **85,000 bytes**. Fits → report the length and write the buffer; overflows → discard it and stream.

**Why a threshold at all, rather than always streaming.** Always-streaming changes the wire for every JSON request this library has ever sent, including a twelve-byte body, to fix a failure that needs a multi-megabyte one. That is a blast radius wildly out of proportion to the defect, and §8.1 says plainly that I cannot measure who depends on `Content-Length`.

**Why 85,000 bytes, and why that is not a magic number.** It is the .NET large-object-heap threshold — the exact size at which an allocation stops being a cheap gen0 object and becomes an LOH object that fragments and is collected only on a full GC. **The defect is an LOH allocation**, so the threshold is the boundary of the defect, not a tuning guess. Below it, the current code was never going to cause the reported failure; above it, it eventually does.

**Cost of the gate, named:** a document that overflows is serialised **twice** — once into the discarded buffer, once to the wire. That is CPU on exactly the payloads that are already large. It is affordable because the alternative it buys is not crashing, and because re-serialisation is measured to work (§3.3). A caller who wants to skip it supplies their own encoder through the seam that already exists.

### D3 — no option, no knob

#1136 §3 wants a named operator, an environment difference or a secret. **None applies**, and the escape hatch is not hypothetical: `HttpOptions.Encoder` already lets any caller replace this entirely — that is precisely how mamgo-backend shipped its workaround without a library change. **The extension point that makes the knob unnecessary is the one this defect was already worked around through.**

### D4 — `JsonEncoder` keeps its name, its constructors and its interface

The change is behavioural and internal. `IResponseEncoder.Encode(object) → HttpContent` is unchanged and needs no change: it hands the encoder the **object**, which is exactly the source a streaming content needs. Had the seam taken a serialised string, this design would have required an API change; it does not, and that is worth noticing rather than assuming.

---

## 5. Consequences

### 5.1 Who is affected

| Caller shape | Change |
|---|---|
| JSON body **under 85 KB** | **none** — same bytes, same `Content-Length` |
| JSON body **at or above 85 KB** | body goes out `chunked`; peak memory stops scaling with payload |
| JSON body large enough to OOM today | **fixed** |
| A custom `IResponseEncoder` | **none** — the seam is untouched |
| A `Stream`, `HttpContent`, form or multipart body | **none** — different strategies, unchanged |
| A 307/308 hop carrying a JSON body | **none** — replay preserved on both branches (§3.3, §3.5) |

### 5.2 The one thing that genuinely changes semantics

**On the streaming branch the body is serialised at send time, not at encode time.** If the caller mutates the object between building the request and the hop re-sending it, the two hops carry **different bytes**. Today's `StringContent` freezes the document when `Encode` is called.

This is narrow — it needs a large payload, a redirect, and a mutation in between — but it is a real behavioural difference and it is **not** detectable by any test that does not mutate. §6 row 7 pins the honest version of it: that the hops are identical when the object is not touched.

### 5.3 What this does not fix

`JsonDecoder` on the response side may have the same shape. **Out of scope and not folded in** (§2) — but worth stating that it is *not* symmetric: a response body arrives as a stream the library already controls, so the fix there is a different one, and #10071 explicitly left it uninvestigated.

---

## 6. Coverage

`Http.Tests/` — a new fixture, `JsonEncoderTests.cs`, because this is encoder behaviour and does not belong in the redirect or service fixtures. Each row names the **guard**; the fourth column is what makes it go red **uniquely**.

| # | Guard | Shape | Goes red uniquely when |
|---|---|---|---|
| 1 | `Encode_SmallBody_ReportsContentLength` | an object serialising well under the cap; assert `Headers.ContentLength` is non-null and equals the byte count | the gate is removed and everything streams — **the row that protects every existing caller** |
| 2 | `Encode_SmallBody_BytesMatchWriteString` | same object; assert the content's bytes equal `Json.WriteString`'s UTF-8 | the encoder changed what it emits, not just how |
| 3 | `Encode_LargeBody_ReportsNoContentLength` | an object over the cap; assert `Headers.ContentLength` is null | the gate never trips, so the OOM path is still materialising |
| 4 | `Encode_LargeBody_BytesMatchWriteString` | same; drain the content and compare to `Json.WriteString`'s UTF-8 | the streaming branch emits different JSON from the buffered one — the two branches diverging is the defect this design most risks |
| 5 | `Encode_LargeBody_DoesNotMaterialiseTheDocument` | serialise a large object through the content and assert peak allocation stays below a multiple of the document size | **the guard for the actual defect.** Without it every other row passes on an implementation that still calls `WriteString` |
| 6 | `Encode_ContentTypeIsApplicationJson` | **dual** — both branches; assert the media type survives | the header was set on the old `StringContent` and dropped in the rewrite |
| 7 | `Encode_LargeBody_SurvivesFiveSends_WithIdenticalBytes` | send the same content instance five times through a draining handler; assert five equal byte counts | replay broke — the brief's concern, pinned even though §3.3 says it does not currently apply |
| 8 | `Post308_LargeJsonBody_HopRepeatsIdenticalBytes` | the redirect hop end-to-end with an over-cap JSON body; assert hop 0 and hop 1 bytes are identical | the interaction with #14516 D2 regressed at the integration level rather than in isolation |

Rows 1 and 6 are the duals: without them the suite is green for an implementation that streams everything and one that loses the content type.

**Row 5 is the load-bearing one and the hardest to write honestly.** A peak-allocation assertion is inherently noisy; it should assert a generous bound (the document size plus a fixed margin, not a tight multiple) so that it fails on *materialisation* and not on GC timing. **If it cannot be made stable, say so and drop it rather than weakening it into a test that passes for the wrong reason** — a flaky guard removed is better than a tight one relaxed until it is decoration.

---

## 7. Version

**Minor — `0.16.0-preview`.** The convention is *patch when behaviour is byte-identical for callers who opt into nothing, minor when it is not*. Callers under the cap are byte-identical; callers above it get a different framing on the wire. The literal test fails, so: minor.

The release note must name the cap, the framing change and the size at which it starts — a caller debugging a server that rejects chunked bodies needs to find that sentence.

---

## 8. Falsifiable claims (#9951 + #14516 §D7.2)

| Claim | Where | What would falsify it | Does that class exist? |
|---|---|---|---|
| "a source-backed content replays" | §3.3, §3.5 | a content whose second serialisation differs or throws | **Yes, one, and it is §5.2:** an object mutated between sends. Measured only for the unmutated case; the mutated case is a real behavioural change this design introduces and does not guard against. |
| "the task's ~4× reproduces" | §3.1 | the figure not reproducing at this commit | **No** — 61 M peak on a 15.4 MB document. **But it reproduces only as *peak*;** measured as total allocation it is ~2×, and my first attempt got that and would have understated the defect. |
| **discriminator:** "85,000 bytes is the boundary of the defect" | D2 | a materialisation that causes the reported failure below the LOH threshold, or an LOH allocation that does not | **Partially** — the *string* is UTF-16, so a document of ~42,500 UTF-8 bytes already produces an 85 KB string and lands on the LOH. **So the cap is generous by roughly 2× in the direction of still-buffering.** Named rather than tuned: moving it to ~42,500 would trip the gate for more callers, and the failure this fixes needs megabytes, not tens of kilobytes. |
| "no public API change is required" | D4 | a caller who cannot express this through `IResponseEncoder` | **No** — the seam takes the object and returns `HttpContent`; mamgo-backend already shipped exactly this design through it without a library change, which is the existence proof. |
| "byte-identical output" | §3.2 | an object graph where the two writers differ | **Not found**, but only one shape was compared. The falsifier class is any type whose serialisation depends on writer state — and rows 2 and 4 exist to keep checking it on whatever the suite happens to cover. |

### 8.1 The claim I am not making

**"No caller depends on `Content-Length` for JSON requests."** I cannot measure it — the consumers are a different repository — and it is load-bearing for the always-stream option that D2 rejects. **That is exactly why D2 gates rather than streams unconditionally:** the gate makes the claim unnecessary, because callers under 85 KB keep the header whether or not anyone depends on it.

---

## 9. Pre-Design Checklist (#1136 §5)

| Item | Verdict |
|---|---|
| No new type mirroring an existing one | **Pass** — one internal `HttpContent`; no parallel encoder, no mirror enum |
| No new abstraction with one implementation | **Pass** — `IResponseEncoder` already exists and is reused unchanged |
| Nothing justified by "we might need X later" | **Pass** — D3 rejects the knob; the threshold is justified by the LOH boundary, not by future tuning |
| No deprecation window / compat shim / feature flag | **Pass** — immediate; the escape hatch is the pre-existing `HttpOptions.Encoder` |
| DRY math on inline-vs-extract | **N/A** — one method's body changes |
| Existing systems first | **Pass, twice** — `Json.WriteAsync` already exists in Pooshit.Json (verified by reflection), and `HttpOptions.Encoder` is the escape hatch that makes D3's "no knob" affordable. mamgo-backend's workaround is proof both were sufficient |
| Every config knob has a named operator | **Pass by removal** — D3 |
| Magic numbers stay named constants with a reason | **Pass** — 85,000 is the LOH threshold, and §8 states where it is generous and by how much |
| Can-it-be-deleted / merged / inlined | **Pass** — ran on the gate (kept: §3.4 is the reason), on the knob (deleted), on the decoder change (deleted, §5.3) |
| Trade-offs named explicitly | **Pass** — D2 (double serialisation), §3.4 (the framing), §5.2 (mutation between hops), §7 (minor), §8 (the cap is 2× generous) |
| Out-of-scope listed explicitly | **Pass** — §2 |
| No multi-paragraph rationale for things that obviously stay | **Pass** |
| Predecessor design banner where superseded | **N/A** — #14516 is relied on; §3.3 measures that its D2 constraint does not bind this change |
| Coverage rows name the test identifier | **Pass** — §6, eight named guards, with row 5's stability limit stated rather than hidden |

---

## 10. Open questions

### 10.1 Should this ship at all? Yes — and the gate is what makes that easy

The brief puts it sharply: the failure needs a large payload and the fix may cost a capability on every payload. **The gate removes the second half.** Under 85 KB nothing changes — same bytes, same header, same framing. Above it, the caller was on a path that allocates four times the document and eventually throws.

Three reasons it earns shipping now:

1. **The failure is measured, in production, and ran 125 times over six months undetected.** It is not a theoretical allocation concern.
2. **The fix is already proven in the field** — mamgo-backend ships this exact shape through the public seam. This design moves it into the library so it is not reinvented per call site, which is #10071's stated reason for filing.
3. **The replay objection, which was the only architectural reason to hesitate, is measured false** (§3.3).

**What would change my answer:** a consumer that requires `Content-Length` on large JSON requests. That caller is *worse off* after this change — they move from a working request to a rejected one, or from a crash to a different failure. §8.1 refuses to claim they do not exist, and §10.2 is how someone would find out.

### 10.2 The measurement I could not make

In the consumers: does any endpoint receiving a >85 KB JSON body from this library reject `Transfer-Encoding: chunked`? The predicate must be **behavioural** — the server's actual response to a chunked request — not a grep for `Content-Length`, since the header is added by the transport rather than written by the caller.

### 10.3 Should the decoder side ride along?

**No**, and not only on scope. #10071 leaves it uninvestigated, and the shape is not symmetric: a response arrives as a stream the library already holds, so the remedy there is about *not* materialising something it already has, rather than about *not producing* something twice. Different change, different risks, its own task.

### 10.4 Is the cap in the right place?

§8's third row: the UTF-16 string crosses the LOH boundary at roughly half the byte count the cap tests. The cap is therefore generous by ~2× toward buffering. I have left it at the LOH threshold because it is the number with a reason attached, and because tightening it trips the framing change for more callers to prevent a failure that needs megabytes. **Worth a reviewer's disagreement**; it is the one number here I would change on argument rather than on measurement.

---

## 11. Implementation order

1. **The content type**, in `Encodings/`: holds the object and the options; `TryComputeLength` attempts the bounded serialisation; `SerializeToStreamAsync` writes the buffer or streams. Pool the buffer.
2. **`JsonEncoder.Encode` returns it**, and the `// TODO: WriteAsync Stream?` comment goes — it is the thing being done.
3. **Rows 1, 2, 3, 4, 6** — the cheap, stable guards.
4. **Row 5**, the allocation guard, with a generous bound. **If it will not hold still, drop it and say so in the PR** rather than tightening the suite around a flaky number.
5. **Rows 7 and 8** — replay, in isolation and through the hop.
6. **Bump to `0.16.0-preview`** and write the release note from §7, naming the cap and the framing change.
7. **Reconcile the map** — #8306 (`JsonEncoder` — its description is the defect), #8294, #8297's body-strategy list, and #8311 stage 2's fourth bullet, which describes the object path as "the configured encoder, or a freshly-constructed JSON encoder" and says nothing about materialisation. Concept nodes count (#3414).
