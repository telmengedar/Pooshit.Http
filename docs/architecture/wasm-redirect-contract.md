# Architectural Document: the redirect contract on WebAssembly

> **Repo path:** `docs/architecture/wasm-redirect-contract.md` (repository `telmengedar/Pooshit.Http`).
> **Node:** **#14652** — this document's own DiVoid node, maintained as a byte copy of this file.
> **DiVoid:** source task **#14620** · project **#2281** · repo map root **#8292** · `HttpService` **#8297** · `HttpOptions` **#8299** · request lifecycle **#8311** stage 1 · the IL trace this document rests on **#14654**.
> **Predecessor on the same task, relied on and not superseded:** **#14627** / `docs/architecture/wasm-redirect-divergence.md` (PR #28) — it answers *why the platform branch stays and what its comment must say*. **This document answers a different question: what the library promises on that platform, and where the promise is written.** §1.1 states the split so no reader has to diff the two.
> **Predecessors relied on for what the WASM path does *not* get:** **#9633** (the cross-origin credential strip) · **#14516** (verb-and-body preservation, the loud replay failure, D7 ownership) · **#14574** (#8316, the band) · **#14617** (#8323, the hop cap, and the desktop credential measurement) · **#14601** (the `UrlProcessor` contract).
> **Contracts cited as load-bearing:** Design Contracts **#1136** (§1 KISS/DRY/YAGNI, §2 existing systems first, §3 configurability is not free, §4 less is better, §5 checklist including the measurement-discipline rows, §6 anti-patterns) · Code Contracts **#114** §0 and **§4** (the XML-doc rule, which shapes D2, and the one sanctioned `//`) · falsifiable universals **#9951** and the discriminator rule **#14516** §D7.2.
> **Baseline:** `origin/master` @ `1457df8`, tree clean. Version on master: `0.15.1-preview`.

---

## TL;DR

**The question is not whether `AllowAutoRedirect` throws on WebAssembly. That is settled and it does not** (§3.1, re-verified here with a rigorous control). **The question is what this library promises a caller on that platform — and the answer today is that it promises things it does not implement there.**

**The contract, decided:**

> On WebAssembly redirect policy belongs to the user agent. `HttpOptions.FollowRedirects` has no effect, `HttpOptions.UrlProcessor` never runs, and none of the library's redirect guarantees apply — not the cross-origin strip of `SensitiveHeaders`, not verb-and-body preservation for 307/308, not the loud failure when a body cannot be replayed, not the hop cap. **The library makes no claim about what the browser does instead, in either direction.** A caller who needs this library's redirect policy cannot have it on that platform.

**What ships is that sentence, written where it contradicts what is written today.** Three public doc comments make unqualified claims that the library does not back on `browser-wasm`, and the sharpest is a **security** claim: `SensitiveHeaders` says its names *"are not carried onto a redirect hop which leaves the origin"*. On WebAssembly there is no hop this library controls, so that is a promise resting on another party's undocumented behaviour. **`UrlProcessor`'s contract has the same defect and was written this release** (#14601, `0.15.1-preview`) — which is the evidence that naming the divergence in design documents has not stopped it being re-omitted from the contracts the library actually ships.

**Measured here, and it closes the gap #14627 flagged as its own weakest point** (§3.2): the value of `AllowAutoRedirect` is genuinely consumed, and an IL trace through the browser assembly shows `false` producing a literal `fetch(…, { redirect: "manual" })`, `true` producing `"follow"`, and **never touching the setter omitting the key entirely**. The third state is the one the constructor's browser arm takes today, so *"WASM follows redirects transparently"* — assumed by every design in this arc since the map was built — is now **measured** rather than inherited. `"error"`, the Fetch standard's third mode, is **not present anywhere in the assembly**.

**Consequently D1's inference is one step wide, not two.** #14627 rested on *the runtime maps false to manual* **and** *manual yields an opaque response*. The first is now measured. **Only the second is still read from a specification**, and §10.1 names the ten-minute instrument that would settle it.

**No behaviour changes. No public signature changes. No new option.** #14627's D1 (keep the branch) and its rejection of *delete*, *refuse to construct* and *an option* are **adopted**, each with one reason added that #14627 did not give (§5). One alternative none of the four prior designs considered — **throw when `FollowRedirects` is set on WebAssembly** — is raised and rejected in D5, and it is the decision most sensitive to the one measurement nobody here can take.

**Also measured, and it inverted a recommendation this document nearly shipped** (§3.3): the package contains **no XML documentation file and no README** — two DLLs, a licence and the nuspec, nothing else. So *"fix the doc comment so callers see the limit"* is **false as stated**: a consumer never sees a doc comment from this package. The comments still get fixed — they are what a contributor and a reviewer read — and the consumer-facing carrier is the release note, the only prose this package ships. The packaging gap is **filed, not fixed here** (§10.3).

**Coverage: the browser cannot be guarded from this repo and this document does not pretend otherwise** (§7). What it does add is the guard #14627 looked for and concluded was unavailable — it sought the *handler's property*, which needs reflection; the observable is the *wire behaviour*, and `new HttpService()` over a loopback socket is a pattern this test project already ships.

---

## 1. Problem

#14620, whose own framing is the best statement of it:

> Every safety property this surface has accumulated is absent on one of its two platforms, silently, for a caller who wrote identical code.

The constructor branches on platform. Off `browser-wasm` it builds a handler with the transport's redirect following disabled, so redirect policy is this library's. On `browser-wasm` it takes a bare client, so redirect policy is the user agent's — and `HandleResponse`'s redirect branch is never reached, because no 3xx arrives.

Recorded on **#8311 stage 1** as *"the first place behaviour silently diverges"*; named without being owned by **#14574 §5.4** and **#14617 §9.3 item 4**; and taken up by **#14627** (PR #28), which established the mechanism and corrected the comment.

**What is still undecided after #14627 is the contract.** #14627 §4 states the honest form — *"on WASM the library makes no redirect guarantees and the user agent's rules apply"* — **in a design document**, and leaves the library's own public surface saying the opposite. That is not a criticism of its scope, which was the comment; it is the residue, and residue named by five documents and owned by none is the shape #14620 exists to stop.

### 1.1 The split between this document and #14627

Two documents on one task is the doc-debt #1136 §6 warns about when they answer the **same** question. They do not, and the split is stated here so nobody has to diff them:

| | #14627 / `wasm-redirect-divergence.md` | this document |
|---|---|---|
| Question | why the platform branch stays, and what its `//` comment must say | what the library promises on that platform, and where the promise is written |
| Artefact | one comment line in `HttpService.cs`, one source-reading guard | three doc comments, one release note, one behavioural guard |
| Relationship | **relied on, not superseded.** Its Measurement 1 is re-verified in §3.1, the measurement it flagged as missing is taken in §3.2, and its D1/D3 are adopted in §5 with additional reasons | **it is the predecessor.** Nothing here contradicts it |

**If PR #28 is closed unmerged, its one-line comment correction folds into this change** and §11 says where.

---

## 2. Scope

**In scope:** the contract sentence; the three public doc comments that contradict it; the release note; and one behavioural guard on the constructor's non-browser arm.

**Out of scope, unchanged, and not folded in:**

| Filed as | Not done here |
|---|---|
| **#14627** / PR #28 | the `//` comment correction and the source-reading guard. Owned there; adopted, not re-derived |
| **#8323** / PR #26 | the hop cap. This document names it in the contract's list; it does not build or change it |
| **#14648** | the result-less `Post<TRequest>(url, body, options)` not following redirects at all. A **second** context in which `FollowRedirects` has no effect, discovered independently. Adjacent and deliberately separate — it is a defect on the desktop path, not a platform contract |
| **§10.3** | `GenerateDocumentationFile` and a packed README. A packaging decision affecting every contract this library has; it does not belong inside a redirect-contract change |
| **§10.1** | the browser measurement. Needs a host this repo will never have |
| #14547, #14613 | unrelated surfaces |

**Also not done:** no behaviour change, no new option, no new public type or member, no change to `HandleResponse`, `FollowRedirect`, `SendRedirect`, `CreateRedirectRequest` or the band.

### 2.1 The outcome that must be true when this ships

> A caller reading this library's own description of its redirect and credential behaviour is not told it holds on a platform where the library does not implement it.

Every decision below is checked against that sentence, and §8 states what would break it.

---

## 3. What was measured, and what was reasoned

The brief required this split and required the two halves not be blended. §3.1–§3.5 are measurements. §3.6 is read from a specification and is labelled as such everywhere it is used. §3.7 records what was discarded and why, because a discarded measurement is the only evidence anyone will ever have about the instrument (#1136 §5).

Instrument for §3.1 and §3.2: the shipped `Microsoft.NETCore.App.Runtime.Mono.browser-wasm` runtime packs for **6.0.36, 7.0.20 and 8.0.12**, read metadata-only through `System.Reflection.Metadata`. Nothing was executed. Full trace and raw output: **#14654**.

### 3.1 Re-verified — the constraint in the comment is gone, and the control is what makes that readable

In `BrowserHttpHandler`, **identical in all three versions**:

| Property | setter body |
|---|---|
| **`AllowAutoRedirect`** | **15 bytes, a real implementation** |
| `MaxAutomaticRedirections`, `Credentials`, `CookieContainer`, `UseCookies`, `Proxy`, `UseProxy`, `DefaultProxyCredentials`, `PreAuthenticate`, `AutomaticDecompression`, `MaxConnectionsPerServer`, `MaxResponseHeadersLength`, `SslOptions` | 6 bytes — `newobj PlatformNotSupportedException::.ctor(); throw` |

**The control is what licenses reading "no throw" as "supported", and its first form was wrong** — §3.7 records how. In its rigorous form: every settable property on **both** types was enumerated (`BrowserHttpHandler` 14–15, `HttpClientHandler` 25–26) and the comparison restricted to the **13 names that exist on both**. On that domain the throwing set equals the `[UnsupportedOSPlatform("browser")]` set **exactly, in all three versions**. Names present on only one type are reported as outside the comparison rather than folded into a pass or a fail.

**So `// wasm crashes with allowautoredirect` is false and has been for the whole supported lifetime of the platform.** The live falsifier is a runtime older than 6.0, which this package's `netstandard2.0` target can still be consumed on — §8.

### 3.2 Measured here — the value is consumed, and it reaches `fetch` as a literal redirect mode

**This is the measurement #14627 §2.1 item 4 could not make and named as inferred.** It had the presence of the strings `"manual"`, `"follow"` and `"redirect"` in the assembly as circumstantial evidence that *"the setting reaches `fetch`"*. A string constant in an assembly is not evidence that **this property** feeds it. The trace closes that.

**The property is a genuine backing field, plus a second field that gates it** — byte-identical across all three versions:

```
get_AllowAutoRedirect   ldarg.0 ; ldfld _allowAutoRedirect ; ret
set_AllowAutoRedirect   ldarg.0 ; ldarg.1 ; stfld _allowAutoRedirect
                        ldarg.0 ; ldc.i4.1 ; stfld _isAllowAutoRedirectTouched ; ret
.ctor                   ldarg.0 ; ldc.i4.1 ; stfld _allowAutoRedirect
```

`_isAllowAutoRedirectTouched` is set only by the setter, never by the constructor, so the value the send path sees is a **tri-state**: untouched, `true`, `false`.

**The branch that picks the string** (8.0.12, inside `CallFetch`'s generated state machine; 7.0.20 is the same shape; 6.0.36 writes the same option through `JSObject::SetObjectProperty` in a flattened method — architecturally different, semantically identical):

```
ldflda   allowAutoRedirect          ; Nullable<bool>
call     Nullable<bool>::get_HasValue
brfalse  -->                        ; untouched: NO "redirect" key is sent at all
ldstr    "redirect"
call     Nullable<bool>::get_Value
brtrue   -->
ldstr    "manual"                   ; AllowAutoRedirect == false
br       -->
ldstr    "follow"                   ; AllowAutoRedirect == true
```

**Three findings, each settled by the trace rather than inferred:**

1. **`AllowAutoRedirect = false` produces `fetch(…, { redirect: "manual" })`.** #14627's first inference is now a measurement.
2. **The untouched state omits the key**, so the browser default (`follow`) governs. **The constructor's browser arm takes exactly this path** — a bare `HttpClient`, the setter never touched. So *"on WebAssembly redirects are followed transparently"*, which every document in this arc has asserted since #8311 was built, is measured here for the first time rather than assumed.
3. **`"error"` — the Fetch standard's third redirect mode — does not occur anywhere in any of the three assemblies.** A clean negative from a global scan, not an absence of evidence. There is no mapping other than the two above to hope for.

**What the trace cannot settle, and does not claim to:** what a browser *does* with `redirect: "manual"` at runtime. That is §3.6 item 1, it is read from a specification, and metadata-only reading can never reach it. §10.1 is the instrument.

### 3.3 Measured here — what the shipped package actually contains

**Instrument: `dotnet pack` on `Pooshit.Http.csproj` at `0.15.1-preview`, then listing the archive.** Contents, complete:

```
LICENSE
Pooshit.Http.nuspec
[Content_Types].xml
_rels/.rels
lib/net8.0/Pooshit.Http.dll
lib/netstandard2.0/Pooshit.Http.dll
package/services/metadata/core-properties/<guid>.psmdcp
```

**No `lib/**/*.xml`. No `<readme>` element in the nuspec.** `GenerateDocumentationFile` is set nowhere in the repo — there is no `Directory.Build.props` — so the compiler emits no documentation file and `dotnet pack` has none to include.

**Consequence, and it inverts a recommendation this document nearly shipped:** a consumer of this package sees no doc comment, in IntelliSense or anywhere else. The doc-comment corrections in D2 are therefore a fix for **the contributor and the reviewer**, not for the caller. The caller-facing prose this package ships is `<PackageReleaseNotes>`, which the nuspec carries and nuget.org renders, and nothing else. D2 and D6 are split along exactly that line.

### 3.4 Measured here — the existing test suite is structurally blind to the constructor's platform branch

| Fixture | constructs | reaches the platform branch? |
|---|---|---|
| `HttpServiceRedirectTests` (60+ tests) | `new HttpService(handler)` at every site | **no** — a supplied handler takes the constructor's *first* arm and the branch is never evaluated |
| `HttpServiceStatusBandTests` | `new HttpService(handler)` | **no**, same reason |
| `HttpServiceMediaTypeFallbackTests`, `HttpServiceExplicitMediaTypeRefusalTests` | `new HttpService()` parameterless, over `LoopbackServer` | **yes — the branch runs**, but the server answers only `200`, so nothing observes the setting it produces |

**So the entire redirect corpus cannot notice the constructor at all, and the two fixtures that do construct it never give it a redirect to not-follow.** That is why the guard in §7 row 2 is new rather than a gap in an existing row, and it is also why `SequenceHandler` — which can script any status trivially — **cannot** be used for it: passing a handler is precisely the thing that bypasses the code under test.

### 3.5 Measured previously, carried here, and explicitly not carried to WebAssembly

From **#14617 §3.1**, on desktop and two loopback origins: `AllowAutoRedirect`'s own follower strips **`Authorization` and nothing else** — `X-Api-Key`, `Cookie` and a caller-registered `X-Tenant-Secret` all reached the foreign origin.

**That measurement is desktop-only and this document does not carry it to the browser.** A browser applies CORS, which is a different mechanism and is plausibly stricter in places. §4 says what follows, which is less than #14620 anticipated.

### 3.6 Reasoned, not measured — what the Fetch standard says

Everything here is read from a specification. **A browser could falsify any of it**, and §8 marks each accordingly. Note that §3.2 removed the *mapping* half of what #14627 had to infer; what remains is only what the browser does with a mode we now know is sent.

1. **`redirect: "manual"` yields an opaque-redirect filtered response** — type `opaqueredirect`, status `0`, an empty header list, no `Location` exposed to script. **This is the single load-bearing inference of this document and of D1.**
2. **The user agent has its own hop cap of 20.** So the honest word for that row of the guarantee table is **different**, not *absent* — #14620's own framing says the hop cap is absent, and for this row that is too strong.
3. **A 3xx the user agent will not follow still reaches the caller** — notably a redirect response carrying no `Location`, which fetch returns as-is. After #14574 that raises on WebAssembly exactly as it does on desktop, which is one place the two platforms already agree.
4. **Credentials across a browser redirect are governed by CORS**, not by anything this library can reach. Whether that is stricter or looser than §3.5's desktop result is **not claimed here in either direction** (§4).

### 3.7 Discarded, and what made each wrong

**The control's first form reported a mismatch, and the mismatch was an artefact of the comparison rather than of the subject.** Comparing thirteen named `BrowserHttpHandler` setters against `HttpClientHandler`'s annotation set said *"`SslOptions` throws but is not annotated"* — which would have invalidated the whole reading, because a throw without an annotation means the annotation set cannot be used as a control. **`HttpClientHandler` has no `SslOptions` property at all**; it has `SslProtocols` and `ClientCertificates`. Two incommensurate property sets were being compared by name. The fix was to restrict the comparison to the thirteen names present on **both** types and to report the rest as outside it. **Recorded because a control that fails for a reason about the control is indistinguishable, at a glance, from one that fails for a reason about the subject** — and the tempting move is to drop the offending row.

**The first consumer scan missed the real sink entirely, at a layer boundary nobody would think to look for.** Matching direct `ldfld`/`ldsfld` against `_allowAutoRedirect` found only the getter, the setter and the constructor — i.e. it reported that nothing consumes the value. That is **false**: on 7.0 and 8.0 the string-selection logic lives inside `CallFetch`'s compiler-generated async state machine, which captures the value into **its own field**, a distinct metadata row. Finding it needed the scan widened to calls of the getter and then one manual hop along `SendAsync` → `Impl` → `CallFetch` → the state machine. **This is the same class of error #14627 §2.1 records for `HttpClientHandler` versus `BrowserHttpHandler`** — a measurement answered by the wrong layer — except the boundary here is a compiler-synthesised closure rather than a hand-written forwarding call, so no reading of the source would have exposed it. **Had the scan been believed, this document would have reported "the setting is stored and never read", which is the opposite of the truth and would have argued for deleting the branch.**

**A `grep` for `GenerateDocumentationFile` across the repo.** It returned nothing, and "nothing" is the right answer to *"is the property set"* — which is not the question. The question is *what the package contains*, and only the pack answers it (§3.3). The two agree here; the point is that they are different questions, and the grep could not have detected a documentation file included by an explicit `<None Include>`. Recorded because the grep is the cheaper instrument and therefore the one the next author reaches for.

**A recommendation, drafted and withdrawn: "fix the XML doc so a caller reading IntelliSense learns the platform limit."** Withdrawn by §3.3 — there is no XML doc in the package, so no caller reads one. The recommendation survives in a narrower form, but the *reason* changed and with it the carrier for the consumer-facing half (D6). **Recorded rather than silently corrected**, because the wrong version is the plausible one and the next author will draft it too.

**An earlier framing: "`SensitiveHeaders` makes a promise that is false on WebAssembly."** Withdrawn as an over-claim. Nothing measured says the browser fails to strip those headers; §3.5 is desktop-only and §3.6 item 4 is a specification reading. The accurate statement is that the library **promises something it does not implement there**, and a security promise backed by another party's undocumented behaviour is not a promise regardless of how that party happens to behave. That is the form used throughout, and it is weaker and correct.

---

## 4. The credential question, answered with its boundary

**No claim is made, in either direction, about what a browser does with a credential across a redirect.**

§3.5 is a desktop measurement. §3.6 item 4 is a specification reading. **Neither is a browser observation, and no browser observation was taken** — this repo has no `wasm-tools` workload and no browser host, and a Blazor page is the instrument (§10.1). *"I could not run one"* is the result, reported as such.

What follows for the contract is narrower than #14620 anticipated and is the honest form:

> The library does not strip `SensitiveHeaders` on a WebAssembly redirect, because it does not perform that redirect. What the user agent does instead is the user agent's rule and this library does not characterise it.

**That is not the same sentence as "WASM leaks your vendor key", and writing the second would be the over-claim this arc has already paid for.** It is also not "WASM is fine" — the caller-registered case remains the sharp one, because `SensitiveHeaders` is public and mutable precisely so a consumer can name a credential the library could not have known about (#9633), and no transport-level or user-agent-level policy can honour a name it has never seen.

---

## 5. Decisions

### D1 — keep the platform branch. Adopted from #14627, with its inference narrowed from two steps to one

#14627 D1 rests on two claims: that `AllowAutoRedirect = false` maps to `redirect: "manual"`, and that `manual` yields an opaque response. Its §8.1 correctly named the pair as the thing a reviewer would attack, because both came from documents rather than runs.

**§3.2 measures the first.** The IL trace shows `false` producing a literal `"manual"`, `true` producing `"follow"`, and `"error"` absent from the assembly entirely — so there is no third mapping and no ambiguity about what the runtime sends. **Only the second claim is still read from a specification**, and it is marked as such in §3.6 item 1, §8 and §10.1.

**What deleting the branch would cost:** on WebAssembly every redirect goes from transparently followed (§3.2 finding 2, measured) to a `fetch` with `redirect: "manual"` (measured) returning a response the library cannot act on — status `0` matching no redirect arm and falling to the band, which raises (#14574), with no `Location` to chase. That is a regression on the one platform the deletion would claim to improve.

**And the honest residual:** if §3.6 item 1 turned out wrong — if some browser did expose `Location` under `manual` — D1 would need re-deciding. §10.1 settles it in ten minutes for anyone with a Blazor host. **Until then this decision rests on one specification reading, and that is one fewer than it did yesterday but not zero.**

### D2 — the contract is written on the three surfaces that currently contradict it

**This is the decision of this document.** Three public doc comments describe redirect and credential behaviour without the platform limit:

| Surface | today | why it is wrong |
|---|---|---|
| `HttpService.SensitiveHeaders` | *"…and they are not carried onto a redirect hop which leaves the origin"* | **a security promise.** On WebAssembly there is no hop this library controls, so the promise rests on another party's undocumented behaviour. The sharp case per #14620: the set is public and mutable, so a caller registers names no transport can know |
| `HttpOptions.FollowRedirects` | *"determines whether to follow redirects automatically"* | on WebAssembly it determines nothing. Redirects are followed whatever it is set to |
| `HttpOptions.UrlProcessor` | *"…runs on the redirect path only, before the target is resolved against the request url and therefore before the same origin decision which governs whether `SensitiveHeaders` ride the hop"* | describes a mechanism that does not exist on that platform — **and it was written this release** (#14601, PR #24). It is the evidence that the divergence is still being re-omitted from new contracts, five designs after it was first recorded |

**The shape of the correction is dictated by #114 §4**, which says an XML summary is one tight line saying *what the thing is*, carries no rationale and no history, and sends the *why* to the design document. So:

- **The platform fact is stated once, on the member that owns the decision.** `FollowRedirects` gains a clause saying it has no effect on WebAssembly, where redirects are followed by the user agent. One clause, no reason — the reason is this document.
- **The other two are re-scoped to the mechanism rather than repeating the platform.** `SensitiveHeaders` promises its names are not carried onto *a redirect hop this library follows* which leaves the origin; `UrlProcessor` describes *the redirect path this library follows*. Both sentences then become true on every platform without growing a platform paragraph each, and the library-follows-none case is covered by `FollowRedirects`' clause.

**Why not put the platform clause on all three:** it is the same fact three times (#1136 §1 DRY), and it would push two already-long summaries further past #114 §4 for no information a reader does not get by composing them.

**Precedent for the instrument, not invented here:** `IHttpService` already says `HttpOptions.FollowRedirects` *"has no effect"* on `Send`. Same clause, second context.

### D3 — no refusal to construct on WebAssembly

Adopted from #14627 D3, whose reason stands: it is a breaking change on a platform whose callers work today, to guard a hazard §4 says is uncharacterised — #1136 §6's guard for a scenario nobody has established.

**The reason #14627 did not give, and it is the decisive one:** the refusal's only escape is *"supply your own handler"*, and the handler a WASM caller would supply is one whose `AllowAutoRedirect` they never touch — which §3.2 finding 2 measures as sending **no redirect key at all**, i.e. **exactly the auto-redirecting client the refusal was guarding against**. A guard whose escape hatch restores the guarded state buys nothing and costs every WASM caller a line of ceremony.

### D4 — no option to choose the policy

Adopted from #14627 D3. #1136 §3 wants a named operator and there is none; and the option could not be honoured whichever way it were set, because the library cannot follow a redirect the browser will not surface (D1).

### D5 — do **not** throw when `FollowRedirects` is set on WebAssembly

**Considered by none of the four prior designs**, and it is the alternative a reviewer reaches for once D3 is rejected, because it is the narrow form of the same instinct: refuse only the caller who asked for the thing that cannot be delivered, leave everyone else working. It also looks like this library's own idiom — #14516 made the hop fail loudly rather than downgrade quietly when a body could not be replayed.

**Rejected, and the reason is what the option actually says.** `FollowRedirects` says *follow redirects*. On WebAssembly redirects are followed (§3.2 finding 2, measured). The caller's stated request is **satisfied** — by someone else — so raising would convert a satisfied call into an exception. The clause that is not honoured is an unwritten one, *"follow them under this library's policy"*, and the remedy for an unwritten clause is to write it down (D2), not to raise on it.

**The falsifier, and this is the decision most sensitive to the one measurement nobody took:** if §10.1 comes back showing the user agent carries a caller-registered credential to a foreign origin, the divergence stops being *different* and becomes *worse*, and a loud refusal for the caller who has both `FollowRedirects` and a non-default `SensitiveHeaders` entry returns to the table. **Until that measurement exists, a throw would be a guard built on a specification reading**, which is the shape D3 rejects.

### D6 — the consumer-facing carrier is the release note, and the packaging gap is filed

§3.3 measured that no doc comment reaches a consumer. D2 fixes the comments for the reader who has the source; the reader who has only the package needs the contract sentence in `<PackageReleaseNotes>`, which is the one prose surface this package ships.

**Precedent:** `0.15.1-preview` gave a documentation-only change — the `UrlProcessor` contract — its own release-note paragraph. This contract is more load-bearing than that one and gets the same treatment.

**Not fixed here:** turning on `GenerateDocumentationFile` and packing a README. That is a packaging decision affecting every contract this library has rather than this one, it would put a packaging change inside a redirect-contract PR, and #1136 §2 is explicit that a new concern does not ride along on the change that noticed it. Filed — §10.3.

### D7 — rejected: a read-only capability member so a caller can branch

A `bool` or enum on `HttpService` reporting who owns redirect policy. **Rejected on two counts.** #1136 §3 and §4: no named consumer asks for it. And more decisively — **a caller who could branch has nothing to branch to.** The library offers no WebAssembly redirect policy to select, so the member would report a fact whose only possible use is to log it.

**Named because it is the only shape in the option space nobody has written down yet**, and a reviewer who rejects D3, D4 and D5 will land on it next.

### D8 — rejected: surface the final URL a browser redirect landed on

The one capability the platform genuinely offers that the library does not use: a followed fetch response exposes `redirected` and the final `url`, so the library could tell a WebAssembly caller *that* they were redirected and *where to*, after the fact.

**Rejected.** It is detection, not policy — it arrives after the hop, so it strips nothing, preserves nothing and caps nothing, and none of §4's absent guarantees come back. **It is also unmeasured** whether .NET's browser handler surfaces it at all; §3.2 traced the request side of the interop, not the response side. No named consumer (#1136 §3). Recorded because it is the honest answer to *"is there really nothing available on that platform"* — there is one thing, and it is not the thing.

---

## 6. Consequences

### 6.1 Who is affected

| Caller shape | Change |
|---|---|
| Any caller, any platform | **none.** No behaviour, no signature, no option moves |
| A contributor reading `SensitiveHeaders`, `FollowRedirects` or `UrlProcessor` in source | the summary stops claiming something the library does not implement on one of its two platforms |
| A consumer reading the release note | learns the platform contract in one paragraph |
| A consumer reading only IntelliSense | **still learns nothing — the package ships no XML docs** (§3.3). That is §10.3's task, not this one, and saying otherwise would be the coverage over-claim #1136 §6 is about |

### 6.2 Version and sequencing

**Patch.** The convention is *patch when behaviour is byte-identical for callers who opt into nothing*, and here it is byte-identical for every caller including those who opt into everything. Relative to whatever this lands on — `0.15.1-preview` today, `0.16.0-preview` if PR #26 lands first.

**Sequencing is a precondition, not a preference.** This change edits `HttpService.cs` (the `SensitiveHeaders` summary) and `HttpOptions.cs`. **PR #26 and PR #28 both hold `HttpService.cs`**, so it lands after both. That also means D2's `SensitiveHeaders` edit and #14627's comment correction will be nearby lines in the same file, made by different PRs — §11 orders them.

---

## 7. Coverage

**The browser cannot be guarded from this repo, and this section does not pretend it can.** `browser-wasm` needs the `wasm-tools` workload (not installed) plus a browser host to execute in; and the open question is what `fetch` does at runtime, so compiling for browser would prove nothing. That is #14627 §7's finding and it stands unchanged — **§3.2 narrowed what is unmeasured, it did not make it testable from here.**

Rows name the guard and the fourth column is what makes it go red **uniquely**.

| # | Guard | Where | Goes red uniquely when |
|---|---|---|---|
| 1 | `ConstructorKeepsTheBrowserArmAndDisablesRedirectsOnlyOffIt` | `Http.Tests/HttpServiceConstructorTests.cs` — **exists on PR #28's branch, not on master.** Adopted, not rewritten | the **platform test itself** is deleted, i.e. the branch collapses to an unconditional `AllowAutoRedirect = false`. That change is byte-identical on every runtime this project can execute on, so **no behavioural test can catch it** — which is what keeps row 1 from being redundant against row 2. Pins source shape; cannot fail for a browser reason |
| 2 | `DefaultConstructedService_Redirect302_IsNotFollowedByTheTransport` | **new**, same fixture. `new HttpService()` with **no handler**, against `LoopbackServer` answering `302` with a `Location` to a second path, `FollowRedirects` unset; assert `HttpServiceException` **and that the server received exactly one request** | the constructor stops disabling transport redirect following on this platform — deleting the whole branch, or flipping the setting to `true`. **The only row that pins the effect rather than the shape** |
| 3 | `DefaultConstructedService_Redirect302_FollowRedirects_IsFollowedByTheLibrary` | **new**, same fixture. As row 2 with `FollowRedirects = true`; assert the second resource's body and **exactly two** requests | **dual.** Row 2 alone passes if the loopback server is broken and every call throws for an unrelated reason; this row fails in that world and is what makes row 2's red mean what it says |
| — | the WebAssembly contract itself | **no row exists** | nothing. Stated as a finding, not filled with a proxy. §10.1 names the instrument |

**No row asserts comment text.** #14627 D2 declined to pin the `//` comment's prose on the grounds that a source-reading test asserting prose converts a comment into behaviour and makes every #114 §4 edit a failure. **That reasoning is adopted and extends to D2's three XML summaries** — none of them gets a grep-shaped guard. A documentation change in this repo is verified by review, and saying otherwise would be the coverage over-claim #14528 was about.

### 7.1 The guard #14627 concluded was unavailable, and why it is available

#14627 D3 skipped the handler-observing guard because `readonly HttpClient client` is private with no accessor, there is no reflection anywhere in the test project, and #114's *RULING 2026-08-08* refuses `InternalsVisibleTo` for testability. **All of that is correct about observing the handler's property — and the property is the wrong observable.** The constructor's effect is visible on the wire: a default-constructed service either follows a `302` or does not, and a socket can tell you which.

**This arc has now made the layer error in three directions, which is why it earns a subsection rather than a footnote.** #14627 §2.1 probed `HttpClientHandler` where the decision lived in `BrowserHttpHandler` — one layer too high, caught by a known-negative control. §3.7 records a consumer scan that stopped at the handler's own field where the decision lived in a generated state machine — one layer too shallow, and it would have reported the exact opposite of the truth. And here a guard was sought at the handler's property where the observable is the socket — one layer too low. #1136 §5 covers all three: *a measurement with no control cannot distinguish "the subject behaves this way" from "I am not talking to the subject."*

### 7.2 Fixture change, and the alternative that cannot be used

`LoopbackServer` today writes a hardcoded `HTTP/1.1 200 OK` status line and accepts **one** connection. Rows 2 and 3 need a chosen status line with a `Location`, and row 3 needs two connections.

**The change is to extend it, not to add a sibling** (#1136 §2 — a parallel fixture with a slightly different shape is Form 2). It gains the ability to answer a **sequence** of scripted responses, each with its own status line, headers and body, and to record what was requested; **the current constructor's behaviour must survive as the one-element case**, because `HttpServiceMediaTypeFallbackTests` and `HttpServiceExplicitMediaTypeRefusalTests` depend on it.

**`SequenceHandler` cannot be used for these rows, by construction.** It scripts statuses trivially and it is the obvious reach — but it is supplied to the constructor as a handler, which takes the **first** arm and bypasses the platform branch entirely (§3.4). The fixture that makes redirect testing easy everywhere else is the one thing that cannot test this.

---

## 8. Falsifiable claims (#9951 + #14516 §D7.2)

| Claim | Where | What would falsify it | Does that class exist? |
|---|---|---|---|
| "`AllowAutoRedirect` does not throw on `browser-wasm`" | §3.1 | a supported runtime whose `BrowserHttpHandler` setter is a `PlatformNotSupportedException` stub | **No, across 6.0.36 / 7.0.20 / 8.0.12** — measured in the shipped assemblies. **Live falsifier: a runtime older than 6.0**, which this package's `netstandard2.0` target can still be consumed on, and which is presumably where the comment came from |
| **discriminator:** "the throwing setters are exactly the unsupported ones" | §3.1 | a setter that throws without the annotation, or carries the annotation without throwing | **No, on the comparable domain** — the thirteen names present on both types matched exactly in all three versions. **The first form of this control reported a false mismatch** and §3.7 records why; without the control, "no throw found" would only have meant the scanner missed it |
| "the stored value is consumed and selects a `fetch` redirect mode" | §3.2 | a path that reads `_allowAutoRedirect` and does something else with it, or a second writer of `_isAllowAutoRedirectTouched` | **No** — traced to the single `ldstr` site in all three versions, with a global scan confirming `"manual"`, `"follow"` and `"redirect"` occur once each and only there. **The first scan said nothing consumed it**; §3.7 records the closure boundary that hid it, and that failure is the reason this row is phrased about the *sink* rather than about the field |
| "`AllowAutoRedirect = false` sends `redirect: "manual"`, and there is no third mapping" | §3.2, D1 | an `ldstr "error"` anywhere on the path | **No** — `"error"` is absent from all three assemblies. A clean negative from a global scan, not an absence of evidence |
| "on WebAssembly redirects are followed transparently today" | §3.2 finding 2 | a path that touches the setter on the bare-client arm | **No** — the constructor's browser arm constructs `new HttpClient()` and never reaches a handler property, so the key is omitted and the browser default governs. **Asserted by every document in this arc since the map was built and measured here for the first time** |
| "`redirect: "manual"` yields a response with no readable `Location`" | §3.6 item 1, D1 | a browser that surfaces `Location` on an opaque-redirect response | **Not under the standard** — the filtered response has an empty header list by definition. **This is read from the specification, not observed, and it is the one load-bearing inference left in this document.** §10.1 is the instrument |
| "deleting the branch regresses WebAssembly callers" | D1 | the row above being false | **No**, given it — and the mapping it depends on is now measured rather than inferred |
| "the library's redirect guarantees do not apply on WebAssembly" | the contract, §4 | a guarantee that holds without `FollowRedirect` / `SendRedirect` running | **No** — every one is implemented inside those two methods, reached only from `HandleResponse`'s `FollowRedirects` branch, which no 3xx arrives at. Derived from code in the tree |
| "the hop cap is **absent** on WebAssembly" | **not claimed** | — | **#14620 says absent and that is too strong.** Fetch gives the user agent a cap of 20, so the row is *different*, not absent. **Read from the specification**, not observed |
| ~~"WebAssembly leaks credentials cross-origin"~~ | **not claimed, in either direction** | — | §3.5 is desktop-only, §3.6 item 4 is a specification reading, and **no browser measurement was taken**. §4 states the boundary |
| "no XML documentation reaches a consumer of this package" | §3.3 | a nupkg containing `lib/**/*.xml` | **No** — measured by packing `0.15.1-preview` and listing the archive in full |
| "no existing test observes what the constructor's platform branch produces" | §3.4 | a test that constructs `HttpService` **without a handler** and asserts on redirect behaviour | **No.** The two parameterless fixtures reach the branch but their servers answer only `200`; every test in the redirect and band fixtures supplies a handler and takes the first arm. Both halves are greppable: `new HttpService(` with no argument, and `LoopbackServer`'s status line |

### 8.1 The claim I expect a reviewer to attack first

**That a documentation change is the right answer at all**, given the task says *"every safety property this surface has accumulated is absent on one of its two platforms"*. The instinct that a safety gap deserves a code change is a good one and D3, D5, D7 and D8 are where it gets tested.

**The reason it loses is that no code change available on that platform closes the gap.** D1 shows the library cannot obtain the hop; D3's refusal has an escape that §3.2 measures as restoring what it guards; D5 raises on a caller whose request was satisfied; D7 reports a fact with nothing to do about it; D8 detects after the fact. **What is left is a promise the library should stop making, and that is a documentation change because the defect is in the documentation.**

**What would change the answer** is §10.1 coming back badly — a measured browser behaviour a caller would call wrong rather than merely different. Then D5 returns first.

---

## 9. Pre-Design Checklist (#1136 §5)

| Item | Verdict |
|---|---|
| No new type mirroring an existing one | **Pass** — nothing added |
| No new abstraction with one implementation | **Pass** |
| Nothing justified by "we might need X later" | **Pass** — D4, D7 and D8 each rejected for a named absent consumer |
| No deprecation window / compat shim / feature flag | **Pass** — no behaviour changes at all |
| DRY math on inline-vs-extract | **N/A**, and applied qualitatively in D2: the platform fact is stated once rather than three times |
| Existing systems first | **Pass** — the contract is written into the doc comments and the release note that already exist; the guard extends `LoopbackServer` rather than adding a sibling (§7.2); PR #28's guard is adopted, not rewritten |
| Every config knob has a named operator | **Pass by removal** — D4 and D7 record that none could be named |
| Can-it-be-deleted / merged / inlined | **Pass, and it is most of §5** — run against the branch (D1), the refusal (D3), the option (D4), the narrow throw (D5), the capability member (D7), the final-URL surface (D8) |
| Trade-offs named explicitly | **Pass** — §4 (what is not known about the browser), D1 (the residual inference), D2 (why not the platform clause on all three), D5 (the decision that turns on an unmeasured fact), D6 (the gap the release note does not close), §7 (no browser guard exists) |
| Out-of-scope listed explicitly | **Pass** — §2 table |
| No multi-paragraph rationale for things that obviously stay | **Pass** |
| Predecessor banner where superseded | **N/A** — §1.1 states the split with #14627, which is relied on rather than superseded. Nothing here contradicts it |
| Coverage rows name the test identifier | **Pass** — §7, three named guards, and the missing browser row is stated as absent rather than filled with a proxy |
| **Every figure is in the metric the defect is in** | **Pass** — §3.3 packs rather than greps, and §3.7 records why the grep answers a different question. §3.2 measures the *sink* rather than the presence of a string constant, which is the metric #14627 had available and correctly labelled as inference |
| **Discarded measurements recorded with what made them wrong** | **Pass** — §3.7, five of them, including a control that failed for a reason about the control, a scan that reported the opposite of the truth, and a recommendation withdrawn |
| **A control wherever a measurement could be taken at more than one layer** | **Pass, and it was load-bearing twice.** §3.1's control is the thirteen-name intersection, whose first form produced a false mismatch that had to be investigated rather than dropped. §3.2's "control" is the global `ldstr` scan: `"manual"`, `"follow"` and `"redirect"` occurring exactly once each and only on the traced path is what distinguishes *"I found the sink"* from *"I found a sink"*, and `"error"`'s total absence is what makes the mapping exhaustive rather than merely observed. §7.1 names the three layers this arc has now been wrong at |

---

## 10. Open questions

### 10.1 The measurement nobody here can take — and it is now the only unmeasured thing D1 rests on

A Blazor WebAssembly page against a redirecting endpoint. **Three answers in one run:**

1. With `AllowAutoRedirect = false` — which §3.2 measures as sending `redirect: "manual"` — the status the library is handed, and whether `Location` is readable. **Settles §3.6 item 1, the last inference under D1.**
2. With the default client: whether a cross-origin redirect carries a **caller-registered custom header** and a cookie to the second origin. **Settles §4**, replacing "not claimed in either direction" with a fact.
3. Whether a redirect the user agent declines to follow (no `Location`) reaches the library as a 3xx. **Settles §3.6 item 3.**

**Answer 2 is the one that matters for D5.** Ten minutes for anyone with a Blazor host; this repo will never have one. **Filed as #14655**, which names the CORS trap the probe has to avoid — a CORS failure and a credential strip look identical from the client.

### 10.2 Does D5 survive that measurement?

Flagged separately because it is the only decision here whose input is missing rather than merely inferred. If the user agent carries a caller-registered credential off-origin, *different* becomes *worse*, and the narrow refusal — or at minimum a louder contract than a doc clause — comes back on the table. **Recorded now so the person who runs §10.1 knows which decision to bring back rather than having to rediscover it.**

### 10.3 Should the package ship XML documentation and a README?

**Needs Toni.** §3.3 measured that it ships neither, so every contract this library has written down — the `UrlProcessor` contract, the `Send` escape-hatch contract, the `SensitiveHeaders` dual behaviour, and now this one — is invisible to a consumer who has only the package. That is a bigger finding than this task and it is **deliberately not fixed here** (D6). It is a packaging policy question, it affects every member rather than three, and it does not belong inside a redirect-contract PR.

### 10.4 The map nodes still carry the false claim

**#8311 stage 1** and **#8297** both say the setting *crashes* on WebAssembly. Both are stale in exactly the way the `//` comment was, and #8297 is additionally behind on the success band (#8316, closed by #14574). **Not fixed here and not to be built on** — #14627 §11 step 3 already owns the reconcile for the comment's half. Named so a reader of either node checks it against §3.1 first. **Both also assert transparent following on WebAssembly, which was unmeasured until §3.2 and is now correct** — a rare case where a stale node was right by luck, and worth recording so the reconcile does not strike a true sentence along with the false one.

### 10.5 Is two design documents on one task the right shape?

§1.1 argues yes, because they answer different questions and the split is stated rather than left to be diffed. **A reviewer may reasonably disagree** and prefer this document folded into #14627 as a second half. I am raising it rather than deciding it unilaterally, because #1136 §6's superseded-design rule exists for precisely the reader who now has two files to reconcile. If the answer is "fold", the merge is mechanical — nothing here contradicts #14627 — and the cost is that a merged document mixes a merged PR's content with an unmerged one's.

---

## 11. Implementation order

Each step leaves the suite green. **Steps 1–2 are preconditions held by other branches.**

1. **Wait for PR #26 and PR #28.** Both hold `HttpService.cs`. If **#28 is closed unmerged**, fold its one-line `//` comment correction into step 5 — the comment and the `SensitiveHeaders` summary are near neighbours and the reasoning is #14627 D2's, adopted unchanged.
2. **Adopt PR #28's `HttpServiceConstructorTests` fixture** as the home for rows 2 and 3 rather than creating a second constructor fixture. If #28 did not merge, create it.
3. **Extend `LoopbackServer`** per §7.2 — scripted response sequence with status line and headers, request recording, current behaviour preserved as the one-element case. **Run the two existing loopback fixtures before touching anything else**; they are the regression surface for this step.
4. **Write rows 2 and 3** (§7) and confirm each reds under its own mutation: delete the branch, and flip the setting to `true`. Row 3 must be red when the fixture is broken — verify by breaking it deliberately once.
5. **Correct the three doc comments** (D2). One clause on `FollowRedirects` naming the platform; `SensitiveHeaders` and `UrlProcessor` re-scoped to *a redirect hop this library follows*. **#114 §4 governs: one tight line, what it is, no rationale, no ticket reference.**
6. **Bump patch and write the release-note paragraph** (D6) from the contract sentence in the TL;DR. It must say that redirect policy on WebAssembly is the user agent's, that `FollowRedirects` and `UrlProcessor` have no effect there, that the `SensitiveHeaders` strip does not run, and that the library makes no claim about what the browser does instead.
7. **Reconcile the map** — #8311 stage 1 and #8297 (§10.4), coordinating with #14627 §11 step 3 so the constructor sentence is corrected once rather than twice, and keeping the transparent-following sentence that §3.2 confirms. Concept nodes count (#3414).
8. **§10.1 is already filed as #14655** — nothing to do but keep it linked. **§10.3 is a decision for Toni, not a task**, and is deliberately left as an open question rather than pre-filed. **Close #14620 with the contract sentence**, not with a pointer to this file.
