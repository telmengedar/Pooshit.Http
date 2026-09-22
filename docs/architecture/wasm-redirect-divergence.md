# Architectural Document: the WebAssembly redirect divergence

> **Repo path:** `docs/architecture/wasm-redirect-divergence.md` (repository `telmengedar/Pooshit.Http`).
> **Node:** **#14627** — this document's own DiVoid node, maintained as a byte copy of this file.
> **DiVoid:** source task **#14620** · request lifecycle **#8311** stage 1 (where the divergence was first recorded) · project **#2281** · repo map root **#8292** · `HttpService` **#8297**.
> **Predecessors, none superseded, all four relied on for what the WASM path does *not* get:** **#9633** (the cross-origin credential strip) · **#14516** (verb-and-body preservation, the loud replay failure) · **#14574** (#8316, the band) · **#14617** (#8323, the hop cap — and the measurement that made this sharp).
> **Contracts cited as load-bearing:** Design Contracts **#1136** (§5 walked as §9) · Code Contracts **#114** §0 and §4 (the sanctioned `//` comment) · falsifiable universals **#9951** and the discriminator rule **#14516** §D7.2.
> **Baseline:** branch `fix/wasm-redirect-divergence` off `origin/master` @ `e694474`, tree clean. Version on master: `0.15.0-preview`.

---

## TL;DR

**The stated constraint is obsolete. The branch it justifies is still correct — for a different reason. That combination is the finding, and it is a trap.**

**Measured:** `AllowAutoRedirect` does **not** throw on WebAssembly and has not on any supported runtime. In the real `browser-wasm` `System.Net.Http.dll`, `BrowserHttpHandler.AllowAutoRedirect` has a real implementation on **.NET 6, 7 and 8**, while its eleven genuinely-unsupported siblings are six-byte `throw new PlatformNotSupportedException()`. So `// wasm crashes with allowautoredirect` is false, and has been for the whole supported lifetime of the platform.

**So delete the branch?** That is what the measurement alone says, and it would ship a regression. Setting `AllowAutoRedirect = false` in a browser does not hand the library a 3xx to follow — per the Fetch standard it yields an **opaque-redirect** response: status `0`, no headers, no `Location`. That status matches no arm of the redirect branch, falls through to the band, and **raises** (#14574). Every redirect on WASM would go from *transparently followed* to *an exception carrying status 0 and no target.*

**Decision: keep the branch, correct the comment, and write the contract down.** The code does not change. What changes is that the tripwire stops pointing at a claim that is false and starts pointing at the reason that is true — because the current comment invites exactly the deletion this document talked itself out of.

**The safety gap is real and is now named rather than mentioned:** on WASM none of `SensitiveHeaders` stripping, verb-and-body preservation, the loud replay failure or the hop cap exists, because that path never reaches the redirect branch. The caller gets the user agent's rules instead — **which are different, and which I did not measure** (§4).

**Coverage: this platform cannot be guarded from this repo's test project, and that is a finding** (§7), of the same class as the `netstandard2.0` target that nothing built until PR #19 — except worse, because the workload needed to build it is not installed and a browser host would be needed to run it.

---

## 1. Problem

#14620, and the sentence that makes it more than a platform note:

> Everything this library has built on top of the redirect hop **does not exist on the WASM path**, because that path never reaches `HandleResponse`'s redirect branch.

The constructor:

```
if (handler != null)                     client = new(handler);
else if (not BROWSER) {
    // wasm crashes with allowautoredirect
    client = new(new HttpClientHandler { AllowAutoRedirect = false });
}
else                                     client = new();
```

On every non-browser runtime redirect policy belongs to this library. On WebAssembly it belongs to the user agent. Identical caller code, two policies, silently — recorded on **#8311 stage 1** as *"the first place behaviour silently diverges"*, then named without being owned by **#14574 §5.4** and **#14617 §5.4**.

---

## 2. What was measured, and what was not

The brief asked for this split explicitly. It is the most important section here.

### 2.1 Measured — the constraint is gone

The claim *"the setting crashes there"* is a claim with a date on it. `git log -S` puts the comment in the tree at least since **`f83c78c`, 2024-10-13** (a namespace refactor that moved it, so it is older still).

**I could not run a browser here** — `dotnet workload list` shows no installed workloads, so `wasm-tools` is absent and no `net8.0-browser` target can be built or hosted. **That does not make the question unmeasurable**, and the route round it is the point:

1. **The platform annotations.** In .NET 8's `System.Net.Http`, eleven `HttpClientHandler` properties carry `[UnsupportedOSPlatform("browser")]` — including `MaxAutomaticRedirections`. **`AllowAutoRedirect` carries none.**
2. **The actual browser assembly.** I fetched `Microsoft.NETCore.App.Runtime.Mono.browser-wasm` from NuGet and decoded the IL of `BrowserHttpHandler`'s property setters with `System.Reflection.Metadata`:

| Property | setter body in `browser-wasm` |
|---|---|
| **`AllowAutoRedirect`** | **15 bytes, real implementation** |
| `MaxAutomaticRedirections` | 6 bytes — `throw new PlatformNotSupportedException()` |
| `Credentials`, `CookieContainer`, `UseCookies`, `Proxy`, `UseProxy`, `DefaultProxyCredentials`, `PreAuthenticate`, `AutomaticDecompression`, `MaxConnectionsPerServer`, `MaxResponseHeadersLength`, `SslOptions` | 6 bytes — the same throw |

**The eleven throwing properties are exactly the eleven annotated ones. That is the control, and it is what makes the result trustworthy** — a method that reported "no throw" for everything would have proved nothing. It also caught an earlier error: measured one layer up, on `HttpClientHandler` rather than `BrowserHttpHandler`, *every* setter looked like a real implementation, because they forward to the underlying handler. The control failed there, which is how I knew to go deeper.

3. **Across versions.** Identical result on **6.0.36, 7.0.20 and 8.0.12** — `AllowAutoRedirect` real, `MaxAutomaticRedirections` throwing, in all three. The constraint is not merely fixed; it is absent from every supported runtime.
4. **The value is plumbed.** The browser assembly carries the managed string constants `"manual"`, `"follow"` and `"redirect"` — the Fetch API's redirect modes — in all versions checked. So the setting reaches `fetch`.

### 2.2 Inferred, not measured — what the browser then does

**Per the Fetch standard**, a request with `redirect: "manual"` yields an **opaque-redirect filtered response**: type `"opaqueredirect"`, **status `0`**, an empty header list, and no body. The user agent does not expose `Location` to script.

**I did not measure this and I cannot from here.** It is read from the specification, and it is the load-bearing inference of this document — §3 turns on it. §8 states what would falsify it and §10.2 records the measurement someone with a Blazor host should make.

**Also inferred:** what a browser does with credentials across a redirect. CORS governs it — cookies ride only under `credentials: "include"`, custom headers trigger preflight, and the redirect target must itself pass CORS. The browser's rules are therefore **different** from both the library's and desktop .NET's, plausibly stricter in places, and **I have measured none of them**. §4 says so rather than assuming the divergence runs in either direction.

### 2.3 Measured previously, and carried here

From #14617: on desktop, `AllowAutoRedirect`'s own follower **strips `Authorization` and nothing else** — a vendor key, a session cookie and any caller-registered `SensitiveHeaders` name all ride to the foreign origin. That measurement is desktop-only and **must not be assumed to describe a browser** (§4).

---

## 3. The trap, stated plainly

The measurement in §2.1 says the branch is obsolete. Acting on it ships a regression.

| | today (WASM) | with the branch deleted |
|---|---|---|
| a redirect | followed transparently by the user agent | `AllowAutoRedirect = false` → fetch returns an **opaque redirect**, status `0` |
| what `HandleResponse` sees | nothing — no 3xx ever arrives | status `0`, which matches **no** arm of the redirect branch |
| what the caller gets | the final response | `HttpServiceException` — `0` is outside `200–299`, so the band raises (#14574) |
| can the library follow it? | n/a | **no** — there is no `Location` to read |

**So the branch is right and its stated reason is wrong.** The code has been correct by accident since the runtime changed underneath it.

**This is the shape worth recording beyond this repo.** A comment that cites a *cause* rather than a *consequence* becomes dangerous the moment the cause expires: it reads as a live constraint, it invites verification, and verification returns "false — remove it". A reader who trusts the comment leaves correct code alone for a wrong reason; a reader who checks it removes correct code for a right one. **The second reader is the more careful one, which is what makes this worse than an ordinary stale comment.** I was that reader, and §3's table is what stopped me.

---

## 4. The safety divergence, owned rather than mentioned

On WASM the library's redirect path never runs, so none of this exists there:

| Property | Where it came from | On WASM |
|---|---|---|
| cross-origin strip of the whole public `SensitiveHeaders` set | #9633 | **absent** |
| verb and body preserved for 307/308 | #14516 | **absent** |
| loud, typed failure when a body cannot be replayed | #14516 D3 | **absent** |
| the hop cap and its diagnosis | #14617 | **absent** (once it lands) |
| `UrlProcessor` running on the hop | #14601 | **absent** — no hop to run on |

**What replaces them is the user agent's policy, and this document does not claim to know it.** §2.3's "strips `Authorization` only" is a **desktop** measurement; a browser applies CORS, which is a different mechanism with different — quite possibly better — results for cross-origin credentials. **Writing "WASM leaks your vendor key" would be the same over-claim this session has paid for repeatedly**, so the contract says what is true: *on WASM the library makes no redirect guarantees, and the user agent's rules apply.*

That is a weaker statement than #14620 anticipated, and it is the honest one.

---

## 5. Decisions

### D1 — keep the branch; change no behaviour

§3. The alternative ships a regression on the one platform it aims to improve. Nothing in `HttpService.cs` moves except a comment.

### D2 — correct the comment to cite the consequence, not the expired cause

The comment is #114 §4's sanctioned kind — a cited platform quirk outside this code — and #14620 is right that it is the tripwire that should survive. **A tripwire pointing at a false claim is worse than none**, because it fails in the direction of a confident deletion.

It should say, in substance: *the browser's redirect policy is the user agent's; turning it off here yields an opaque redirect with no `Location` and no status, which this library cannot follow.* It should **not** say "crashes" — that is measurably false and invites the check that leads to the wrong edit.

### D3 — the divergence is documented in the design corpus, not in a new API

Rejected: **refusing to construct without an explicit handler on WASM.** It is a breaking change on a platform whose callers currently work, to protect them from a policy difference whose actual shape §4 says we have not measured. #1136 §1 — that is a guard built for a hazard nobody has characterised.

Rejected: **an option to choose the policy.** #1136 §3 — no named operator, and the option could not be honoured, since the library cannot follow an opaque redirect however the caller sets it.

**No public surface changes.**

### D4 — what a WASM caller does instead, stated because a cap needs a contract

- **Redirects are followed** — by the user agent, transparently, as today.
- **`HttpOptions.FollowRedirects` has no effect** on that platform. Nothing 3xx reaches the library.
- A caller who needs the library's policy on WASM **cannot have it**, and the reason is the opaque-redirect rule, not a missing feature. That is a platform limit, not a backlog item.

---

## 6. Scope

**In:** the comment, this document, and the map reconcile.

**Out:** `HttpService.cs` behaviour; the constructor's shape; any new option; #14547 and #14613; and the browser-side credential measurement (§10.2), which needs a host this repo does not have.

---

## 7. Coverage — and the finding is that there is none

**This platform cannot be guarded from this repo's test project.** Stated as a finding because the brief asked for it and because it is the same class as the `netstandard2.0` target nothing built until PR #19 — **except worse in two ways**:

1. `netstandard2.0` needed only a `dotnet build`; PR #19 added one step. **`browser-wasm` needs the `wasm-tools` workload** — `dotnet workload list` shows none installed here — **plus a browser host to execute in.** That is a CI job, not a step.
2. The `netstandard2.0` gap was a *compile* gap; a build caught it. This is a *runtime-behaviour* gap. Compiling for browser would prove nothing, because the thing in question is what `fetch` does.

**What can be guarded, cheaply, and what it is worth:**

| # | Guard | What it pins | Honest limit |
|---|---|---|---|
| 1 | `Constructor_NonBrowserPlatform_DisablesAutomaticRedirects` | that the non-browser arm still builds a handler with `AllowAutoRedirect = false` | runs only on the arm it is already on; it cannot fail for a browser reason |
| 2 | `Constructor_WasmBranch_IsStillPresentInSource` | a source-reading guard, like `EveryStatusCheckCallSiteNamesTheOptions`, asserting the platform branch has not been deleted | pins the *shape*, not the behaviour — but it is the one guard that would have failed if I had acted on §2.1 |

**Guard 2 is the only one that addresses this document's actual risk**, and it is a structural assertion rather than a behavioural one. **I am not proposing it as a substitute for a browser test**, and saying it covers the divergence would be the coverage over-claim #14528 was about. The real guard does not exist and this design does not add it.

---

## 8. Falsifiable claims (#9951 + #14516 §D7.2)

| Claim | Where | What would falsify it | Does that class exist? |
|---|---|---|---|
| "`AllowAutoRedirect` does not throw on browser" | §2.1 | a supported runtime whose `BrowserHttpHandler` setter throws | **No, across 6/7/8** — measured in the shipped assembly, with eleven throwing siblings as a control. **Falsifier still live:** a runtime **older than 6.0**, which is where the comment presumably came from, and which this library's `netstandard2.0` target can still be consumed on. §10.3. |
| **discriminator:** "the eleven throwing setters are the unsupported ones" | §2.1 | a property that throws without the annotation, or carries it without throwing | **No** — the two sets matched exactly. This is what licenses reading absence-of-throw as support; without the match, "no PNSE found" would only have meant my scanner missed it. |
| "an opaque redirect cannot be followed by this library" | §3, D4 | a browser exposing `Location` on a `redirect: "manual"` response | **Not under the Fetch standard** — the filtered response has an empty header list by definition. **But this is inferred, not measured** (§2.2), and it is the single load-bearing inference here. If a browser exposed it, D1 would need re-deciding. |
| "deleting the branch regresses WASM callers" | §3 | a status-`0` response that the redirect branch or the band handles gracefully | **No** — `0` matches no redirect arm and is outside `200–299`, so it raises. Derived from code that is in the tree, not inferred. |
| "the library's guarantees are absent on WASM" | §4 | a guarantee that survives without the redirect branch running | **No** — every item in §4's table is implemented inside `FollowRedirect`/`SendRedirect`, which that platform never enters. |
| ~~"WASM leaks credentials cross-origin"~~ | — | — | **Not claimed.** §2.3's measurement is desktop-only; a browser applies CORS. Recorded as a claim deliberately *not* made. |

### 8.1 The claim I expect a reviewer to attack first

**That the opaque-redirect inference is strong enough to decide D1 on.** It comes from a specification, not a run. Break it by running a Blazor WASM app against a redirecting endpoint with `AllowAutoRedirect = false` and printing `(int)response.StatusCode` — one page, ten minutes, and it settles §10.2 outright. Until someone does, **D1 rests on a standard rather than an observation, and I would rather say so than let the measured half of §2 lend its credibility to the unmeasured half.**

---

## 9. Pre-Design Checklist (#1136 §5)

| Item | Verdict |
|---|---|
| No new type mirroring an existing one | **Pass** — nothing added |
| No new abstraction with one implementation | **Pass** |
| Nothing justified by "we might need X later" | **Pass** — D3 rejects both the refusal and the option |
| No deprecation window / compat shim / feature flag | **Pass** — no behaviour changes at all |
| DRY math on inline-vs-extract | **N/A** |
| Existing systems first | **Pass** — the finding is that the *existing* branch is already correct; the work is to stop misdescribing it |
| Every config knob has a named operator | **Pass by removal** — D3 rejects the knob, and notes it could not be honoured even if wanted |
| Can-it-be-deleted / merged / inlined | **Pass, and it is the whole document** — §2 ran the delete-test on the branch and §3 is why it fails |
| Trade-offs named explicitly | **Pass** — §3 (the trap), §4 (what we do not know about the browser), §7 (no real guard), §8.1 (an inference carrying a decision) |
| Out-of-scope listed explicitly | **Pass** — §6 |
| No multi-paragraph rationale for things that obviously stay | **Pass** |
| Predecessor design banner where superseded | **N/A** — four designs relied on for what WASM does *not* get |
| Coverage rows name the test identifier | **Pass** — §7, two named guards, with their limits stated rather than implied |

---

## 10. Open questions

### 10.1 Should this be built now? Yes — and it is almost entirely a comment

The behaviour change is nil. What ships is a corrected tripwire and a written contract, and the argument for shipping it *now* rather than filing it is that **the current comment actively misleads the careful reader.** It survived four designs because each author read it, believed it, and scoped out. The fifth reader checked it, found it false, and was one step from deleting correct code. That is the cost of leaving it, and it is not hypothetical.

**Against building:** it touches a file held by the multi-hop branch in QA, so it queues behind it.

### 10.2 The measurement I could not make

A Blazor WASM page: set `AllowAutoRedirect = false`, request a redirecting endpoint, print the status and whether `Location` is readable. **Confirms or breaks §2.2**, which is the inference D1 rests on. Also worth capturing in the same run: whether a cross-origin redirect carries a custom header and a cookie, which would replace §4's honest "unmeasured" with a fact.

**Whoever has a Blazor app has this in ten minutes; this repo will never have it.**

### 10.3 Should the branch be version-gated rather than platform-gated?

§8's live falsifier: the constraint may genuinely have existed before .NET 6, and this package's `netstandard2.0` target can be consumed on older runtimes. The branch tests the *platform*, not the runtime version, so it is correct for both eras — which is a point in favour of leaving it exactly as it is, and an argument against anyone "simplifying" it later.

### 10.4 Filing against a runtime upgrade

**No.** A runtime upgrade is what made the comment false; it is not what will fix the divergence. The divergence is a property of `fetch`, not of .NET, and it will outlive every runtime version this library targets.

---

## 11. Implementation order

1. **Correct the comment** (D2) — cite the opaque-redirect consequence, drop "crashes".
2. **Guard 2** from §7, if it is wanted: a source-reading test asserting the platform branch still exists. Small, and it is the guard that would have caught the edit this document rejected.
3. **Reconcile the map** — #8311 stage 1 carries *"the setting crashes there"* and has since the map was built, so it is stale in the same way the comment is; #8297's constructor description likewise. Concept nodes count (#3414).
4. **Close #14620** with the contract, and file §10.2 as its own task for whoever has a browser host.
