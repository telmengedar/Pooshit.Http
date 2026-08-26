# Architectural Document: The media-type fallback — decode what arrived, or say why you cannot

> **Repo path:** `docs/architecture/media-type-fallback.md` (repository `telmengedar/Pooshit.Http`).
> **DiVoid:** source task **#9615** · consumer-side consequence **#9598** · repo map root **#8292** · response-type dispatch model **#8312** · buffering vs streaming **#8313** · handle lifetime **#8311** · decoders **#8305** / **#8307** · project **#2281**.
> **Contracts cited as load-bearing:** Design Contracts **#1136** (§1 KISS/DRY/YAGNI, §2 existing systems first, §3 configurability, §4 less is better, §5 Pre-Design Checklist) and Code Contracts **#114 §0** (implementer-side principles + the bounce rule).
> **Baseline:** designed against `master` @ `cb50a50` (PR #7, cross-origin credential stripping, merged). Package version on master: `0.9.2-preview`.
> **Measurement worktree:** every figure in §2 was taken from a detached worktree at `cb50a50`, against the live Meta Graph API and a loopback listener. Nothing here is inferred from documentation or memory.

---

## 1. Problem Statement

### 1.1 The defect, quoted from #9615

> `HttpService.ReadResponse<T>` switches on `response.Content.Headers.ContentType?.MediaType`, handling `application/json`, `application/xml`, `text/xml` and `text/plain`. Anything else falls through to \[the content stream, force-cast to the requested type\].
>
> **That line can never succeed.** `typeof(T) == typeof(Stream)` is handled by an early return further up, so by the time control reaches the fallback, `T` is guaranteed *not* to be `Stream`. The cast throws `InvalidCastException` **unconditionally**, for every caller whose response carries an unrecognised media type.
>
> Not a rare edge case — a guaranteed failure waiting for a server to send a Content-Type outside those four.

### 1.2 One correction to that statement, measured

"Unconditionally" holds for every **domain type**, which is the whole point of the method, and it is what production hit. It is not literally true of every `T`. The fallback hands over a buffered stream, and three requested types can legally receive one: `object`, `MemoryStream` and `IDisposable` all succeed today (§2.3). That narrow tail is not a defence of the current code — it is the entire compatibility surface of this change, and it drives the version verdict in §7.

### 1.3 Why this is urgent rather than merely wrong

Meta's Graph API, `POST /v24.0/act_<id>/campaigns` (#9598). The request **succeeded** — the advertising campaign was created and its id was in the body. The read threw, so the id was never persisted; the caller then treats the campaign as not-yet-created and creates another on the next scheduled run. **A parse bug became a duplicate-resource bug on a paid advertising platform.** `facebookservice` cannot work around it: it controls neither Meta's `Content-Type` nor anything between `HandleResponse` and the cast. The fix belongs in the library.

### 1.4 Success criteria

| # | Criterion | How it is observed |
|---|---|---|
| S1 | A JSON body under a media type outside the recognised set decodes to the requested type. | The exact production call shape (§2.2) returns a decoded object rather than throwing. |
| S2 | A JSON body under a `+json` structured-suffix type, `text/json`, or a differently-cased canonical type decodes. | Loopback cases, all four spellings. |
| S3 | A JSON body under **no** `Content-Type` decodes. | Loopback case, header suppressed. |
| S4 | A genuinely undecodable body produces an exception naming the URL, the received media type and the requested type — never `InvalidCastException`. | Loopback case, non-JSON body. |
| S5 | The force-cast in the fallback is unreachable from every one of the above. | No `InvalidCastException` originates inside `ReadResponse<T>` in any S1–S4 case. |
| S6 | Every response whose media type already matched a branch behaves byte-identically. | Existing test suite unchanged and green. |

---

## 2. What was actually measured

#9615 flags its own hypothesis — *"Facebook's Graph API has historically answered with `text/javascript`"* — as **to verify rather than assume**. It was verified, and the verification found the mechanism, which is more useful than the hypothesis was.

### 2.1 Meta content-negotiates on `Accept`; .NET sends no `Accept`

| Probe | Status | `Content-Type` |
|---|---|---|
| `GET /v24.0/4/picture?redirect=false`, `Accept: */*` | 200 | `application/json; charset=UTF-8` |
| `GET /v24.0/4/picture?redirect=false`, **no `Accept` header** | 200 | **`text/javascript; charset=UTF-8`** |
| `POST /v24.0/act_1242186380824213/campaigns`, `Accept: */*` | 400 | `application/json; charset=UTF-8` |
| `POST /v24.0/act_1242186380824213/campaigns`, **no `Accept` header** | 400 | **`text/javascript; charset=UTF-8`** |
| any endpoint with a `callback` parameter (JSONP) | 200 | `text/javascript; charset=UTF-8` |

And the two facts that close the loop:

- **.NET `HttpClient` sends no `Accept` header by default.** Measured directly: the default request headers carry an empty accept collection.
- **`Pooshit.Http` sets no `Accept` header anywhere.** Grep across the library at `cb50a50` returns nothing.

Therefore **every** Graph API call made through this library receives `text/javascript` and lands in the fallback. Not intermittent, not endpoint-specific — the default behaviour of the pairing.

That is also why the hypothesis resisted casual confirmation: every hand-rolled `curl` reproduction sends `Accept: */*` and gets `application/json` back, which looks like the theory is wrong. The discriminating variable was the header the library does not send.

### 2.2 The production failure, reproduced end-to-end

Driving the real `HttpService` from `cb50a50` against the real Graph API, requesting a domain type:

```
[1] request Accept header sent: '' (empty = none)
[1] status=200 Content-Type='text/javascript; charset=UTF-8' MediaType='text/javascript'
[1] body starts: {"data":{"height":50,"is_silhouette":tru
[2] THREW System.InvalidCastException: Unable to cast object of type 'System.IO.MemoryStream' to type 'Dto'.
```

A 200, a JSON body, and the production stack's exception verbatim.

Loopback controls, same body `{"Id":"42"}`, requested as a domain type:

| Declared media type | Result at `cb50a50` |
|---|---|
| `application/json` | decodes |
| `text/javascript` | `InvalidCastException` |
| `application/hal+json` | `InvalidCastException` |
| `text/json` | `InvalidCastException` |
| `Application/JSON` (casing) | `InvalidCastException` |
| *(header absent)* | `InvalidCastException` |

Case sensitivity is a real member of the failure class, not a theoretical one — `MediaType` returns the server's own casing.

### 2.3 What the fallback currently lets through

| Requested type | Result at `cb50a50`, media type `text/javascript` |
|---|---|
| `Get<Dto>` | `InvalidCastException` |
| `Get<object>` | **succeeds**, returns a `MemoryStream` |
| `Get<MemoryStream>` | **succeeds** |
| `Get<IDisposable>` | **succeeds** |
| `Get<Stream>` | succeeds — but via the early return, never reaching the fallback |

For contrast: `Get<object>` on `application/json` returns a `Dictionary<string, object>`. The library already disagrees with itself about what `object` means, depending on a header. §6.4 decides what to do about that.

### 2.4 The body can be read exactly once — the design's hardest constraint

A sniff consumes bytes. Whether it can be undone was measured under both completion options (#8313), on a body of declared length 11:

| Completion option | Stream type | Seekable | Second read of the stream | String read after the stream was drained |
|---|---|---|---|---|
| `ResponseContentRead` (default, buffering) | `MemoryStream` | yes | same instance, positioned at end, yields empty | works — returns the body |
| `ResponseHeadersRead` (streaming opt-in) | `ContentLengthReadStream` | **no** | same instance, yields empty | **throws** `InvalidOperationException: The stream was already consumed. It cannot be read again.` |

Two consequences, both load-bearing:

1. **Peek-and-rewind is not viable.** It works under buffering and is impossible under the streaming opt-in. A design that peeks would be correct in tests and broken for exactly the callers who adopted #8313's opt-in.
2. **The content handle is single-use in both modes.** The stream accessor returns the *same* instance every time; the decoder, which takes the response and reads the stream itself (#8305), gets nothing if the sniff got there first.

The design therefore materialises rather than peeks (§5.2), and carries an explicit invariant about what the decoder receives (§9 R2).

---

## 3. Scope & Non-Scope

**In scope**

- The fallback branch of the media-type dispatch — branch 9 of #8312 — and the exception it throws in place of the force-cast.
- Widening the **JSON-family** selection condition so `+json` suffix types, `text/json` and casing variants are recognised from the header without a sniff.
- Disposal on the paths this change creates.

**Explicitly out of scope — filed as linked tasks, not folded in**

| Item | Why separate | Disposition |
|---|---|---|
| The exact-string media-type gap as a **general dispatch redesign** (a media-type registry, `+xml` suffixes, parameterised matching) | This change fixes one family and the unknown-header class. Generalising the matcher redesigns the dispatch itself, recorded in #8312. | Task filed |
| The `text/plain` and XML branches' **force-cast hazard** (#8312 branches 7–8) — same `InvalidCastException` shape | Those branches act on an *explicit, recognised* header. Repairing them means deciding whether the library overrides a server that stated its type clearly — a different posture question from "the header told us nothing usable". Mixing the two would make the posture argument in §6.1 unfalsifiable. | Task filed |
| The **header-escaping** defect and the follower's **redirect limits** | Named out of scope by the brief. | Pre-existing tasks |
| The **zero-length** guard returning `default` with no signal | Decided deliberately in §8. | Task filed |
| Sending a default `Accept` header from the library | Surfaced in §6.5. It is the other true root cause, and it is not this change. | Task filed |

---

## 4. Assumptions & Constraints

| # | Statement | Confidence |
|---|---|---|
| A1 | `IResponseDecoder` is public surface on a published NuGet package. Its shape — decode from a *response*, not a stream — cannot change in this work. | Verified in source |
| A2 | The fallback is reached only for requested types that are neither the raw message, nor `Stream`, nor `string`, nor `byte[]` — all four return earlier. | Verified in source |
| A3 | Callers reaching the fallback are asking for a **materialising** result, so reading the whole body there is consistent with the contract #8313 already states for materialising types. | Verified against #8313 |
| A4 | `text/javascript` is the observed trigger, but the design must not depend on that string appearing anywhere. | Verified §2.1 |
| A5 | No caller depends on `InvalidCastException` escaping `ReadResponse<T>`. | Assumed; an unconditional defect cannot be a contract |
| A6 | The library targets `netstandard2.0` and `net8.0`; the test project has a `net8.0` leg only (per #8313). Behaviour asserted here is asserted on `net8.0`. | Verified in csproj |

---

## 5. Architectural Overview

### 5.1 The shape of the change

The dispatch keeps its structure. Two of its nine branches change.

```
ReadResponse<T>
  |- raw response message ---------> return, no disposal          unchanged
  |- declared length == 0 ---------> dispose, return default      unchanged (see 8)
  |- Stream -----------------------> return, no disposal          unchanged
  |- string / byte[] --------------> dispose, return              unchanged
  |
  |- media type IS JSON FAMILY ----> decode via decoder           <== WIDENED
  |     canonical | text/json | any subtype with a +json suffix | any casing
  |- media type is XML ------------> XDocument                    unchanged
  |- media type is plain text -----> string                       unchanged
  |
  '- everything else, incl. absent > +---------------------------+
                                     |  materialise the body     |  <== REPLACED
                                     |  first meaningful char?   |
                                     |    '{' or '[' -> decode   |
                                     |    otherwise  -> throw    |
                                     +---------------------------+
```

The blind force-cast is deleted. It has no replacement — nothing in the new fallback casts a stream to a caller's type.

### 5.2 The fallback's three-step resolution

1. **Materialise the body once.** The fallback is a materialising path already (A3), and §2.4 says single-read is the only shape that works under both completion options. Materialising first removes the peek/rewind question rather than answering it.
2. **Decide from the first meaningful character** — skipping leading whitespace *and a UTF-8 byte-order mark*. .NET servers emit a BOM routinely, and a BOM ahead of `{` would defeat a naive first-character test. `{` or `[` means JSON; the body goes to the decoder.
3. **Otherwise, refuse loudly.** Throw the library's own `HttpServiceException`, naming the request URL, the received media type (or an explicit marker for "none"), and the requested type. The materialised body travels on the exception's `Body` property, which exists for exactly this (#2279).

The sniff alphabet is deliberately **only** `{` and `[`. A bare JSON scalar is legal JSON, but those tokens are also legal plain text, and widening the alphabet trades a rare success for a class of confident mis-decodes. A scalar body under an unrecognised header produces the descriptive exception, which names the media type and the requested type and is diagnosable in a minute. A stated limit, not an omission.

### 5.3 Where the exception message stops, and why

The message names URL, media type, requested type. It does **not** inline the body; the body goes on `Body`. Two reasons: response bodies carry credentials and personal data often enough that widening what lands in a log line is a real cost (the header-redaction work exists for this reason), and `Body` is already the established carrier. `CheckHttpResponse` does inline the body for error statuses — a different precedent for a different case, where the response is an error document and the caller has nothing else. This design does not extend it.

---

## 6. Trade-offs, argued

### 6.1 Sniffing is a posture change. It is the right one, and smaller than it looks.

The library's dispatch is header-driven. Sniffing means the library decides it knows better than the server. That deserves the challenge it got.

The mitigation is **ordering**. The sniff runs only after every declared media type has failed to match — that is, only when the server has told us nothing we can use. For every response whose header names a type the library recognises, nothing changes; the header stays authoritative where it is informative. The library is not second-guessing servers, it is refusing to give up when a server has said nothing useful.

And the alternative is worse than a posture change: today the library *also* ignores the header in that case, it just does so by handing back a stream and casting it blindly. There is no header-respecting behaviour being given up. There is a guaranteed exception being replaced.

**The rejected alternative — widen the switch case by case.** Adding `text/javascript` fixes Meta and leaves the next vendor to rediscover this. The failure class is "the header does not describe the body"; §2.2 shows six distinct spellings of it that a single vendor string does not address. #9615 names this explicitly and is correct.

### 6.2 JSON is sniffed, XML is not. The asymmetry is justified, not lazy.

Three reasons, in order of weight:

1. **`<` is ambiguous where `{` is not.** The most common non-XML thing an HTTP API returns starting with `<` is **HTML** — a proxy error page, a WAF block, a captive portal, a load balancer's 502 body. Sniffing `<` into an XML document turns "your gateway returned an HTML error page" into an XML parse error at line 3, or worse, into a successfully loaded document of nonsense. `{` and `[` have no comparable impostor in HTTP response bodies.
2. **The XML branch is not pluggable.** It consults no decoder and force-casts its result, so a sniffed XML success would help only callers who asked for `XDocument` — a far narrower population than "asked for a domain type". The JSON branch is the one that serves the actual use case.
3. **The evidence points at JSON.** JSON is where mislabelling is epidemic, for a traceable reason: `text/javascript` is a JSONP-era artifact, and JSONP is a JSON convention. There is no equivalent legacy pushing XML APIs to lie about their type.

If an XML sniff is ever wanted, it arrives with a real case attached. Building it now is YAGNI (#1136 §1).

### 6.3 The fallback forfeits streaming. This costs nothing.

Under `ResponseHeadersRead`, the new fallback reads the whole body to decide. #8313 states the contract already: *"materialising result types are unaffected — asking for a string, a byte array, or a decoded domain object reads the whole body either way."* The fallback is reachable only for those types (A2). No caller loses a passthrough they had; the `Stream` and raw-message paths, which are the passthrough paths, return before this point.

There is a *gain* here. Branch 9 disposes nothing today, so its success path (the §2.3 tail) leaves an undisposed response — under the streaming opt-in, an open connection. The new fallback disposes on the success path, in line with every other materialising branch.

**Disposal on the throw path: it does not dispose.** That matches `CheckHttpResponse`, which throws without disposing, and matches the void verbs, which dispose strictly after validation *"so the throw path never reaches it and the exception's response is not disposed out from under a caller inspecting it"* (#8312). This design follows the existing convention rather than inventing a second one — and it must not repeat the live bug #8312 records on branch 6, where the exception is constructed inside the disposing scope and arrives at the catch site holding a dead response.

### 6.4 The `object` / `MemoryStream` / `IDisposable` tail: let it change.

§2.3 measured three requested types that succeed on the fallback today and would behave differently after. A guard could preserve them — hand the stream over when, and only when, the requested type can actually receive it. It is one predicate. It was considered and **rejected**. #1136 §4's four-step exercise:

1. **The downside, concretely.** A caller writing `Get<object>` (or `<MemoryStream>`, `<IDisposable>`) against an endpoint with an unrecognised media type receives a decoded value instead of an undisposed `MemoryStream` — or a descriptive exception where the body is not JSON.
2. **Probability and cost.** Low probability: the types are exotic, and the change only manifests outside the four recognised media types. Where it manifests, the new behaviour is almost certainly what the caller wanted — and where it is not, the failure is a cast at *their* call site, which is the failure they already have today, relocated.
3. **The present cost of the guard.** One predicate, one concept in the doc, one test — and permanence. Ship the guard and the incoherence measured in §2.3 (`object` decodes on `application/json`, streams on `application/hal+json`) becomes the compatible behaviour and can never be removed.
4. **The call.** Drop the guard. The package is `0.9.x-preview`; pre-1.0 preview is exactly where an incoherence is removed rather than frozen. The simpler design also wins on #1136 §4's own can-it-be-deleted test.

This is the only reason the version bump is a minor rather than a patch (§7). An acceptable price for not carrying a wart to 1.0.

### 6.5 The other root cause — surfaced, not designed around

§2.1 shows the library sends no `Accept` header, and that this is *why* Meta answers `text/javascript`. Sending `Accept: application/json` by default would make Meta return `application/json` and this bug would not have fired.

**It is not the fix, and it is not folded in.** Two reasons. It changes what servers return for **every existing caller** — content negotiation is exactly what `Accept` is for, so the blast radius is every endpoint that varies on it, and the change is invisible until a body comes back different. That is a far larger compatibility event than this one, and in the hazardous direction: previously-succeeding calls returning different content. And it does not fix the class — a server that ignores `Accept`, a vendor `+json` type, an absent header and a JSONP callback all still land in the fallback.

Filed as its own task so it gets its own decision, its own release note and its own bump. Flagging per the brief: this is the point where the narrow ask and the root cause diverge, and the divergence is deliberate.

### 6.6 The decoder question

The fallback uses the **configured** decoder when one was supplied, falling back to the JSON decoder otherwise — identical to the canonical-JSON branch. A caller who supplied a decoder supplied it to be used; giving the sniff path its own hard-wired decoder would mean two decode policies in one library (#1136 §1, DRY). A decode failure wraps into `HttpServiceException` exactly as the canonical branch already does.

---

## 7. Compatibility verdict and version

**Verdict: source- and binary-compatible. Behaviourally compatible for every caller whose responses carry a recognised media type. One narrow behaviour change, in the safe direction, plus one exotic tail.**

| Population | Before | After |
|---|---|---|
| Media type is one of the four recognised | unchanged | unchanged |
| Media type is JSON-family (`+json`, `text/json`, cased) — domain type requested | `InvalidCastException` | decodes |
| Media type unrecognised or absent, JSON body — domain type requested | `InvalidCastException` | decodes |
| Media type unrecognised or absent, non-JSON body — domain type requested | `InvalidCastException` | `HttpServiceException`, descriptive |
| Media type unrecognised or absent — `object` / `MemoryStream` / `IDisposable` requested | undisposed `MemoryStream` | decoded value, or descriptive exception |
| Public API surface | — | unchanged; no type, member or signature added or altered |

**The convention, and why this change does not simply pattern-match it.** The repo's convention across four releases is *patch when behaviour is byte-identical for callers who opt into nothing, minor when not*. That convention was written for the three preceding changes, all of which altered paths that **previously succeeded** — a redirect that used to return 200 now returning 401 is silent, caller-visible, and can produce a wrong answer rather than a loud one. The minor bump there buys the release note the caller must read.

This change is mostly the opposite shape: previously-**throwing** paths now succeed. Nobody builds on an unconditional exception (A5), so the headline carries none of the hazard the convention exists to flag. Read on spirit alone, the headline is patch-shaped.

But the §2.3 tail is a genuine previously-succeeding path that changes, and §6.4 chooses to let it. By the convention's letter that is a minor, and the letter should win — the tail is small but it is exactly the kind of thing a caller deserves to be told about, and downgrading to a patch would be the convention bending to the change rather than the reverse.

**Bump: `0.9.2-preview` → `0.10.0-preview`.**

The release note must carry both halves, in this order: the headline is that unrecognised and JSON-family media types now decode instead of throwing; the caveat is that a request for `object`, `MemoryStream` or `IDisposable` against an unrecognised media type no longer yields a raw stream. A caller who genuinely wants the undecoded body should ask for `Stream` or `byte[]` — both unaffected, and always the right way to ask.

---

## 8. The adjacent zero-length finding — its own task

#9615 raises it and says *"worth deciding deliberately"*. **Decided: it is a separate change, and this design leaves the guard alone.** Three reasons, and the third settles it:

1. **Different failure class.** This change is about *having a body and refusing to read it*. The zero-length guard is about *having no body and inventing a value*. They share a method and nothing else; bundling them would put two unrelated arguments behind one release note (brief §3, one feature per PR).
2. **Opposite compatibility direction.** Fixing the guard changes paths that currently **succeed** — every caller relying on `default` for a `204` would start seeing an exception unless the design carefully separates "no content by status" from "no content by fault". That is the hazardous direction, and folding it in would drag an urgent, safe fix into a risky release.
3. **It cannot be designed alone.** #8313 records that an unfollowed redirect legitimately reaches this guard with an empty body, and that redirect statuses fall inside the accepted status band. Any rule distinguishing "legitimately empty" from "faulted empty" has to decide what a 3xx means first. That is redirect design, not media-type design.

The task is filed with the guard's full shape recorded, including #9615's point that the declared length is absent on a chunked response, so the two empty cases already diverge.

---

## 9. Risks & Mitigations

| # | Risk | Mitigation |
|---|---|---|
| R1 | A non-JSON body that happens to start with `{` or `[` is fed to the decoder. | The decoder fails and the failure wraps into `HttpServiceException` naming the URL and media type — the same descriptive outcome as the refuse path. No silent mis-decode, and the alphabet is narrow (§5.2). |
| R2 | The sniff drains the body and the decoder receives nothing — silently under buffering, loudly under the streaming opt-in. | The measured hazard of §2.4, stated here as an invariant: **the decoder must receive a content handle that has not been drained.** Pinned by an explicit test under `ResponseHeadersRead`, not only the default. |
| R3 | The exception is constructed inside a disposing scope and reaches the catch site holding a dead response — the live bug #8312 records on branch 6. | §6.3: the throw path does not dispose, matching `CheckHttpResponse`. `Body` is populated from the already-materialised text regardless. |
| R4 | The JSON-family predicate is written too loosely and claims types it should not (`application/javascript`, `text/html`). | The predicate is exactly three shapes — canonical, `text/json`, and a subtype carrying a `+json` suffix — compared case-insensitively. Nothing matches by substring. `text/javascript` is deliberately **not** in the family; it reaches the sniff, which is the general mechanism rather than a vendor special case. |
| R5 | The fallback now buffers under the streaming opt-in, surprising a caller who adopted #8313. | Cannot reach a passthrough caller: the fallback is unreachable for `Stream` and the raw message (A2), the only passthrough types. |
| R6 | `netstandard2.0` behaves differently from `net8.0` in stream or BOM handling. | Acknowledged limit (A6): the test project has no `netstandard2.0` leg, matching the precedent set by #8313. Called out rather than papered over. |

---

## 10. Verification

Per #9615, by measurement rather than by reasoning about media types. Drive a real `HttpService` against a loopback listener.

**Decode expected** — JSON body, domain type requested:

| Case | Declared media type |
|---|---|
| V1 | `text/javascript` — the production trigger |
| V2 | `application/hal+json` — RFC 6839 suffix |
| V3 | `application/vnd.acme.thing+json` — vendor suffix |
| V4 | `text/json` |
| V5 | `Application/JSON` — casing |
| V6 | *(no `Content-Type` header at all)* |
| V7 | `text/javascript`, body prefixed with a UTF-8 BOM |
| V8 | `text/javascript`, body prefixed with leading whitespace and newlines |
| V9 | `text/javascript`, JSON **array** body, requested as a collection type |

**Descriptive exception expected:**

| Case | Shape |
|---|---|
| V10 | `text/javascript`, body is not JSON. Assert `HttpServiceException`; assert the message contains the URL, the media type and the requested type name; assert `Body` carries the raw text. |
| V11 | `application/octet-stream`, binary body. Same assertions, media type reported as received. |
| V12 | No `Content-Type`, non-JSON body. Assert the message reports the absent media type explicitly rather than rendering an empty string. |

**Invariants:**

| Case | Shape |
|---|---|
| V13 | **No `InvalidCastException` originates inside `ReadResponse<T>` in any of V1–V12.** This is S5 and the point of the change. |
| V14 | V1 and V10 repeated with `HttpOptions.CompletionOption` set to headers-read. R2's pin, and the case a buffering-only suite would pass while broken. |
| V15 | A caller-supplied decoder is invoked on the sniff path (§6.6), asserted with a counting decoder. |
| V16 | The response is disposed on the fallback's decode path and **not** disposed on its throw path — the existing disposal test file already has the shape for this. |
| V17 | The full existing suite is green and unedited. This is S6; any edit to an existing test is a signal the change reached further than designed and should bounce. |

**Live confirmation, outside the suite:** the exact §2.2 probe — real `HttpService`, real Graph API, domain type — must return a decoded object. It was the reproduction; it is the proof.

---

## 11. Implementation Guidance

Ordered. One PR — a single defect in a single method (brief §3, one feature per PR).

1. **Pin the defect first.** Add V1 and V10 as failing tests before altering `ReadResponse<T>`. They must fail with `InvalidCastException`, reproducing production.
2. **Widen the JSON-family selection.** Replace the canonical-JSON branch's exact-string condition with the three-shape, case-insensitive family predicate of R4. V2–V5 turn green. Nothing else moves.
3. **Replace the fallback.** Materialise, inspect the first meaningful character past BOM and whitespace, decode or throw. Delete the force-cast; do not leave it as an unreachable else. V1, V6–V9 turn green.
4. **Satisfy R2 explicitly.** The decoder must receive an undrained content handle, without altering `IResponseDecoder` (A1). Verify under **both** completion options before believing it — §2.4 is the measurement that says the default option will lie to you here.
5. **Get the exception right.** URL, media type (with an explicit marker when absent) and requested type name in the message; raw text on `Body`; no disposal on the throw path (§6.3, R3). V10–V12 and V16 turn green.
6. **Run V13–V17.** V14 is the one most likely to be skipped and most likely to catch a real defect.
7. **Version and release note.** `0.10.0-preview`, note per §7 carrying both halves.
8. **Commit this document** to `docs/architecture/media-type-fallback.md` on the same branch.

**Do not:** add an `Accept` header (§6.5); alter the zero-length guard (§8); alter the plain-text or XML branches (§3); add a configuration knob to enable or disable sniffing (#1136 §3 — no operator will tune it and no environment differs); or introduce a media-type abstraction, registry or interface (#1136 §2 — this is one predicate on one branch).

---

## 12. Pre-Design Checklist (#1136 §5)

**KISS / DRY / YAGNI**
- [x] No new type mirroring an existing one — no type is added at all.
- [x] No new abstraction with one implementation — no media-type strategy, registry or interface; the sniff is a condition on one branch.
- [x] Nothing justified by "we might need X later" — the XML sniff was considered and dropped (§6.2); the `Accept` header was considered and filed (§6.5).
- [x] No deprecation period, feature flag, compatibility shim or transition window. The §6.4 guard was the candidate shim and was rejected on the §4 exercise.
- [x] No inline-at-N-sites decision — the change affects one branch of one method, so the `block_size × site_count` math does not arise.

**Existing systems first**
- [x] Existing surfaces audited and reused: `HttpServiceException` and its `Body` for the failure, `IResponseDecoder` and the configured decoder for the success, the existing disposal convention for both.
- [x] No new layer proposed.
- [x] No new persisted data.

**Configurability**
- [x] No new knob. Sniffing is unconditional in the fallback: no operator would disable it and no environment wants the fallback to keep throwing.
- [x] No magic numbers introduced — the sniff alphabet is two characters, named and argued in §5.2, not tuned.

**Less is better**
- [x] Can-it-be-deleted run on every element; it deleted the §6.4 guard and the XML sniff.
- [x] Trade-offs named explicitly wherever the chosen design has a downside: §6.1 (posture), §6.2 (asymmetry), §6.3 (streaming forfeit), §6.4 (compatibility tail, with the four-step exercise), §6.5 (root cause not fixed).
- [x] Radical-clean shape chosen where the existing behaviour had no defensible consumer (§6.4).

**Document discipline**
- [x] Code Contracts (#114 §0) and Design Contracts (#1136) cited as load-bearing.
- [x] Out-of-scope items listed explicitly with dispositions (§3), not merely absent.
- [x] No multi-paragraph rationale for things that obviously stay.
- [x] Supersedes nothing; no predecessor doc needs a banner.
- [x] No code, pseudocode or signatures. The only verbatim excerpts are measurement output and the quoted defect statement.

**Not applicable:** data deliverables (no SQL, migration or backfill); reader and carrier-swap inventories (no field or symbol renamed).

---

## 13. Open Questions

Per #8727 these do not block. Each carries the recommendation taken.

| # | Question | Taken |
|---|---|---|
| Q1 | Should `text/javascript` join the JSON-family predicate directly, since §2.1 proves it is a JSON carrier in practice? | **No.** It is a vendor-shaped special case and #9615 rules it out by name. It reaches the sniff, which is the general mechanism, and the sniff handles it. Revisit only if the sniff proves insufficient for it, which the measurement says it will not. |
| Q2 | Should the descriptive exception be a new dedicated type rather than `HttpServiceException`? | **No.** A new public exception type on a published package is new surface for no gain; the existing type already carries `Response` and `Body`, and callers already catch it. |
| Q3 | Should the exception message include a truncated body prefix, given how much it would help diagnosis? | **No** — `Body` carries it in full (§5.3). Revisit if field diagnosis proves it needs to be in the log line. |
| Q4 | Is a `netstandard2.0` test leg worth adding, so R6 stops being an acknowledged gap? | **Not in this change.** Test-infrastructure work with its own scope; #8313 set the precedent of noting the gap. Worth filing separately if a divergence ever bites. |
