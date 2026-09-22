# Architectural Document: following a redirect chain

> **Repo path:** `docs/architecture/multi-hop-redirect.md` (repository `telmengedar/Pooshit.Http`).
> **Node:** **#14617** — this document's own DiVoid node, maintained as a byte copy of this file.
> **DiVoid:** source task **#8323** · project **#2281** · repo map root **#8292** · `HttpService` **#8297** · `HttpOptions` **#8299** · request lifecycle **#8311** stage 4.1 · the forced-`GET` finding **#9626** · .NET Framework replay **#14607**.
> **Predecessors, none superseded, all three relied on:** **#9633** (the per-hop credential rule, written for a follower that did not yet exist) · **#14516** (D7 ownership, D2 body replay, the verb-preserving arm) · **#14574** (#8316, the band that makes an unfollowed 3xx loud).
> **Contracts cited as load-bearing:** Design Contracts **#1136** (§1 KISS/DRY/YAGNI, §2 existing systems first, §3 configurability is not free, §4 less is better, §5 checklist, §6 anti-patterns) · Code Contracts **#114** §0 · YAGNI **#1184** · falsifiable universals **#9951** and the discriminator rule **#14516** §D7.2.
> **Baseline:** branch `fix/redirect-multi-hop` off `origin/master` @ `8281322`, tree clean. Version on master: `0.15.0-preview`.

---

## TL;DR

**Build it.** The case against — *"`HttpClient.AllowAutoRedirect` already does this, so a library follower is redundant"* — is **dead on a measurement**, and that measurement is the most important thing in this document.

**Measured (§3):** on a cross-origin hop the transport strips **only `Authorization`**. `X-Api-Key`, `Cookie` and a caller-added `X-Tenant-Secret` all reach the foreign origin. The library strips **all four**, because #9633 made the strip list the caller-extensible `SensitiveHeaders` set. **Delegating to the transport would silently downgrade a security property to a third of its coverage** — and would do it for exactly the callers who configured the set because they had vendor credentials to protect.

**The cost is far lower than it looks, and the reason is that this session already paid it.** The three hard rules were each written per-hop rather than per-call, so all three compose to N without redesign:

| Rule | Composes? | Why |
|---|---|---|
| Credential strip (#9633) | **yes, structurally** | the hop inherits from `response.RequestMessage` — *the previous hop's request*, not the original. A header stripped at hop 2 is absent from hop 2's request, so hop 3 cannot restore it. Verified by reading, not cited. |
| Body replay (#14516 D2) | **yes, measured** | one `HttpContent` instance survives **five** sends for every replayable shape. A content that survives hop 2 survives hop N; depth adds nothing. |
| Disposal (#14516 D7) | **yes** | the rule is already per-hop over one method. A loop is that method applied N times. |

**What is actually new is a counter and a cap.** `HandleResponse`'s `if`/`else if` becomes a `while`; a private `const int maxRedirects = 10` bounds it; exceeding it raises `HttpServiceException` carrying the last 3xx undisposed. **No knob** — #1136 §3 finds no operator.

**The cost where it is non-zero:** a chain that exceeds 10 now costs 10 round-trips before failing where it previously cost 1. §5.3 states the trade and the falsifier.

---

## 1. Problem

#8323 items 1 and 3, the last two still open:

> **Single hop only.** `HandleResponse` follows at most one redirect. A two-hop chain — entirely normal, e.g. `http`→`https` then a path rewrite — delivers the *second* redirect response to the caller.
>
> **No loop or hop-count guard.** Moot at one hop, but the moment hop-following is generalised a cycle becomes an infinite loop. Whoever implements 1 must implement this in the same change.

Items 4 and 5 (307 throws, 308 unrecognised) closed under #14513. Item 2 (the legacy arm forces `GET`) is settled behaviour and stays — §2.

**What the caller sees today**, measured against a real three-hop chain on a loopback server (§3): the library follows hop 1, and the second `302` reaches the band check and raises `HttpServiceException` naming it. Loud, correct, and not followed — so the caller has a target they must chase by hand, which is the shape #8323 calls surprising.

---

## 2. Scope

**In scope:** the hop count, the cap, and the loop guard. Nothing about what a single hop *does*.

**Out of scope, unchanged, and not folded in:**

| Filed as | Not done here |
|---|---|
| **#9626** — the forced `GET` unpinned | **Already closed, verified rather than assumed:** three tests pin it (`Post301_…`, `Post303_…`, `PostWithBody_FollowedRedirect_HopIsIssuedAsGetWithoutBody`). Nothing to fold in. |
| #8323 item 2 | the legacy arm re-sends as `GET` and drops the body, on **every** hop of a chain. Deliberate, matches browsers, pinned. |
| **#14547**, **#14613** | excluded by the brief. |
| the WASM divergence | on WebAssembly the bare client auto-redirects, so the library's follower never runs there. §5.4. |

**Also not done:** no new option, no public surface, no change to `FollowRedirect`, `SendRedirect`, `CreateRedirectRequest` or the band.

### 2.1 The outcome that must be true when this ships

> A caller who opts into redirect following reaches the end of an ordinary redirect chain; a chain that does not end is refused loudly, in bounded time, without the caller's credentials being handed to an origin the chain wandered into.

§8 states what would break each clause.

---

## 3. What was measured

Two loopback `HttpListener` servers on different ports — so *cross-origin* is real (RFC 6454 counts the port) rather than simulated — and a five-deep content-replay probe. .NET 8.

### 3.1 The transport already follows chains. It does **not** protect them.

| | `AllowAutoRedirect = true` | the library's follower |
|---|---|---|
| 3-hop chain `/h1→/h2→/h3` | **follows all three** | follows one, then raises on the second |
| 307 with a `POST` body | **method and body preserved** (`len=7` on both) | same, per #14516 |
| cycle `/loop → /loop` | capped at `MaxAutomaticRedirections` (**default 50**), returns the 3xx without throwing | n/a — cannot loop |
| **cross-origin hop, four credential headers** | **strips `Authorization` only** — `X-Api-Key`, `Cookie`, `X-Tenant-Secret` all arrive at the foreign origin | **strips all four**, including the caller-added name |

The last row is the whole decision. Raw:

```
TRANSPORT   A :18091/cross  Authorization=Bearer SECRET X-Api-Key=VENDOR-KEY Cookie=session=abc X-Tenant-Secret=TENANT
            B :18092/final                              X-Api-Key=VENDOR-KEY Cookie=session=abc X-Tenant-Secret=TENANT
LIBRARY     A :18091/cross  Authorization=Bearer SECRET X-Api-Key=VENDOR-KEY Cookie=session=abc X-Tenant-Secret=TENANT
            B :18092/final  <no watched headers>
```

**Three of the library's four measured credential classes survive the transport's hop.** `SensitiveHeaders` ships ten names by default and is public precisely so a consumer can add `X-Vendor-Signature`; none of that reaches `AllowAutoRedirect`.

### 3.2 The escape hatch exists, works, and is not a substitute

`new HttpService(new HttpClientHandler { AllowAutoRedirect = true })` follows the three-hop chain at the transport — measured, three requests, correct body. So a caller *can* have multi-hop today.

**But it is only safe for a caller whose sole credential is `Authorization`.** For anyone using `SensitiveHeaders` as designed, this route is a credential leak, so *"cap at one and point them at the constructor"* would be recommending one. That is what kills the cap-it option, and it is a measurement rather than a preference.

### 3.3 Body replay is depth-independent

One `HttpContent` instance, re-sent across five hops with a transport that drains the body each time:

| Content | 5 sends |
|---|---|
| `StringContent` (the JSON encoder path) | survived all |
| `ByteArrayContent` | survived all |
| `FormUrlEncodedContent` | survived all |
| `StreamContent` over a seekable stream | survived all |
| `MultipartFormDataContent` | survived all |

**A content that survives hop 2 survives hop N.** The non-seekable stream fails on send 2 (#14516 §5) and therefore fails at the same place regardless of depth. **#14607's .NET Framework finding — that request content survives *zero* replays there — is likewise depth-independent:** it makes hop 1 of a body-carrying chain fail, which is a property of the first hop, not of the chain. Depth introduces no new replay case.

### 3.4 The credential rule composes structurally

`SendRedirect` reads `HttpRequestMessage redirected = response.RequestMessage` and passes it to `CreateRedirectRequest`. In a loop, `response` is the **previous hop's** response, so `redirected` is the **previous hop's request** — not the original.

**So #9633's *"once stripped, never restored"* is not a rule the loop has to enforce; it is a consequence of what the hop inherits from.** A header dropped at hop 2 is absent from hop 2's request, so hop 3 has nothing to copy. #9633 wrote that sentence against a follower that did not exist; it turns out to be true by construction rather than by intent, which is the happier of the two ways to be right.

---

## 4. Decisions

### D1 — build the follower in the library, do not delegate to the transport

§3.1. Delegation costs three of four credential classes. The library owns redirect policy (#8311 stage 1) precisely because policy is what the transport does not have, and #9633 is the policy.

**The honest counterweight, stated because the brief asked for it:** every hop is a hop whose credential, replay and disposal rules must hold. What makes this affordable is not that those rules are easy — four QA rounds say otherwise — but that **all three were written per-hop against the immediately preceding request**, so they are already N-hop rules being used once. §3.3 and §3.4 are the evidence; neither is an argument.

### D2 — the cap is a private `const int maxRedirects = 10`, with no knob

#1136 §3 promotes a constant to a knob given a named operator, an environment difference, or a secret. **None applies**, and I looked for the first rather than assuming: there is no consumer asking for a tunable depth, and a caller who needs unbounded following has the transport route (§3.2) with its own 50.

**Why 10.** Real chains measured and described in #8323 are 2–3 hops (`http`→`https`, a path rewrite, a trailing-slash normalisation). Ten is three times the observed worst case. The number is a **failure threshold, not a capability limit** — every legitimate chain terminates far below it, so raising it would buy nothing and lowering it would risk a real chain.

**Why not 50, matching the transport.** The cap's cost is paid on failure: a cycle burns the cap in round-trips before raising. Fifty round-trips to a server that is looping is five times the diagnostic delay for no additional reach.

### D3 — a hop counter only: no visited-set, no chain list

A cycle is caught by the counter, so a visited-set changes the **message**, not the outcome. #1136 §4's delete-test: what breaks without it? Nothing observable — the call still fails, loudly, bounded.

**The trade, named rather than hidden:** the caller learns *"this chain exceeded 10 redirects"* rather than *"this chain returned to a URL it had already visited"*. The second is a better diagnosis and it costs a `HashSet<string>` plus the decision of what to do when a chain legitimately revisits a URL (which is not itself an error — a `303` back to a resource you already fetched is legal).

**The falsifier, and what I would do about it:** if a caller reports hitting the cap and being unable to tell a cycle from a long chain, the remedy is to carry the visited URLs into the message. That is purely additive, costs one field, and needs no decision reversed. **I am not building it now because no such caller exists** — and the predicate for that claim is honest: I have not searched the consumer, because the feature does not exist yet, so there can be no caller of it. That is the one case where "nobody does X" is safe to assert.

### D4 — the loop lives in `HandleResponse`; nothing below it changes

```
while (the status is a redirect the options say to follow)
    if (++hops > maxRedirects) raise
    response = await FollowRedirect(response, options, preserveRequest)
```

`FollowRedirect` already owns disposal of the response it supersedes and hands the caller the new one (#14516 D7). **A loop is that method applied N times**, each iteration disposing exactly the response the previous one produced, so D7's grid holds per iteration without a new row.

**Mixed chains work by construction and are worth stating:** the status is re-read each iteration, so `302 → 308 → 200` follows the first as a bodyless `GET` and the second preserving the (now absent) body — which is correct, and is a case nobody would think to write down if the loop were written as "repeat the first arm N times".

### D5 — exceeding the cap raises `HttpServiceException` carrying the last 3xx, undisposed

Consistent with every other redirect failure since #14513: the caller gets the status, the target they would have chased, and the response to inspect. The throw happens **before** `FollowRedirect` is entered, so the loop still owns that response and hands it over rather than disposing it — which is D7's exemption clause, unchanged.

The message names the limit, the original request, and the last target, all through the existing redaction helpers.

---

## 5. Consequences

### 5.1 Who is affected

| Caller shape | Change |
|---|---|
| `FollowRedirects` unset (the default) | **none** — the loop is inside the same option check |
| Single-hop chain | **none** — one iteration, byte-identical behaviour |
| **2–10 hop chain** | **fixed.** Was: `HttpServiceException` on hop 2. Now: followed. |
| Chain longer than 10, or a cycle | **changed.** Was: raised on hop 2 after 1 extra request. Now: raises after 10. Louder message, later. |
| A caller relying on the raise-at-hop-2 behaviour | **broken, deliberately.** That behaviour was the defect. |
| Caller-supplied auto-redirecting handler | **none** — the transport consumes the 3xx and the library never sees it |

**No public signature changes. No new option.**

### 5.2 The credential property this preserves, stated as the reason the change exists

After this, a 5-hop chain that wanders cross-origin at hop 3 drops every `SensitiveHeaders` name at that hop and cannot restore them at hops 4 and 5 (§3.4). **Under the transport route the same chain would carry a vendor API key and a session cookie to every origin in it.** That difference is the justification for the whole design and it is measured, not argued.

### 5.3 The cost: a cycle now costs ten round-trips

Today a cycle costs one extra request and raises. After this it costs ten. That is the price of following chains at all, it is bounded, and it is paid only by chains that were already broken.

**Falsifier for "bounded is enough":** a caller on a slow link for whom ten round-trips to a looping server is a timeout rather than a delay. That class exists in principle; it is served by `HttpService.Timeout`, which bounds the whole call and is unaffected by the hop count. If that turns out to be insufficient in practice, the remedy is a lower cap, not a knob.

### 5.4 WASM is unchanged and still divergent

On WebAssembly the constructor takes a bare `HttpClient`, which auto-redirects, so the library's follower never runs and the transport's rules apply — including the weaker credential strip of §3.1. **This change does not narrow that divergence and does not widen it.** It is #8311 stage 1's oldest deliberate decision, it belongs with whatever eventually revisits the constructor, and it is named here because a reader of §3.1 will ask.

---

## 6. Coverage

`Http.Tests/HttpServiceRedirectTests.cs`. Each row names the **guard**; the fourth column is what makes it go red **uniquely**.

| # | Guard | Shape | Goes red uniquely when |
|---|---|---|---|
| 1 | `Get302_ThreeHopChain_FollowsToTheEnd` | `302 → 302 → 200`; assert the value and **three** requests | the loop was not built — the core change |
| 2 | `Get302_ChainExceedingTheCap_Throws` | 11 consecutive 302s; assert `HttpServiceException` and **exactly `maxRedirects` + 1** requests | the cap is absent, wrong, or off by one |
| 3 | `Get302_Cycle_TerminatesAtTheCap` | a 302 pointing at itself; assert it raises and does not hang | the counter is reset inside the loop — the classic cycle bug, which row 2 does **not** catch because a non-cyclic chain ends on its own |
| 4 | `Get302_SingleHop_StillIssuesExactlyTwoRequests` | **dual** — one hop, assert two requests | the loop runs an extra iteration on a terminal response |
| 5 | `Get302_NoRedirect_IssuesOneRequest` | **dual** — a plain 200, assert one request | the loop's entry condition was dropped |
| 6 | `GetWithTokenProvider_ChainCrossingOriginMidway_CredentialStrippedAndNotRestored` | `same-origin → cross-origin → back to the original origin`; assert the credential is present on hop 1, absent on hops 2 **and 3** | **the load-bearing row.** A follower that rebuilt each hop from the *original* request passes rows 1–5 and fails only here — and that implementation restores a stripped credential, which is the security property this whole design exists to keep (§3.4) |
| 7 | `Post308_ThreeHopChain_BodyRepeatsOnEveryHop` | `308 → 308 → 200` with a body; assert the drained bytes are identical on all three | replay was made depth-dependent, e.g. by buffering once and reusing a consumed copy |
| 8 | `Get302Then308_MixedChain_EachHopUsesItsOwnArm` | `302 → 308 → 200` from a `POST`; assert hop 1 is a bodyless `GET` and hop 2 is a `GET` too | the arm is chosen once and reused for the chain rather than re-read per hop (D4) |
| 9 | `Get302_ChainExceedingTheCap_ExceptionCarriesTheLastResponseUndisposed` | as row 2; assert `error.Response` is the last 302 and its content is **not** disposed | D5 regressed — the cap path disposes what it hands over, violating D7 |
| 10 | `Get302_ChainExceedingTheCap_MessageNamesTheLimitAndTheTarget` | as row 2; assert the message contains the limit and the last target | the diagnosis D3 traded the visited-set away for is not actually there |

Rows 4, 5 and 8 are the duals #114 §13.1.1 asks for. **Row 6 is the one to write first**, because it is the only row that distinguishes a correct follower from one that is correct-looking and leaks.

**Not added here:** anything asserting a specific cap *value* beyond row 2's arithmetic — the number is a constant, not a contract, and a test naming `10` would pin a decision D2 explicitly reserves the right to lower.

### 6.1 Fixture note

`SequenceHandler` already replays a canned sequence and stamps `RequestMessage`, so an N-hop chain is N responses in the constructor. **No fixture change is needed** — which is worth stating, because the two previous redirect changes both needed one.

---

## 7. Falsifiable claims (#9951 + #14516 §D7.2)

| Claim | Where | What would falsify it | Does that class exist? |
|---|---|---|---|
| "the credential rule composes to N hops" | §3.4, D1 | a hop that rebuilds its request from the **original** request rather than the previous one | **Not in the current code** — verified by reading `SendRedirect`, which binds `redirected = response.RequestMessage`. It is, however, exactly what a plausible refactor would introduce, which is why row 6 exists rather than a comment. |
| "body replay is depth-independent" | §3.3 | a content whose replayability degrades with **count** rather than being fixed at the first replay | **None measured to depth 5** across all five shapes the library builds. The two known failures — a non-seekable stream, and .NET Framework (#14607) — both fail at a *fixed* send number, so they are depth-independent in the other direction. A content with an internal counter would falsify it; none exists in the library's own strategies, and a caller-supplied `HttpContent` could be one. |
| "the cap catches every non-terminating chain" | D2, D3 | a chain that does not increment the counter | **No** — the counter increments once per iteration and the loop has one entry. This is the one claim here with no live falsifier, which is why row 3 pins the cycle case separately from row 2's plain overrun. |
| **discriminator:** "a redirect status is one the loop follows" | D4 | a 3xx that enters the loop but is not one of the five, or one of the five that does not | **Yes, and it is already handled elsewhere:** `300`, `304`, `305` and other 3xx match neither arm, so they fall out of the loop on the first test and reach the band, which raises (#14574). The discriminator is the same `is` pattern the single-hop version used; the loop does not widen it, and row 5's dual keeps a terminal status out. |
| "delegating to the transport would lose credential coverage" | §3.1, D1 | a transport configuration that strips the full `SensitiveHeaders` set | **No** — `HttpClientHandler` exposes no hook for it; the strip is hard-coded to `Authorization`. Measured, not inferred from documentation. |

### 7.1 The claim I expect a reviewer to attack first

**"Ten is the right cap."** It is the one number here chosen by judgement rather than measurement — three times an observed worst case of three. Break it by finding a real chain longer than ten; I could not, and the search space I checked (the chains #8323 names, plus the ones this session's probes constructed) is small enough that the absence proves little. **The mitigation is that the cap's cost of being wrong is asymmetric and cheap in the direction it is most likely wrong:** too low raises on a legitimate chain with a message naming the limit, which is a one-line change to fix; too high wastes round-trips on a broken one.

---

## 8. Compatibility and version

**Minor — `0.16.0-preview`.** The convention is *patch when behaviour is byte-identical for callers who opt into nothing, minor when it is not*. `FollowRedirects` defaults to false, so a caller who opts into nothing is untouched and the literal test says **patch**.

**It still ships minor, and the reason is stated rather than felt:** every caller who has opted in gets a materially different outcome on any chain longer than one — an answer where they previously got an exception. That is a capability change, not a correction, and it is the first in this library since `0.13.1`. Where #14574 §6.3 argued the convention should be applied literally even when a change feels large, this is the inverse case: the literal test passes and the change is still one a consumer must read about. **Both readings are defensible and Toni's call at packaging** — §9.3.

The release note must state the cap and its value, because a caller whose chain exceeds it needs to recognise the message.

---

## 9. Open questions

### 9.1 Should this be built? Yes — and I would have said no before the measurement

The brief invited *"cap it at one and document the cap"* as a legitimate answer, and on the argument as stated it is the better one: the transport does all of this, the library disables it deliberately, and four QA rounds went into a single hop.

**The measurement reverses it.** The transport's cross-origin strip covers `Authorization` and nothing else, so *"use `AllowAutoRedirect`"* is advice to leak a vendor key and a session cookie to whatever origin a chain wanders into. Recommending the cap would have meant recommending that route, and I would have written it without checking if the brief's framing had gone unexamined. **That is the entire value of §3.1 and it took twenty minutes.**

The second half of the reversal is that the work is small: §3.3 and §3.4 measure that the two rules people would expect to be hard are already N-hop rules. What is left is a counter.

### 9.2 What a caller does if the cap is wrong for them

Nothing, today, and that is deliberate (D2). The routes are: accept the exception, or supply a handler with `AllowAutoRedirect` and accept the weaker strip (§3.2). **If a real caller appears with a legitimate chain over ten hops, the answer is to raise the constant, not to add a knob** — one line, no surface.

### 9.3 The rest

2. **Patch or minor** (§8). The literal convention says patch; the change is a capability. I recommend minor and name the tension rather than resolving it silently.
3. **Should the visited-set ship after all?** D3 says no on #1136 §4 and names the falsifier. Raising it because the diagnosis is this library's differentiator, and a reviewer may reasonably weigh that higher than I did.
4. **The WASM divergence keeps coming up** (§5.4, #8311 stage 1, #14574 §5.4). Three designs have now named it and none has owned it. It is not a redirect question, it is a constructor question, and it probably deserves its own task rather than a fourth mention.

---

## 10. Pre-Design Checklist (#1136 §5)

| Item | Verdict |
|---|---|
| No new type mirroring an existing one | **Pass** — no new type; one `const`, one counter |
| No new abstraction with one implementation | **Pass** — the loop replaces a branch in the method that already owns it |
| Nothing justified by "we might need X later" | **Pass** — D3 rejects the visited-set with its falsifier; D2 rejects the knob |
| No deprecation window / compat shim / feature flag | **Pass** — immediate and total |
| DRY math on inline-vs-extract | **N/A** — nothing extracted; `FollowRedirect` is reused as-is |
| Existing systems first | **Pass, and it is the design's spine** — #9633's rule, #14516's replay and disposal, `SequenceHandler`, and the exception shape are all reused unchanged; §3.1 evaluates the *transport* as the existing system first and rejects it on measurement |
| Every config knob has a named operator | **Pass by removal** — D2 looked for one and records that none exists |
| Can-it-be-deleted / merged / inlined | **Pass** — ran on the visited-set (deleted, D3), the knob (deleted, D2), and the chain list (deleted) |
| Trade-offs named explicitly | **Pass** — D1 (the counterweight), D2 (why 10), D3 (the worse diagnosis), §5.3 (ten round-trips), §7.1 (the cap is judgement), §8 (patch vs minor) |
| Out-of-scope listed explicitly | **Pass** — §2 table, with #9626 verified closed rather than assumed |
| No multi-paragraph rationale for things that obviously stay | **Pass** |
| Predecessor design banner where superseded | **N/A** — #9633, #14516 and #14574 are all relied on; §3.4 records that #9633's forward-looking sentence turned out true by construction |
| Coverage rows name the test identifier | **Pass** — §6, ten named guards, none opaque |

---

## 11. Implementation order

1. **Duals first** (§6 rows 4, 5), against current behaviour. They must pass before and after; that is what makes them duals.
2. **The counter and cap.** `HandleResponse`'s branch becomes a loop with `maxRedirects`. Rows 1, 2, 3 go green.
3. **Row 6 — the cross-origin-midway chain.** Write it immediately after the loop, not at the end: it is the row that separates a correct follower from a leaking one, and the longer the loop exists unpinned the more likely a refactor introduces the original-request form.
4. **Rows 7–10.**
5. **Version and release note** (§8), naming the cap and its value.
6. **Reconcile the map** — #8297, #8311 stage 4.1 (the "exactly one hop and no loop guard" clause is in both and has been since the map was built), #8299, and close #8323. Concept nodes count (#3414).
