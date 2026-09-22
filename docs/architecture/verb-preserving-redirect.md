# Architectural Document: the verb-preserving redirect hop (307 / 308)

> **Repo path:** `docs/architecture/verb-preserving-redirect.md` (repository `telmengedar/Pooshit.Http`).
> **Node:** **#14516** — this document's own DiVoid node, maintained as a byte copy of this file.
> **DiVoid:** source task **#14513** · older sibling **#8323** · project **#2281** · repo map root **#8292** · `HttpService` **#8297** · `HttpOptions` **#8299** · request lifecycle **#8311** (stage 4.1 is the hop this document edits).
> **Predecessors, none superseded:** **#9618** (`send-options-contract.md`) · **#9633** (`redirect-credential-policy.md`) · **#9939** (`error-message-query-redaction.md`, and the false-universal incident **#9951** whose discipline §8 applies).
> **Contracts cited as load-bearing:** Design Contracts **#1136** (§1 KISS/DRY/YAGNI, §3 configurability is not free, §4 less is better, §5 checklist, §6 anti-patterns) · Code Contracts **#114** §0 · YAGNI **#1184**.
> **Baseline:** branch `fix/redirect-308` off `origin/master`, tree clean. Package version on the branch: `0.13.0-preview`.

---

## TL;DR

**What.** `308` and `307` are followed, as one rule, preserving the original method and the original request body. Today `308` matches no branch, passes the 200–399 success check and returns `default(T)`; `307` throws `NotSupportedException`. Both stop doing that.

**How.** The hop stops hard-coding `GET`/no-body and instead inherits `Method` and `Content` from the request that produced the redirect — **the same `HttpContent` instance, nothing buffered**. Measured on .NET 8: request content is not disposed after a send, and a second send of the same instance is byte-identical for every content shape this library builds except a stream that cannot seek (§5). `308` is named as a `const` cast from the integer, never `HttpStatusCode.PermanentRedirect`, which does not exist on `netstandard2.0` (D6, compiled — §5).

**The cost, where it is non-zero.** Three, all named and all loud:
1. A non-seekable stream body cannot be replayed. That call now throws `HttpServiceException` naming the status, the target and the reason — where today it returns `null`. Loud beats silent; it is still a capability gap, not a fix.
2. A cross-origin `308` carries the **body** to a host the remote server named. Credentials are still stripped (#9633 is untouched), but a secret inside a body now travels where it previously did not. This is what RFC 7538, browsers and `curl -L` all do, and the caller opted in.
3. `307` stops throwing `NotSupportedException`. Anyone catching that type catches nothing now.

**Strongest rejected alternative — buffer the request body so every `307`/`308` is replayable.** It closes cost 1 completely and it loses. Buffering must happen *before* hop 0, because after it the stream is gone; so every body-carrying request under `FollowRedirects` pays full body size in memory to insure against a redirect that almost never comes, and a streaming upload stops streaming. Paying an unbounded cost on every request to avoid a loud failure on a rare one is the wrong trade (#1136 §4). The runtime is the only authority on whether a body can be replayed, so the design asks it and translates its answer (D3) rather than pre-empting it.

**Not folded in:** #8316 (unfollowed 3xx is silent). §2.2 argues it is genuinely separable and says what would make that argument wrong.

---

## 1. Problem

Toni, to the operator, 2026-09-22:

> *"it seems (theory) that httpservice doesn't handle 308 as a redirect. check that out and fix if it is true."*

The theory is true. Re-measured on this branch, the four behaviours #14513 composed:

| # | `HttpService.cs` | behaviour |
|---|---|---|
| 1 | `:392` | the redirect branch matches `Moved` (301) / `Redirect` (302) / `RedirectMethod` (303) only |
| 2 | `:403` | `RedirectKeepVerb` (307) throws `NotSupportedException("307 redirect is not implemented yet")`; **308 appears nowhere in the library** and matches neither arm |
| 3 | `:286` | `CheckHttpResponse` throws only outside 200–399, so a 3xx is classified as success |
| 4 | `:297` | `ReadResponse<T>` returns `default` when `ContentLength == 0` |

A `308` with `Content-Length: 0` — the header IIS sets by default — therefore returns a typed `null` with no exception and no log. #14513 records how that presented: 15 production `NullReferenceException`s across three unrelated-looking call paths, several frames from the cause, with the real cause a provider host migration.

### 1.1 Why the existing hop is not the answer

The obvious fix is to add `308` to the list at `:392`. It is wrong, and the brief, #14513 and #8323 item 4 all say so independently.

`CreateRedirectRequest` (`:115`) builds `new(HttpMethod.Get, url)` unconditionally and attaches no content. For 301/302/303 that matches what browsers do and is pinned by `PostWithBody_FollowedRedirect_HopIsIssuedAsGetWithoutBody`. For 307/308 it inverts the status's entire meaning: RFC 7231 §6.4.7 and RFC 7538 §3 define them as the redirects that **do not** permit the method or body to change. A `308` answering a `POST /orders` that the library re-issued as `GET /orders` would return the order list and the caller would decode it as a creation result. That is a worse outage than the silent `null`, because it succeeds.

So the design question is not *"which list does 308 join"*. It is *"what does a hop that must preserve the request look like, and what happens when it cannot"*.

---

## 2. Scope

**In scope:** recognising `307` and `308` in `HandleResponse`, and the hop they require.

**Out of scope, unchanged, and not folded in:**

| Filed as | Not done here |
|---|---|
| **#8316** | 3xx passes `CheckHttpResponse` as success, so an **unfollowed** redirect is silent. See §2.2. |
| **#8323** items 1 + 3 | one hop only; no loop or hop-count guard. A `308` answering a `308` still terminates in the silent path, exactly as a `302` chain does today. |
| **#8323** item 2 | the 301/302/303 hop keeps forcing `GET`. §4's table states why that is deliberate here and not an oversight. |
| **#8318**, **#8319** | unrelated surfaces on the same file. |
| **#8311** stage 1 | `AllowAutoRedirect = false` on the default handler, and the WASM divergence. Untouched — redirect policy stays the library's. |

**Also not done:** no new option, no new public type, no new interface member, no policy knob. §6.2.

### 2.1 What must be true when this ships

From the brief, and it is the acceptance test for the whole document:

> a caller who has opted into redirect following and receives a 308 gets either the redirected result or a loud, diagnosable failure — never a silent `default(T)`.

Every decision below is checked against that sentence, and §8 states the input class that would break it.

### 2.2 Does #8316 have to land with this? No — and here is what would make that wrong

**The argument.** #8316 is the silence on the path where `FollowRedirects` is **off**. Under D1 every member of the redirect family — 301, 302, 303, 307, 308 — is either followed or raises `HttpServiceException` when `FollowRedirects` is **on**. The two sets do not intersect, so #8316 cannot leave a hole inside this design's scope. Further, #8316 is not a 308 problem: it is identical for the 301 that has worked since PR #2. Folding it in would change the behaviour of every caller who deliberately turns redirect following off to inspect a 3xx — a strictly larger blast radius than the bug being fixed, in a change nobody asked for it in (#1136 §1).

**What would falsify it.** A 308 that is silent *despite* `FollowRedirects = true`. Two candidate input classes, both real:

- **A 308 carrying no `Location`.** Measured: `new Uri(base, (string)null)` and `new Uri(base, "")` both return the **original URL** unchanged. So the hop would re-send to where it came from, get 308 again, and — one hop only — fall into the silent path. This class exists (a malformed or proxy-generated 308), and under D2 it would also repeat a side-effecting body. **D4 closes it**, which is why D4 is in this design rather than deferred to #8323.
- **A 308 chain of length two.** Hop 1 answers 308 again. That is #8323 item 1, it is identical for a 302 chain today, and it is not closed here. **This is a genuine residual of this design**, stated plainly: after this change, a two-hop permanent-redirect chain still returns `default(T)` under `FollowRedirects = true`. It is bounded — one hop is followed where zero were — and it is filed. I do not think it blocks shipping; if Toni disagrees, the remedy is #8323 item 1 + 3 together, not #8316.

So: #8316 is separable, and the part of it that was *not* separable (no `Location`) is folded in as D4 under its own reasoning.

---

## 3. Decisions

### D1 — 307 and 308 are one rule, and the rule is "preserve"

`307` and `308` are recognised together and handled identically: re-send to the resolved target with the **original method and the original body**.

**Why they are a pair, stated so a caller reading the docs is not surprised.** RFC 7238/7538 introduced 308 as the permanent counterpart of 307; the sole difference between them is **permanence** — whether a client may update a stored reference to the resource. `HttpService` stores no references: it has no cache, no cookie jar of URLs, no bookmark file, and no member that would expose "the URL moved permanently" to a caller. There is therefore **no behaviour available to this library that could differ between the two**. Shipping them apart would mean shipping the same code twice under two status numbers and documenting a distinction the library cannot act on.

The brief asked whether a split could be defensible. It could be, on one shape: a library that persists the resolved URL for reuse would treat 308 as authoritative and 307 as one-shot. This library does not, and building that in to justify a split would be a future-capability member with no present consumer (#1184, #1136 §1 YAGNI). If a caller ever needs to *observe* the permanence, the shape is a callback or a property on the response — a named possibility here in prose, not a member John materialises.

**`307` therefore stops throwing `NotSupportedException`.** See §6.1.

### D2 — the hop reuses the existing content instance; nothing is buffered

The hop's body is the **same `HttpContent` object** that hop 0 sent, taken from `response.RequestMessage.Content`.

This is the decision the whole design turns on, and §5 is the measurement that supports it. Summarised: on .NET 8, `HttpClient` does not dispose request content after a send, and re-attaching that instance to a fresh `HttpRequestMessage` reproduces the body **byte for byte** for `StringContent`, `ByteArrayContent`, `FormUrlEncodedContent`, `StreamContent` over a seekable stream, and `MultipartFormDataContent` whose parts are all of those. That covers four of the five body strategies in `CreateRequest` outright, and the fifth conditionally:

| `CreateRequest` strategy (`:163`–`:205`) | concrete content | replayable |
|---|---|---|
| URL-encoded form | `FormUrlEncodedContent` (a `ByteArrayContent`) | **yes** — buffered |
| multipart form | `MultipartFormDataContent` | **yes, if every part is** — a non-seekable stream part is the exception |
| ready-made content | caller's own `HttpContent` | **depends on the caller's object**; buffered shapes yes |
| stream | `StreamContent` | **yes iff the stream can seek** — `StreamContent` records its start position and rewinds |
| object encoder | `JsonEncoder` → `StringContent` (`JsonEncoder.cs:28`) | **yes** — buffered |

**Cost of D2: zero on the happy path.** No allocation, no copy, no memory ceiling, and a streaming upload still streams on hop 0. The content headers — `Content-Type`, `Content-Length`, `Content-Disposition`, the multipart boundary — ride along for free, because they live on the content object rather than on the request's header collection. That is also the reason `redirectExcludedHeaders` only ever needed to name `Expect` and `Transfer-Encoding` (D5).

**Cost of D2: the non-seekable stream.** It fails, deterministically, and D3 is what makes that failure usable.

### D3 — a request that cannot be faithfully replayed fails loudly, as `HttpServiceException`

Two conditions make a faithful replay impossible. Both raise `HttpServiceException`; neither ever downgrades the verb and neither ever reaches `ReadResponse<T>`.

| Condition | How it is known | Why it cannot be a silent downgrade |
|---|---|---|
| The response carries **no request message** | `response.RequestMessage` is null | There is no method and no body to preserve. Re-issuing as `GET` is precisely the silent downgrade §1.1 rejects. |
| The **content is already consumed** | the hop's send fails with an `InvalidOperationException` **anywhere in its inner-exception chain** | The runtime is the only authority on this; `StreamContent` does not expose its stream, so it cannot be asked in advance. |

**The second condition is detected by catching, not by predicting.** A pre-check would have to reason about a private field on `StreamContent`, recursively through multipart parts, and through a caller-supplied `HttpContent` of unknown type — a heuristic tower that would be wrong in both directions. Catching is one predicate and it is exact — provided it is asked of the whole chain rather than of the outermost type. This is not a guard for an impossible scenario (#1136 §6): §5 reproduces the failure three times, twice against the fixture and once against the real transport, and the exception text is measured — *"The stream was already consumed. It cannot be read again."*

**The net is `InvalidOperationException` *anywhere in the inner-exception chain*, never the outermost type.** The real transport wraps it: measured against a default `HttpClientHandler` over a loopback TCP server, a consumed-stream replay surfaces as `HttpRequestException` — *"An error occurred while sending the request."* — whose `InnerException` is the `InvalidOperationException` (§5.1). `ObjectDisposedException` derives from `InvalidOperationException` (verified by walking the base chain), so a runtime that disposes request content after a send — see §10.3 on .NET Framework — lands in the same net without a second clause, wrapped or not.

**The net must also not be wider than that.** Every other transport failure on the hop — a reset connection, a DNS failure, a timeout — arrives as `HttpRequestException` too, and must keep reaching the caller as itself rather than being relabelled an unreplayable body. That is the false-positive direction and §7 row 14 pins it.

> **CORRECTION 2026-09-22 — this paragraph shipped false.** It originally read:
>
> > *"**`InvalidOperationException` is the right net, and it is wider than it looks.** `ObjectDisposedException` derives from `InvalidOperationException` (verified by walking the base chain), so a runtime that disposes request content after a send — see §10.3 on .NET Framework — lands in the same catch without a second clause."*
>
> Retained rather than deleted, per #11228 Lesson 3. **It was false for the real transport**, and a `catch (InvalidOperationException)` written from it would have caught **nothing** on the path every default-constructed `HttpService` uses — while the coverage rows below sat green, because the fixture does not wrap. Falsified by John during implementation and re-measured independently here (§5.1). The `ObjectDisposedException` half survives unchanged, so the §10.3 .NET Framework degradation argument (filed as **#14519**) is untouched.

**The exception carries the superseded 308 response, undisposed.** That is the library's established failure convention, stated in the `0.13.0-preview` release note: *"a failing call now leaves it undisposed on `HttpServiceException.Response` for the caller to read and dispose"*. It is what lets a caller read the `Location` they could not follow. The message is assembled from the members that already exist — `DumpUrl` for the query-redacted URL and `DumpHeaders` for the header block under the configured `HeaderDumpMode` — so redaction policy (#9617, #9939) is inherited rather than re-implemented (#1136 §1 DRY). The transport's exception becomes `InnerException`.

**`Body` is not pre-read on this path**, unlike `CheckHttpResponse`. Deliberate, and the reason is that this failure is **local**: the server did nothing wrong, it named a target the library could not reach with the caller's own one-shot body. There is no server error body to surface, and the response is handed over live for a caller who wants to look anyway.

**The message must name the verb.** A caller debugging this needs to know the library refused to downgrade, not merely that something threw. Required content, in prose: the status, the resolved target, the original method, and which of the two conditions fired.

### D4 — the verb-preserving hop requires a usable `Location`

If a 307/308 carries no `Location`, or one that resolves to the request's own URL, **no hop is attempted** and D3's failure is raised instead.

Measured, not assumed: `new Uri(base, (string)null)` and `new Uri(base, "")` both return the base URI unchanged. On the legacy arm that produces a harmless duplicate `GET` and is recorded as a known footgun on #8311. On the verb-preserving arm it produces a **duplicate side-effecting request** — the same `POST`, the same body, the same URL. That hazard does not exist today; D2 creates it; so closing it belongs in this change rather than in #8323.

**The guard is asymmetric, and that is deliberate.** The 301/302/303 arm keeps its current no-`Location` behaviour. Unifying them would change a path this design does not otherwise touch, in a direction (`silent re-GET` → exception) that is #8316/#8323 territory. The asymmetry is defensible to a caller in one sentence: *a hop that replays your request must know where to send it; a hop that issues a fresh `GET` cannot do harm by guessing.* It should converge when #8323 lands, and §10.2 records that a reviewer may reasonably push to unify now.

### D5 — the body-descriptor exclusion becomes conditional on the hop carrying no body

`redirectExcludedHeaders` (`:19`) drops `Expect` and `Transfer-Encoding` from the hop. That list is not a rule about redirects; it is a rule about **a request with no body**, written when the only hop was bodyless. Its premise is now conditional, so the rule becomes conditional on the same thing:

> body descriptors are excluded exactly when the hop carries no body.

This is a **correction of the existing rule's premise, not a second rule.** It adds no status check — the predicate is whether the hop has content, which is already known at that point. Consequences, both correct:

- A `307`/`308` replaying a body keeps `Expect: 100-continue` and `Transfer-Encoding`, because they describe the body that is actually being sent again.
- A `307`/`308` answering a body-less `GET` or `DELETE` still drops them, because there is still no body.
- The 301/302/303 arm is unchanged in every case, since it never carries content. `SendWithTransferEncoding_BodyDescriptor_IsDroppedWhileOtherHeadersSurvive` stays green untouched.

### D6 — 308 is a named constant cast from the integer

A single private `const HttpStatusCode` on `HttpService`, initialised by casting `308`, used in the one switch arm.

**`HttpStatusCode.PermanentRedirect` cannot be used.** Compiled on this branch against both target frameworks: it raises `CS0117` on `netstandard2.0` and builds on `net8.0`. `HttpStatusCode.RedirectKeepVerb` (307) compiles on **both** — verified the same way, rather than taken on trust — so 307 keeps its symbolic name and only 308 needs the cast.

**Rejected: `#if NETSTANDARD2_0`.** Two definitions of one constant, of which the `net8.0` branch would never behave differently — a mirror pair in its smallest form (#1136 §6), and twice the surface for zero benefit.

**Rejected: the bare cast inline.** `308` is the one number in this file whose identity is not self-evident. #1136 §3 puts a named `const` in code as the right home for exactly this.

**The constant's doc comment must say why it is not the framework symbol.** That sentence is the only thing standing between this design and the failure mode D6.1 describes.

### D6.1 — how the netstandard target is protected, and where the protection stops

The brief's constraint: the test project is `net8.0` only, so a `net8`-only constant would pass every test and break the package.

**The mechanism chosen cannot produce that failure.** A cast of an integer literal to an enum is not a target-conditional construct; it compiles identically on both. D6 introduces no framework symbol that `netstandard2.0` lacks.

**That is a claim about this diff, not about the file, and here is what breaks it.** The input class that makes it false is *a later edit that replaces the cast with `HttpStatusCode.PermanentRedirect`* — the obvious-looking tidy-up, which an IDE will actively suggest and which a reviewer unaware of the constraint would wave through. That class exists in the wild; it is how the constraint came to be worth stating.

**The real protection has to be a build.** ~~**So the real protection has to be a build, and the repo does not currently run one at the right time.** `.github/workflows/publish.yml` runs `dotnet test Http.Tests` (net8 only) and then `dotnet pack Pooshit.Http`, which does build both target frameworks — **but only on a `v*` tag push, i.e. after review and merge.** There is no PR-time build of the `netstandard2.0` target at all.~~ Two consequences, both for the record:

> **CORRECTION 2026-09-22 — PR #19 (`c8a3d6a`).** The struck text was true when written and is now false. `.github/workflows/ci.yml` builds `Pooshit.Http/Pooshit.Http.csproj` on every `pull_request` and on every push to `master`, compiling **both** target frameworks, and `build-test` is a **required** status check on `master` with *require branches to be up to date* — so the green it blocks on was computed against the current `master`, not an older one, which is what closes the **semantic-conflict** case where two PRs pass alone and break together. (Testing the *merge combination* is inherent to the `pull_request` event, which checks out GitHub's precomputed merge of head into the base tip; `strict` buys **freshness** of that base, not the combination itself.) Retained per #11228 Lesson 3.
>
> **The mechanism this section describes is unchanged, and that is the part not to lose.** The constant is still a cast that an IDE will offer to tidy into `HttpStatusCode.PermanentRedirect`, and the offer still looks correct to a reader who does not know the constraint. **What changed is that something now catches the tidy-up — not that the tidy-up stopped being tempting.** The doc comment remains the first line of defence, because it is what makes the edit look wrong to the person about to make it; CI is the second, and it is the one that blocks.
>
> Measured, and the reason the build is its own step rather than a side effect of the test step: with `netstandard2.0` deliberately broken, **from a clean tree** `dotnet test` alone produces only `bin/Release/net8.0/` and reports **340 passed, exit 0**. The precondition is load-bearing for the sentence, not just for the recipe — on a dirty tree an earlier build's `bin/Release/netstandard2.0/` is still sitting there, so the output claim would be false while the underlying one (`dotnet test` does not build that target) still holds. A green suite was never evidence about that target.

- **Required of the implementer:** build the library project itself, not just the test project, before returning the work. That compiles both targets. ~~and is the guard that exists today~~ — it was the only guard when this was written; since PR #19 it is the fast local pre-check for a gate CI now enforces anyway.
- **Recommended, not folded in:** a PR-time workflow step building `Pooshit.Http/Pooshit.Http.csproj`. It is the durable fix and it is a CI change in a design about redirects — §9 raises it rather than smuggling it in. **→ Shipped 2026-09-22 as PR #19, separately, which is the outcome this bullet was arguing for:** the recommendation stayed a recommendation, the redirect change stayed a redirect change, and the durable fix arrived on its own branch.

The doc comment from D6 is the third, cheapest layer: it makes the tidy-up visibly wrong to the person about to make it.

---

### D7 — the superseded response has exactly two legal fates, and the rule is written as a grid

**The rule.** From the moment `FollowRedirect` takes ownership of the superseded response, every exit either **disposes** it or **hands it to the caller undisposed on the exception being thrown**. There is no third outcome. A response that is neither disposed nor handed over is a leak — under `HttpCompletionOption.ResponseHeadersRead` a leaked connection, not merely retained memory.

**The discriminator is identity, not type.** The exempt exception is the one **carrying this response** — not one of the right class. An exception is exempt from disposal when it is the library's own `HttpServiceException` *and* the response it carries is the very response `FollowRedirect` owns. Everything else propagates with this response disposed on the way out, including an `HttpServiceException` that somebody else raised and that carries somebody else's response.

**The sentence above is the name of the filter.** The shipped form is a named predicate, `CarriesResponse(e, response)`, negated at the catch — not an inlined double negation. The name is the **grep path from this paragraph to the code**: a reader who has the rule can find the filter, and a reviewer who has the filter can find the rule that justifies it. That traceability is the reason the named form is preferred over an equivalent inline condition, and it carries an obligation — **if this sentence is reworded, the predicate is renamed with it, and vice versa.** Two names for one rule is the same defect as two copies of one claim.

**The distinction is load-bearing because one call site inside the region runs caller code.** `UrlProcessor` is a caller-supplied delegate, and a rewriting processor plausibly resolves its new target by calling a discovery endpoint *through this same library* — which is exactly how it comes to throw `HttpServiceException`. A type test exempts it, and the superseded response is then neither disposed nor handed over, while the caller receives an exception carrying a **different** response. Measured by QA (#14546 CF-4): `disposed=False`, carried response is not the superseded one.

**The identity form also closes a loophole the type form leaves open, and it does so for free.** `HttpServiceException` is **unsealed** and its `Response` is **non-virtual**, so a caller can define a subclass and the library will still read `Response` through the base type. A bare type test therefore exempts *any* subclass — including one carrying an unrelated response, which then leaks. `ReferenceEquals` on the response closes that without a second clause, because the question it asks is about the object rather than about the class. Stated here because it is a property a later *simplification* back toward a type test would silently discard, and nothing in the code says so: the test that would catch the regression is the subclass-carrying-an-unrelated-response cell, not the plain one. Found by QA (#14549), unclaimed by the design or the implementation at the time.

**A caller-supplied extension point inside a guarded region is its own hazard class, and this one is enumerable.** `UrlProcessor` is the only such point in `FollowRedirect`, and it has now been the trigger three times in this arc — §6.2 recommends it as the mitigation for the body crossing the origin, D7.4 rejects narrowing the rule because the leak's trigger is `UrlProcessor`, and CF-4's trigger is `UrlProcessor` again. A remedy the design recommends is, by construction, a path the design's own rules have to survive. It deserves to be listed as such once rather than met three times by accident. The operational form of the question — *does any call inside the guarded region run code you do not control?* — is filed as **#14548**.

> **CORRECTION 2026-09-22 — the discriminator, falsified as QA #14546 CF-4.** This paragraph originally read:
>
> > *"**The discriminator is the exception's owner, not its location.** The library's own `HttpServiceException` is the only exception with a slot that can carry a response, so it is the only one exempt from disposal; everything else — a caller's delegate throwing, a framework parse failure, a transport error — propagates unchanged with the response disposed on the way out."*
>
> Retained per #11228 Lesson 3. **The premise was true and the inference was not:** having a slot that can carry a response is not the same as that slot carrying *this* response. The type test exempted an `HttpServiceException` thrown by caller code, reproducing CF-3's shape inside the form built to eliminate it. The concept — exemption belongs to the exception that owns this response — was right; its operationalisation as a class check was wrong. *Identity* is the operationalisation that matches the concept, and it keeps the invariance under statement motion that was the point of D7.2.

**Stating it that way is a design decision, not a phrasing choice.** A rule expressed as *"the region between these two statements disposes"* is invariant under nothing: any later edit that moves a statement across the boundary silently changes the rule, and the edit looks correct in isolation. A rule expressed as *"this exception type is exempt, everything else disposes"* is invariant under statement motion — hoisting a call, extracting a helper, or adding a guard cannot move an exit out of it. §D7.2 is the incident that makes this concrete.

#### D7.1 The exit grid

Nine exits. This grid is the **specification**; where the shipped code differs, the code is brought to the grid, not the reverse. **As of the landed identity pass, nothing differs** — grid and code agree on all nine rows, independently verified (both targets clean, 335/335).

| # | Exit of `FollowRedirect` | Exception raised by | Superseded response | State |
|---|---|---|---|---|
| 1 | `UrlProcessor(location)` throws | the caller's own delegate — **any type**; the only row whose exception set the library does not bound | **disposed — except when the exception carries *this* response, which is handed over undisposed** (D7's discriminator) | ✓ as specified — CF-3 and CF-4 closed, probed; outcome clause corrected #14549 W-13 |
| 2 | target resolution throws (`UriFormatException`) | the framework | **disposed** | ✓ as specified — CF-3 closed, probed |
| 3 | `CreateRedirectRequest` throws (`InvalidOperationException` — *"An invalid request URI was provided…"* — **not** `UriFormatException` as row 2 might suggest; #14546 W-11, since confirmed against the landed code) | the framework | **disposed** | ✓ as specified — CF-3 closed, probed |
| 4 | preserving arm, response carries no request message | `RedirectFailure` | **handed over, undisposed** | ✓ as specified |
| 5 | preserving arm, no request URI to resolve against | `RedirectFailure` | **handed over, undisposed** | ✓ as specified |
| 6 | preserving arm, target resolves to the request's own URL (D4) | `RedirectFailure` | **handed over, undisposed** | ✓ as specified |
| 7 | hop send fails, consumed content (D3) | `RedirectFailure`, filtered catch | **handed over, undisposed** | ✓ as specified |
| 8 | hop send fails, anything else | the transport | **disposed** | ✓ as specified |
| 9 | hop send succeeds | — | **disposed** | ✓ as specified |

Rows 4–6 are the three pre-send guards. QA's round-2 grid grouped them into one row and counted seven exits; that grouping is withdrawn and **nine is the agreed count** (#14546). The reason the separation matters is QA's own W-5: two of those three guards were unpinned, which a grouped row cannot show. Each is an independent position and needs its own assertion.

**The status column is upkeep, not decoration.** It lagged the code once already (#14546 W-11): the grid was written while rows 1–3 leaked and was not refreshed when they were fixed, which is precisely the failure mode the grid exists to remove. It is refreshed with each landed pass. The identity refinement in D7 has now landed; as predicted it corrects *how* exemption is decided and **added no row and moved none** — the nine exits are unchanged by it.

What it did change is **row 1's outcome, for exception types the grid never named.** Before it, row 1 was correct only for exceptions the library does not raise; a caller delegate throwing `HttpServiceException` was exempted by class and leaked. After it, row 1 holds for every type. That is the whole substance of CF-4, and it is recorded in the row rather than left as a re-derivation, because *"the row did not change"* and *"the row became true"* are different facts and only the second one is what happened.

A second, smaller instance of this same upkeep point: row 3's **outcome** was right from the start, and its **type label** was wrong for two rounds — in this grid and in the implementer's own return. A cell can be right in the column that matters and wrong in the column that identifies it, and only the enumerated form makes the second kind visible at all.

**Row 1 is the only row with a conditional outcome, and that is not an untidiness — it is the grid showing where the discriminator actually bites.** Rows 2–9 each have an exception set the library bounds: a `UriFormatException`, an `InvalidOperationException`, a `RedirectFailure`, a transport error. None of them can carry this response, so for those rows *identity* and *type* give the same answer and the outcome is unconditional. Row 1 runs caller code, its exception set is open, and it is therefore the **only** row where the two discriminators differ — which is exactly why CF-4 lived here and nowhere else. A grid in which every outcome is unconditional would be a grid that had not yet noticed the caller.

**No exit double-disposes, and no exit hands over something already disposed.** Rows 8 and 9 are mutually exclusive by construction (one is the throw path, the other the no-throw path), and no `RedirectFailure` site disposes. **Row 1's conditional clause is forced by this sentence rather than being an exception to it**: disposing an `HttpServiceException` that carries this response would hand the caller an exception whose `Response` is already disposed, which is the second half of this sentence failing.

#### D7.2 The general rule this grid is an instance of

**A rule of the form "X happens on every path" must be enumerated over its paths at the point it is stated.** Unenumerated, it is not a claim a reader can check — only one they can re-derive; and each reader derives a different path set, so the first one to miss a path is the one who ships.

Three properties make the enumerated form stronger, and none of them is specific to any one domain:

1. **It is checkable by inspection instead of by re-derivation.** A table has a row count. A reader can ask *"is that all of them?"* of a table; there is no useful way to ask it of a sentence.
2. **It makes motion visible.** When a later edit moves a path across the rule's boundary, the enumerated form shows a row changing column. The prose form shows nothing, because the sentence is unchanged and still reads as true — which is the case that gets shipped.
3. **It localises a failure to a cell.** *"The rule is wrong"* is a redesign; *"row 3 is in the wrong column"* is a fix.

**The cost, and the counter-case, because the rule is not worth adopting without facing it.** The cost is one table plus the obligation to keep it current as the code moves — and that obligation cuts both ways. A cell that has gone stale is at least *visible*, where a sentence that has gone stale is not; but a table also reads as **authoritative** in a way a hedged sentence does not, so a stale cell tends to be believed rather than checked. That is not hypothetical: the grid this rule was drawn from carried three rows marked *leaks today* for a full round after they had been fixed, and nobody re-derived them because the table looked settled. **So the rule comes with its upkeep or not at all.** Where nobody will refresh the enumeration when the code moves, prose that admits uncertainty does less damage than a table that asserts completeness.

**Where it does not apply.** A rule whose paths are the arms of one switch or a two-branch condition is already enumerated by the code, and a table restating it is duplication. The rule bites when the paths are **exits rather than branches** — thrown exceptions, early returns, cleanup, disposal, cancellation — because those are not adjacent in the source and no single construct lists them.

**A corollary, and in practice the one that bites hardest.** A rule is almost never stated once. It is stated as the rule, and then restated as the instruction that implements it — an implementation order, a checklist row, a release note, a predicate's name, **and a cell of the very table that enumerates it**. Those are **copies of one claim**, and correcting the rule does not correct the copies.

**The table cell is the copy least likely to be re-checked**, and it is worth calling out separately because it is counter-intuitive: a grid row reads as *data* rather than as a *claim*, so when the rule above it changes, a reader updates the rule and scans past the row that restates it. The enumerated form thus solves the problem it also quietly re-creates one level down — which is not an argument against enumerating, but it is the reason the sweep has to include the table and not merely be prompted by it. So: **when a rule changes, sweep the document for its restatements before calling the correction done**, exactly as one would sweep across documents. The intra-document case is the more dangerous of the two, because a single file reads as a single artefact and nobody thinks of it as having copies — whereas two files obviously do. An implementation order still carrying the superseded form is worse than a stale sentence: it does not merely misinform a reader, it instructs a builder.

**The trigger to watch for:** a rule you can state in one sentence whose path set you cannot write down in under a minute. If you cannot enumerate it, neither can your reviewer, and the sentence is doing no work.

**The obligation applies to the rule's discriminator, not only to its paths.** *"Only X is exempt"* is itself a universal — over whatever can reach the region, which is a **second path set and usually the less obvious one**. A rule can have a complete, correct enumeration of its paths and still be false, because its exemption clause was never enumerated at all. Ask of every exemption: *what is the full set of things this could match, and who gets to add to that set?* Where any of it is supplied by a caller, the set is not yours to bound.

#### D7.3 The evidence: two failures of the prose form, one round apart

The rule shipped as prose and was wrong on three of nine exits at once. **The mechanism is worth recording because neither review round could have found it alone.**

- **Round 1 (#14530, W-2)** hoisted `CreateRedirectRequest` *above* the `try`. Correct for its own purpose: the filtered catch must not misdiagnose a request-construction failure as an unreplayable body.
- **Round 2** then added a bare, disposing catch *inside* that already-narrowed `try`.

Each change was right on its own, one round apart. Composed, the hoist had moved an exit out of the only region that disposes — free when no such region existed, a leak the moment one did. **Something moved out of scope and nothing moved in, and no single diff shows it.**

A prose rule cannot make that visible. A grid can: the hoist is a row visibly leaving the disposing column — a question a reviewer asks about a table and does not think to ask about a sentence.

**The rule then earned a second piece of evidence from its own remedy.** The restatement that closed those three exits was itself wrong on a fourth case (CF-4, D7 above), because it operationalised *"the exception that owns this response"* as a class check. The correction to the rule also left a copy of itself behind: §11 step 8, the implementation order for this very change, still specified the falsified class check after the rule above it had been corrected. It was caught on a re-read and not by any process, which is why D7.2 now carries the sweep as a corollary rather than leaving it to diligence. A design document that steers an implementer into a defect the same document has already retracted is the worst shape this failure takes.

**It then happened a third time, inside the grid.** The row describing the caller-code exit was rewritten in the same pass that corrected the rule, and it was rewritten in the *superseded* vocabulary — *"any type … disposed"*, a type-shaped claim restating an identity-shaped rule. Caught by QA (#14549 W-13), not by the author. Three copies of one rule, corrected one round apart each: the rule itself, its implementation order, and its own table row. **The pattern is not that these were missed; it is that each was found by a different mechanism** — review, re-read, review — and none by the correction that created them. That is what a sweep is for, and it is why D7.2 states it as an obligation rather than as advice.

That is the enumeration argument applying to the discriminator as well as to the exits: *"only this type is exempt"* is a universal over the exceptions that can reach the region, and the region contains caller code, so the exception set was never bounded by the library's own types.

#### D7.4 Consequence for the release note

The note's clause *"the superseded redirect response is released rather than leaked"* (#14538 W-8) is **wide, and D7 makes it true** rather than requiring it to be narrowed. If CF-3 is instead closed by narrowing the stated rule to *"a hop whose send fails"*, the note must be narrowed to match and rows 1–3 stay leaked — which this design rejects: making the sentence true by shrinking it leaves the connection leak in place, and the leak's trigger is `UrlProcessor`, the very mitigation §6.2 recommends to callers worried about the body crossing the origin. The remedy and the defect would be the same code path.

---

## 4. Where the rules live

No new file, no new type, no new call site.

**`HandleResponse` (`:390`)** gains a second recognised family. Its shape becomes three arms rather than two: the legacy family, the verb-preserving family, everything else falls through untouched. `UrlProcessor` and the URI combining are shared by both redirect arms exactly as today — **ordering is unchanged and remains load-bearing**: `UrlProcessor` → URI combining → origin comparison → header copy (#9633 §3).

**`CreateRedirectRequest` (`:115`)** already owns the question *"what does the hop's request look like"*. It holds the body-descriptor exclusion and the cross-origin credential exclusion. The verb and body decision is the same question, so it stays in the same method — which means the method must learn **whether this hop preserves the request**. One additional piece of information; everything else is derived from it and from `redirected`:

| Derived | Preserving hop | Legacy hop |
|---|---|---|
| method | `redirected.Method` | `GET` |
| content | `redirected.Content` | none |
| body descriptors (`Expect`, `Transfer-Encoding`) | kept when content is present (D5) | excluded |
| `SensitiveHeaders` on a cross-origin target | **stripped** — #9633 unchanged | **stripped** |
| all other headers | inherited | inherited |

**#9633 is not re-opened and not weakened.** The credential strip is a property of the *target's origin*, not of the status code, so it applies to the preserving arm identically and needs no new reasoning. §6.2 names the one consequence that *is* new — the body, not the credential, crossing the origin.

**Ownership of the superseded response is also `FollowRedirect`'s, for the whole of its body.** The exit grid that governs it is D7; it is stated there rather than here because it ranges over every exit of the method, not over the hop request this section builds.

**The `null`-`redirected` case gains no branch on the legacy arm.** It already goes out bare. On the preserving arm it is D3's first condition and raises instead.

---

## 5. What was measured

Everything in D2 and D3 rests on this, so it is recorded rather than asserted. Probe built and run in this session under `.claude/tmp/`, .NET 8.0.12, against a handler that drains each request body with `CopyToAsync` — the way a transport does. An earlier version of the probe used `ReadAsStringAsync`, which silently buffers the content and made **every** case appear replayable; that first result was discarded. The distinction is the entire measurement.

| Content | hop 0 | hop 1 | identical | result |
|---|---|---|---|---|
| `StringContent` | 7 bytes | 7 bytes | yes | replay OK |
| `ByteArrayContent` | 3 bytes | 3 bytes | yes | replay OK |
| `FormUrlEncodedContent` | 3 bytes | 3 bytes | yes | replay OK |
| `StreamContent` over a seekable stream | 3 bytes | 3 bytes | yes | replay OK — rewound |
| `StreamContent` over a **non-seekable** stream | 3 bytes | — | — | **`InvalidOperationException`: "The stream was already consumed. It cannot be read again."** |
| `MultipartFormDataContent`, string parts | 102 bytes | 102 bytes | yes | replay OK |
| `MultipartFormDataContent`, seekable stream part | 103 bytes | 103 bytes | yes | replay OK |
| `MultipartFormDataContent`, **non-seekable** part | 103 bytes | — | — | **same `InvalidOperationException`** |

Separately measured: after `HttpClient.SendAsync` completes, the request's content is **still readable** — .NET 8 does not dispose it. And `ObjectDisposedException` walks to `InvalidOperationException` in two steps, which is why D3's single catch also covers a runtime that does dispose it.

### 5.1 The table above measured the runtime, not the transport — added 2026-09-22

Every row above ran against a hand-written `HttpMessageHandler`. That is sufficient to establish **whether the content can be serialised a second time**, and those eight rows stand unchanged. It is **not** sufficient to establish **what the caller sees when it cannot** — because a hand-written handler is the one transport that does not wrap.

Re-measured against the real stack: `HttpClient` + a default `HttpClientHandler` (`AllowAutoRedirect = false`, as `HttpService`'s own constructor builds), non-seekable `StreamContent`, loopback TCP server, .NET 8.0.12.

| | observed |
|---|---|
| outermost | `System.Net.Http.HttpRequestException` — *"An error occurred while sending the request."* |
| `InnerException` | `System.InvalidOperationException` — *"The stream was already consumed. It cannot be read again."* |
| outermost **is** `InvalidOperationException` | **false** |
| `InvalidOperationException` **anywhere in the chain** | **true** |
| `ObjectDisposedException` anywhere in the chain | false — expected on .NET 8, which does not dispose request content |

**So the predicate is the inner chain, not the outermost type.** `SocketsHttpHandler` wraps; the fixture does not. D3 is corrected accordingly and retains the sentence it replaced.

**The generalisation is worth more than the correction.** This is the second time in this document's own measurements that a probe was right about the layer it touched and blind to the layer above it. The first was caught before shipping — the discarded `ReadAsStringAsync` version, which buffered and made every content shape look replayable. This one was not. Both have the same shape: **a probe that substitutes a component for the one production uses measures the substitute.** The test fixture is such a substitution by construction, which is why a claim about *what the caller sees on failure* can never be established by the fixture alone, and why §7 row 14 exists.

Also measured, for D4: `new Uri(base, (string)null)` and `new Uri(base, "")` both return the base URI unchanged; `new Uri(base, "/t")` resolves normally.

Also compiled, for D6: `HttpStatusCode.PermanentRedirect` → `CS0117` on `netstandard2.0`, builds on `net8.0`. `HttpStatusCode.RedirectKeepVerb` → builds on both.

**What was not measured: .NET Framework.** No targeting pack is installed here. §10.3 carries the consequence as an open question rather than a claim.

---

## 6. Compatibility

### 6.1 What changes for an existing caller

| Caller shape | Change |
|---|---|
| No options bag, or `FollowRedirects` unset | **None.** `HandleResponse` short-circuits at `:391`. This is every caller who opts into nothing. |
| `FollowRedirects = true`, 301/302/303 | **None.** Both arms of the existing branch, the forced `GET`, the body drop, the credential strip and the disposal are untouched. |
| `FollowRedirects = true`, **308** | **Fixed.** Followed with method and body, or `HttpServiceException`. Was: `default(T)`. |
| `FollowRedirects = true`, **307** | **Changed.** Followed, or `HttpServiceException`. Was: `NotSupportedException`. A caller catching `NotSupportedException` specifically now catches nothing — small blast radius (the throw says "not implemented yet", so catching it was catching a placeholder), but it is a typed-exception change and belongs in the release note. |
| `FollowRedirects = true`, 307/308, **non-seekable stream body** | `HttpServiceException`. Was: `default(T)` for 308, `NotSupportedException` for 307. Loud, still not followed. |
| `FollowRedirects = false`, any 3xx | **None.** #8316 unchanged, deliberately (§2.2). |

**No public signature changes.** No interface member added, removed or re-typed. `HttpOptions` gains nothing. `SensitiveHeaders`, `HeaderDumpMode` and `UrlProcessor` keep their meanings exactly.

### 6.2 The one genuinely new exposure

A cross-origin `307`/`308` now carries the **request body** to a host the remote server chose. `Authorization` and every other `SensitiveHeaders` name is still stripped (#9633), but a secret that lives in a body — a credential in a JSON login payload, a token in a form field — was previously dropped with the body and now travels.

**Accepted, and here is the reasoning rather than a shrug.** Preserving the body cross-origin is what RFC 7538 requires, what browsers do for `fetch`, and what `curl -L` does by default for 307/308. The caller opted into `FollowRedirects`. And the two escape hatches #9633 §2.3 already named both apply unchanged: `UrlProcessor` runs *before* origin resolution, so a caller who knows where the redirect should land can rewrite the target to a host they name; and `FollowRedirects = false` with `Get<HttpResponseMessage>` hands back the raw 308 for a caller to follow by hand.

**No knob.** #1136 §3 requires a named operator or an environment difference, and there is no candidate caller. Predicting the shape now produces the wrong shape (#1184).

**This belongs in the release note**, alongside the 307 exception-type change. Both are behaviour changes for an opt-in caller, and the version digit is the wrong instrument for a warning.

### 6.3 Version

**`0.13.0-preview` → `0.13.1-preview`, a patch bump.** The repo convention across four consecutive changes is *patch when behaviour is byte-identical for callers who opt into nothing, minor when it is not*. `FollowRedirects` defaults to `false`; every caller who opts into nothing is untouched; the literal test is met.

The argument for minor is real: this adds a capability and changes a thrown exception type. It loses for the same reason it lost in #9633 §4.4 — applying a convention inconsistently because one change feels larger is how a convention stops being one. §10.5 records it as Toni's call at packaging.

---

## 7. Coverage

`Http.Tests/HttpServiceRedirectTests.cs`. Each row names the **guard John writes**, and each is red uniquely for the thing it protects — the fourth column says what breaks it and nothing else.

### 7.1 Fixture change required first

`SequenceHandler` records requests but **never touches their content**, so under it a consumed stream is never consumed and rows 5 and 9 cannot fail. The fixture must reproduce the transport:

- **`SequenceHandler` drains each request body** (`CopyToAsync` into a sink) before responding, and exposes the bytes per request. Draining always, not opt-in: it is what a real handler does, it is what makes non-replayability reproducible, and it changes no existing assertion (every current redirect test asserts on headers, URIs, methods, or `Content is null`; response-content probes are unaffected).
- **A non-seekable stream wrapper** in `TestSupport` — a stream that reports `CanSeek == false` and refuses `Seek`. Rows 5 and 9 need it and nothing else provides it.

Without the first bullet, rows 5 and 9 are green against an implementation with no D3 at all.

| # | Guard (test name) | Shape | Goes red uniquely when |
|---|---|---|---|
| 1 | `Post308_HopRepeatsPostWithSameBody` | `Post<string,string>`, 308 + `Location`, assert hop 1 method is `POST` and content is not null | the hop downgrades the verb or drops the body — the core fix |
| 2 | `Post307_HopRepeatsPostWithSameBody` | same, status 307 | 307 and 308 are not shipped as one rule (D1) |
| 3 | `Post308_BodyBytesAreIdenticalOnBothHops` | seekable-stream body; assert the drained bytes of hop 0 equal those of hop 1 | the content is re-attached but not rewound, i.e. an empty or truncated second body (D2) |
| 4 | `Post308_ContentTypeRidesTheHop` | JSON-encoded body; assert hop 1's `Content-Type` | the implementer rebuilds content instead of re-using the instance, losing content headers (D2) |
| 5 | `Post308_NonSeekableStreamBody_ThrowsHttpServiceException` | non-seekable body; assert `HttpServiceException`, and that its `InnerException` is the transport's | the transport exception escapes untranslated, or is swallowed back into `default(T)` (D3) |
| 6 | `Post308_WithoutLocation_ThrowsAndSendsNoSecondRequest` | 308, no `Location`; assert throw **and** `Requests.Count == 1` | D4 is missing — the count assertion is what distinguishes it from row 5 |
| 7 | `Post308_UnstampedResponse_ThrowsInsteadOfDowngrading` | `SequenceHandler { StampRequestMessage = false }`; assert `HttpServiceException` | D3's first condition is missing and the hop silently becomes a `GET` |
| 8 | `Post308_CrossOrigin_AuthorizationStrippedWhileBodyRides` | cross-host 308 + `TokenProvider` + body; assert no `Authorization` on hop 1 **and** content present | the preserving arm bypasses #9633, or over-corrects by dropping the body cross-origin |
| 9 | `Post308_ExpectContinueSurvivesTheBodyCarryingHop` | `ExpectContinue = true`, 308; assert `Expect` present on hop 1 | D5 is missing and the exclusion is still applied unconditionally |
| 10 | `PostWithBody_FollowedRedirect_HopIsIssuedAsGetWithoutBody`, and its siblings `Post301_HopIsIssuedAsGetWithoutBody` / `Post303_HopIsIssuedAsGetWithoutBody` | a `POST` with a body into 302 / 301 / 303; assert hop 1 is a `GET` with **no** content | **dual** — verb preservation leaked onto the legacy arm. All three legacy statuses are pinned, not just 302. |
| 11 | `SendWithTransferEncoding_BodyDescriptor_IsDroppedWhileOtherHeadersSurvive` | `Send` into a 302 carrying `Transfer-Encoding: chunked` and a marker header; assert the descriptor is present on hop 0, absent on hop 1, and the marker survives | **dual** — D5 inverted the legacy arm and began keeping body descriptors on a bodyless `GET`. It does **not** separate D5's two candidate predicates; see the correction below the table. |
| 12 | `Get308_FollowRedirectsFalse_Throws` | 308, `FollowRedirects` unset; assert `HttpServiceException`, **one** request, and `error.Response.StatusCode` is 308 | **dual** — the unfollowed path stopped being the boundary between this design and #8316. It fails if a future change makes an unfollowed 308 *followed* (one request becomes two) or *silent* again. **Its behaviour inverted when #8316 shipped — see the correction below.** |
| 13 | `Get308_BodylessHop_StillDropsBodyDescriptor` | a 307/308 answering a **body-less** request; assert `Expect`/`Transfer-Encoding` absent on hop 1 | **the real dual of 9** — D5's predicate was written as "this is a preserving hop" instead of "this hop has a body". Only a preserving hop that carries no body separates the two. |
| 14 | `Post308_HopFailsWithAnUnrelatedTransportError_IsNotReportedAsAnUnreplayableBody` | 308 whose hop fails for an unrelated transport reason; assert the caller sees that failure, not an unreplayable-body `HttpServiceException` | the net is **over-broad** — the false-positive direction, e.g. a reset connection relabelled as a non-replayable body. Nothing else pins the catch's width. |

Rows 10, 11, 12 and 13 are the duals #114 §13.1.1 asks for: without them the suite is green for four implementations this design explicitly rejects. Rows 10 and 11 pre-date this design and must stay green untouched; row 12 pinned, when it was written, a behaviour nobody would think to test precisely because it was the *absence* of a change — **that is no longer what it pins** (see the correction below); row 14 pins the one direction the other thirteen cannot — that the failure net is not wider than the failure.

> **CORRECTION 2026-09-22 — row 11's claim was wrong, and rows 13 and 14 were missing.** Row 11 originally read:
>
> > *"**dual of 9** — D5's predicate was written as 'preserving hop' instead of 'hop has a body'"*
>
> Retained per #11228 Lesson 3. **Row 11 cannot be that dual.** It exercises a 302 on the legacy arm, where the legacy hop never carries content — so *"preserving hop"* and *"hop has a body"* are both false and the two predicates agree. Measured by John: row 11 stays green under exactly that mutation. The case that separates them is a **307/308 answering a body-less request**, where the wrong predicate keeps `Expect`/`Transfer-Encoding` on a hop with no body — now row 13. Row 14 closes a second gap found in the same pass: nothing pinned that the consumed-content net is not *over*-broad, which fails in the false-positive direction. Row 11 is still a real guard, against a different mutation, and is restated as such.

> **CORRECTION 2026-09-22 — rows 10, 11 and 12 named identifiers that do not resolve (DiVoid #14528).** The three rows originally read:
>
> > *"| 10 | `Post302_HopIsStillGetWithoutBody` | …"*
> > *"| 11 | `Get302_TransferEncodingStillDroppedOnBodylessHop` | …"*
> > *"| 12 | `Get308_FollowRedirectsFalse_StillReturnsDefault` | 308, `FollowRedirects` unset; assert `null`, no throw, one request | **dual** — #8316 was folded in after all, silently widening the change"*
>
> Retained per #11228 Lesson 3, because *“the identifier was wrong”* is precisely the failure the naming convention exists to make visible, and a silent rewrite would erase the evidence for it.
>
> **Rows 10 and 11 were wrong, not superseded.** Both named guards the author intended to describe rather than guards that exist: the tests were already in the suite under different names, quoted correctly one column to the right. A reader grepping the guard column found nothing. **Naming an identifier is necessary and not sufficient — it has to be *the* identifier**, which is the sharpening #14528 took from finding these, and the reason the rule in #1136 §5 says *names the test identifier it pins* rather than *names a test*.
>
> **Row 10 also understated its own coverage.** The behaviour is pinned on **all three** legacy statuses — 302, 301 and 303 — not only the 302 the row quoted. Verified by reading each body: every one asserts hop 1 is a `GET` with `Content` null.
>
> **Row 12 is a different defect: its behaviour inverted underneath it.** When written, an unfollowed 308 returned `default(T)` silently, and the row existed as a dual proving this design had *not* folded in #8316. #8316 then shipped on its own (design #14574, PR #21), the test was rewritten, and the row's assertion became the opposite of the truth. The guard is now `Get308_FollowRedirectsFalse_Throws` — read, not matched: it sends one request, raises `HttpServiceException`, and asserts the carried status is 308. **The row's *role* changed too**: it no longer guards a scope boundary this design declined to cross, because the thing on the other side has since been built. It now guards that the unfollowed path stays loud and stays unfollowed.
>
> **These were the last three SHAPE B rows in the corpus** — the audit in #14595 reported exactly three, all here.

**Not to be added here:** any assertion about hop count or loop behaviour. That is #8323 and folding a capability assertion into a correctness change blurs both.

---

## 8. Falsifiable claims (#9951)

This surface shipped a false universal once, inside the fix that existed to prevent it. Every sentence in this document shaped *covers everything* / *cannot happen* / *falls out by construction* is listed here with the input class that would break it, and whether that class exists.

**Three of the seven rows in this table have since fired**, all on 2026-09-22 — the `InvalidOperationException` net, falsified during implementation (D3, §5.1); row 2's remedy clause, falsified in QA round 1 (**#14530** W-3); and the disposal rule, falsified in QA round 2 (**#14538** CF-3). All three are kept in place with their outcomes recorded rather than removed, because a falsifier table that only ever lists unfired claims is the same decoration #9951 warns about.

**Three of seven is a different statement about this table than one of six, and the pattern is the useful part — it has now held three times.** None of the three rows was wrong about *what would break it*: row 2 named its own falsifier class exactly, row 3's was one step out from what it wrote, and D7's condition (*disposed unless handed over*) was correct as stated. All three were wrong about **the region the claim ranged over** — what the system does when the condition is met, and on which paths. The falsifier clauses held; the remedy and scope clauses did not. A reader auditing this table should start at the right-hand column, not the third one.

| Claim | Where | What would falsify it | Does that exist? |
|---|---|---|---|
| "a caller who opted in gets the result or a loud failure, never `default(T)`" | §2.1 | a 308 that is followed but whose *next* response is also a 3xx | **Yes** — a two-hop chain. Stated as a residual in §2.2 and **not** closed here. The claim holds for chains of length one only, and that is the honest scope of the fix. |
| "the replay is byte-identical" — claim stands; ~~its remedy clause~~ **FALSIFIED 2026-09-22 (QA #14530 W-3)** | D2, §5 | *a caller-supplied `HttpContent` subclass with one-shot serialisation* — the class this row already named | **Yes, and it fired.** The replay claim itself is unchanged and correct: byte-identical **for the shapes this library builds**, not for all content. What was false is what this row said happens *next*. Such a body does **not** reach D3's net — `IsConsumedContentFailure` walks for `InvalidOperationException`, and a one-shot content typically signals exhaustion with something else (`SingleUseContent` in the test suite throws `IOException`) — so it surfaces as a bare `HttpRequestException`. §2.1 still holds: loud, never `default(T)`. What is lost is that the failure is **untyped** — no `HttpServiceException`, no status, target or verb in the message, and no 308 response handed back. Row 3's *"a type outside that chain entirely"* is this same class, stated correctly there. |
| ~~"`InvalidOperationException` is the right net"~~ — **FALSIFIED 2026-09-22; the falsifier fired** | D3, §5.1 | *a transport that wraps the exception rather than throwing it directly* — a class the original row did not consider, because it named only "some other type" | **Yes, and it fired in implementation.** The real `HttpClientHandler` wraps it in `HttpRequestException`, so the outermost-type claim was false on the path every default-constructed `HttpService` uses. Corrected: the net is the **inner chain**. What remains genuinely unknown is a runtime signalling consumed content with a type outside that chain entirely; `ObjectDisposedException` is inside it (verified), and .NET Framework is unmeasured (§10.3, filed as #14519). |
| "the mechanism cannot break `netstandard2.0`" | D6.1 | a later edit swapping the cast for `HttpStatusCode.PermanentRedirect` | **Yes, and it is the likely one — now *caught*, not prevented.** The class is unchanged: an IDE offers the swap and it looks correct. Since PR #19 a **required** CI build of both targets fails on it before merge, so the falsifier now fires against a guard instead of against a release. D6.1 was right not to stop at the mechanism. |
| "#8316 cannot leave a hole inside this scope" | §2.2 | a 308 silent despite `FollowRedirects = true` | **Yes, two classes.** One (no `Location`) is closed by D4; the other (chain of two) is not and is stated. |
| "D5 changes nothing on the legacy arm" | D5 | a legacy hop that carries content | **No** — the legacy arm attaches no content on any path, and row 11 pins it. This is the one claim here with no live falsifier, which is why it gets a dual test rather than a caveat. |
| ~~"the superseded response is disposed unless it is handed to the caller on an exception"~~ — **FALSIFIED 2026-09-22 (QA #14538 CF-3)** | D7 | *an exit that leaves the region the rule is implemented over* — not an exit that disobeys the rule, but one the region never reached | **Yes, and it fired on three exits at once**, measured live (`disposed=False` on each). The condition was right; the **region** was wrong. The rule was implemented as *"this `try` block disposes"*, and a hoist one review round earlier had already moved three exits outside it — so the rule was false for `UrlProcessor` throwing, for target resolution throwing, and for request construction throwing. D7 restates it over the exception's **owner** rather than over a block, which is invariant under the statement motion that caused this. **The restatement then needed two forms, and its copies needed a third pass:** the first operationalised *owner* as a class check and was itself falsified (#14546 CF-4) by caller code inside the region throwing the library's own type; identity is the form that matches the concept. The corrected rule then left two restatements behind in the superseded vocabulary — §11 step 8 and this grid's own row 1 (#14549 W-13). **One rule, three corrections, three different finders** — which is the strongest single argument in this document for D7.2's sweep corollary. What remains unfalsified: that every exit is one of the nine in D7.1 — a tenth would have to be added by a future edit, and the grid is what makes that visible. |

> **CORRECTION 2026-09-22 — row 2's remedy clause. Raised by QA as #14530 W-3.** The row originally read:
>
> > *"| "the replay is byte-identical" | D2, §5 | a content shape outside the eight measured — chiefly a caller-supplied `HttpContent` subclass with one-shot serialisation | **Yes**, and it is uncovered by the measurement. **It lands on D3's catch, so it fails loudly rather than sending a truncated body.** The claim is "byte-identical for the shapes this library builds", not for all content. |"*
>
> Retained per #11228 Lesson 3. **The emphasised sentence was false**, and it contradicted row 3 of the same table — so §8 asserted both answers at once, which is the one failure a falsifier table cannot afford, given that §8 exists precisely to be the place where claims are checkable.
>
> **The class was never hypothetical, and the suite already contained an instance.** `Http.Tests/TestSupport/SingleUseContent.cs` is exactly *a caller-supplied `HttpContent` subclass with one-shot serialisation*, and §7 row 14 (`Post308_HopFailsWithAnUnrelatedTransportError_IsNotReportedAsAnUnreplayableBody`) asserts the bare `HttpRequestException` this row denied. **The same test object reads two ways** — as row 14's *unrelated transport error* and as row 2's *named falsifier class* — and this row read it the wrong way round. That is the lesson worth carrying: a falsifier class you can point at in your own test fixture is not a hypothetical, and should have been run rather than reasoned about.
>
> **The package release note is correct as shipped** and did not need correcting: it scopes the followed-or-diagnosed guarantee to the two stream shapes rather than to all content. The document was the only overclaim.

### 8.1 The claim I expect a reviewer to attack first

**"307 and 308 are a pair because this library persists nothing."** The way to break it is to find a member of `HttpService` or `IHttpService` that stores or returns a resolved URL, or any caching behaviour that would make permanence actionable. I read the public surface and found none — no cache, no cookie container, no URL memo, and `HttpOptions.UrlProcessor` is caller-supplied and one-way. If such a member exists or is added, the pair decision needs re-opening, not the rest of the design.

---

## 9. Pre-Design Checklist (#1136 §5)

| Item | Verdict |
|---|---|
| No new type mirroring an existing one | **Pass** — no new type at all; D6 rejects the `#if` pair explicitly as the mirror anti-pattern in its smallest form |
| No new abstraction with one implementation | **Pass** — one additional piece of information into a private method that already exists |
| Nothing justified by "we might need X later" | **Pass** — D1 names the permanence-observing callback as prose and builds nothing; §6.2 rejects the policy knob |
| No deprecation window / compat shim / feature flag | **Pass** — the change is immediate and total; `NotSupportedException` is removed, not deprecated |
| DRY math on inline-vs-extract | **N/A** — one method, one call site per arm. D3's message reuses `DumpUrl`/`DumpHeaders` rather than restating them |
| Existing surface audited before adding one | **Pass** — `CreateRedirectRequest`, `SensitiveHeaders`, `HeaderDumpMode`, `UrlProcessor`, `HttpServiceException`, `DumpUrl`, `DumpHeaders`, `SequenceHandler` all reused; nothing duplicated |
| Every config knob has a named operator | **Pass by removal** — no knob ships; §6.2 records that no operator could be named |
| Can-it-be-deleted / merged / inlined | **Pass** — D5 *deletes* a rule's unconditional form rather than adding a second rule; D6's `const` is the #1136 §3 named-magic-number shape, not an indirection |
| Trade-offs named explicitly | **Pass** — TL;DR (buffering rejected, with the cost), D3 (loud-not-followed), D4 (the asymmetry), D7.4 (extending the disposal region rather than narrowing the rule, with the reason the narrow option is rejected), §6.2 (body cross-origin), §6.3 (patch over minor) |
| Out-of-scope listed explicitly, not merely absent | **Pass** — §2 table, with §2.2 answering the #8316 question the brief demanded rather than leaving it implicit |
| No multi-paragraph rationale for things that obviously stay | **Pass** |
| Predecessor design banner where superseded | **N/A** — #9618, #9633 and #9939 are all extended, none superseded. #9633's rule applies to the new arm unchanged (§4) |
| Data deliverables (SQL/schema casing) | **N/A** — no data layer in this repo |
| Defensive code for impossible scenarios | **Pass** — D3's catch is reproduced three times in §5 — twice against the fixture, once against the real transport (§5.1); D3's null-request-message condition is a state the existing code already branches on and the test suite already constructs |

---

## 10. Open questions

1. **Is the two-hop residual acceptable?** After this change a `308 → 308` chain still returns `default(T)` under `FollowRedirects = true` (§2.2, §8). I judge it shippable — one hop followed where zero were, identical to the 302 chain that has shipped since PR #2, and filed as #8323 items 1 + 3. If Toni disagrees, the remedy is #8323, not #8316, and it is a materially larger change (a loop guard is a new decision, not a correction).
2. **Should the no-`Location` guard (D4) apply to 301/302/303 too?** I kept it asymmetric to avoid touching a path this design does not otherwise change. The unified version is simpler to state and arguably more correct; it is also a behaviour change on the legacy arm that belongs to #8323. A reviewer may reasonably push either way — the cost of unifying is one test to update, not a redesign.
3. **.NET Framework request-content disposal is unverified.** No targeting pack is installed here, so §5 measures .NET 8 only. `HttpClient` on .NET Framework is widely reported to dispose request content after a send. If it does, then on a `netstandard2.0` consumer running on .NET Framework **every body-carrying 307/308 lands on D3's loud-failure path** rather than being followed — the design still satisfies §2.1's acceptance sentence there, but the capability is absent rather than present. `ObjectDisposedException` being inside D3's catch means this degrades gracefully and needs no code change; it needs a **measurement** before the release note claims 307/308 following works everywhere. Cheapest form: one manual run against a `net48` consumer, or a sentence in the release note scoping the capability to .NET Core / .NET 5+.
4. ~~**Should a PR-time netstandard build be added to CI?** (D6.1.) There is none today; `dotnet pack` builds both targets but only on a tag push, i.e. after merge. One step building `Pooshit.Http/Pooshit.Http.csproj` would close it permanently. I did not fold it in — a CI change inside a redirect fix is scope creep — but it is the durable answer to the constraint the brief raised, and it is cheap.~~ — **Answered 2026-09-22 by PR #19 (`c8a3d6a`).** `ci.yml` builds the library project on every pull request and every push to `master`, and `build-test` is a required check with *require branches to be up to date*. Closed; D6.1 carries the detail and the measurement.
5. **Patch or minor.** §6.3 recommends `0.13.1-preview` on the convention's literal test. The other reading — that adding a capability and changing a thrown exception type warrants `0.14.0-preview` — is defensible. Toni's call at packaging; costs nothing to change.

---

## 11. Implementation order

Each step leaves the suite green.

1. **Fixture first.** Make `SequenceHandler` drain request bodies and expose the bytes; add the non-seekable stream to `TestSupport` (§7.1). Behaviour-neutral for the library. Run the full suite — it must still be green, which is itself the check that draining changed nothing.
2. **The constant.** Add the private `const HttpStatusCode` for 308 with the doc comment from D6 explaining why it is not the framework symbol. No behaviour yet.
3. **`CreateRedirectRequest`.** Teach it whether the hop preserves the request; derive method and content from `redirected`; make the body-descriptor exclusion conditional on the hop having content (D5). Leave the cross-origin credential rule exactly as it is. Legacy callers pass the non-preserving form, so behaviour is unchanged at this point and rows 10 + 11 stay green.
4. **`HandleResponse`.** Add the verb-preserving arm for 307 and 308; remove the `NotSupportedException`; add D4's `Location` check and D3's two failure conditions, including the catch that translates the transport exception. `HttpServiceException` is built from `DumpUrl` + `DumpHeaders` + the original method, with the transport exception as `InnerException` and the response left undisposed.
5. **Coverage.** Rows 1–9 and 12 from §7; confirm 10 and 11 unchanged and green.
6. **Build the library project against both target frameworks** — not only the test project (D6.1). ~~This is the only guard the repo has for `netstandard2.0` before a tag push.~~ Since PR #19 CI enforces this on every pull request, so the step survives as a fast local pre-check rather than as the repo's only guard.
7. **Version and release note.** Bump to `0.13.1-preview` (§6.3) and write the note from §6.1 and §6.2: 307/308 are now followed with method and body; `NotSupportedException` is gone; a non-replayable body raises `HttpServiceException`; a cross-origin hop carries the body while still stripping credentials.
8. **Added 2026-09-22 after QA rounds 2 and 3 (#14538 CF-3, #14546 CF-4).** Bring the disposal region to D7: dispose the superseded response on every exit **except** the one throwing the library's own `HttpServiceException` *carrying that same response* — identity, not type, because `UrlProcessor` runs caller code inside the region and can throw the library's own type carrying a different response. Shipped as the named predicate `CarriesResponse(e, response)`, negated at the catch (D7). Rows 1–3 of D7.1 stop leaking. Pin the `UrlProcessor`-throws cell **and** the `UrlProcessor`-throws-`HttpServiceException` cell — the second is the one a type test passes — and add the missing undisposed assertions on the two unpinned guards (rows 5 and 6; #14538 W-5). The release note's existing wide wording then stands as written (D7.4).
