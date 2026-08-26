# Architectural Document: credential headers on the redirect hop

> **Repo path:** `docs/architecture/redirect-credential-policy.md` (repository `telmengedar/Pooshit.Http`).
> **DiVoid:** source task **#9619** · project **#2281** · repo map root **#8292** · `HttpService` **#8297** · `IHttpService` **#8298** · `HttpOptions` **#8299** · request lifecycle **#8311** · `HeaderDumpMode` **#9617**.
> **Predecessor:** design **#9618** (`docs/architecture/send-options-contract.md`, PR #6). Not superseded — this document closes the limit #9618 §7 named and accepted.
> **Contracts cited as load-bearing:** Design Contracts **#1136** (§1 KISS/DRY/YAGNI, §3 configurability is not free, §4 less is better, §5 checklist, §6 anti-patterns) · Code Contracts **#114** §0.
> **Baseline:** `master` @ `c8ba37a`, tree clean. Package version on master: `0.9.1-preview`.

---

## 1. Problem

#9619, filed by QA during the design of PR #6:

> `HttpService.HandleResponse` follows a 301/302/303 to whatever `Location` names, including a **different host**, and sends the `Authorization` header along. `Location` is attacker-controlled from the library's point of view — it is a value the *remote server* chose. A compromised or merely sloppy upstream can therefore harvest the caller's bearer token by answering `302 Location: https://attacker.example/`.

The question this document answers: **does the hop strip credential-bearing headers when it leaves the origin, and what exactly are "credential-bearing" and "the origin"?**

### 1.1 Re-measured against `c8ba37a` — #9619's table is stale on one row

#9619 was written before PR #6 merged. Its `Send` row — *"hop 1 today carries only the options bag's headers, so a caller who passes no options headers currently forwards nothing"* — **is no longer the current state.** Measured now:

`HandleResponse` (`HttpService.cs:263-277`) calls `CreateRedirectRequest(url, previousResponse.RequestMessage)` (`:87-96`), which copies **every** header from the previous request except `Expect` and `Transfer-Encoding` (`redirectExcludedHeaders`, `:19-22`). There is no longer a per-entry-point difference:

| Call path | Where hop 0's credential comes from | On the cross-origin hop today |
|---|---|---|
| URL overloads (`Get`/`Post`/…) | `options.TokenProvider` → `CreateRequest`, `:121` | **forwarded** |
| `Send` / `Send<T>` | the caller's own pre-built request | **forwarded** |

Both families now leak. The hazard is uniform, which is convenient: one rule fixes both.

### 1.2 Does #9618 §7's argument still hold?

§7 argued that dropping the caller's credentials on the hop *"makes the redirect follower useless rather than safe"*. **The argument still holds exactly as written, and it does not reach this decision.** It was written against #9609's option 3 — send hop 1 **bare, always** — and against that it is correct: a follower that never authenticates produces a `401` one hop later and is a broken feature.

What changed is not the argument but the option set. #9618 had a binary in front of it (keep on every hop / drop on every hop) because the third shape was explicitly deferred to #9619. That third shape — **keep on same-origin, drop on cross-origin** — leaves the follower fully useful for the case it is actually used for (a relative `Location` back to the same service) and removes credentials only where the destination was chosen by the remote server. §7's argument is not an argument against it.

So: nothing shipped in PR #6 invalidated §7. The reasoning stands; it simply never covered this case.

---

## 2. Decision

**D1 — the hop drops credential-bearing headers when the resolved target is not same-origin with the request that produced the redirect.** #9619 option 1; the browser and `curl -L` behaviour.

Everything else about the hop is unchanged: non-credential headers still ride, body descriptors are still excluded, `UrlProcessor` still runs first, URI combining, disposal and the completion option are untouched.

### 2.1 "Cross-origin" means all three of scheme, host, port

RFC 6454 origin. Two URLs are same-origin when scheme, host and port all match; anything else strips. Concretely:

| Hop | Same origin? | Credentials |
|---|---|---|
| `https://api.example/a` → `/b` (relative) | yes | kept |
| `https://api.example/a` → `https://api.example/b` | yes | kept |
| `https://api.example/a` → `https://cdn.example/b` | no — host | stripped |
| `https://api.example/a` → `https://api.example:8443/b` | no — port | stripped |
| `http://api.example/a` → `https://api.example/a` | no — scheme | stripped |
| target not an absolute URI, or previous request URI unknown | **cannot be proven** | stripped |

Three notes, each a decision rather than an accident:

- **Subdomains are different origins.** `api.example.com` → `cdn.example.com` strips. The alternative — a registrable-domain comparison — needs a public-suffix list, which is a dependency and a permanent maintenance surface for an unattested case (#1136 §1 YAGNI).
- **The scheme row is deliberate and is not a loss.** A credential that travelled to `http://` was already sent in the clear; it is compromised before the redirect. Stripping on the upgrade hop protects the *next* hop and costs a caller nothing they had not already lost. This is also what browsers and `curl` do.
- **Unprovable is treated as cross-origin.** The default port for a known scheme normalises on the framework's `Uri` type, so no special-casing is needed there; but a relative resolved target or a response with no stamped request URI leaves nothing to compare, and the safe direction is to strip. There is no fallback branch — the predicate is *"provably same origin"*, and everything else falls out of it. (#1136 §6, no defensive branches for cases the invariant already covers.)

**The comparison is against the final URL** — after `UrlProcessor` and after URI combining, i.e. the string the hop actually sends. That is load-bearing; see §3.

### 2.2 The credential-header list is the existing `SensitiveHeaders` set — shared, not duplicated

`HttpService.SensitiveHeaders` (`:58-67`) already exists for error-message redaction (#9617, PR #5): `Authorization`, `Proxy-Authorization`, `Cookie`, `Set-Cookie`, `Api-Key`, `X-Api-Key`, `X-Auth-Token`, `X-Access-Token`. It is a public, mutable, case-insensitive set.

The genuine question is whether *"do not print this"* and *"do not send this off-origin"* are the same predicate. **They are not identical, and the set is still shared.** The reasoning, in order:

1. **They are not the same predicate.** Two concrete divergences. `Set-Cookie` is a *response* header and cannot appear on a request's header collection at all — on the strip path it is inert. `Proxy-Authorization` is scoped to the *proxy*, not the origin server, and the proxy does not change when the redirect target does; stripping it is semantically wrong.
2. **Both divergences are inert in practice.** `Set-Cookie` simply never matches, which costs nothing and needs no guard. A hand-set `Proxy-Authorization` request header is unattested in this library and its consumers; .NET's own proxy credentials are attached by the handler per connection and never come from `HttpRequestMessage.Headers`, so the transport path is unaffected either way.
3. **The asymmetry of forgetting decides it.** The set is public precisely so a caller can add their own credential name — `X-Tenant-Secret`, say. With two lists, a caller who adds a name to the redaction set has told the library that header is a secret and will *still* have it forwarded to an attacker-named host. Forgetting the redaction entry costs a leaked log line; forgetting the strip entry costs a leaked credential. The list callers already know about must be the one that governs the sharper consequence.
4. **#8299 already positioned this set on the right axis.** Its PR #5 note records why the *mode* is per-call and the *name set* is not: *"mode is diagnostic verbosity, which varies per call; the name set is security policy, configured once at composition."* Stripping is security policy configured once at composition. Same axis, same lifetime, same list — this is DRY landing on an existing surface rather than a new one (#1136 §1, §2).

**No new type, no new option, no new set.** The property keeps its name — `SensitiveHeaders` is already the right name for *credential-bearing header names* — and its XML doc is rewritten to state both consequences: values are redacted in error dumps, **and** the headers are not carried onto a hop that leaves the origin.

**Accepted limit:** `Proxy-Authorization` is stripped cross-origin although the proxy has not changed. Named here rather than special-cased, per #1136 §6 — a third exclusion rule for an unattested case is the guard-for-impossible-scenarios anti-pattern.

### 2.3 No policy knob, and no new escape hatch

#9619 option 2 (a forward-always / same-origin / never enum on `HttpOptions`) is **rejected.** #1136 §3 requires a named operator or an environment difference before a knob ships, and there is no candidate caller with a cross-host authenticated redirect. Three further reasons:

- **The most common cross-host redirect in practice prefers stripping.** A storage or CDN handoff answers with a *pre-signed* URL that carries its own authorisation in the query string. Sending `Authorization` as well is not merely unnecessary there — the major object stores reject a request that presents two auth mechanisms. On that shape stripping makes the hop *work* where forwarding makes it fail.
- **Two mechanisms already cover the attested shapes, and both already ship.** `UrlProcessor` runs before origin resolution (§3), so a caller who knows a redirect really returns to their own service can rewrite the target to their own host and keep the credential — which requires them to *name the host they trust* rather than blanket-trusting whatever `Location` says. That is a strictly better shape than the enum. For a genuine foreign-host authenticated redirect, `FollowRedirects = false` plus `Get<HttpResponseMessage>` returns the raw 302 (status validation is skipped for that type) and the caller issues the second call themselves.
- **If a real caller with that need appears, the knob comes back with their shape in hand** (#1136 §3). Predicting it now produces the wrong shape.

**The break is accepted and is the correct behaviour.** A same-service redirect that changes hostname loses its credential and returns `401`. That is the cost, it is real, and it is the direction every mature HTTP client chose.

---

## 3. Where the rule lives

`CreateRedirectRequest` (`HttpService.cs:87`) already owns the question *"which headers must not travel on this hop"* — it holds the body-descriptor exclusion. The credential rule is the second answer to the same question and belongs in the same place. Two exclusion rules, one method, one responsibility.

It needs three things it does not have: the resolved target URL, the previous request's URL, and the sensitive-name set. The set is an instance member, so the method stops being `static`. That is the whole structural change — no new type, no new file, no new call site.

**Ordering is fixed and load-bearing:** `UrlProcessor` → URI combining → **origin comparison** → header copy. The comparison must see the final target, so that a `UrlProcessor` rewrite is honoured (§2.3) and so that a rewrite cannot smuggle a credential to a host the caller never named.

**The `null` cases already have answers and gain no branches.** When `response.RequestMessage` is null (an unstamped handler) the hop already goes out bare — no headers to inherit, nothing to strip. When its `RequestUri` is null, or the resolved target is not absolute, origin is unprovable and §2.1 strips.

### 3.1 Forward-compatibility with a multi-hop follower

The rule is defined **per hop, against the immediately preceding request's origin.** Today the follower does exactly one hop, so this is indistinguishable from comparing against the original request. It will not be indistinguishable once a multi-hop follower ships (one of the limits in §5), so the extension is stated now rather than re-opened then: **once stripped, credentials are not restored** — a later hop back to the original origin does not re-attach them, because the intervening origin has already seen the URL chain. That is the browser rule. Whoever takes the multi-hop task inherits this decision instead of re-deciding it.

---

## 4. Compatibility, tests, and version

### 4.1 What breaks for an existing caller

**No public signature changes.** No interface member is added, removed or re-typed. `SensitiveHeaders` keeps its name, type and mutability; only its documented meaning widens.

| Caller shape | Change |
|---|---|
| No options bag, or a bag without `FollowRedirects` | **None.** `HandleResponse` short-circuits at `:263`. |
| `FollowRedirects` + a same-origin redirect (relative `Location`, the common shape) | **None.** |
| `FollowRedirects` + cross-origin redirect + no credential header | **None.** |
| `FollowRedirects` + cross-origin redirect + a credential header | **Changed, deliberately.** The hop goes out unauthenticated. Where the target actually required the credential, a `200` becomes a `401` — and it fails **silently as a status**, not as an exception. This is the whole point of the change and the whole cost of it. |

Unlike PR #6, the affected caller need not have done anything exotic: setting `FollowRedirects` together with a `TokenProvider` is enough, and that is a plausible existing configuration. **This must appear in the release notes as a behaviour change, not only as a version digit.**

### 4.2 One green test is invalidated — and it is not the one flagged in the brief

`HttpServiceRedirectTests.cs:14` `AbsoluteLocationResolvesToAbsoluteUrl` — the cross-host constraint named in the brief — **survives untouched.** Verified by reading it: it asserts `RequestedUris[1]` only, sets no `TokenProvider`, and attaches no headers. It is about URL resolution, and URL resolution does not move.

The test that breaks is **`HttpServiceRedirectTests.cs:159` `GetWithTokenProvider_AuthorizationHeader_ReachesBothHops`**, whose assertion at **`:173`** reads:

```
Assert.That(HeaderValues(handler.Requests[1], "Authorization"), Is.EqualTo(new[] { "Bearer url-overload-token" }));
```

on a `https://original-host.example/start` → `https://other-host.example/target` hop. That assertion inverts under D1. It was added by PR #6 to guard *"no regression for the URL overloads"* (#9618 §8 case 3), and that intent survives — only its cross-host framing was incidental.

**Required edit:** change its `Location` to a **same-origin** target (`https://original-host.example/target`). This preserves exactly what the test was guarding — the URL overload's token reaches hop 1 — and stops it pinning credential forwarding as a side effect. The cross-host case is then pinned in the opposite direction by new case 1 below.

`GetWithTokenProvider_FollowedRedirect_RequestsTokenOnce` (`:178`) survives: the token is still minted once, at hop 0. Every other redirect test uses non-credential marker headers and is unaffected.

### 4.3 Coverage

`Http.Tests/HttpServiceRedirectTests.cs`; `SequenceHandler` already records full requests, so no fixture change is needed.

| # | Case | Guards |
|---|---|---|
| 1 | `Get<T>` + `TokenProvider`, **cross-host** `Location`: `Authorization` on hop 0, **absent** on hop 1 | the fix, URL-overload path |
| 2 | `Get<T>` + `TokenProvider`, **same-origin relative** `Location`: `Authorization` on **both** hops | the dual — an implementation that strips unconditionally passes case 1 and fails here |
| 3 | `Send<T>` with a hand-set `Authorization`, cross-host: absent on hop 1 | the `Send` path, i.e. the leak #9618 §7 introduced |
| 4 | Cross-host hop with a **non-credential** caller header: present on hop 1 | that only the named set is stripped, not the header set wholesale |
| 5 | `[TestCase]` fan over **scheme change**, **port change**, **host change** (same host otherwise): stripped in all three | pins the definition to all three components — without it "cross-origin" silently degrades to "cross-host" |
| 6 | A caller-added name in `SensitiveHeaders` (e.g. `X-Tenant-Secret`), cross-host: stripped | pins the shared-set decision (§2.2); a hard-coded internal list passes 1–5 and fails only here |

Cases 2 and 6 are the duals #114 §13.1.1 asks for: without them the suite is green for two implementations this design explicitly rejects.

**Do not add an assertion on the hop's `GET` method.** It is unpinned (#9626), it is genuinely a defect, and it sits in the method this change edits — but it belongs to the redirect-follower limits (§5), not here. Folding it in would blur a security change into a capability change.

### 4.4 Version

**`0.9.1-preview` → `0.9.2-preview`, a patch bump.** The repo convention across three consecutive changes is *patch when behaviour is byte-identical for callers who opt into nothing, minor when it is not*. `FollowRedirects` defaults to `false`, so every caller who opts into nothing is untouched, and the literal test is met.

The argument for minor is real and loses on purpose: this change *removes* something that was arriving (unlike PR #6, which restored something that was being dropped), and it fails silently as a `401`. But applying a convention inconsistently because one change feels scarier is how a convention stops being one — and the version digit is the wrong instrument for the warning. §4.1's release-note line is the right one. Recorded as an open question (§7) since packaging is Toni's call and costs nothing to change.

---

## 5. Scope

**In scope:** which headers ride the redirect hop when it crosses origin. Only that.

**Out of scope, unchanged by this design, and *not* folded in** — the redirect follower's capability limits, recorded in #8297 and #8311:

- one hop only, and no loop or hop-count guard
- the hop is always re-sent as `GET`, dropping verb and body
- `307` throws outright; `308` is unrecognised and falls through the "3xx is not an error" path
- **the hop's `GET` method is pinned by no test** — mutating it to `POST` survives the full suite (measured, review #9626)

### 5.1 Should this ship alone? Yes.

#9619's own `Abgrenzung` says those limits *"should be decided together if any of them are taken up."* **The condition is not triggered:** this design takes none of them up. It changes no hop count, no verb, no status handling and no loop behaviour — only the header set on the single hop that already exists. Three further reasons to ship it alone:

1. **It is the only member of that set that is a security defect** rather than a capability limit. The others produce a call that does less than the caller wanted; this one produces a credential in a third party's log. Different urgency class.
2. **Bundling leaves the hole open for the duration** of a follower rewrite that has not been scoped, briefed or scheduled.
3. **The one genuine coupling is pre-answered.** A multi-hop follower needs to know whether origin is compared per-hop or against the original, and whether a stripped credential can return; §3.1 states both, so the multi-hop task inherits a decision rather than re-opening this one.

No new task filed. Every limit above is already recorded on #8297 and #8311, and #9626 already carries the untested-`GET` finding — filing a fourth node that restates them would be a dump (#1136 §2 form 1).

---

## 6. Pre-Design Checklist (#1136 §5)

| Item | Verdict |
|---|---|
| No new type mirroring an existing one | **Pass** — the credential list *is* the existing `SensitiveHeaders`; §2.2 rejects a parallel set explicitly, which is the §6 mirror anti-pattern in its set-shaped form |
| No new abstraction with one implementation | **Pass** — no new abstraction; a predicate inside an existing private method |
| Nothing justified by "we might need X later" | **Pass** — §2.3 rejects the policy enum; §3.1 states a forward decision without building anything for it |
| No deprecation window / compat shim / feature flag | **Pass** — none; the change is immediate and total |
| DRY math on inline-vs-extract | **N/A** — one call site, one method |
| Existing surface audited before adding one | **Pass** — §2.2 (`SensitiveHeaders`), §2.3 (`UrlProcessor`, raw-response manual follow); all three already ship and are reused rather than duplicated |
| Every config knob has a named operator | **Pass by removal** — no knob ships; §2.3 records that no operator could be named, which is exactly why |
| Can-it-be-deleted / merged / inlined | **Pass** — the rule is merged into the method that already holds the sibling exclusion rule (§3) |
| Trade-offs named explicitly | **Pass** — §2.3 (the accepted break), §2.2 (`Proxy-Authorization`), §4.1 (the silent `401`) |
| Out-of-scope listed explicitly, not merely absent | **Pass** — §5, with §5.1 answering the ship-alone question the `Abgrenzung` raises |
| No multi-paragraph rationale for things that obviously stay | **Pass** |
| Predecessor design banner where superseded | **N/A** — #9618 is extended, not superseded; §1.2 records that its §7 argument survives intact |
| Data deliverables (SQL/schema casing) | **N/A** — no data layer in this repo |

---

## 7. Open questions

1. **Patch or minor.** §4.4 recommends patch (`0.9.2-preview`) on the convention's literal test, and argues the warning belongs in the release note rather than the version digit. The other reading — that a silent `401` for an opt-in caller warrants `0.10.0-preview` — is defensible. Toni's call at packaging; costs nothing to change.
2. **Does the version convention itself still serve its purpose?** Every capability in this library is opt-in, so "byte-identical for callers who opt into nothing" makes almost every behaviour change a patch. Noted rather than acted on — redefining a versioning convention inside a security change is the wrong place for it.

---

## 8. Implementation order

1. Widen the `SensitiveHeaders` XML doc to state both consequences (§2.2). No behaviour.
2. Make `CreateRedirectRequest` an instance method taking the resolved target and the previous request; add the origin comparison and the credential exclusion (§2.1, §3). Keep the body-descriptor exclusion exactly as it is.
3. Fix `GetWithTokenProvider_AuthorizationHeader_ReachesBothHops` to a same-origin `Location` (§4.2). Suite green again at this point.
4. Add coverage cases 1–6 (§4.3).
5. Bump the package version (§4.4) and write the release-note line from §4.1.
