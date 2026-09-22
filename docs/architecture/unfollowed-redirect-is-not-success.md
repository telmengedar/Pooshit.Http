# Architectural Document: an unfollowed redirect is not a success

> **Repo path:** `docs/architecture/unfollowed-redirect-is-not-success.md` (repository `telmengedar/Pooshit.Http`).
> **DiVoid:** source task **#8316** (severity 4) · project **#2281** · repo map root **#8292** · `HttpService` **#8297** · request lifecycle **#8311** stage 4.2 · sibling limits **#8323** · downstream consumer **#8916** (*not in scope — see §2*).
> **Predecessor:** **#14516** / `docs/architecture/verb-preserving-redirect.md`. **Its §2.2 is falsified by this document** — see §1.2, which is the most load-bearing section here.
> **Contracts cited as load-bearing:** Design Contracts **#1136** (§1 KISS/DRY/YAGNI, §2 existing systems first, §3 configurability is not free, §4 less is better, §5 checklist, §6 anti-patterns) · Code Contracts **#114** §0 · YAGNI **#1184** · falsifiable universals **#9951** and the discriminator rule from #14516 §D7.2.
> **Baseline:** branch `fix/3xx-not-success` off `origin/master` @ `7c03159`, tree clean. Version on master: `0.14.0-preview`. Suite: 375 green.

---

## TL;DR

**What.** `CheckHttpResponse` accepts `200–399`. It becomes **`200–299`**. Any 3xx that nobody followed stops being classified as success and raises `HttpServiceException` naming the redirect target, instead of falling into the zero-length short-circuit and returning `default(T)`.

**Why it is not a one-liner.** `FollowRedirects` defaults to **false**, so this changes behaviour for **every caller on every 3xx**, not for an opt-in subset. #14513 could be shipped as a patch because nobody who opted into nothing was touched. This one cannot.

**Measured, not assumed** (§3): with the default options, **every** 3xx carrying `Content-Length: 0` returns a silent `null` today — and so do `200` and `204`, which is why a caller genuinely cannot tell "moved" from "nothing". Without a `Content-Length` the same 3xx already throws, so the silence depends on a header IIS sets by default.

**The finding that matters more than the fix.** #14516 §2.2 argued #8316 was separable, and stated what would falsify that. **It is falsified twice over** — `300`, `304`, `305` and any other non-family 3xx are silent *even with `FollowRedirects = true`*, which my "the two sets do not intersect" claim said was impossible; and the two-hop residual I attributed to #8323 is actually closed by **this** change, not that one. §1.2.

**The cost, where it is non-zero.** A caller who today receives `null` from a 3xx and handles null gracefully now receives an exception. **#8916 records a real consumer in that shape** — mamgo connectors treating a 3xx as satisfying a delete-on-confirmed-2xx guarantee. That consumer is not mine to fix and the change does not break it: it **reveals** that it was already deleting on "moved". Sequencing, not scope, is the answer (§6.2).

**Strongest rejected alternative — narrow the band only for the five redirect-family statuses.** It keeps `304` and `300` working as they do today and sounds surgical. It is the same mis-scoped discriminator that produced this document's headline finding, and **D1.1** rejects it on those grounds.

**Version: minor, `0.15.0-preview`**, and the release note leads with it. Patch is the wrong instrument and §6.3 says why.

**Should it ship now? Ruled: yes, and without a migration window** (Toni, 2026-09-22 — §9.1). His reasoning is stronger than the case this document made from inside the library: **the affected call is already failing.** A `null` that means "moved" reaches a `NullReferenceException` a few frames later, so this does not convert working calls into failing ones — it **converts a dishonest failure into a truthful one**. And where no response is expected there is no NRE to surface anything, so those calls are silent today; that is where he most wants the noise.

---

## 1. Problem

#8316, filed at the repo-map build on 2026-08-18 and untouched since:

> A request that gets a `301`/`302`/`303`/`308` returns **`null`** instead of either following the redirect or reporting one. No exception.

Three defaults line up: `CheckHttpResponse` throws only outside `200–399`; the default handler is built with `AllowAutoRedirect = false`; and `HttpOptions.FollowRedirects` defaults to `false`. The 3xx is classified as success, reaches `ReadResponse<T>`, meets the `ContentLength == 0` short-circuit and becomes `default(T)`.

This is the **other half of the production outage** that #14513 closed. #14513's four-point account described exactly this chain; the fix shipped in `0.13.1-preview` closed it only for callers who set `FollowRedirects = true`. **For everyone else the outage's mechanism is intact**, and "everyone else" is the default.

### 1.1 What the caller actually loses

Not just the status. A 3xx carries the **one piece of information that would have diagnosed the outage in a line** — the `Location` header — and `default(T)` discards it along with everything else. #14513 records the cost of that discarding: 15 `NullReferenceException`s across three unrelated-looking call paths, several frames from the cause, with the real cause a provider host migration nobody had been told about.

### 1.2 #14516 §2.2 is falsified, and I was wrong twice

My own predecessor design argued that #8316 was separable from #14513. It stated the falsifier, as #9951 requires. **Both halves of that argument are now measured wrong.**

**First error — the claim.** §2.2 said:

> *"Under D1 every member of the redirect family — 301, 302, 303, 307, 308 — is either followed or raises `HttpServiceException` when `FollowRedirects` is **on**. The two sets do not intersect, so #8316 cannot leave a hole inside this design's scope."*

Measured at `7c03159` (§3): **`300`, `304`, `305` and `399` return a silent `null` with `FollowRedirects = true`.** They are 3xx statuses outside the five-member family, so neither arm of the redirect branch matches them and they fall straight through to the band check. The sets **do** intersect, and #14513 shipped with a silent-`null` hole inside its own scope.

**The mechanism of the error is the one I wrote a rule about afterwards.** §2.2's falsifier clause read *"a **308** that is silent despite `FollowRedirects = true`"*. The falsifying class is *"a **3xx** that is silent despite `FollowRedirects = true`"*. I enumerated the paths correctly and **mis-scoped the discriminator** — which is precisely the failure #14516 §D7.2 now names: *the enumeration obligation applies to the rule's discriminator, not only to its paths*. That rule was written **three rounds after** this paragraph, from a different defect, and it describes this one exactly. I did not go back and re-apply it to the argument that had shipped.

**Second error — the remedy.** §2.2 said of the two-hop chain:

> *"if Toni disagrees, the remedy is #8323 item 1 + 3 together, not #8316."*

**Backwards.** #8323 is the remedy for *following* more hops. The remedy for the chain being **silent** is the band — this change. A `308 → 308` chain under `FollowRedirects = true` delivers the second 308 to the band check, which will now throw. §5.3.

**What I am taking from this, stated because it generalises past this repo.** A separability argument is a universal (*"no hole can exist inside this scope"*) and decays like one. It was checked once, at the moment it was written, by its author, against the case in front of him. **Nothing re-checks a scope argument after the thing it scoped has shipped** — there is no diff that nominates it, no test that fails for it, and the reviewer who would have attacked it has moved on. That is the same blind spot #8311's own history records for concept nodes, applied to a decision instead of a description.

---

## 2. Scope

**In scope:** what `CheckHttpResponse` accepts, and the message it produces for the statuses it stops accepting.

**Out of scope, unchanged, and not folded in:**

| Filed as | Not done here |
|---|---|
| **#8323** items 1–3 | one hop only; no loop guard; the legacy arm's forced `GET`. This change makes a two-hop chain **loud** (§5.3); it does not make it **followed**, which is the actual capability #8323 tracks. |
| **#8916** | mamgo connectors treating a 3xx as satisfying a delete-on-confirmed-2xx guarantee. **Severity 2, different project, explicitly not mine.** It is named in §6.2 because it is the one consumer whose behaviour is known to change, not because anything here touches it. |
| the WASM divergence | #8316 asks that the two runtimes agree. After this change they are both **loud** but they still **differ** — see §5.4, which states what "agree" would actually cost and why it is not bought here. |
| #8318, #8319, #9665 | unrelated surfaces on the same file. |

**Also not done:** no new option, no new public type, no opt-out knob, no new escape hatch. **D4**.

### 2.1 The outcome that must be true when this ships

> A caller who receives a redirect they did not ask to follow must be able to tell that from an empty successful response.

Every decision is checked against that sentence, and §8 states what would break it.

---

## 3. What was measured

Probe built and run in this worktree against the library project at `7c03159`, .NET 8. Each cell is one real call through `HttpService` with a handler returning the named status; `Location` present throughout.

| status | `Get<T>`, default options, `Content-Length: 0` | same, no `Content-Length` | `Get<T>`, `FollowRedirects=true` | `Get<HttpResponseMessage>` | `Get<string>` |
|---|---|---|---|---|---|
| 200 | **null, silent** | throws | **null, silent** | live | **null, silent** |
| 204 | **null, silent** | throws | **null, silent** | live | **null, silent** |
| **300** | **null, silent** | throws | **null, silent** | live | **null, silent** |
| 301 | **null, silent** | throws | value (followed) | live | **null, silent** |
| 302 | **null, silent** | throws | value (followed) | live | **null, silent** |
| 303 | **null, silent** | throws | value (followed) | live | **null, silent** |
| **304** | **null, silent** | throws | **null, silent** | live | **null, silent** |
| **305** | **null, silent** | throws | **null, silent** | live | **null, silent** |
| 307 | **null, silent** | throws | value (followed) | live | **null, silent** |
| 308 | **null, silent** | throws | value (followed) | live | **null, silent** |
| **399** | **null, silent** | throws | **null, silent** | live | **null, silent** |
| 400 | throws | throws | throws | live | throws |

Five things follow, and three of them changed the design:

1. **The default path is silent for the entire 3xx band**, not for the redirect family. That is #8316 as filed.
2. **`300`, `304`, `305`, `399` are silent even when following** — §1.2, the falsification.
3. **`200` and `204` are silent in exactly the same way.** This is why the outcome in §2.1 cannot be met by changing what `null` means: `null` already means "empty success" for two legitimate statuses, and it is not available as a redirect signal. **Only the status check can carry this.**
4. **Without `Content-Length` everything already throws**, via the unknown-media-type path — loudly, but with the unrelated message *"Unable to decode response"*. So the silence depends on a header IIS sets by default, and a caller who has never hit this may simply have been talking to nginx.
5. **`Get<HttpResponseMessage>` returns a live response for every status**, 3xx included. The sanctioned escape hatch is real, complete, and already exempt from validation — which is what makes **D4**'s "no new knob" affordable.

---

## 4. Decisions

### D1 — the success band becomes `200–299`

`CheckHttpResponse` throws for any status outside `200–299`. One comparison changes; no branch is added.

**Why the band and not the short-circuit.** The silence has two ingredients — a 3xx classified as success, and a zero-length body read as `default(T)`. Only the first is wrong. `200` with an empty body **should** yield `null` (measured row 1; it is how every result-carrying call against an empty `204` behaves, and callers depend on it). Narrowing the zero-length guard would change that and would be a much larger blast radius for no gain. **The defect is the classification, so the classification is what moves.**

**Why `299` and not `2xx`-plus-exceptions.** Extension statuses inside 2xx (`207 Multi-Status`, `226 IM Used`) remain success, correctly and without enumeration. Statuses above 399 already throw. The band becomes the HTTP definition of success rather than a range that happened to include the redirect classes.

#### D1.1 Rejected: narrow only for the five redirect-family statuses

The surgical-sounding alternative. `CheckHttpResponse` would throw for `301`/`302`/`303`/`307`/`308` and keep accepting the rest of the band, so `304` and `300` behave exactly as they do today and the blast radius shrinks.

**Rejected, and the reason is this document's own subject matter.** That rule discriminates by *family membership* where the defect is defined by *band membership* — which is character-for-character the error §1.2 records me making in #14516 §2.2, where the falsifier clause said *"a 308"* and the falsifying class was *"a 3xx"*. Adopting it would leave `300`, `304`, `305` and every other non-family 3xx returning a silent `null`, i.e. **it would close the reported half of #8316 and preserve the half nobody had noticed** — and it would do so in a way that reads as complete, because the reported half is the one the task names.

Two further reasons it is worse than it looks:

- **It requires an enumeration where the band requires none.** Five statuses named in a condition is five things to get wrong when a sixth redirect status is registered, and it has happened before — `308` itself post-dates `301`–`307` by fifteen years and is the reason #14513 existed.
- **It makes the rule un-statable in one sentence.** *"3xx is not success"* is checkable by a reader against any status. *"These five are not success and those four are"* is a table nobody maintains.

§6.1 row 5 exists specifically to go red for this implementation, because rows 1–4 all pass under it.

### D2 — the message names the target and the way out

The `HttpServiceException` for an unfollowed 3xx carries, beyond the existing url/status/header block: **the redirect target**, and **the two ways to handle it** — set `HttpOptions.FollowRedirects`, or request `HttpResponseMessage` to inspect the response directly.

**Why the target belongs in the message line and not only in the header dump.** `DumpHeaders` already renders `Location` with its query redacted (`urlValuedHeaders`, #9617/#9940), so the information is *sometimes* there. But `HeaderDumpMode.Omitted` is a supported mode, and a consumer logging only `ex.Message` under it would get a status and no destination — which is the exact diagnostic #14513's outage needed and did not have. It is one interpolated clause on a path that already builds a message.

**Why the remedy hint.** The caller most likely to meet this exception is one who has never heard of `FollowRedirects`, because they never set it. An exception that says what to do costs one clause and removes the support round-trip. Redaction is inherited from `RedactQuery`, not re-implemented (#1136 §1 DRY).

**No new exception type.** `HttpServiceException` already carries the response undisposed for the caller to inspect, which is exactly what someone reading a redirect needs (#1136 §2).

### D3 — `304 Not Modified` throws too, and that is the decision, not an oversight

A conditional request answered `304` will now raise where it previously returned `null`.

**This looks like the one case worth exempting and it is not.** The argument for exempting it: `304` is semantically a success — the conditional was satisfied, and there is deliberately no body. The argument that wins: **a caller who received `default(T)` from a `304` never learned that either.** They could not distinguish "not modified" from "empty" any more than they could distinguish "moved" from "empty". Exempting `304` would preserve a caller's ability to keep not knowing something, and would add a named special case to a band check whose whole value is that it has none.

**A caller who genuinely performs conditional requests needs the response, not the type** — the `ETag`, the `Cache-Control`, the status itself. `Get<HttpResponseMessage>` gives all of it and is exempt from validation by design. That is the shape, it already ships, and it is the same answer **D4** gives to everyone else.

**Accepted cost, named plainly:** a caller sending `If-None-Match` through `HttpOptions.Headers` and reading a typed result goes from a silent `null` to an exception. I judge that an improvement — they were misreading their cache state — but it is a real break and it belongs in the release note beside the redirect one.

### D4 — no opt-out knob, and the escape hatch is the one that already exists

#1136 §3 requires a named operator or an environment difference before a knob ships. Neither exists. Three further reasons:

- **The knob's only honest setting is the defect.** A `LegacyStatusBand` option preserves the ability to receive `null` for "moved" — that is not a preference, it is the bug with a switch on it.
- **The escape hatch already ships and is complete** (§3, measured for all twelve statuses). `Get<HttpResponseMessage>` skips validation by design and hands back a live response.
- **A caller who wants the redirect followed has an option for that**, and it is the one this exception names.

**If a real caller with a real need appears, the knob comes back with their shape in hand** (#1136 §3). Predicting it now produces the wrong shape (#1184).

---

## 5. Consequences

### 5.1 Who is affected

| Caller shape | Change |
|---|---|
| Any status outside 3xx | **None.** 2xx succeeds, 4xx/5xx throws, both exactly as before. |
| `FollowRedirects = true`, 301/302/303/307/308, single hop | **None.** Followed before the band check is reached. |
| `FollowRedirects = true`, **300/304/305/399** | **Changed.** Was silent `null` (§1.2's falsification); now throws. |
| `FollowRedirects = true`, a **two-hop chain** | **Changed.** Was silent `null`; now throws. §5.3. |
| **Default options, any 3xx** | **Changed. This is the fix, and it is every caller.** |
| Result-less members (`Get(url)`, `Post(url, body)`, …) | **Changed, and this is the row that benefits most — see §5.5.** They call `CheckHttpResponse` directly, so a 3xx now throws where it returned silently. Consistent with PR #14, which made the bodyless POST validate for the same reason. |
| `Get<HttpResponseMessage>` / `Send<HttpResponseMessage>` | **None.** Exempt by design. |

**No public signature changes.** No interface member added, removed or re-typed; no option added.

### 5.2 The known downstream consumer

**#8916 records that mamgo connectors treat a 3xx as satisfying a delete-on-confirmed-2xx guarantee.** It is severity 2, it is a different project, and it is **not in scope here**.

It is named because it is the one place where this change's effect is *known* rather than assumed, and the honest description matters: **this change does not break that consumer — it reveals that the consumer was already deleting records on "the resource moved".** The data-loss risk exists today and is silent; after this change it is an exception. That is strictly safer, and it will nonetheless arrive at that team as "the http library started throwing".

**The consequence for this design is sequencing, not scope** (§6.2 and §9.1).

### 5.3 It closes the two-hop chain's silence — the residual #14516 named

With `FollowRedirects = true`, a `308` answering a `308` is followed once; the second reaches the band check and now throws. The silent-`null` residual §2.2 volunteered is **closed by this change**, and closed for every chain of every redirect status, not only 308.

**What is not closed:** the chain is still not *followed*. That is #8323 item 1 and it stays filed. The distinction is the one §1.2 records me getting backwards: **#8316 is the remedy for silence; #8323 is the remedy for capability.**

### 5.4 The runtime divergence #8316 asked about is narrowed, not closed

#8316 ends: *"the two runtimes should agree."* On WebAssembly the constructor takes a bare `HttpClient`, which auto-redirects, so a 3xx is usually never seen. After this change: WASM follows transparently; every other runtime throws. **Both are loud; they are still not the same.**

Making them actually agree means changing which handler the constructor builds — either enabling platform redirects off-WASM, or disabling them on WASM. Both are constructor-level changes to the library's oldest deliberate decision (the `// wasm crashes with allowautoredirect` comment records why the branch exists), both belong with #8323's follow-policy work, and neither is bought by a one-comparison change to a band. **Stated rather than silently left**, because a reader of #8316 will expect it and should know it is still open.

---

### 5.5 The two arguments this document could not make from inside the library

Both come from Toni's ruling (§9.1) and both invert something §5.1 states neutrally. Recorded here rather than only in the open-questions section, because they change how the change should be *described*, not just whether it ships.

**First: the affected call is already failing.** This design argued the change *"makes a wrong answer loud"*. That understates it. A `null` standing in for "the resource moved" does not sit quietly in a caller — it is dereferenced a few frames later and becomes a `NullReferenceException`, which is exactly how #14513's outage presented: 15 NREs, none of them near the cause. **So the comparison is not between a working call and a failing one. It is between a failure that lies about its cause and a failure that names it.** A library cannot see that; it is a fact about what callers do with the value, and it is the strongest argument in favour of the change.

**Second: the result-less members are the biggest win, not the biggest risk.** From inside the library they look like the riskiest row in §5.1 — they are the calls with no return value to inspect, so changing them from *returns silently* to *throws* looks like the largest behavioural jump. The inversion: **because there is no return value, there is no later dereference, so there is no NRE and nothing surfaces at all.** Those are the only genuinely silent failures in the whole table. Everywhere else the caller eventually finds out by accident; here they never do. A trigger, a webhook or a state-change poke that has been quietly redirected for months is precisely the case this change exists to expose.

**Consequence for §6.2:** the release note should lead with the result-less members rather than treat them as a footnote to the typed ones.

---

## 6. Compatibility, coverage, version

### 6.1 Coverage

`Http.Tests/HttpServiceStatusBandTests.cs` (new fixture — the band is now its own behaviour and does not belong inside the redirect fixture). Each row names the **guard John writes**; the fourth column is what makes it go red **uniquely**.

| # | Guard | Shape | Goes red uniquely when |
|---|---|---|---|
| 1 | `Get_Redirect302_DefaultOptions_Throws` | 302 + `Content-Length: 0`, no options; assert `HttpServiceException` | the band was not narrowed — the core fix |
| 2 | `Get_Redirect302_DefaultOptions_MessageNamesTheTarget` | same; assert the message contains the `Location` value | D2's target clause missing |
| 3 | `Get_Redirect302_MessageNamesFollowRedirectsAndRawResponse` | same; assert both remedies named | D2's hint clause missing |
| 4 | `Get_Redirect302_TargetQueryIsRedactedInTheMessage` | `Location` carrying `?token=…`; assert the value is absent | the target was interpolated raw instead of through `RedactQuery` |
| 5 | `Get_NonFamily3xx_DefaultOptions_Throws` | `[TestCase]` over **300, 304, 305, 399**; assert throws | the implementer narrowed by **family** rather than by **band** — **D1.1**'s rejected alternative, and the only row that catches it |
| 6 | `Get_NonFamily3xx_FollowRedirectsTrue_Throws` | same four, `FollowRedirects = true`; assert throws | the §1.2 hole is still open with following on |
| 7 | `Get_Status200EmptyBody_StillReturnsNull` | 200 + `Content-Length: 0`; assert `null`, no throw | **dual** — the zero-length short-circuit was changed instead of the band (D1) |
| 8 | `Get_Status204_StillReturnsNull` | 204; assert `null`, no throw | **dual** — the band was narrowed past 2xx |
| 9 | `Get_Status207_StillSucceeds` | 207 with a JSON body; assert the value | **dual** — the band was narrowed to a literal 200/201 list rather than to 2xx |
| 10 | `Get_RawResponse_Redirect302_StillReturnsLiveResponse` | `Get<HttpResponseMessage>`, 302; assert status 302, undisposed | **dual** — validation stopped being skipped for the raw type, removing the escape hatch D4 depends on |
| 11 | `GetResultLess_Redirect302_Throws` | `Get(url)` with no type; assert throws | the result-less members were missed — they call `CheckHttpResponse` directly, not through `HandleResponse` |
| 12 | `Get_Redirect308_FollowRedirectsTrue_TwoHopChain_Throws` | 308 → 308, following; assert throws and exactly two requests | §5.3 regressed, or the implementer added hop-following (#8323) while here |
| 13 | `Get_Redirect302_FollowRedirectsTrue_SingleHop_StillSucceeds` | 302 → 200, following; assert the value | **dual** — the band check moved ahead of the redirect hop and now throws before it can follow |

Rows 7–10 and 13 are the duals #114 §13.1.1 asks for: without them the suite is green for five implementations this design rejects. **Row 5 is the one that catches the mistake this document was written about** — narrowing by family reproduces §1.2's hole exactly, and passes rows 1–4.

**Not to be added here:** anything asserting hop count or loop behaviour beyond row 12's premise. That is #8323.

### 6.2 Release note, and the one thing that is not a code change

The note must lead with this, because it is the first change in this library's history that alters behaviour for callers who set **nothing**. It must state: the band is now 2xx; an unfollowed 3xx raises; `304` is included and why; the two remedies; and that a two-hop chain now raises instead of returning null.

**And it should say plainly that a caller currently receiving `null` from a 3xx is receiving a redirect.** That sentence is what turns an upgrade into a five-minute audit for anyone in #8916's shape.

**Lead with the result-less members** (§5.5). For every other shape the caller has probably already met this as a `NullReferenceException` somewhere downstream and will recognise the description; for `Get(url)`, `Post(url, body)` and their siblings there has been **no symptom at all**, so the note is the only way that reader learns their trigger has been redirected. The typed-overload paragraph tells people why their NREs stop; the result-less paragraph tells people they had a problem.

### 6.3 Version: minor, `0.15.0-preview`

The repo convention across five consecutive changes is *patch when behaviour is byte-identical for callers who opt into nothing, minor when it is not*. #14516 §6.3 applied it and chose patch, because `FollowRedirects` defaults to false and every caller who opted into nothing was untouched.

**Here the literal test fails in the other direction.** A caller who opts into nothing is precisely the caller who is affected. The convention, applied consistently rather than by feel, returns **minor** — and this is the case it exists for. Recommending `0.15.0-preview`; Toni's call at packaging, as always.

---

## 7. Where it lives

`CheckHttpResponse` (`HttpService.cs`) already owns *"is this response an error, and what does the error say"*. Both halves of this change are answers to that question, so both stay there. **No new file, no new type, no new call site, no change to `HandleResponse`, `ReadResponse<T>` or either redirect arm.**

**Ordering is unchanged and remains load-bearing:** the redirect hop runs **before** the band check, so a followed redirect is validated on the hop's response rather than on the 3xx that caused it. Row 13 pins that; inverting it would make `FollowRedirects` useless.

The message is assembled from the members that already exist — `DumpUrl`, `RedactQuery`, `DumpHeaders` — so redaction policy (#9617, #9940) is inherited rather than re-implemented.

---

## 8. Falsifiable claims (#9951 + #14516 §D7.2)

This document exists because a scope argument in its predecessor was a universal that nobody re-checked. Every universal here is listed with the input class that would break it, **and the discriminator gets its own row**, per the rule that argument's failure produced.

| Claim | Where | What would falsify it | Does that class exist? |
|---|---|---|---|
| "after this, a caller can always distinguish a redirect from an empty success" | §2.1 | a 3xx that does not reach `CheckHttpResponse` | **Yes, one: `Get<HttpResponseMessage>`** — deliberately exempt, and the caller asked for the raw response, so they *have* the status. No other path skips it: the typed members route through `HandleResponse`, the result-less members call it directly, and row 11 pins the second. |
| "narrowing the band cannot affect non-3xx callers" | D1 | a status outside 3xx whose classification changes | **No** — the change moves exactly one boundary, from 399 to 299, and nothing between 200 and 299 or above 399 crosses it. Rows 7–9 pin the 2xx side. This is the one claim here with no live falsifier, which is why it gets three dual tests rather than a caveat. |
| **discriminator:** "the band test sees every status a server can send" | D1 | a status the transport handles before the library sees it | **Yes** — 1xx. `100 Continue` and `101 Switching Protocols` are consumed or transformed by `HttpClient` and do not surface as a response status here. They are below the band and would throw if they did; unreachable rather than handled, and named so nobody re-derives it. |
| "`null` cannot carry the redirect signal" | §3 note 3 | a `null` that only ever arises from a 3xx | **No, measured** — `200` and `204` with an empty body produce the identical `null`. This is why the fix is in the status check and not in the read path. |
| "the escape hatch is complete" | D4, §3 | a status for which `Get<HttpResponseMessage>` does not return a live response | **No** — measured across all twelve statuses in §3, 3xx included. Row 10 keeps it that way. |
| "this closes the two-hop silence" | §5.3 | a chain whose second response is not seen by the band check | **No for two hops.** For **three or more**, the second response is already the one delivered, so the claim is about chains of any length terminating at the band check — which they do, because only one hop is ever followed. If #8323 ever adds hop-following, this claim needs re-deriving, and that is exactly the kind of re-check §1.2 says nobody performs. **Noted here so the #8323 implementer inherits it.** |

### 8.1 The claim I expect a reviewer to attack first

**D3 — that `304` should throw.** The way to break it is to find a caller in this repo's consumer set performing conditional requests through a typed overload and depending on `null` meaning "not modified". I cannot measure that from here; §9.2 records it as an open question rather than an assumption I have quietly made. If such a caller exists, the answer is still not an exemption — it is that they move to `Get<HttpResponseMessage>`, which is where the `ETag` they also need already lives.

---

## 9. Open questions

### 9.1 Should this ship at all right now? **Ruled: ship, no migration window**

**Closed by Toni, 2026-09-22**, verbatim because the reasoning is better than the case either the design or the briefing made:

> *"3xx is something, but every time it returns null it usually runs into a null reference exception anyways, so the shape of the error changes into something truthful. In cases where no response is expected then a silent error turns up loudly which is also something we like to have rather than to migrate and leave the error silent :D"*

Two things in that are arguments from outside the library, and both are folded into §5.5 where the design can act on them: **the affected call is already failing**, so this trades a dishonest failure for a truthful one rather than breaking working calls; and **the result-less members are the biggest beneficiaries, not the biggest risk**, because they are the only shapes with no downstream dereference to surface the problem.

**The migration window is declined**, and with it the one fact §9.1 originally said it wanted before packaging — whether 3xx is routine rather than exceptional somewhere hot in the gateway. That question existed only to size a migration, so it is moot; the consumer grep was deliberately **not** run. Recorded because "we chose not to measure this, and here is why the measurement stopped mattering" is a different state from "nobody thought to measure it", and only one of them is safe to inherit.

**The #8916 notification (§11 step 7) survives the ruling and is downgraded from precondition to courtesy.** It is not a migration and it does not gate anything; it is a message to the one consumer known to be in the affected shape, and the difference between "we were told" and "it started throwing in prod" costs nothing to buy.

---

*The reasoning below is the case the design made before the ruling. Retained per #11228 Lesson 3 — it is what a reader would have had to evaluate, and it is weaker than what Toni supplied, which is the useful part.*

**Ship it.** Three reasons, in order of weight:

1. **The silence is the defect, and it is the half of a real outage that is still live.** #14513 cost 15 production NREs and a diagnosis measured in frames-from-the-cause. That mechanism is intact today for every caller who has not opted in, which is the default and therefore most of them. Leaving it costs the next outage.
2. **The change makes a wrong answer into a loud one. It does not make a working thing fail.** Every caller it affects is currently receiving `null` for "moved" and doing something with that `null` — and whatever they are doing is wrong, because the resource is elsewhere. #8916 is the proof that this is not hypothetical.
3. **The blast radius is large but the cost per site is one exception and a five-minute read.** This is a `0.x-preview` package whose consumers are in a monorepo the same people control. That is the cheapest this change will ever be; it gets more expensive every release.

**The precondition, and I would not ship without it:** the release note must say *"if you are currently receiving `null` from a 3xx, you are receiving a redirect"*, **and #8916's owners should see this before the mamgo side takes the upgrade** — not to gate it, but because that is the one consumer known to be in the affected shape, and the difference between "we were told" and "it started throwing in prod" is entirely in the sequencing. Filing that as a task on #8916 costs nothing and is not a scope extension: it is a notification, not a fix.

~~**What would change my answer.** If the mamgo gateway's ~372 passthrough actions turn out to route 3xx responses through typed overloads at volume — that is, if 3xx is a *routine* answer rather than an exceptional one somewhere hot — then the change converts a steady trickle of silent nulls into a steady trickle of exceptions, and it should go behind a release boundary with a migration window rather than into the next patch. **I cannot measure that from this repo.** It is the single fact I would want before packaging, and it is one grep in the consumer.~~ — **moot as of the ruling above**; the migration window it was sizing was declined, and the grep was not run.

### 9.2 The rest

2. **Does any consumer do conditional requests through a typed overload?** (D3, §8.1.) Not measurable from here. If yes, `304` deserves its own line in the release note above the redirect one, because it will surface first and look unrelated.
3. **Patch or minor.** §6.3 recommends minor on the convention's literal test — the first time in this repo the test has come out that way. Toni's call at packaging.
4. **Should the WASM divergence be closed at the same time?** §5.4 says no, and that it belongs with #8323's follow-policy work. Raising it because #8316 explicitly asks for the runtimes to agree, and this change does not deliver that — only the "loud" half. If the answer is that they must agree, that is a different and larger design.
5. **A separability argument has no owner after it ships.** §1.2 is the second time this arc a claim decayed because nothing re-checks it. I have no proposal, and I do not want to invent a process here — but the pattern is now twice-attested and might be worth a line in the contracts, the way D7.2 was.

---

## 10. Pre-Design Checklist (#1136 §5)

| Item | Verdict |
|---|---|
| No new type mirroring an existing one | **Pass** — no new type; the exception, the message builders and the escape hatch all already exist |
| No new abstraction with one implementation | **Pass** — one comparison and one message clause |
| Nothing justified by "we might need X later" | **Pass** — D4 rejects the knob; §5.4 states the WASM question without building for it |
| No deprecation window / compat shim / feature flag | **Pass** — the change is immediate and total. §9.1 names the one case that would justify a migration window and says it is unmeasured, rather than pre-emptively building one |
| DRY math on inline-vs-extract | **N/A** — one method, one comparison; the message reuses `DumpUrl` / `RedactQuery` / `DumpHeaders` |
| Existing surface audited before adding one | **Pass** — `Get<HttpResponseMessage>` (D4), `FollowRedirects` (D2), `HttpServiceException` (D2), the redaction helpers (§7); nothing duplicated |
| Every config knob has a named operator | **Pass by removal** — no knob; D4 records that no operator could be named and that the knob's only setting would be the defect |
| Can-it-be-deleted / merged / inlined | **Pass** — the change *narrows* an existing condition rather than adding one; D1 explains why the alternative site (the zero-length guard) is the wrong one to touch |
| Trade-offs named explicitly | **Pass** — TL;DR (rejected family-narrowing), D3 (`304`), §5.2 (#8916), §5.4 (runtimes still differ), §6.3 (minor), §9.1 (ship-now, with the fact that would reverse it) |
| Out-of-scope listed explicitly, not merely absent | **Pass** — §2 table; §5.2 and §5.4 say what is *not* being fixed in the two places a reader would assume otherwise |
| No multi-paragraph rationale for things that obviously stay | **Pass** |
| Predecessor design banner where superseded | **N/A for supersession — but #14516 §2.2 is falsified, not superseded.** §1.2 states it in full here; the predecessor gets a dated correction in the same PR, per the house style it established |
| Data deliverables (SQL/schema casing) | **N/A** — no data layer |
| Defensive code for impossible scenarios | **Pass** — no branch added; the 1xx case in §8 is named as unreachable rather than guarded |

---

## 11. Implementation order

Each step leaves the suite green.

1. **The fixture first.** Add `HttpServiceStatusBandTests` with rows 7–10 and 13 — the **duals** — against the *current* behaviour. They must pass before the change and after it; that is what makes them duals rather than decoration.
2. **Narrow the band** in `CheckHttpResponse` to `200–299`. Rows 1, 5, 6, 11, 12 go green here; some existing tests may need their expectations corrected, and each such correction should be looked at individually rather than in bulk — an existing test that asserted a 3xx succeeded is evidence about a caller.
3. **Add the message clauses** (D2): target, and the two remedies. Rows 2–4.
4. **Correct #14516 §2.2 in place** with a dated note retaining the original, linking here. It is the predecessor's own house style and it is the reason this document exists.
5. **Reconcile the map** — #8297 (the band is a hazard that has been on its list since the build), #8311 stage 4.2, #8316 closed. Concept nodes count (#3414).
6. **Bump to `0.15.0-preview`** and write the release note from §6.2, leading with the `null`-means-redirect sentence.
7. **File the #8916 notification** (§9.1). Not a fix, not scope — a message to the one consumer known to be in the affected shape.
