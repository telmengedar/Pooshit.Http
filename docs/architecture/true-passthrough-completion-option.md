# Architectural Document: True Passthrough — an opt-in response completion option

> **Repo path:** `docs/architecture/true-passthrough-completion-option.md` (repository `telmengedar/Pooshit.Http`).
> **DiVoid:** source task **#8290** · diagnosis + measurements **#7959** · consumer-side adoption (blocked on this) **#7961** · error-body precedent **#2279** · repo map root **#8292** · project **#2281**.
> **Contracts cited as load-bearing:** Design Contracts **#1136** (§1 KISS/DRY/YAGNI, §5 Pre-Design Checklist) and Code Contracts **#114 §0** (implementer-side principles + the bounce rule).
> **Baseline:** designed against `origin/master` @ `4058425` (both PR #1 — `HttpServiceException.Body` — and PR #2 — redirect `Uri` combining — are merged). Package version on master: `0.7.18-preview`.

---

## 1. Problem Statement

### 1.1 The ask, verbatim

Toni, 2026-08-18:

> *"task 8290 is about streaming results looking like streams but actually being buffered (never a true passthrough). We at least need a httpoption to behave like a true passthrough - task should contain most of the details."*

And earlier, from the mamgo-backend side:

> *"api should be passthrough, buffering is not passthrough."*

### 1.2 Restated

`HttpService` issues every request without specifying a completion behaviour. The framework default is *read the entire response body into memory before the send completes*. `HttpOptions` carries no field that can change this, and there is no overload that accepts one. Consequently:

- The library's two **streaming-shaped** return types — a stream, and the raw response message — hand the caller something whose bytes are already fully materialised. The signature promises incremental consumption; the send has already defeated it.
- A caller who wants a genuine passthrough has no escape short of abandoning the library and holding a client directly, which is the thing the library exists to prevent.

The goal is a **caller-controlled opt-in** that makes the send complete as soon as the response headers have arrived, leaving the body on the wire for the caller to consume incrementally.

### 1.3 Success criteria

| # | Criterion | How it is observed |
|---|---|---|
| S1 | A caller can request headers-only completion through `HttpOptions`, on every request-issuing member of `IHttpService`. | Every public member accepts options; the option reaches the send. |
| S2 | With the opt-in set, the library reads **zero bytes** of a successful response body before returning to the caller. | A probe content that counts bytes read reports 0 after the call returns. |
| S3 | With the opt-in set, a response with **no declared content length** (chunked / undeclared) is delivered, not silently swallowed. | A chunked-shaped response requested as a stream returns a readable, non-null stream. |
| S4 | Callers who do **not** set the option observe byte-for-byte identical behaviour to today. | The narrowing in §5.1 is provably equivalent under buffering; regression tests pin it. |
| S5 | `HttpServiceException.Body` (#2279) still carries the error body, under both completion modes. | Regression tests on both paths. |

### 1.4 Why this is worth doing

Measured downstream (#7959): **3.6–5.5× the payload** in working set; **372** gateway passthrough actions affected; one confirmed OOMKill (`api-66f558dddc-46n75`, 2026-08-09, exit 137). #7961 is open and blocked on this change.

---

## 2. Scope & Non-Scope

### 2.1 In scope

| # | Element | Why it is here |
|---|---|---|
| E1 | A new `HttpOptions` member carrying the completion behaviour. | The literal ask. |
| E2 | A single shared send helper that reads E1 and applies it, replacing 17 direct send call sites. | DRY — math in §6.2. |
| E3 | Narrowing the zero-length short-circuit in the typed read so an **absent** length is no longer treated as zero. | Structural blocker: without it the feature returns silent nulls (§5.1). |
| E4 | Disposal of the response in the members that return no result, and of the superseded response on the redirect hop. | Both are connection leaks **created by E1**; fixing a leak your own feature introduces is in scope (§5.2). |
| E5 | An options parameter on the one interface member that lacks one, the result-less raw-request send. | Otherwise a public member cannot participate in the feature (§5.5). |
| E6 | Test infrastructure + the coverage matrix in §11. | Deliverable requirement. |
| E7 | Package/assembly version bump to `0.7.19-preview`. | Release hygiene; master is at `0.7.18-preview`. |

### 2.2 Explicitly out of scope

Named here so their absence is a decision, not an oversight. The repo map surfaced ~20 defects; the operator is filing them separately.

| Item | Status |
|---|---|
| Inferring the completion behaviour from the requested response type. | **Deliberately rejected for this phase** — see §8.2. Recorded as a candidate, not a design element. |
| The result-less no-body post member not validating status. | Out of scope. Do **not** add a status check. §5.4 covers only its disposal. |
| The forced-GET on 301/302/303, the unimplemented 307/308, and the absence of a multi-hop follower. | Out of scope. The redirect **URL resolution** defect is already fixed on master (PR #2) — do not re-fix it. |
| Exact-string media-type matching, and the invalid-cast that an unrecognised media type produces. | Out of scope. |
| The decode-failure exception being constructed inside the disposing scope, so it carries a disposed response. | Out of scope. Noted in §5.2 as unchanged, not worsened. |
| Synchronous reads (the XML branch's document load, the decoder's sync member) becoming blocking network reads under the opt-in. | Out of scope as a fix; **documented** in §5.2 and §9.2 as a known consequence. |
| Request-side (outbound) streaming; the encoder still materialises the whole body. | Out of scope — the ask is about responses. |
| Cancellation tokens on the send path. | Out of scope — not asked (YAGNI). |
| A response-size cap / `MaxResponseContentBufferSize`. | Out of scope — not asked. |
| Header encoding, query-parameter, and path-encoding defects. | Out of scope. |
| Consumer-side adoption in the mamgo gateway (#7961). | Separate task, unblocked by this one. |

---

## 3. Assumptions & Constraints

| # | Assumption / constraint | Confidence | Consequence if wrong |
|---|---|---|---|
| A1 | The completion-behaviour enum's default member (numeric value 0) is the *buffering* one. | **High** — it is the documented framework default and is what the current no-argument send resolves to. | If wrong, a plain non-nullable field would silently flip every caller to streaming; the design would need a nullable field instead. **John: verify this once before implementing; it is the load-bearing assumption behind "zero blast radius".** |
| A2 | Under buffering completion, the framework has materialised the body before the typed read runs, so the content's length is always computable at that point — even for a response that declared none. | **High** — the buffering step fills an internal buffer, and the length property falls back to the buffered length when no header was sent. The library reads the length nowhere earlier, so no null gets cached first. | This is the proof behind S4 / §5.1. If wrong, the narrowing is not behaviour-neutral and must instead be made conditional on the completion mode. Test T-G4 pins it. |
| A3 | The completion enum and the two-argument send are available on **both** target frameworks (`netstandard2.0` and `net8.0`). | **High** — both are part of the base HTTP client surface, not a modern-only addition. | Would force conditional compilation. |
| A4 | The project's language-version setting resolves to a modern compiler default on both targets, so `using`-declarations and target-typed construction are legal (they are already used in the file). | **High** — observable in the existing source. | None; already proven. |
| A5 | On the browser/WASM runtime the platform handler may ignore headers-only completion and buffer anyway. | Medium. | Degrades to today's behaviour — no error, no correctness loss. Documented, not guarded against. |
| A6 | The consumer that motivates this (#7961) requests the **raw response message** or a **stream**; it does not ask for a decoded domain object with the opt-in set. | High (from #7959: the 372 actions return the raw message). | Only affects how much weight the residual in §5.1.4 carries. |
| A7 | Consumers recompile against the new package rather than binding to the old assembly. | High — it is a preview-versioned internal NuGet. | E5 changes a method signature; a non-recompiled consumer would fault at call time. |

**Organisational constraint.** The library ships as a NuGet package consumed by mamgo. There is no atomic deploy across the boundary: this must merge and publish before #7961 can move. That ordering is already recorded in #7959 §7.

---

## 4. Architectural Overview

The library has four stages. The completion behaviour belongs to exactly one of them — **the send** — and today no stage can influence it.

```
  caller
    │  url / body / TResponse / HttpOptions
    ▼
┌──────────────────────┐
│ 1. request build     │  auth, headers, expect-continue, body strategy
└──────────┬───────────┘
           ▼
┌──────────────────────┐        ◄── THE ONLY STRUCTURAL CHANGE
│ 2. send              │   E2: one helper, reads E1 from the options,
│    (17 call sites)   │       applies it to every send incl. the redirect hop
└──────────┬───────────┘
           ▼
┌──────────────────────┐
│ 3. response handling │   redirect hop (re-enters stage 2) → status validation
│                      │   status validation reads the body ONLY on error (#2279)
└──────────┬───────────┘
           ▼
┌──────────────────────┐
│ 4. typed read        │   E3: the length guard stops swallowing undeclared lengths
│    (9 branches)      │   E4: disposal rules restated per branch archetype
└──────────┬───────────┘
           ▼
  caller  (owns the connection when it received a stream or the raw message)
```

The design adds **no new type, no new interface, no new abstraction layer**. It adds one data member to an existing options bag, one private helper, and corrects two lines that the new member would otherwise turn into defects. That is the whole shape.

---

## 5. The five hard parts, answered by name

### 5.1 Hard part 1 — the content-length-zero guard (the structural blocker)

**What the code does today.** Before any typed read path is selected, the reader takes the content's declared length, **substitutes zero when the length is absent**, and on zero returns the default value for the requested type without reading anything.

**Why it blocks this feature.** Under headers-only completion, a chunked or otherwise undeclared-length response has **no** declared length. The substitution turns "I do not know yet" into "there is nothing", and the caller receives a null — no exception, no log, HTTP 200. The streaming passthrough this feature exists to provide would silently return nothing. This is a hard blocker, not a footnote.

**Why it is invisible today.** Because the body is always fully buffered before that line runs, the length is always computable (assumption A2). So today the guard only ever fires on a body that is *genuinely* empty. The defect is **latent**, and the opt-in is what makes it live.

#### 5.1.1 The fix

**Narrow the guard: short-circuit only on a length that is present and equal to zero. An absent length no longer short-circuits.**

#### 5.1.2 What it does to existing callers — nothing, provably

For a caller that does not set the opt-in, the body is buffered before the guard runs, so the length is never absent (A2). For those callers the narrowed condition and the current condition are **the same predicate over the same inputs** — every branch below the guard is reached in exactly the same cases. Zero blast radius, and this is a testable claim, not an assertion (T-G3 / T-G4).

#### 5.1.3 Why narrowing rather than the alternatives

| Alternative | Verdict |
|---|---|
| Remove the guard entirely. | **Rejected.** A genuinely empty body with no content type would fall to the final fallback branch, which force-casts a stream to the requested type — turning today's benign null into an invalid-cast failure. A real regression. |
| Make the guard conditional on the completion mode. | **Rejected.** It adds a branch, keeps a known-wrong line alive, and makes behaviour depend on a mode that has nothing to do with the question being asked. Narrowing is simpler *and* removes the latent defect (KISS, #1136 §4 "can it be deleted"). |
| Narrow to a present-and-zero test. | **Chosen.** One line, strictly smaller, provably equivalent for every existing caller, and it unblocks the feature. |

#### 5.1.4 The residual, stated plainly

Under the opt-in **and** an empty body **and** no declared length, the guard no longer fires and the read path runs:

| Requested type | Before | After (opt-in only) |
|---|---|---|
| Stream | null | an empty, readable stream |
| String | null | the empty string |
| Byte array | null | an empty array |
| Raw response message | unchanged — returns before the guard | unchanged |
| Domain type, JSON content type | null | a decode failure surfaced as the library's exception |
| Domain type, no content type | null | invalid cast from the fallback branch |

The first three are arguably more correct. The last two are a change from a silent null to a loud failure, reachable only by a caller who opts into streaming *and* asks for a decoded object *and* receives an empty undeclared-length body — a combination A6 says the motivating consumer does not produce. **No extra guard is added for it** (#1136 §6, "defensive code for impossible scenarios" / YAGNI). It is documented in the member's documentation comment and pinned by tests T-G5 and T-G6 so the behaviour is deliberate rather than discovered.

#### 5.1.5 One line more, on the line we already own

When the guard fires, the response is currently dropped **undisposed**. Under buffering that costs a garbage-collected object; under the opt-in it holds a pooled connection open until finalisation. Since the returned value is the type default and references nothing, disposing before returning is unambiguously safe. **Dispose the response when the guard fires.**

---

### 5.2 Hard part 2 — disposal ordering, per branch, archetype-aware

Under headers-only completion the response object must **outlive the reading of its stream**. A flat rule is wrong in both directions, so the contract is stated per branch. The rule that decides each row is:

> A `using` around a read that **fully materialises a value before the scope exits** stays correct under both completion modes. A branch that **returns the stream itself** must not dispose, and must transfer ownership to the caller.

| # | Branch (selected by requested type / media type) | Disposes today? | Correct under opt-in? | Action |
|---|---|---|---|---|
| 1 | Raw response message — returned first, before validation and before the guard | No | **Yes** — ownership transfers to the caller | **No change.** This is the recommended shape for passthrough. |
| 2 | Length guard short-circuit | No | No — leaks a connection | **Change (E4):** dispose before returning the default (§5.1.5) |
| 3 | Stream | No, deliberately, with a comment saying why | **Yes** — and now load-bearing rather than merely tidy | **No change to the code.** Strengthen the comment and the member documentation: under the opt-in the caller owns the connection. |
| 4 | String | Yes | Yes — the read completes inside the scope | No change |
| 5 | Byte array | Yes | Yes — same | No change |
| 6 | JSON, via the configured or default decoder | Yes | Yes — the decode completes inside the scope | No change |
| 7 | XML (both canonical media types) | Yes | Yes for correctness; see the note below | No change |
| 8 | Plain text | Yes | Yes | No change |
| 9 | Fallback — any other or absent media type; returns the stream | No | Yes — same archetype as branch 3 | No change |

**Note on branch 7 (and on the decoder's synchronous member).** The XML branch loads the document **synchronously** from the content stream. Under buffering that is a memory read; under the opt-in it becomes a blocking network read on the calling thread. This is a thread-utilisation hazard, not a correctness bug, and it is **out of scope** (§2.2) — but John must not "fix" it by adding a `using` change or an await that alters the disposal shape.

**Note on branch 6, unchanged.** The decode-failure exception is constructed inside the disposing scope, so the response it carries is already disposed when anyone catches it. Pre-existing; identical under both modes; out of scope. `HttpServiceException.Body` is unaffected — the error path (§5.3) is a different code path entirely.

#### 5.2.1 Who disposes on the streaming path, and how they get a handle

This is the question the feature must answer for its users, and the answer is deliberately **not** a new carrier type.

| Caller asked for | Handle the caller holds | Obligation |
|---|---|---|
| The raw response message | The message itself | Dispose the message. Disposing it disposes the content and returns the connection. This is the shape #7961 should adopt. |
| A stream (branch 3 or the fallback branch 9) | The stream only — **no handle on the response** | Dispose the stream. Disposing the response's content stream releases the connection; the response object itself then holds nothing but headers and is collected. |
| Anything materialised (string / bytes / XML / decoded object) | Nothing | The library disposed the response before returning. |
| Nothing (a result-less member) | Nothing | The library disposes (§5.4). |

**Rejected:** introducing a wrapper that pairs the stream with its response so callers can dispose both. It changes the return type of an existing branch (breaking every current caller), it is an abstraction with exactly one consumer, and it solves a problem that disposing the stream already solves. #1136 §4 and §6 both bite. **Instead:** the obligation is stated in the documentation comment on the new option and on the stream-returning members.

---

### 5.3 Hard part 3 — the error path must keep working (do not regress #2279)

**Finding: no change is required, and the reason matters.**

Status validation is shaped as *"if the status falls outside the accepted band, read the body to a string and throw carrying it"*. Two properties follow directly from that shape:

1. **On success it touches the content not at all.** The read lives inside the failure branch. It cannot re-buffer a successful streaming response — which is exactly the confirmation the brief asks for, and it is what makes S2 achievable. Pinned by test T-C1.
2. **On failure it performs the read itself**, so the body is materialised **regardless of the caller's completion choice**. Under the opt-in the read simply pulls from the wire instead of from memory. `HttpServiceException.Body` is populated identically in both modes. Pinned by T-C2 and T-C3.

**Consequence, stated as a deliberate boundary of the feature:** *error responses are not passthrough.* Their bodies are always fully materialised into a string, because #2279 requires it and mamgo depends on it. No size cap is introduced (out of scope, §2.2). This is a bounded exception and must be named in the option's documentation so nobody reads "true passthrough" as "nothing is ever buffered".

**Ordering, unchanged:** validation runs **before** the typed read, so a caller who receives a stream has already had the status checked — except when the requested type is the raw response message, which is exempt from validation today and stays exempt.

---

### 5.4 Hard part 4 — the redirect re-send

**Baseline.** On `origin/master` @ `4058425` the redirect block resolves the target by combining the response's request URI with the `Location` value (PR #2, DiVoid #7277), after the URL processor has been applied to the raw location. It then issues a fresh GET through a direct send call. The URL-resolution defect is **fixed and out of scope** — do not touch that resolution logic.

**What this design changes there:** exactly two things.

1. **The second send goes through the shared helper (E2)** and therefore carries the same completion behaviour as the first. Nothing else is needed — the helper reads the same options instance that the redirect block already has in hand. This is the whole of the carry-through, and it is a consequence of E2 rather than a special case, which is one of the reasons E2 exists.
2. **The superseded response is disposed** once it has been replaced. Today it is abandoned; under the opt-in that abandons an open connection. Everything the block needs from it (the status, the location header, the originating request URI) has been read before the re-send is issued, and nothing afterwards references it. One line.

**Interaction with the pre-existing redirect defects, none of which this change touches or worsens:**

| Pre-existing behaviour | Interaction |
|---|---|
| Only a single hop is followed; a second redirect in a chain is not followed. | Unchanged. A second redirect falls through to validation, which accepts the 3xx band as success, then reaches the typed read. Under the opt-in with a stream or raw-message request, handing back that 3xx is the correct passthrough outcome. Under buffering nothing changes (A2 keeps the guard's behaviour identical). |
| 301/302/303 are all re-sent as GET. | Unchanged. |
| 307/308 throw. | Unchanged — and the throw happens before any send, so no completion concern. |
| Absence of a hop-count limit. | Unchanged, and mildly **improved**: the disposal in (2) means a followed hop no longer strands a connection. |

---

### 5.5 Hard part 5 — the result-less raw-request send has no options parameter

**The problem.** The interface's result-less raw-request send is the **only** member that takes no options. It therefore has no way to receive the new member, leaving a hole in a feature whose entire point is uniform behaviour.

**Decision: add an optional options parameter to it, on both the interface and the implementation.**

| Option | Verdict |
|---|---|
| Add an optional options parameter. | **Chosen.** Every other member of the interface already takes options; this member's omission is an accidental asymmetry, not a deliberate one. Restoring symmetry costs one optional parameter and adds no new member. |
| Leave it alone; tell callers to use the generic raw-request send requesting the raw response message. | **Rejected.** Not equivalent — that member skips status validation and changes the return type. It would hand callers a worse workaround than the hole. |
| Add a second, parallel two-argument member alongside the existing one. | **Rejected.** Two near-identical members is the parallel-surface smell (#1136 §6); the optional parameter achieves the same reach with less surface. |

**What it does to existing callers:**

- **Source compatibility: preserved.** The parameter is optional and appended last; every existing call site compiles unchanged.
- **Binary compatibility: broken** for an assembly compiled against the previous signature (A7 says consumers recompile against the package; the version bump E7 signals it).
- **Implementers of the interface outside this repo: broken** — they must add the parameter. Accepted; the interface is small and internally consumed.

**Behaviour inside the member is otherwise unchanged**: it still validates the status and still ignores the response's body. It gains the disposal described in §5.6.

---

### 5.6 Consequential element — disposal in the result-less members (E4)

Not one of the five named hard parts, but it is created by hard part 1's element and must be settled here.

Eight members return no result: the four verb-with-body members that validate status, the two no-body members (get and delete), the custom-verb member, and the raw-request send from §5.5. **All eight currently abandon the response undisposed.** Under buffering that is a collected object. Under the opt-in it is a pooled connection held until finalisation — a leak the new option introduces, in half the public surface. Left unfixed, the option is a footgun.

**Rule: in every result-less member, dispose the response after status validation returns.**

Deliberately **after**, not a `using` around the whole body:

- On the **failure** path validation throws, so the response is left exactly as it is today — the thrown exception's response object is not additionally disposed by this change, and `Body` (already captured as a string) is unaffected. **Zero change on the error path.**
- On the **success** path the connection is released promptly. Nothing observable changes for the caller, which receives nothing.

The one result-less member that does not validate status keeps not validating it (out of scope, §2.2); it simply disposes.

---

## 6. Components, Responsibilities & the DRY decision

### 6.1 Responsibilities

| Component | Owns | Does **not** own |
|---|---|---|
| The options bag | Carrying the caller's completion preference as inert data alongside the other seven request options. | Interpreting it. Defaulting it at use sites. |
| The new send helper (private, single-purpose) | Resolving the effective completion behaviour from a possibly-null options instance, and issuing the send with it. **The single point where completion policy lives.** | Building requests. Validating status. Reading bodies. |
| Response handling | The redirect hop (re-entering the helper) and status validation ordering. | Choosing a completion behaviour of its own. |
| The typed read | Selecting a read branch, and the per-branch disposal contract of §5.2. | Anything about completion. |
| The caller | Disposing whatever it was handed on a streaming path (§5.2.1). | — |

### 6.2 The DRY math for the 17 send call sites

There are **17** direct send call sites: sixteen public request-issuing members plus the redirect re-send inside response handling.

If each site were edited in place, each would carry the completion-resolution expression — the null-safe read of the option with the buffering fallback.

```
block_size (1 line of policy, repeated verbatim) × site_count (17) = 17 lines of duplicated policy
```

That sits at the DRY threshold of ~15–20 (#1136 §1, #1267), and three further factors push it decisively over:

1. **It is policy, not mechanics.** The null-options fallback is a behavioural decision. Seventeen copies is seventeen places for it to drift.
2. **The named-helper test passes in one word.** A helper called `SendRequest` names itself; the extraction earns its keep by #1136's own criterion.
3. **The seventeenth site is the one that gets missed.** The redirect re-send is nested inside response handling, not in the flat list of public members — precisely the "alternate code path" shape that #2928 §7 records as the recurring first-pass miss. A helper makes missing it impossible rather than merely unlikely.

**Decision: extract the helper.** All 17 sites call it; no site touches the completion behaviour directly. A grep for the raw two-argument send should find exactly one occurrence in the library after this change — that is the reviewable invariant.

**Naming constraint for the implementer:** the type already exposes public members named for sending. Give the helper a name that cannot collide or be confused with them at a call site (`SendRequest` rather than `Send`).

---

## 7. Contracts & Interfaces (abstract)

### 7.1 The new option

| Aspect | Contract |
|---|---|
| Name | `CompletionOption` on the options bag. |
| Type | The framework's HTTP completion-option enumeration. **Non-nullable.** |
| Default | The enumeration's zero-valued member, which is the buffering behaviour (A1). An options instance constructed without touching this field therefore behaves exactly as today. |
| Semantics when options is null | Buffering. The helper supplies the fallback; no call site repeats it. |
| Invariant | Read at exactly one place — the send helper. Nothing else in the library branches on it. |
| Documentation obligation | The member's comment must state: (a) that the streaming value transfers connection ownership to the caller for stream and raw-message results; (b) that **error** bodies are still fully read so the exception can carry them (#2279); (c) that on the browser runtime the platform may buffer regardless (A5). |

**Why a plain non-nullable enum and not a nullable one, or a boolean.** The enum's default *is* today's behaviour, so nullability would buy nothing but ceremony (#1136 §4, can-it-be-deleted). A boolean would be a mirror of an existing framework enum — the parallel-type smell of #1136 §6 / Code Contracts §5.4 — and would need translating at the send anyway.

### 7.2 The send helper

| Aspect | Contract |
|---|---|
| Visibility | Private to the implementation. Not on the interface — callers steer it through the option, not by calling it. |
| Inputs | A built request message; the options instance that produced it (may be null). |
| Output | The response message, awaited to whatever degree the resolved completion behaviour dictates. |
| Semantics | Resolve the completion behaviour from the options with a buffering fallback; issue the send with it. Nothing else — no validation, no reading, no disposal. |
| Invariant | The **only** site in the library that names a completion behaviour. |

### 7.3 Interface surface change

| Member | Change |
|---|---|
| The result-less raw-request send | Gains a trailing optional options parameter (§5.5). Source-compatible; binary-breaking. |
| All other members | **Unchanged signatures.** |

### 7.4 Behavioural contract summary

| Caller configuration | Send completes when | Body read by the library | Response disposed by |
|---|---|---|---|
| No options, or option left at default | The whole body is in memory | Fully, by the framework | Per §5.2 table |
| Streaming option, result-less member | Headers arrive | Not at all (success) / fully (error, into `Body`) | The library, after validation (§5.6) |
| Streaming option, materialising result type | Headers arrive | Fully, by the read branch | The library, inside the read branch |
| Streaming option, stream or raw-message result | Headers arrive | Not at all | **The caller** (§5.2.1) |

---

## 8. Quality Attributes, Trade-offs & Rejected Alternatives

### 8.1 Trade-offs made

| Trade-off | Call |
|---|---|
| Ownership of the connection moves to the caller on streaming paths, and a caller who forgets to dispose holds a pooled connection until finalisation. | **Accepted.** It is inherent to any passthrough; the alternative (a wrapper type) is rejected in §5.2.1. Mitigated by documentation, not by machinery. |
| Error bodies remain fully buffered, so "true passthrough" is true only for successful responses. | **Accepted and named.** #2279 requires it. |
| The narrowing in §5.1 changes behaviour for opt-in callers who hit the residual in §5.1.4. | **Accepted.** Only reachable under the opt-in; the alternative (keeping the guard mode-conditional) preserves a known-wrong line. |
| One binary-breaking signature change (§5.5). | **Accepted.** Preview-versioned internal package; source-compatible; the alternative leaves a hole in the feature. |

### 8.2 The rejected candidate — inferring the behaviour from the requested type

Task #8290 offers a second candidate: give the streaming-shaped result types (stream, raw response message) headers-only completion automatically. **Rejected for this phase, on two independent grounds:**

1. **It is not what was asked for.** The ask names "a httpoption".
2. **It silently changes behaviour for every existing caller of those two types** — including all 372 gateway actions — with no opt-in and no way to opt out. That is exactly the kind of unannounced change that produces an audit.

Recorded as a **candidate for a future phase, not a design element here**, and deliberately left as a list item that the implementer must not materialise. Should it ever be taken up, the sequencing that makes it safe is: this option ships first; consumers adopt it explicitly (#7961); only once the explicit path is proven in production does inference become a defensible default, and then as a major-version change with the audit #1217 asks for. **Nothing in this design pre-builds for it** — no hook, no flag, no seam. If it happens, it is a one-line default change at the helper, which is precisely the seam that already exists for free.

### 8.3 Quality attributes

- **Performance / memory.** The point of the change: 3.6–5.5× amplification removed on adopting call paths (#7959). No cost on non-adopting paths — the same send, one enum argument.
- **Maintainability.** Net surface added: one data member, one private helper. Net defects removed: one latent silent-null guard, nine leaked responses (the guard, eight result-less members) plus the redirect hop.
- **Compatibility.** Non-adopting callers: provably unchanged (S4). Adopting callers: opted in explicitly.
- **Testability.** The constructor already accepts a message handler, and master already carries a sequence-returning fake handler in the redirect tests. The whole matrix in §11 runs offline.

---

## 9. Risks & Mitigations

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| R1 | A1 is wrong and the enum's zero member is the streaming one — every caller silently flips. | Low | Severe | John verifies A1 before writing the helper. Test T-D1 asserts the default resolves to buffering by observing that the body **was** read. |
| R2 | A2 is wrong and the narrowing is not behaviour-neutral — an existing caller starts receiving a stream where it received null. | Low | High | T-G3 and T-G4 pin both halves of the equivalence, with an undeclared-length response under the default option. If T-G4 fails, fall back to making the guard conditional on the completion mode (§5.1.3) and re-open the design. |
| R3 | Adopting callers leak connections by not disposing. | Medium | Medium | Documentation on the option and on the stream branch; the adoption task #7961 gets this section referenced. Not solved in code (§5.2.1). |
| R4 | A caller sets the option and requests a decoded object, hitting the §5.1.4 residual. | Low (A6) | Low | Documented; T-G5/T-G6 make the behaviour deliberate. |
| R5 | The implementer edits 16 send sites and misses the redirect hop. | Medium **if hand-edited** | High — the exact defect class of #7959 | Eliminated structurally by E2; verified by the grep invariant in §6.2 and by test T-R2. |
| R6 | Disposal added in the result-less members disturbs the error path. | Low | Medium | Disposal is placed **after** validation, so the throwing path is byte-identical to today. T-M2 asserts `Body` survives on that path. |
| R7 | A slow or stalled downstream now holds a connection for the caller's whole read, instead of for the buffering window. | Medium | Medium | Inherent to passthrough and desired. The existing timeout still bounds the send; note in the option's documentation that it does **not** bound the caller's read. |

---

## 10. Rollout

No migration, no deprecation window, no feature flag (#1136 §5 — none of those belong in a package with a default-preserving opt-in).

1. Merge to `master`; publish `0.7.19-preview` (E7).
2. Report the published version on task #8290.
3. #7961 unblocks: the gateway passthrough actions set the option and adopt the disposal contract of §5.2.1.
4. Per the map-freshness discipline (#2928 §10), the map nodes touched by this change — the buffering/streaming concept node **#8313**, the response-dispatch concept node **#8312**, the extension concept node **#8314** (its "a new option" archetype explicitly says the completion behaviour is "currently unreachable from options at all"), and the file nodes **#8297** and **#8299** — are reconciled when this merges.

---

## 11. Coverage matrix — a test per branch condition

Per #2928 §7, the recurring first-pass miss is always *a branch gated by a specific input type or an alternate code path*. Every such branch touched by this change is enumerated below.

### 11.1 Test infrastructure (build first, reuse — do not duplicate)

| Fixture | Purpose |
|---|---|
| **Sequence handler** | Already exists on master, nested inside the redirect test fixture: returns a prepared queue of responses without touching the network and records the request URIs it saw. **Promote it to a shared test-support file and reuse it** — do not write a second one (DRY). It already stamps the originating request onto the response, which the redirect path depends on. |
| **Probe content** | New. An HTTP content whose **declared length can be present or absent**, and which **counts the bytes read from it**. Absent declared length is what a chunked response looks like to the client; the byte counter is how S2 and the completion behaviour are observed at all. Both knobs are essential — most rows below need one or the other. |

**Why this observes the right thing.** The completion behaviour is consumed by the client *outside* the message handler: with buffering, the client loads the content into a buffer before the send task completes; with headers-only completion it does not. So "was the option honoured" is directly observable as "how many bytes had been read from the probe content by the time the call returned". No network, no timing, no flakiness.

### 11.2 Option plumbing — every entry point (E1/E2)

| ID | Branch condition | Test | Assertion |
|---|---|---|---|
| T-P1 … T-P16 | Each of the 16 public request-issuing members, with the streaming option set | **One parameterised test over an invocation table**, one case per member (verb-with-body ×4, no-body post, typed no-body post, typed put/patch variants, get ×2, delete ×2, custom verb ×2, raw-request send ×2) | Zero bytes read from the probe content when the call returns |
| T-P17 | The redirect re-send (the seventeenth site, reached only through response handling) | Redirect + final response, follow-redirects on, streaming option | Zero bytes read from the **final** response's content |
| T-P18 | Invariant | Static check | The library contains exactly one call site naming a completion behaviour |

### 11.3 Default resolution (E1)

| ID | Branch condition | Assertion |
|---|---|---|
| T-D1 | Options is null | Body fully read before return (buffering) |
| T-D2 | Options instance created but the new member never assigned | Body fully read before return — pins A1 |
| T-D3 | Option explicitly set to the buffering member | Body fully read before return |

### 11.4 The length guard (E3) — every state of the condition

| ID | Declared length | Completion | Requested type | Assertion |
|---|---|---|---|---|
| T-G1 | Present, zero | Default | Domain type | Type default returned; no read attempted |
| T-G2 | Present, zero | Default | Domain type | **Response disposed** when the guard fires (§5.1.5) |
| T-G3 | **Absent**, body non-empty | **Streaming** | Stream | Non-null, readable stream carrying the full body — *the feature's core regression test* |
| T-G4 | **Absent**, body non-empty | Default | Domain type | Decoded correctly — pins A2, i.e. that the length is computable after buffering and the narrowing is a no-op for existing callers |
| T-G5 | Absent, body empty | Streaming | String | Empty string, not null (documented §5.1.4 residual) |
| T-G6 | Absent, body empty | Streaming | Domain type, no content type | The documented failure, not a silent null |
| T-G7 | Present, non-zero | Default | Domain type | Unchanged |

### 11.5 Typed read branches (E4 disposal contract) — every branch of §5.2

| ID | Branch | Completion | Assertion |
|---|---|---|---|
| T-B1 | Raw response message | Streaming | Returned undisposed, content unread, **status validation skipped** (existing exemption) |
| T-B2 | Stream | Streaming | Stream readable **after** the call returns — the truncation/disposed-object test |
| T-B3 | Stream | Default | Unchanged behaviour |
| T-B4 | String | Streaming | Correct value; response disposed afterwards |
| T-B5 | Byte array | Streaming | Correct value; response disposed afterwards |
| T-B6 | JSON media type, default decoder | Both | Decodes correctly |
| T-B7 | JSON media type, decoder supplied through options | Both | The supplied decoder is used |
| T-B8 | JSON media type, decoder throws | Default | Surfaced as the library's exception (unchanged) |
| T-B9 | Application XML media type | Both | Document returned |
| T-B10 | Text XML media type | Both | Document returned — the second, easily-missed media-type case |
| T-B11 | Plain-text media type | Both | String returned |
| T-B12 | Unrecognised media type | Both | Falls through to the stream fallback (unchanged) |
| T-B13 | **Absent** content type | Both | Falls through to the stream fallback (unchanged) |

### 11.6 Status validation and the error body (#2279)

| ID | Branch condition | Assertion |
|---|---|---|
| T-C1 | Status in the accepted band, streaming option | **Zero bytes read** by validation — proves no re-buffering of the success path |
| T-C2 | Status outside the band, body present, **streaming** option | Exception's `Body` carries the text — the #2279 regression test under the new mode |
| T-C3 | Status outside the band, body present, default option | Exception's `Body` carries the text — the #2279 regression test unchanged |
| T-C4 | Status outside the band, body empty | `Body` is null; the no-body message form is used |
| T-C5 | A 3xx status with redirect-following **off** | Accepted as success (existing band behaviour), both modes |

### 11.7 Redirect (§5.4)

| ID | Branch condition | Assertion |
|---|---|---|
| T-R1 | Follow-redirects on, a 302 | Two requests issued; the existing URL-resolution tests on master still pass unmodified |
| T-R2 | Follow-redirects on, a 302, streaming option | The **second** response also honours the option (zero bytes read) |
| T-R3 | Follow-redirects on, a 302, streaming option | The **superseded** response is disposed |
| T-R4 | Follow-redirects on, a 307 | Still throws the not-supported error; no second request issued |
| T-R5 | Follow-redirects **off**, a 302 | No second request; the 3xx flows to validation |
| T-R6 | Follow-redirects on, a non-redirect status | No second request |

### 11.8 Result-less members (§5.6) and the interface change (§5.5)

| ID | Branch condition | Assertion |
|---|---|---|
| T-M1 | Each result-less member, success | Response disposed after the call |
| T-M2 | Each validating result-less member, failure | Throws with `Body` populated; **the error path is unchanged** |
| T-M3 | The result-less no-body post member | Still performs **no** status validation (pins the out-of-scope decision so nobody "fixes" it accidentally) |
| T-M4 | The result-less raw-request send, called with **one** argument | Compiles and behaves as before — source compatibility |
| T-M5 | The result-less raw-request send, called **with** options carrying the streaming value | Option honoured (zero bytes read) |

---

## 12. Pre-Design Checklist (#1136 §5), answered in order

**KISS / DRY / YAGNI**

1. *No new type mirroring an existing type.* ✔ No new type at all. A boolean flag was considered and rejected precisely because it would mirror the framework enum (§7.1).
2. *No new abstraction with one implementation and no plan for a second.* ✔ No interface, no wrapper, no carrier type. The stream/response wrapper was considered and rejected (§5.2.1).
3. *No element justified by "we might need X later".* ✔ Type-inference is recorded as a rejected candidate with **no pre-built seam** (§8.2).
4. *No deprecation period, feature flag, compatibility shim, or transition window.* ✔ None — the default preserves current behaviour, which is the whole compatibility story (§10).
5. *For every inline-vs-extract decision, `block_size × site_count` quoted.* ✔ `1 × 17 = 17` lines of duplicated policy, at the threshold, with three named reasons that carry it over (§6.2). Decision: extract.

**Existing systems first**

6. *Audited whether an existing surface already covers the concern.* ✔ It does: the options bag is the established carrier for per-request behaviour, and #8314 names "a new option" as the correct archetype for something that must affect sending. The option goes there; nothing new is created.
7. *If a new layer is proposed, the concrete reason it cannot live on the existing surface.* ✔ No new layer. The one new private helper is a DRY extraction over existing call sites, not a layer.
8. *If new persisted data is proposed, the decision it enables in 4 weeks.* ✔ n/a — nothing is persisted.
9. *Every field justified by "the existing reader projects it" has its consumer chain recursed.* ✔ n/a — no field is added on that basis. The one field added has a named consumer: #7961, 372 endpoints.

**Configurability**

10. *Every new knob has a named operator or environment difference.* ✔ Not an operator knob — a **per-call API affordance** with a named consumer (#7961). It is not read from configuration and has no environment dimension.
11. *Every "telemetry-then-tune" knob paired with a filed tuning task.* ✔ n/a — not a tuning knob; there is no value to tune, only two behaviours to choose between.
12. *Magic numbers that need not vary stay as constants.* ✔ No numbers introduced.

**Less is better**

13. *Can-it-be-deleted / merged / inlined run on every element.* ✔ Run on all seven elements of §2.1. E3–E5 each survived a deletion test by naming what breaks without them (a silent null; a leaked connection; a member that cannot participate). The wrapper type and the nullable field did **not** survive and were deleted.
14. *Trade-offs named explicitly where a complex design wins.* ✔ §8.1, §8.2, §5.1.3, §5.2.1, §5.5.
15. *When the existing surface has no consumer, pick the radical-clean shape.* ✔ Applied to the length guard: rather than a mode-conditional compromise between "keep it" and "remove it", the guard is **narrowed to the predicate it should always have been** (§5.1.3).
16. *Reader inventories cover AST and string-literal references.* ✔ n/a — no rename. The inventory that matters here is the 17 send sites, enumerated and made structurally verifiable (§6.2).
17. *Carrier-swap tables enumerate every affected member.* ✔ §11.2 enumerates all 16 public members plus the redirect hop; §5.2 enumerates all 9 read branches; §5.6 enumerates all 8 result-less members.

**Data deliverables** — n/a (items 18–20): no SQL, no schema, no migration.

**Document discipline**

21. *Cites Code Contracts (#114) and Design Contracts (#1136) as load-bearing.* ✔ Header block.
22. *Reader / scope inventories explicit, not implicit.* ✔ §5.2, §5.6, §6.2, §11.
23. *Out-of-scope items listed explicitly.* ✔ §2.2, twelve rows.
24. *No multi-paragraph rationale for things that obviously stay.* ✔ Branches that need no change are single table rows.
25. *Superseded predecessor designs banner-marked.* ✔ n/a — this is the first design document in this repository.

---

## 13. Open Questions

| # | Question | Blocking? | Default if unanswered |
|---|---|---|---|
| Q1 | Should the shared test-support handler be extracted from the redirect fixture into its own file, or left nested and duplicated? | No | **Extract** — the design assumes reuse (§11.1). |
| Q2 | Is `0.7.19-preview` the right next version, or does the operator want a different bump given the binary-breaking signature change in §5.5? | No | `0.7.19-preview`. |
| Q3 | Does any consumer outside mamgo implement `IHttpService` itself? If so, §5.5 breaks it at compile time. | No | Assume not (A7); the operator can veto §5.5 and accept the hole. |

---

## 14. Implementation Guidance — ordered build phases

No code appears in this document by design; each phase is an architectural unit with its own verification gate.

**Phase 0 — verify the one load-bearing assumption.** Confirm A1 (the completion enum's zero-valued member is the buffering one) and A3 (the enum and the two-argument send exist on both target frameworks). If A1 is false, **stop and bounce** — the "zero blast radius" claim collapses and the option must become nullable.

**Phase 1 — test infrastructure.** Promote the existing sequence handler to shared test support and build the probe content with its two knobs (declared-length present/absent, byte counter). Prove the harness by reproducing today's behaviour: an undeclared-length response under the default option currently decodes fine (T-G4). Nothing in the library changes yet.

**Phase 2 — the option and the helper (E1 + E2).** Add the member to the options bag with its documentation obligations (§7.1). Add the private send helper. Reroute **all 17** sites through it, including the redirect hop. Verify with §11.2 and §11.3, and with the grep invariant: exactly one occurrence of a completion behaviour being named in the library.

**Phase 3 — the length guard (E3).** Narrow the condition; dispose when it fires. Verify with §11.4. **T-G4 is the gate** — if it fails, A2 is wrong and Phase 3 must be re-designed before proceeding.

**Phase 4 — disposal contracts (E4).** Result-less members dispose after validation; the redirect hop disposes the superseded response. Leave every other branch's disposal exactly as it is. Verify with §11.7 and §11.8.

**Phase 5 — the interface change (E5).** Add the optional options parameter to the result-less raw-request send, on the interface and the implementation. Verify with T-M4 and T-M5.

**Phase 6 — remaining coverage and release.** Complete §11.5 and §11.6. Bump the package and assembly version (E7). Confirm the three redirect tests already on master pass **unmodified** — if any needed editing, Phase 4 changed redirect semantics and must be revisited.

**Standing constraints for the implementer.** Do not fix anything in §2.2. Do not add guards for the §5.1.4 residual. Do not add a status check to the result-less no-body post member. Do not alter the redirect URL resolution. If any phase appears to require a KISS/DRY/YAGNI violation, bounce per Code Contracts #114 §0 rather than rationalising it.
