# Architectural Document: what `HttpOptions` means on `Send`

> **Repo path:** `docs/architecture/send-options-contract.md` (repository `telmengedar/Pooshit.Http`).
> **DiVoid:** source task **#9609** · project **#2281** · repo map root **#8292** · `HttpService` **#8297** · `HttpOptions` **#8299** · `IHttpService` **#8298** · how-to-extend **#8314** · request lifecycle **#8311**.
> **Contracts cited as load-bearing:** Design Contracts **#1136** (§1 KISS/DRY/YAGNI, §4 less is better, §5 checklist) · Code Contracts **#114** §0.
> **Baseline:** `origin/master` @ `21152e4`. Package version on master: `0.9.0-preview`; this change ships `0.9.1-preview`.
> **Precedent:** `docs/architecture/true-passthrough-completion-option.md` (PR #3) · `docs/architecture/error-message-header-redaction.md` (PR #5, §3.4 on the option-plumbing hazard).

---

## 1. Problem

#9609, filed by QA off the round-2 guard work on PR #5:

> `HttpService.Send(HttpRequestMessage, HttpOptions)` and `Send<TResponse>(...)` take a pre-built request and go straight to `SendRequest` — they never run `CreateRequest`. So the options bag is only **partially** applied, with no signal to the caller.

and, on the sharper half of it:

> One `Send<T>` call therefore authenticates the second request and not the first — which is both surprising and, if the redirect target is off-origin, a credential-forwarding question in its own right.

Nobody ever wrote down what the options bag means when it is handed to `Send`. #9609 measured what it currently means and named the prerequisite: **is `Send` a raw-request escape hatch, or a first-class member of the options surface?** Everything else follows from that answer.

---

## 2. Scope

**In scope:** the `Send` / `Send<T>` options contract, and the redirect hop reachable from them (`HttpService.cs:246-257`).

**Out of scope, deliberately** — each is a separate defect with its own blast radius, recorded in #8297 and filed rather than folded in:

- The redirect follower's other limits: one hop only, always re-sent as GET dropping verb and body, no loop guard, 307 throws, 308 unrecognised.
- Non-ASCII header mangling in `EncodeHeaderString`.
- **Credential forwarding to an off-origin redirect target.** §2 of #9609 raises it; it is a property of the whole redirect follower, not of `Send`, and §7 below records what this change does and does not do to it. Filed as **#9619**.

---

## 3. The contract — testing the stage hypothesis

The honoured/ignored split #9609 measured is not arbitrary, and it is not a coincidence either. Every `HttpOptions` member is read at exactly one of four stages, and **the stage predicts the outcome perfectly, 10 members out of 10**:

| Member | Read at | Read where | `Send<T>` | `Send` |
|---|---|---|---|---|
| `TokenProvider` | request construction | `CreateRequest`, `:97` | ignored | ignored |
| `Headers` | request construction | `CreateRequest`, `:107` | ignored | ignored |
| `ExpectContinue` | request construction | `CreateRequest<T>`, `:120` | ignored | ignored |
| `MediaType` | request construction | `CreateRequest<T>`, `:121`/`:151` | ignored | ignored |
| `Encoder` | request construction | `CreateRequest<T>`, `:155` | ignored | ignored |
| `CompletionOption` | sending | `SendRequest`, `:187` | honoured | honoured |
| `HeaderDumpMode` | response handling | `DumpHeaders`, `:170` | honoured | honoured |
| `FollowRedirects` | response handling | `HandleResponse`, `:246` | honoured | n/a — see below |
| `UrlProcessor` | response handling | `HandleResponse`, `:249` | honoured | n/a — see below |
| `Decoder` | typed read | `ReadResponse`, `:266`→`:225` | honoured | n/a — no body is read |

**The hypothesis holds. `Send` is an escape hatch for *request construction* and a first-class member of the options surface for *everything after it*.** That is the answer to #9609's prerequisite, and it is a coherent contract rather than an accident of implementation: the caller who calls `Send` has supplied the request themselves, so options that describe how to build a request have nothing left to build; options that describe how to send it, how to read the answer, and how to report a failure are all still the service's job.

Two facts corroborate that this is the design and not merely the outcome:

1. **`CreateRequest(url, method, options)` is public and is `Send`'s intended partner.** Its own XML doc says *"this can be used to customize http request before sending it"*. The composed flow is `CreateRequest(url, method, options)` → decorate → `Send(request, options)`: the caller opts into request-construction options *explicitly*, at the stage that owns them. #8314 already frames the pair as "the raw-request escape hatches".
2. **The escape hatch has no other way to work.** A hand-built request is the only means a caller has of *suppressing* something a shared options bag would otherwise add. Applying construction options inside `Send` would take that away.

### 3.1 Two places where the contract is not currently true

- **The redirect hop.** `HandleResponse` re-enters `CreateRequest` at `:255` and builds a **brand-new** request from the options bag. That is a second request-construction stage that the contract does not acknowledge, and it is where the whole defect lives.
- **The non-generic `Send` does not follow redirects at all**, because it calls `CheckHttpResponse` directly and never enters `HandleResponse` (`:366-371`). This is **not** a `Send` quirk: it is uniform across every result-less overload in the library (#8314, "Result-less with a body — … No redirect following"). It stays as it is, and is documented rather than changed — making `Send` alone follow redirects would trade one consistent surface for an inconsistent one.

---

## 4. Design

Two changes. The first states the contract; the second makes it true.

### 4.1 D1 — write the contract down (no behaviour change)

`IHttpService.Send<TResponse>` and `IHttpService.Send` get a `<remarks>` block naming the rule and both halves of it: the options bag is honoured from the send onwards, and members read at request-construction time — `TokenProvider`, `Headers`, `ExpectContinue`, `MediaType`, `Encoder` — do not apply, because the caller supplied the request. It must name `TokenProvider` explicitly; #9609 identifies it as the sharp edge (a silent `401` with no indication the token was dropped) and a caller reading the doc must hit that word.

`IHttpService.CreateRequest` gets one `<remarks>` line naming the composed flow, so the pairing is discoverable from either end.

The docs live on `IHttpService` because `HttpService` carries `<inheritdoc />` throughout.

> **Correction 2026-08-26 (implementer, disclosed) — the container is `<summary>`, not `<remarks>`. The content above is unchanged.**
>
> Code Contracts #114 RULING 2026-08-08 denies `<remarks>` in new or modified code on any but Toni's explicit order. The rule the paragraphs above specify is a usage constraint the caller must honour, which that ruling places in the `<summary>`; it shipped there, naming `TokenProvider` first among the five. `CreateRequest`'s pre-existing `<remarks>` was folded into its `<summary>` under the same ruling's bring-to-contract-while-editing clause. Nothing about *what* is documented changed.
>
> **Also corrected 2026-08-26:** §3's `Decoder` row cited `:245`→`:222`; at this document's stated baseline `21152e4` those lines are the `HandleResponse` declaration and a `switch` brace. The read is at `:266` (`return await ReadResponse<T>(response, options?.Decoder);`) → `:225` (`decoder ??= new JsonDecoder();`), and the cell now says so. Every other citation in §3 was checked and is correct.

### 4.2 D2 — the redirect hop re-issues the previous request, it does not build a new one

**Rule: the redirect hop carries exactly the headers the request that produced the redirect carried.** The header source becomes `response.RequestMessage.Headers` instead of the options bag; `CreateRequest` is no longer called from `HandleResponse`.

Excluded from the copy: headers that describe a body the redirect does not carry — `Expect` and `Transfer-Encoding`. Both live on `HttpRequestHeaders`, not on the content headers, so they would otherwise be copied onto a bodyless GET. Content headers are excluded structurally: the new request has no content, so its content-header collection does not exist. Everything else copies.

`UrlProcessor`, the `Uri` combining at `:252-253`, the disposal of the superseded response at `:256`, and the completion option carried into the second send all stay exactly as they are.

**What this fixes, in one line each:**

- `Send<T>` + `FollowRedirects` hop 1 currently goes out carrying the *options'* headers and **none of the caller's** — the two hops today carry disjoint header sets, so a caller's own `Authorization`, `Accept` or correlation id is silently dropped on the redirect. After the change hop 1 carries what hop 0 carried.
- The hop-0/hop-1 asymmetry #9609 measured disappears, and it disappears in the direction that does not break the escape hatch.
- The URL-based overloads are unaffected on the wire (§5), because for those hop 0's headers *are* the options-derived headers.

**Why not the narrow fix #9609 lists as option 3** ("do not re-apply option headers on a redirect issued from `Send`"): it makes hop 1 consistent with hop 0 by sending hop 1 **bare**, which drops the caller's own headers too. That is worse than the asymmetry it fixes — a redirect that arrives without the caller's credentials just produces a `401` one hop later. D2 reaches the same consistency by keeping the headers rather than by discarding them, and does it with one rule instead of a branch keyed on which entry point was used.

---

## 5. Compatibility and version

**No public signature changes.** No interface member is added, removed or re-typed; `HandleResponse` is private.

| Caller shape | Change |
|---|---|
| No options bag, or a bag without `FollowRedirects` | **None.** `HandleResponse` short-circuits at `:246`. |
| URL overload (`Get`/`Post`/…) + `FollowRedirects` | **Wire-identical.** Hop 0 was built from the options bag, so copying its headers reproduces the same set. One observable difference: `ITokenProvider.GetTokenAsync()` is now called **once per call instead of once per hop**. For a provider that caches or returns a stable token this is invisible; for one that mints single-use tokens the hop-1 credential changes from a fresh token to the reused one. |
| `Send`/`Send<T>` + `FollowRedirects` | **Changed, deliberately.** Hop 1 carries the caller's headers instead of the options bag's. Anyone depending on today's behaviour is depending on a redirect authenticated by an options bag that hop 0 ignored — undocumented, and the thing #9609 was filed about. |

**Version: `0.9.0-preview` → `0.9.1-preview`, a patch bump.** The repo convention is patch when behaviour is byte-identical for callers who opt into nothing, minor when it is not. `FollowRedirects` defaults to `false`, so every caller who opts into nothing is untouched — the same test the completion option passed at `0.7.18` → `0.7.19`. See §9 for the one reading of the convention under which this would be a minor instead.

---

## 6. Decisions

**Decision 1 — `Send` is an escape hatch for request construction, first-class from the send onwards.** This is #9609's prerequisite, answered in §3. Rejected: **#9609 option 2, apply the token and headers to the caller's request.** Three concrete costs, any one of which is disqualifying:
  - It breaks the canonical composed flow. `CreateRequest(url, method, options)` → decorate → `Send(request, options)` would apply the options twice. `TryAddWithoutValidation` **appends**, so every option header would appear **twice on the wire**, and `GetTokenAsync()` would be awaited a second time — an extra auth round-trip on every send.
  - It mutates an object the caller owns. `HttpRequestMessage` is passed by reference; the caller's instance would come back carrying headers it did not have.
  - It removes the only way to suppress a header a shared options bag would add. The bag is normally shared across a whole client (it is how a token provider is supplied), so "send this one without the bag's headers" is a real need with no other answer.

  Reversal cost: cheap — it is a documentation statement plus one absent call.

**Decision 2 — the redirect hop inherits hop 0's headers, uniformly, rather than conditionally on the entry point.** Rejected: keeping `CreateRequest`-from-options for the URL overloads and inheriting only for `Send`. That is two redirect semantics in one method, keyed on a fact `HandleResponse` cannot see without being told, for zero behavioural difference on the URL side (§5). One rule, one path. Reversal cost: cheap.

**Decision 3 — silent, not loud.** Rejected: throwing (or asserting) when `Send` receives an options bag carrying `TokenProvider` or `Headers`. It reads as the obvious cure for "silent" and is a hard break for the most common usage in the library: a single shared bag carrying the token provider, reused across every call including `Send`. Making the escape hatch reject the bag every caller already holds trades a documentation gap for a compile-clean runtime failure at every existing call site. Reversal cost: n/a, not built.

**Decision 4 — the non-generic `Send` keeps not following redirects.** §3.1. Consistency with every other result-less overload beats local consistency between the two `Send` members; the alternative fixes one member and leaves five behaving the other way. Documented in D1 instead. Reversal cost: cheap, and belongs to a redirect-follower task rather than this one.

---

## 7. Known limits this change does not close

- **Credential forwarding to an off-origin redirect target.** For the URL overloads this is pre-existing and unchanged. For `Send`/`Send<T>` this change **introduces** it: a caller's `Authorization` now survives the hop, including to a different host. That is the correct trade — a redirect that drops the caller's credentials is a broken feature, not a safe one, and it aligns `Send` with the rest of the library rather than leaving it a special case. But it is a security-relevant change of direction and is named here rather than buried. Gating it (strip credentials when the redirect crosses origin, as browsers do) is a property of the whole follower and is filed as **#9619**. A caller who needs the guard today can throw from `UrlProcessor`, which sees the target before the hop is issued.
- **An explicitly-set `Host` header is copied onto a cross-host redirect**, where it is wrong. Copying it is a consequence of "copy everything but the body descriptors". No code in this library or its known consumers sets `Headers.Host`, so a third exclusion would be a guard for an unattested case (#1136 §6, defensive code for impossible scenarios); it is recorded as a limit instead. `Transfer-Encoding` is excluded despite being in the same "only a `Send` caller could set it" category because it is a body descriptor, which is the category the exclusion rule already names — no new rule is added for it.
- **`response.RequestMessage` can be null** with a caller-supplied handler that does not stamp it — the repo's own `SequenceHandler` needed an explicit line to do so. When it is null there are simply no headers to inherit and the hop goes out bare, mirroring the existing null-guard on `RequestUri` at `:252`. No fallback branch: reconstructing from options in that case would resurrect exactly the behaviour this change removes.

---

## 8. Coverage

`Http.Tests/HttpServiceRedirectTests.cs`, alongside the five redirect tests already there.

`SequenceHandler` currently records `RequestedUris` only. It needs to record the **requests**, so a test can assert the header set of each hop independently — that is the axis this whole change is about, and no existing fixture can see it.

Required cases:

| # | Case | Guards |
|---|---|---|
| 1 | `Send<T>` + `FollowRedirects`, caller header on the pre-built request: present on hop 0 **and** hop 1 | the fix |
| 2 | `Send<T>` + `FollowRedirects`, `HttpOptions.Headers` set: absent from **both** hops | that the escape-hatch contract is now uniform, not just consistent |
| 3 | `Get<T>` + `FollowRedirects` + `TokenProvider`: `Authorization` on hop 1 unchanged | no regression for the URL overloads (§5 row 2) |
| 4 | `Get<T>` + `FollowRedirects` + `TokenProvider`: `GetTokenAsync()` called **once** | the one observable difference in §5, pinned rather than left to drift |
| 5 | `Post<T>` + `FollowRedirects` + `ExpectContinue = true`: `Expect` on hop 0, **absent** on hop 1 | the exclusion rule |

Case 2 is the dual #114 §13.1.1 asks for: without it, a test suite that only asserts "the caller's header arrives" would pass equally for an implementation that applied *both* sources, which is the double-application failure Decision 1 rejects.

The `[TestCaseSource]` fan and the source-level guard added by PR #5 (`HttpServiceHeaderRedactionTests.cs`) are untouched — this change adds no option and no `CheckHttpResponse` call site, so neither guard's axis moves. Its two `Send` cases already carry their marker on the `HttpRequestMessage` for exactly the reason this document now states as contract; the comment there should point at this doc.

---

## 9. Open questions

1. **Patch or minor.** §5 recommends patch, on the convention's literal test — callers who opt into nothing are byte-identical. The other reading is "byte-identical for *every* existing caller", under which the `GetTokenAsync()` call-count change and the `Send` hop-1 header change make it a minor (`0.10.0-preview`). Recommendation stands at patch; the call is Toni's at packaging time and costs nothing to change.
