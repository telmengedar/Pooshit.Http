# Architectural Document: `UrlProcessor` is a resolver, and its contract gets written down

> **Repo path:** `docs/architecture/urlprocessor-contract.md` (repository `telmengedar/Pooshit.Http`).
> **DiVoid:** source task **#14559** · the seam question **#14548** · project **#2281** · repo map root **#8292** · `HttpOptions` **#8299** · how-to-extend **#8314** · `HttpService` **#8297** · request lifecycle **#8311** stage 4.1.
> **Predecessors, none superseded:** **#14516** (D7 — the hop's ownership rule and the exception contract this design must not break) · **#14574** (#8316, the band).
> **Contracts cited as load-bearing:** Design Contracts **#1136** (§1 KISS/DRY/YAGNI, §2 existing systems first, §3 configurability is not free, §4 less is better, §5 checklist, §6 anti-patterns) · Code Contracts **#114** §0 · YAGNI **#1184** · falsifiable universals **#9951** and the discriminator rule, **#14516** §D7.2.
> **Baseline:** branch `fix/urlprocessor-null-location` off `origin/master` @ `017e815`, tree clean. Version on master: `0.15.0-preview`.

---

## TL;DR

**The finding is not what the task expected.** #14559 offers two remedies — *skip the processor when there is no location*, or *document the nullability*. **Measured (§3): the "synthesise a location from nothing" capability that remedy 1 would cost is not hypothetical. It works today**, on both redirect arms, and it works *coherently*: a processor that returns `null` is already met by the library's own *"names no target"* guard.

**So the seam already behaves as a resolver, correctly, and nobody wrote that down.** The defect is a missing contract, not a missing guard.

**Decision: ratify the resolver contract in the doc comment, and pin it with tests.** `UrlProcessor` is documented as: called with `null` when the response names no target; return `null` to decline; runs inside the redirect hop, so its exceptions reach the caller unchanged. **No behaviour changes. The diff is one XML doc comment and three tests.**

**Why not remedy 1.** It would silently remove a working capability from a population I **cannot measure from this repo** — and saying "nobody does that" without a predicate is the failure this week has already paid for twice (§8.1).

**Why not wrap the processor's exception into a good one.** It is pinned, deliberately, by a shipped test: `Post308_UrlProcessorThrows_DisposesTheSupersededResponse` asserts the caller's own `InvalidOperationException` propagates unchanged. That contract came out of #14516 CF-4 and this design does not get to quietly reverse it.

**The constraint that removes the obvious third option:** the library targets `netstandard2.0` and does **not** enable nullable reference types, so `Func<string?, string?>` cannot be expressed. **Prose is the only declaration mechanism available**, which makes "document it" a design decision rather than a cop-out.

**Is it worth shipping? Yes, and §9.1 argues it is worth *more* than the code change would have been** — but the honest headline is that the code was already right.

---

## 1. Problem

#14559, and the sentence that makes it more than a nullability gap:

> Without a processor, a 307/308 with no `Location` raises `HttpServiceException` naming the status, the target and the reason. **With a processor, the same input produces an `NRE` before that guard is ever reached.** So configuring the recommended mitigation converts a good diagnosis into a bad one.

`SendRedirect` reads the location and hands it straight to caller code:

```
location = response.Headers.Location?.ToString();
if (options.UrlProcessor != null) location = options.UrlProcessor(location);
```

A processor doing the obvious thing — `location.Replace(…)`, `new Uri(location)` — throws `NullReferenceException` from inside the library. **Nothing in the API declares that possible:** the property's summary says only *"if set this function is used to process urls before requests"*, the delegate is `Func<string, string>`, and no design document mentions it.

### 1.1 Why this seam keeps coming back

`UrlProcessor` is the library's **only caller-supplied extension point inside the redirect hop's guarded region** (#14548). It has now been the trigger **four** times: §6.2 of #14516 recommends it as the cross-origin mitigation; D7.4 rejects narrowing the disposal rule *because* of it; CF-4 fired through it; and this is the fourth. §5.3 states what this design makes true about that seam generally, because a fifth is likely.

---

## 2. Scope

**In scope:** what `UrlProcessor`'s contract is, and writing it where a caller meets it.

**Out of scope, unchanged, and not folded in:**

| Filed as | Not done here |
|---|---|
| **#14547** | the equivalent mutant. Explicitly excluded by the brief. |
| **#8323** | the legacy arm's no-`Location` behaviour — a 301/302/303 with no target re-issues a `GET` to the **original URL** (measured, §3 row 5). This design neither fixes nor worsens it, and §5.2 states why the interaction is worth knowing anyway. |
| #8318, #8319, #9665 | unrelated surfaces. |

**Also not done:** no new option, no new type, no new overload, no signature change, and **no behaviour change of any kind**. §4.4.

### 2.1 The outcome that must be true when this ships

> A caller writing a `UrlProcessor` can learn, from the API, that its argument may be `null` and what returning `null` means — without reading the library's source or discovering it in production.

§8 states what would break it.

---

## 3. What was measured

Probe built and run in this worktree against the library at `017e815`, .NET 8, both redirect arms. Every cell is a real call through `HttpService` with `FollowRedirects = true`.

| # | Arm | `Location` | Processor | Requests | Outcome |
|---|---|---|---|---|---|
| 1 | 308 | absent | none | 1 | `HttpServiceException` — *"names no target to repeat it against"* (the good guard) |
| 2 | 308 | absent | `l => l.Replace(…)` | 1 | **`NullReferenceException`** — the reported defect |
| 3 | 308 | absent | `l => l ?? "https://fallback…"` | **2** | **`"done"` — hop went to the synthesised target.** The capability is real. |
| 4 | 308 | present | `l => null` | 1 | `HttpServiceException` — *"names no target"*. **Returning `null` already means "decline", coherently.** |
| 5 | 302 | absent | none | 2 | hop re-issued to **the original URL** — the #8323 footgun |
| 6 | 302 | absent | `l => l.Replace(…)` | 1 | **`NullReferenceException`** |
| 7 | 302 | absent | `l => l ?? "https://fallback…"` | **2** | hop went to the synthesised target |
| 8 | 302 | present | `l => null` | 2 | hop re-issued to **the original URL** (#8323 again) |

**Four things follow, and three of them decided the design:**

1. **Row 3 kills remedy 1.** Synthesis is not a theoretical use — it works, on both arms, and produces exactly the outcome a caller would want. Remedy 1 removes it.
2. **Rows 1 + 4 show the seam is already a coherent resolver.** The library's guard is the *backstop for a processor that declines*, which is precisely the contract a resolver needs. Nothing had to be built for this; it fell out of the guard #14513 added.
3. **Rows 2 + 6 are the defect, and it is identical on both arms** — so it is a property of the seam, not of the verb-preserving work.
4. **Row 5 is the asymmetry worth knowing (§5.2):** on the legacy arm the NRE replaces a *silent duplicate request*, not a good diagnosis. On that arm the crash is arguably the better of two bad outcomes.

**Two further constraints, verified rather than assumed:**

- **The library cannot express nullability.** `Pooshit.Http.csproj` has no `<Nullable>` element and `<LangVersion>default</LangVersion>` on `netstandard2.0;net8.0`. `Func<string?, string?>` is not available, and enabling nullable across the library is a repo-wide change orders of magnitude larger than this task.
- **Caller exceptions propagate unchanged, by shipped contract.** `Post308_UrlProcessorThrows_DisposesTheSupersededResponse` asserts `ThrowsAsync<InvalidOperationException>` — the caller's own type, not a library wrapper — and that the superseded response is disposed. That is #14516 D7/CF-4's decision, pinned.

---

## 4. Decisions

### D1 — `UrlProcessor` is a **resolver**, and that is ratified rather than changed

The seam's contract, as it already behaves and as it will now be documented:

| | Contract |
|---|---|
| **Input** | the `Location` the response named, **or `null` when it named none** |
| **Return** | the target to use, **or `null` to decline** — the library's own no-target handling then applies |
| **When** | inside the redirect hop, before URI resolution and therefore before the same-origin decision |
| **Exceptions** | propagate to the caller **unchanged**; the superseded response is disposed on the way out |

**Why resolver and not rewriter**, which is the axis #14559 correctly identified as the real question: *rewriter* is what the name and the current one-line summary suggest, and it is what most callers will implement. But the behaviour is already a resolver's, the resolver reading is strictly more capable, and **the null input is only incoherent under the rewriter reading.** Under the resolver reading it is the ordinary case of "there is nothing to start from" — which is exactly when a resolver is most useful.

**This is #1136 §2 in its purest form:** the capability exists, it works, it is coherent with the guard beside it, and the only thing missing is the sentence that says so.

### D2 — the doc comment carries the whole contract, because nothing else can

The XML summary on `HttpOptions.UrlProcessor` states all four rows of D1's table, plus the correction #8299 has been carrying as a hazard: **it runs on the redirect path only**, not on outbound URLs generally, which the current wording (*"process urls before requests"*) implies and has always been wrong about.

**Prose is not a consolation prize here, it is the only mechanism.** With nullable reference types unavailable (§3), a doc comment is the sole place the library can declare this. That also means the usual escape — *"the compiler will tell them"* — is not on the table, and the rule generalises (§5.3).

### D3 — the contract is pinned by tests, because a contract nothing checks is a comment

Three guards (§6.1). They are cheap and they exist for a specific reason: **the next reader of `SendRedirect` will see `options.UrlProcessor(location)` with a possibly-null argument and reach for a null check.** That "tidy-up" silently deletes the synthesis capability, and nothing currently goes red. Row 1 of §6.1 is the guard against exactly that edit.

### D4 — no behaviour change, and the two tempting ones are rejected by name

- **Rejected: skip the processor when `location` is null** (#14559 remedy 1). It removes the measured capability of row 3. The population that relies on it is **unmeasurable from this repo** — a different codebase — and §8.1 is explicit that "nobody does that" is not a claim I am entitled to make.
- **Rejected: pass a non-null sentinel** (`""`). A naive `l.Replace(…)` would then silently produce a wrong target instead of throwing. Converting a loud failure into a quiet wrong answer is the exact inversion of everything the #14513 arc did.
- **Rejected: wrap the processor's exception in `HttpServiceException`.** It is the most attractive option — it would fix the diagnosis without touching the null path at all — and it is **pinned against** by `Post308_UrlProcessorThrows_DisposesTheSupersededResponse`, deliberately, out of #14516 CF-4. A design does not get to reverse a shipped decision quietly; if that contract should change, it is its own task with its own argument.

---

## 5. Consequences

### 5.1 Who is affected

**Nobody, mechanically.** No signature, no option, no behaviour. The diff is one XML comment and three test methods.

| Caller shape | Change |
|---|---|
| No `UrlProcessor` | none |
| A processor that already handles `null` | none — now documented as correct |
| A naive processor | none **today**; the `NRE` becomes a declared contract violation rather than a library surprise |
| A synthesising processor | none — and it is now a documented capability rather than an accident that a future cleanup could remove |

### 5.2 The asymmetry between the arms, which the fix does not remove

On the **307/308** arm the `NRE` replaces a good diagnosis — that is #14559's complaint and it is right. On the **301/302/303** arm it replaces a **silent duplicate request to the original URL** (§3 row 5). So on the legacy arm the crash is, in outcome terms, *less bad* than the alternative it pre-empts.

**Stated rather than used.** It is not an argument for leaving the `NRE`, and it is not an argument for fixing the legacy arm here — that is #8323, and it stays there. It is recorded because a reader comparing the two arms will notice the inconsistency and should know it is a property of #8323's open behaviour, not of this decision.

### 5.3 What this makes true about the seam generally — the part that outlives the null case

`UrlProcessor` has been the trigger four times. This design's answer to *"what about the fifth"* is not another guard; it is a rule about this class of seam:

> **A caller-supplied extension point inside a guarded region is a contract, and in this library that contract can only exist in prose.** Nullable reference types are unavailable, so the type system will never state it. Therefore every such seam owes, in its doc comment: **what its inputs can be** (including the degenerate case), **what its return values mean** (including the decline value), **and what happens to exceptions it throws**.

Measured against the library's five seams (#8314): `UrlProcessor` is the only one **inside** the guarded region, and — before this change — the only one whose degenerate input was undeclared. Encoder, decoder and token provider are all invoked with caller-supplied or library-constructed arguments that cannot be absent; the message handler is the transport itself.

**The falsifier for that claim, in the same paragraph:** a sixth seam added inside a guarded region, or an existing seam gaining a degenerate input. Both are edits a future change makes, not states of the code today — which is exactly why the rule is written as an obligation on the *next* seam rather than as a property of the current five.

---

## 6. Compatibility, coverage, version

### 6.1 Coverage

`Http.Tests/HttpServiceRedirectTests.cs` — the existing fixture; this is redirect-hop behaviour and does not warrant its own file. Each row names the **guard**; the fourth column is what makes it go red **uniquely**.

| # | Guard | Shape | Goes red uniquely when |
|---|---|---|---|
| 1 | `Post308_NoLocation_UrlProcessorReceivesNull` | 308 with no `Location`, processor records its argument; assert it was invoked exactly once **with `null`** | someone adds a null-skip to `SendRedirect` — the "tidy-up" D3 exists to stop. **This is the load-bearing row.** |
| 2 | `Post308_NoLocation_UrlProcessorSynthesisesTarget_HopUsesIt` | 308 with no `Location`, processor returns a fallback URL; assert **two** requests and that hop 1 went to the fallback | the synthesis capability is removed, by a null-skip or otherwise (§3 row 3) |
| 3 | `Post308_UrlProcessorReturnsNull_FailsWithNoTarget` | 308 **with** a `Location`, processor returns `null`; assert `HttpServiceException` and that the message names *no target* | the decline semantics break — the backstop that makes the resolver contract coherent (§3 row 4) |
| 4 | `Get302_NoLocation_UrlProcessorReceivesNull` | the legacy arm, same as row 1 | a null-skip is added to only one arm, leaving the seam's contract arm-dependent |

**Duals, and why these are the right ones:** rows 2 and 3 are each other's dual — row 2 fails if declining is treated as synthesising, row 3 fails if synthesising is treated as declining. Row 4 is row 1's dual across the arms. Without them the suite stays green for an implementation that "helpfully" null-skips on the preserving arm only, which is the most likely wrong fix.

**Not added here:** anything asserting the `NRE` itself. Pinning a `NullReferenceException` would pin a *caller's* mistake as library behaviour, and would go red the moment the caller-exception contract is ever revisited. The contract is what gets pinned, not the consequence of violating it.

### 6.2 Release note and version

**Patch — `0.15.1-preview`.** The convention is *patch when behaviour is byte-identical for callers who opt into nothing, minor when it is not*. This change is byte-identical for **every** caller, opted-in or not: it is a comment and three tests. This is the cleanest patch the convention has ever been asked to classify.

The note is one line and should exist anyway, because the contract is new information even though the behaviour is not: **`UrlProcessor` is called with `null` when the redirect names no target, and returning `null` declines.**

---

## 7. Where it lives

`HttpOptions.UrlProcessor`'s XML summary — the one place a caller meets the seam in an IDE, which is the whole point. **`HttpService.SendRedirect` is not touched.**

**Ordering is unchanged and remains load-bearing:** the processor runs **before** URI resolution and therefore before the same-origin comparison, so what it returns still decides whether credentials ride (#9633, #14516 §6.2). That is stated in the doc comment too, because it is the single most consequential thing about this seam and it has never been written where a caller looks.

---

## 8. Falsifiable claims (#9951 + #14516 §D7.2)

| Claim | Where | What would falsify it | Does that class exist? |
|---|---|---|---|
| "the synthesis capability is real" | D1, §3 row 3 | a synthesising processor that does **not** reach the hop | **No** — measured on both arms, two requests, hop at the synthesised target. Rows 2 and 4 of §6.1 keep it measured. |
| "returning `null` already means decline, coherently" | D1, §3 row 4 | an arm where returning `null` does something other than fall to the no-target handling | **Yes, one: the legacy arm** (§3 row 8), where declining yields a duplicate `GET` to the original URL rather than a diagnosable failure. That is #8323's open behaviour, not this contract's — but the contract sentence is true of the preserving arm and *approximate* on the legacy one, and the doc comment says so rather than over-claiming. |
| **discriminator:** "prose is the only mechanism available" | D2, §3 | a way to declare nullability that the library could adopt at this size | **No, checked:** no `<Nullable>` element, `LangVersion default`, `netstandard2.0` in the target set. `[MaybeNull]`/`[AllowNull]` are net-only attributes and would not reach the netstandard consumer; enabling nullable repo-wide is a different change by two orders of magnitude. |
| "no behaviour changes" | D4, §5.1 | any input whose outcome differs before and after | **No** — the production diff is a comment. The eight rows of §3 are the before-state and are unchanged by construction, which is why they double as the after-state. |
| "`UrlProcessor` is the only seam inside a guarded region" | §5.3 | a sixth seam, or an existing seam gaining a degenerate input | **Not today** — measured against #8314's five. Both falsifiers are *future edits*, which is why §5.3 is phrased as an obligation on the next seam rather than a property of the current set. |

### 8.1 The claim I am **not** making, and why that is the point

**"No caller synthesises a location"** and **"no caller relies on the `NRE`"** are both unmeasurable from this repo — the consumers are a different codebase — and both would be load-bearing for remedy 1. I could have written a grep and reported a number.

**This week has already paid twice for exactly that.** #14528's audit reported a row unguarded on the strength of `grep "FollowRedirects = false"` returning nothing, when `false` was the flag's default and no test ever writes it; the claim then propagated into four documents. **A grep for a token is not a measurement of a behaviour**, and *"I grepped for it"* is a method only once you have asked what the absence of a hit would prove. Here the absence of a hit would prove nothing, because the hits would be in a repository I am not reading.

So the design does not remove the capability, and §9.2 records the measurement someone *with* the consumer checked out could make if remedy 1 ever becomes attractive.

---

## 9. Open questions

### 9.1 Is this worth shipping at all? Yes — and it is worth more than the code change would have been

You asked directly, including whether documenting is "the honest end of it".

**It is the honest end of it, and that is a finding rather than a shrug.** The investigation set out to fix a null-handling bug and measured that **the code is already correct** — the seam behaves as a coherent resolver, the decline path is already backstopped by the guard #14513 added, and the only thing wrong is that no caller could learn any of it. Shipping the contract is shipping the actual fix.

Three reasons it earns a PR rather than a note-to-self:

1. **It is the only mechanism available.** With nullable reference types off the table, the doc comment *is* the type signature. Declining to write it is declining to declare the API.
2. **It protects a capability that nothing currently protects.** Row 1 of §6.1 is the guard against a future null-skip "tidy-up" — which is the single most likely next edit to this line, and today it would pass the whole suite.
3. **The #8299 correction rides along legitimately.** The current summary — *"process urls before requests"* — has been wrong about this seam's *reach* since the map was built, and it is the same comment.

**What would change my answer:** if the consumer measurement in §9.2 came back showing no synthesising caller **and** a naive processor in production, remedy 1 becomes the better trade — the NRE would be hurting someone real and the capability would be protecting nobody. That is one grep in a repo I cannot read.

### 9.2 The rest

2. **The measurement I could not make.** In `mamgo-backend` (or any consumer): does any `UrlProcessor` dereference its argument unguarded, and does any return a value when handed `null`? The predicate must be *behavioural*, not a token grep — read each processor body, because a processor that happens to be null-safe by construction looks identical to one that guards deliberately. That is the fact §9.1 would reverse on.
3. **Should the legacy arm's decline path be made coherent?** §8 row 2's falsifier: declining on a 301/302/303 yields a duplicate `GET` rather than a diagnosable failure. It belongs to #8323, it would make the contract sentence exactly true on both arms, and I did not reach for it.
4. **Should this library enable nullable reference types?** The real answer to the class of defect (§5.3), out of scope by a wide margin, and worth its own task if the seam count ever grows. Noted because D2's "prose is the only mechanism" is true *given* that decision, and someone should eventually revisit the decision rather than the consequence.

---

## 10. Pre-Design Checklist (#1136 §5)

| Item | Verdict |
|---|---|
| No new type mirroring an existing one | **Pass** — no new type; no new anything |
| No new abstraction with one implementation | **Pass** |
| Nothing justified by "we might need X later" | **Pass** — §5.3 states an obligation on future seams without building for them |
| No deprecation window / compat shim / feature flag | **Pass** — nothing to deprecate; behaviour is unchanged |
| DRY math on inline-vs-extract | **N/A** — no code moves |
| Existing surface audited before adding one | **Pass** — the whole design is #1136 §2: the capability and its backstop already exist (§3 rows 3 and 4); only the sentence describing them was missing |
| Every config knob has a named operator | **N/A** — no knob added; D4 rejects three behaviour changes by name |
| Can-it-be-deleted / merged / inlined | **Pass** — the smallest possible change that meets §2.1; D4 records the three larger ones and why each loses |
| Trade-offs named explicitly | **Pass** — D4 (three rejections), §5.2 (the arm asymmetry), §8.1 (the claim I decline to make), §9.1 (what would reverse the decision) |
| Out-of-scope listed explicitly, not merely absent | **Pass** — §2 table; §5.2 and §9.2 name the adjacent things deliberately left |
| No multi-paragraph rationale for things that obviously stay | **Pass** |
| Predecessor design banner where superseded | **N/A** — #14516 is *relied on* (D4's third rejection defers to its CF-4 contract), not superseded |
| Data deliverables (SQL/schema casing) | **N/A** |
| Defensive code for impossible scenarios | **Pass** — no branch is added anywhere; §6.1 explicitly declines to pin the `NRE` |
| **Coverage rows name the test identifier** (#1136 §5, new) | **Pass** — §6.1, four named guards, none opaque |

---

## 11. Implementation order

1. **Tests first, against current behaviour.** All four rows of §6.1 pass **before** any doc change — that is what makes them a contract ratification rather than a specification. If row 2 or 3 fails at this step, this design is wrong and stops here.
2. **Rewrite the XML summary** on `HttpOptions.UrlProcessor` to carry D1's four contract rows plus the redirect-path-only correction (D2, §7).
3. **Bump to `0.15.1-preview`** and write the one-line release note from §6.2.
4. **Reconcile the map** — #8299 (its `UrlProcessor` hazard becomes a documented contract, and the "name suggests broader reach" note is now addressed in the comment itself), #8314 (§5.3's obligation on future seams), #8311 stage 4.1. Concept nodes count (#3414).
