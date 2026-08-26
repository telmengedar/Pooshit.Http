# Architectural Document: Header redaction in `HttpServiceException` messages

> **Repo path:** `docs/architecture/error-message-header-redaction.md` (repository `telmengedar/Pooshit.Http`).
> **DiVoid:** source task **#9117** · consumer-side redaction in mamgo (different repo, not this change) **#9116** · persistence leg of the same leak **#9461** · repo map root **#8292** · `HttpService` **#8297** · `HttpOptions` **#8299** · `HttpServiceException` **#8300** · project **#2281**.
> **Contracts cited as load-bearing:** Code Contracts **#114** (§0 KISS/DRY/YAGNI + the bounce rule, §1 one type per file, §4 comments, §13.1.1 guard axes).
> **Baseline:** `origin/master` @ `73c0b71`. Package version on master: `0.8.0-preview`; this change ships `0.9.0-preview`.

---

## 1. Problem

`HttpService` bakes a full dump of request and response headers into the message of every `HttpServiceException` it throws on a rejected status. `Authorization` is dumped verbatim. An exception message is a string that by construction ends up in logs, so `logger.LogError(e, …)` — the default reflex — writes the caller's bearer token in clear text.

#9117 records two measured production incidents in mamgo:

| Date | Service | Credential | Times in the log |
|---|---|---|---|
| 2026-08-21 | `tlouservice` | 900-second bearer | 2 (service + shared error handler) |
| 2026-08-25 | `facebookservice` | **Meta system-user token — does not expire** | 2 (controller + exception handler) |

The second is the worse class: a credential with no natural expiry, granting `ads_management`, sitting in application logs. Consumer-side redaction (#9116) fixes one logging site at a time and has to stay fixed at every future site; redacting where the message is built covers every caller, including the ones nobody has written yet.

### 1.1 Requirements

| # | Requirement |
|---|---|
| R1 | The default must be safe — a default-constructed `HttpService` must not leak. |
| R2 | A caller can turn the header dump off entirely. |
| R3 | A caller can name additional headers whose values must not appear. |
| R4 | A caller can opt back in to the old full dump for debugging. |
| R5 | Redaction keeps the header **name** and replaces only the **value**, on the response side as well as the request side. |

---

## 2. Scope

**In scope:** the header dump built by `HttpService.DumpHeaders` and consumed by `HttpService.CheckHttpResponse`.

**Out of scope, deliberately:**

- Consumer-side redaction in mamgo (#9116) — different repository.
- The request **URL** in the error message. Query-string credentials are an adjacent risk; #9566 §3 measured that mamgo has none today.
- `HttpServiceException.Response` remaining reachable. A caller can walk `Response.RequestMessage.Headers` and read the token. That is deliberate access by someone who asked for it, not accidental logging, and closing it would remove the diagnostic value the property exists for. See §6.

---

## 3. Design

Two independent axes, deliberately carried on different surfaces.

### 3.1 Axis 1 — the mode (per-call and per-service)

A three-state enum, `HeaderDumpMode`:

| Value | Behaviour | Requirement |
|---|---|---|
| `Redacted` | headers dumped, sensitive values replaced by the literal `<redacted>` | R1, R5 |
| `Omitted` | no header block at all | R2 |
| `Full` | every header dumped verbatim | R4 |

`HttpService.HeaderDumpMode` is a non-nullable property initialised to `Redacted`. `HttpOptions.HeaderDumpMode` is **nullable**; the dump resolves `options?.HeaderDumpMode ?? service.HeaderDumpMode`.

The nullability is the load-bearing part and it deviates from the `CompletionOption` precedent, which #8299 records as deliberately non-nullable. The reason the two differ: `CompletionOption`'s zero value *is* its historical behaviour, so collapsing "unset" into "default" is free. Here a non-nullable per-call property would mean a consumer who sets `service.HeaderDumpMode = Full` for a debugging session gets `Redacted` back on every call that passes an options bag — which is nearly all of them, since the options bag is how a token provider is supplied. The service-level setting would be silently dead. Nullable keeps "unset" distinguishable from "explicitly Redacted".

### 3.2 Axis 2 — the sensitive names (per-service only)

`HttpService.SensitiveHeaders` is a get-only `ISet<string>` backed by a `HashSet<string>` with `StringComparer.OrdinalIgnoreCase`, pre-populated with the default list. A consumer extends it (`.Add("X-Vendor-Signature")`) or trims it (`.Remove("Cookie")`) when the service is constructed. The comparer cannot be replaced, so case-insensitive matching is a property of the type, not of how a consumer populates it.

There is **no** per-call name list. See §5, decision 2.

### 3.3 The default list, and why those names

`Authorization`, `Proxy-Authorization`, `Cookie`, `Set-Cookie`, `Api-Key`, `X-Api-Key`, `X-Auth-Token`, `X-Access-Token`.

The membership rule: **a name is in the default list when its value is, by the header's own definition, a credential or a session identifier — not merely correlated with one.** All eight qualify. `Cookie` and `Set-Cookie` carry session identifiers, which are bearer-equivalent; `Set-Cookie` is the reason R5 has to reach the response side at all.

**`WWW-Authenticate` and `Proxy-Authenticate` are deliberately absent.** They are server *challenges* — scheme, realm, error description — and carry no client credential. On a 401 they are the single most useful header in the dump, which is the case the dump exists to serve. Redacting them would cost diagnostics and buy nothing.

**Vendor-specific credential headers are also absent** — `X-Amz-Security-Token`, `X-Auth-Key`, `X-CSRF-Token` and their kin. The list is extensible by construction (R3), so it does not need to be exhaustive; it needs to cover the names a caller would not think to add. A caller integrating AWS knows `X-Amz-Security-Token` is a secret. A caller who writes `logger.LogError(e, …)` is not thinking about `Authorization` at all. The default list covers the universal cases; the extension point covers the vendor cases. Padding it further trades a bounded, readable default for coverage the caller was already going to configure.

### 3.4 Plumbing

#8299 records the hazard: adding an option is a two-place change, and nothing is picked up automatically. Here the second place is `CheckHttpResponse`, which had no `options` parameter — it now takes one, and all nine call sites in `HttpService` pass the `options` already in scope. `DumpHeaders` gained the same parameter and delegates per-header rendering to a small `DumpHeader` helper, so the request-side and response-side loops cannot drift apart (they were already identical two-liners; the helper is what makes R5 hold on both sides by construction rather than by repetition).

`Omitted` returns `string.Empty` from `DumpHeaders` rather than restructuring the two throw sites. The message becomes `Error sending request to '…' -> status 401\n\n{body}` — URL, status and body all intact, one blank line where the header block was.

---

## 4. Compatibility break

**This changes default observable behaviour.** Every existing caller who does not set anything sees `Authorization: <redacted>` where they used to see the token. That is the point of R1, and it is not opt-in — an opt-in safe default is not a safe default.

Nothing in the library's *typed* surface breaks: no public signature changes, no interface members are added or removed. `CheckHttpResponse` and `DumpHeaders` are private. What breaks is anyone parsing header values out of `HttpServiceException.Message`, which was never a supported contract; `Response` remains available for callers who genuinely need the values.

Version therefore goes `0.8.0-preview` → **`0.9.0-preview`**, a minor bump rather than a patch. The precedent in this repo (`0.7.18` → `0.7.19` for the completion option) was a patch bump precisely because that change guaranteed byte-identical behaviour for callers who did not opt in. This one deliberately does not.

---

## 5. Decisions

**Decision 1 — three-state mode, nullable per-call, non-nullable per-service.** Rejected: two independent booleans (`DumpHeaders` + `RedactSensitiveHeaders`), which encodes four states for three real ones and makes the illegal combination representable. Rejected: mode on `HttpOptions` only, which fails R1 for a caller who passes no options. Reversal cost: cheap.

**Decision 2 — the name set is per-service only; no per-call additive list.** The service-level set alone satisfies R1–R5. A per-call list is a second configuration surface for one feature, and no near-term consumer needs per-vendor granularity: mamgo's upstreams (Meta, Recruitee, Jobdaten) all authenticate with names already in the default list, so the 4-week-decision test (#114 §0, YAGNI) cannot be met. The conceptual split also reads cleanly — the mode is a *diagnostic verbosity* decision that varies per call and per debugging session, the name set is a *security policy* configured once at composition. The cost of cutting it is that a consumer with a vendor-specific secret header adds it globally and over-redacts on other vendors, which is the safe direction to be wrong in. Reversal cost: cheap — the property and one `Concat` at the match site.

**Decision 3 — the knobs live on `HttpService`, not on `IHttpService`.** `Timeout` is on the interface, so symmetry argued for putting them there; blast radius argued against, since adding members to a published interface is source-breaking for any external implementer. The deciding fact is that the interface change buys nothing concrete: `HttpOptions.HeaderDumpMode` already gives every `IHttpService` holder full control of the mode, and the name set is composition-time policy, configured where the concrete type is visible anyway. Reversal cost: cheap.

**Decision 4 — replace the value, keep the name (R5), with the literal `<redacted>`.** Rejected: dropping the header line entirely, which loses "an `Authorization` header was present" — diagnostically valuable on a 401 and not itself a secret (#9117). Rejected: truncating the value to a prefix, which leaks entropy for no gain. Reversal cost: cheap.

---

## 6. Escape-route audit (#9117 scope item 4)

Every route by which the same headers could reach the outside, with a verdict.

| # | Route | Verdict |
|---|---|---|
| 1 | `HttpService.DumpHeaders` → `CheckHttpResponse` → `HttpServiceException.Message` (`HttpService.cs:162-196`) | **Closed by this change.** The measured leak. |
| 2 | `Exception.ToString()` | **Closed by consequence.** `ToString` renders `Message` + type + stack + inner exception. `Message` is now redacted; the only inner exception this library attaches is a decode failure (route 4), which carries no headers. |
| 3 | `Exception.Data` | **Not a route.** The library never writes to it — no occurrence of `.Data` anywhere in `Pooshit.Http/`. |
| 4 | Decode-failure message in `ReadResponse<T>` (`HttpService.cs:230`) | **Not a route for headers.** It renders `$"Error decoding response of '{RequestUri}'"` — URL only, no headers. It does carry the URL, which is the out-of-scope concern from §2. |
| 5 | `HttpServiceException`'s own default message (`HttpServiceException.cs:17`) | **Not a route.** `$"{response.StatusCode}: {response.ReasonPhrase}"` — status and reason phrase only. |
| 6 | `HttpServiceException.Response` → `Response.RequestMessage.Headers` / `Response.Headers` | **Deliberately left open.** Reading it is an explicit act by a caller who wants the values; the property exists so a caller can inspect the failed exchange. Unlike the message, it does not reach a log by default. |
| 7 | `HttpServiceException.Body` | **Not a route for the library's dump.** It is the server's own response payload. A server that echoes a credential back in its error body is leaking its own content; the library neither adds to nor can classify that. |
| 8 | Request/response **content** headers (`request.Content.Headers`) | **Not a route today.** `DumpHeaders` enumerates `RequestMessage.Headers` and `response.Headers` only; content headers were never dumped and still are not. Anyone who later adds content headers to the dump must route them through `DumpHeader`. |
| 9 | Library logging | **Not a route.** The library has no logger and no console output — no occurrence of `ILogger` or `Console.` in `Pooshit.Http/`. |

---

## 7. Coverage

`Http.Tests/HttpServiceHeaderRedactionTests.cs`, 13 tests. Axes varied independently: mode (each of the three, plus unset), request-side vs response-side headers, the service-level name set (added / removed), case-insensitivity of the match, per-call-vs-service resolution in both directions, and null options.

The two duals #114 §13.1.1 requires are pinned explicitly: a non-sensitive header sent alongside a sensitive one must still be dumped verbatim (a filter that redacts everything would otherwise pass every token-is-gone assertion), and with the dump `Omitted` the URL, the status and `HttpServiceException.Body` must all survive. The `Omitted` test asserts first that the request genuinely carried the `Authorization` header — via `exception.Response.RequestMessage.Headers` — before asserting the message omits it, so the guard cannot pass by the scenario never arising.

The full mutation table, with the failure signature of each of eleven independent mutations, is in the PR body.
