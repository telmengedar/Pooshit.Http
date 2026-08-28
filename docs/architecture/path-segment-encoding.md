# Architectural Document: path segment encoding in `Rest.Path` / `Rest.PathQuery`

> **Repo path:** `docs/architecture/path-segment-encoding.md` (repository `telmengedar/Pooshit.Http`).
> **DiVoid:** source task **#8320** · repo map root **#8292** · file node `Rest.cs` **#8308** · `QueryParameters` **#8310** · `QueryParameter` **#8309** · `HttpService` **#8297** · project **#2281** · TFM gap **#9965**.
> **Sibling designs, none superseded:** `error-message-query-redaction.md` (#9939), `error-message-header-url-redaction.md`, `redirect-credential-policy.md`, `media-type-fallback.md`. §2.6 below explains why #9939's "never re-encode a URL" argument does **not** transfer here.
> **Contracts cited as load-bearing:** Design Contracts **#1136** (§1 KISS/DRY/YAGNI, §2 existing systems first, §3 configurability is not free, §4 less is better, §5 checklist walked in §9 of this document) · Code Contracts **#114 §0** (principles + the bounce rule) · **#1184** (no invented design questions) · **#1267** (DRY threshold math) · **#1333** (principles trump design) · **#275** (load-bearing tests) · **#6836** (expose the real structures under their real names).
> **Baseline:** the §2 measurements were taken against `master` @ `a5befd1`, package version `0.12.1-preview`. Unit A was implemented against `master` @ `60f68bf`, where the csproj reads `0.13.0-preview` — unpublished, `0.12.1-preview` being still the last release on nuget.org.
> **Every runtime claim in §2 was measured** on `net8.0` and `net48` with a throwaway probe. Nothing in §2 is repeated from #8320 unverified — one of its claims is corrected and one is materially incomplete.

---

## 1. Problem

`Rest.Path` and `Rest.PathQuery` assemble a URL with `string.Join("/", elements)`. No element is encoded, none is validated, and every value is rendered with the ambient culture. The helper exists precisely so callers stop hand-building URLs, so a caller has every reason to assume it escapes. It does not, and `params object[] elements` says nothing either way.

### 1.1 What the design must achieve

1. A caller passing an untrusted value into a path segment cannot restructure the resulting URL.
2. A caller reading the signature can tell what the helper does with their input.
3. Whatever breaks, breaks **legibly** — a silent behaviour change on a URL builder is the worst available shape.

### 1.2 Two structural facts that constrain every option below

**Fact 1 — element 0 is always a URL, never a segment.** `HttpService` owns its `HttpClient` and **never sets `BaseAddress`** (grep-verified across `HttpService.cs`). Every verb takes a `string url` and reaches `new HttpRequestMessage(method, url)`, which constructs `new Uri(url, UriKind.RelativeOrAbsolute)`; `HttpClient.SendAsync` rejects a relative request URI when no `BaseAddress` is set. So any `Rest.Path` result that reaches the wire through this library **must** carry scheme and authority, and they can only have come from the first element. The first element is a base URL by construction, not by convention.

**Fact 2 — the result is normalised by `Uri` before it is sent.** Because the string becomes a `Uri`, `Uri`'s own rules apply to it: dot segments are removed, backslashes become forward slashes, spaces are escaped, `#` truncates. §2 measures what that does and does not rescue.

Fact 1 is what makes "escape every element" unshippable, and it is why the compatibility question (§3.2) has a different answer than the task suggested.

---

## 2. Measurements

Probe on `net8.0` (10.0.203 SDK) and `net48`, both from the same source.

### 2.1 `EscapeDataString` vs `EscapeUriString` — #8320's claim, verified

| input | `Uri.EscapeDataString` | `Uri.EscapeUriString` |
|---|---|---|
| `a/b` | `a%2Fb` | **`a/b`** |
| `a?b` | `a%3Fb` | **`a?b`** |
| `a#b` | `a%23b` | **`a#b`** |
| `a&b` | `a%26b` | **`a&b`** |
| `a=b` | `a%3Db` | **`a=b`** |
| `a:b` / `a@b` / `a;b` / `a,b` | `a%3Ab` / `a%40b` / `a%3Bb` / `a%2Cb` | unchanged |
| `a+b` | `a%2Bb` | **`a+b`** |
| `a b` | `a%20b` | `a%20b` |
| `a%b` | `a%25b` | `a%25b` |
| `\` | `%5C` | `%5C` |
| `café` / `北京` | `caf%C3%A9` / `%E5%8C%97%E4%BA%AC` | identical |
| `~a-b_c.d` | unchanged | unchanged |

**Verdict: #8320's claim is correct.** `EscapeUriString` leaves every URL-structuring character intact — `/`, `?`, `#`, `&`, `=`, `+`, `:`, `@` — and would fix only the malformed-request-line symptom. It is also **obsolete**: compiling a call emits `SYSLIB0013`, whose own text reads *"Uri.EscapeUriString can corrupt the Uri string in some cases. Consider using Uri.EscapeDataString…"*. It is a warning, not an error, so it would compile — the probe had to `NoWarn` it. Two independent reasons to reject it.

The escaping table is **byte-identical between `net48` and `net8.0`**. This is worth stating because #9965 is open: on this one behaviour the two target frameworks do not diverge.

### 2.2 Escaping does **not** stop directory traversal — #8320's suggested direction is incomplete

`Uri.EscapeDataString(".")` → `.` and `Uri.EscapeDataString("..")` → `..`. Both dot segments survive escaping untouched, because `.` is unreserved in RFC 3986. And `Uri` removes them:

| path built | `Uri.AbsolutePath` (identical on both TFMs) |
|---|---|
| `/api/../admin` | **`/admin`** |
| `/api/./admin` | **`/api/admin`** |
| `/api/%2E%2E/admin` | **`/admin`** |
| `/api/.%2E/admin` | **`/admin`** |
| `/api/%252E%252E/x` | `/api/%252E%252E/x` — inert |
| `/api/a..b/x`, `/api/.../x`, `/api/a./x`, `/api/..a/x` | unchanged — whole-segment match only |

Two consequences that decide §3.3:

- **Percent-encoding the dots does not help.** `Uri` decodes unreserved escapes *before* dot-segment removal, so `%2E%2E` traverses exactly as `..` does. There is no encoding that neutralises a dot segment; only rejection does.
- **After escaping, the pre-encoded form is inert.** A caller-supplied `%2E%2E` becomes `%252E%252E` and stays a literal. So a guard needs to match only the rendered forms `.` and `..`, and only as whole segments — `a..b`, `...`, `a.` and `..a` are all measured safe and must not be rejected.

Shipping `EscapeDataString` alone would produce a helper that advertises safety and still traverses. That is the failure shape `error-message-header-url-redaction.md` §3.4 named for the `Link` header — *looks handled, is not* — and it is why §3.3 folds a dot-segment guard into this change rather than leaving it for later.

### 2.3 What `Uri` already rescues, and what it does not

| shape | measured outcome | still a defect? |
|---|---|---|
| space in a segment | `/api/a b/x` → `/api/a%20b/x` | **No.** `Uri` escapes it. #8320's "a space produces a malformed request line" does **not** hold for a URL sent through this library. Correcting the task. |
| `#` in a segment | `/api/a#b/x` → `PathAndQuery` = `/api/a` | Yes — the rest of the URL is silently discarded. |
| `?` in a segment | `/api/a?b/x` → path `/api/a`, query `b/x` | Yes. |
| `\` in a segment | `/api/a\b/x` → `/api/a/b/x` | Yes — a backslash becomes a separator. Escaping fixes it (`%5C`). |
| `//` from a null/empty element | `/api//x` stays `/api//x` | Yes — reaches the server, whose normalisation decides what it addresses. |
| raw `%` | `/api/100%/x` → `/api/100%25/x` | No, but see the double-encode note in §3.2. |

### 2.4 Culture

`string.Join` calls each element's `ToString()`, which uses `CultureInfo.CurrentCulture` for anything culture-sensitive. Measured with a fixed `DateTime`, `DateTimeOffset`, `decimal`, `double`, `float`, `TimeSpan`, `Guid`, `int`, `bool`, `DateOnly`:

| culture | `DateTime` | `decimal` 1234.5 |
|---|---|---|
| Invariant | `08/28/2026 13:05:09` | `1234.5` |
| `de-DE` | `28.08.2026 13:05:09` | `1234,5` |
| `tr-TR` | `28.08.2026 13:05:09` | `1234,5` |
| `th-TH` | `28/8/2569 13:05:09` (Buddhist era) | `1234.5` |
| `ar-SA` (net8) | `15‏‏/3‏‏/1448 بعد الهجرة 1:05:09 م` (Hijri, with RTL marks) | `1234٫5` |

`Guid`, `int`, `bool`, `TimeSpan` and `enum` measured stable across all five cultures.

**Type facts, measured:** `DateTime`, `DateTimeOffset`, `DateOnly`, `decimal`, `double`, `float`, `Guid`, `TimeSpan` and `enum` **are** `IFormattable`. **`string` is not, and `bool` is not** — so an `IFormattable`-keyed invariant rendering never touches the two most common segment types, which is the reason it is safe (§3.4).

**A limit invariance does not close.** `double` and `float` render differently *per target framework* even under `InvariantCulture`: `0.1+0.2` → `0.30000000000000004` on net8, `0.3` on net48; `1.0/3.0` → 16 significant digits on net8, 15 on net48. `decimal` is stable on both. This is a second instance of #9965 and is recorded, not engineered around.

### 2.5 `EscapeDataString` input length

`net48` throws `UriFormatException` ("URI string is too long") above **65519** input characters; bisected exactly. `net8.0` has no limit up to 200 000. Output length is unconstrained on both (30 000 spaces → 90 000 chars, fine, on both). A third instance of #9965; recorded, not defended against — a 64 KiB path segment is not a shape this library needs to serve.

### 2.6 Why #9939 §3.4's "never re-encode" argument does not transfer

`error-message-query-redaction.md` §3.4 argues at length against decoding and re-serialising a URL, and the argument is right — **for a URL that already exists**. Its premise is that the diagnostic must reproduce the string that was sent: *"a diagnostic that silently rewrites the thing it is diagnosing is worse than a coarser one that does not."* Re-encoding there normalises `+` against `%20`, collapses repeated names, and leaves an investigator comparing the log against a vendor's access log two different strings.

Here there is no URL yet. `Rest.Path` is handed *values* — an id, a name, a date — and its job is to **produce** the wire form for the first time. Encoding a value that has never been encoded is not a round trip and loses nothing; there is no earlier string to diverge from, because the string this code emits *is* the earliest one.

The two documents therefore sit either side of one line: **#9939 governs rendering a URL, this one governs constructing one.** The one place they touch is the double-encode hazard in §3.2 — a caller who pre-escapes a value and passes it as a *segment* does hand us an already-encoded string, and that is exactly the population §3.2 names as broken.

---

## 3. Decisions

### 3.1 What is encoded, and with what

**Each segment is rendered to a string and passed through `Uri.EscapeDataString`. Nothing else.** `EscapeUriString` is rejected on the measurement in §2.1 and on its obsolescence. `HttpUtility.UrlEncode` — already in the file's neighbourhood via `QueryParameters` — is rejected too: measured, it emits `+` for space and escapes `~`, both correct for `application/x-www-form-urlencoded` and both wrong for a path segment, where `+` is a literal plus and `~` is unreserved.

**The base URL is emitted verbatim.** Per Fact 1 it is a URL, not a segment; escaping it would produce `http%3A%2F%2Fhost%2Fapi` and break every caller of this library without exception.

**The join rule itself does not change.** No leading/trailing-slash policy is introduced (#8308 already records its absence); a base ending in `/` still yields `//`. That is today's behaviour and the ask does not force it — leaving it out keeps the diff to one concern.

### 3.2 The shape of the fix, and the compatibility break

The hard question is not *whether* to escape but *what the escaped thing is*, and Fact 1 answers it: the parameter list conflates two different kinds of value. `params object[] elements` reads as "a list of segments"; in reality element 0 is a base URL and the rest are segments. Encoding all of them uniformly is not a break for some callers — it breaks **100% of callers whose result is sent through `HttpService`**, at the first request.

**Decision: change the signature so it names the two structures (#6836), in place.**

```
Rest.Path      (string baseUrl, params object[] segments)
Rest.PathQuery (string querystring,          string baseUrl, params object[] segments)
Rest.PathQuery (QueryParameters querystring, string baseUrl, params object[] segments)
```

The base is verbatim; every segment is escaped. The signature now states the split that Fact 1 has always imposed, which satisfies requirement 2 of §1.1 — a positional "element 0 is special" rule inside `params object[] elements` would not.

**Rejected: a second overload `Path(string, params object[])` alongside the existing `Path(params object[])`.** Both are applicable for `Path("a","b")` and the more specific one wins, so existing source silently rebinds on recompile — while `Path(someObjectArray)` keeps binding to the old, unescaped overload. One name, two behaviours, chosen by call shape, with no diagnostic. That is the worst shape available and it is rejected outright.

**Rejected: a new method name (`Rest.EscapedPath`) leaving `Path` untouched.** It ships the fix on a method nobody calls and leaves the defect on the method everybody calls. The whole premise of #8320 is that callers assume `Path` escapes; a safe sibling does not change that assumption, it just gives it somewhere else to be wrong.

**Rejected: `[Obsolete]` on the old shape plus a parallel new one.** With the signature change, `[Obsolete]` has nothing left to mark — the old shape ceases to exist rather than lingering. Keeping both would be a compatibility shim (#1136 §6) with no consumer for the deprecated half.

#### Who breaks, and how they find out

| # | Population | Detected | What they do |
|---|---|---|---|
| B1 | First argument is not a `string` (`Rest.Path(id, "x")`), or the call passes an `object[]` variable (`Rest.Path(parts)`), or passes nothing | **Compile error** | Name the base explicitly. |
| B2 | A segment carrying a **pre-escaped** value — `Rest.Path(root, "a%2Fb")` | **Runtime, silent** — now `a%252Fb`, request 404s | Pass the raw value; escaping is now the helper's job. |
| B3 | A segment carrying a **multi-segment string** — `Rest.Path(root, "users/7/orders")` | **Runtime, silent** — now one segment `users%2F7%2Forders` | Split into segments, or fold the prefix into `baseUrl`. |
| B4 | A segment carrying a reserved character the server was expected to see — a `+`, `:`, `@`, `,` in an id | **Runtime, silent** | Nothing, in most cases: `%2B`, `%3A`, `%40`, `%2C` are the correct wire forms and any conforming server decodes them. A server that does not is a server that was relying on the defect. |

B1 is answered at compile time. **B2 and B3 are the residual silent break, and the mitigation is structural rather than documentary: the escape hatch is already in the new signature.** The `baseUrl` parameter is verbatim text. A caller who genuinely holds a pre-built, pre-escaped, multi-segment tail composes it there — `Rest.Path($"{root}/{tail}", id)` — and gets today's behaviour for that portion with escaping still applied to the values that follow. **No new member is needed to serve them**, which is why §5 can reject an `escape: false` knob without leaving anyone stranded.

#### 3.2.1 Sizing the break — counted, and larger than a design would assume

GitHub code search across `org:mamgoGmbH` (2026-08-28): **352 files reference `Rest.Path`**, spanning **mamgo-backend and `gangolf`**. Within mamgo-backend it is pervasive rather than localised — every `Connectors/*Api.cs`, most `Controllers/V1/*`, and a `TicketRegistration.cs` in roughly twenty services.

Two things follow, and they pull in opposite directions.

**The 352 overstates B1.** A call shaped `Rest.Path("https://host", "a", id)` — which is what almost all of them are — binds to the new signature unchanged and compiles untouched. B1 is confined to array-passing and non-string-first callers, a small subset of the 352, and it is the *loud* break, which is the one worth having.

**The 352 forbids presuming B2/B3 small.** At this population size, "a caller who passes `a/b` as one element" stops being a hypothetical minority and becomes a near-certainty at some non-zero count — and **that count is unknown**. This document does not claim it is small; it claims it is unknown-but-large enough to plan for. It is the design's largest accepted cost, it is silent at the call site, and its magnitude is the one number nobody has.

What makes it tractable rather than merely large is that **the entire consumer population sits inside one GitHub organisation** — two repositories owned by the same team, reachable by code search. That is not an unbounded public consumer base, and it is the fact that decides §3.2.3.

#### 3.2.2 The release note is a migration aid, not an announcement

Announcement still happens where every prior behaviour change in this package has: **the cumulative `PackageReleaseNotes` in the csproj**, this repo's established and only mechanism. It accumulates across versions that have not reached nuget.org and is reset once one does, so at the measurement baseline it carried the eight entries of the untagged `0.9.x`–`0.12.1` window and at the implementation baseline it carries one — the unpublished `0.13.0-preview` body-less POST change, which the Unit A entry therefore accumulates onto rather than replacing. At 352 files that is necessary and nowhere near sufficient — "path segments are now percent-encoded" is true and useless, because it tells a reader that something changed and gives them no way to find their own instances. **The Unit A entry must carry the triage below**; it is the only thing standing between a consumer and reading 352 files.

**Step 1 — narrow by the static type of each segment argument.** Only arguments after the base matter; the base is verbatim and cannot break.

| Static type of a segment argument | Verdict |
|---|---|
| `int`, `long`, `short`, `byte`, `Guid`, `bool` | **Provably unaffected.** Their rendering contains only digits, hex, hyphens or `True`/`False`, every one of which `EscapeDataString` leaves alone (measured, §2.1). No review needed. |
| `DateTime`, `DateTimeOffset`, `decimal`, `double`, `float` | **Changes, but was already broken.** An invariant `DateTime` renders `08/28/2026 13:05:09`, whose two slashes already split it into extra segments today (§3.4). Review these against Unit B, not Unit A. |
| `enum` | **Review, briefly.** A single value renders as an identifier and is safe; a `[Flags]` value with more than one bit set renders `A, B` — comma and space, both now encoded. |
| **`string`, `object`, or a generic parameter** | **The review population.** A separator can only enter through one of these. Every other row is noise. |

**Step 2 — inside the `string` population, two mechanical searches find what breaks.**

- **B3, composed segments:** a `Rest.Path(` / `Rest.PathQuery(` call carrying a `/` inside a string literal, or inside the literal portion of an interpolated string, in any argument after the base — `Rest.Path(root, "users/active")`, `Rest.Path(root, $"{id}/orders")`. Each is now a single encoded segment, and this is the largest mechanically-findable group.
- **B2, pre-escaped values:** any `Rest.Path` / `Rest.PathQuery` call site that also mentions `Uri.EscapeDataString`, `Uri.EscapeUriString`, `HttpUtility.UrlEncode` or `HttpUtility.UrlPathEncode`. Each now double-encodes.

**Step 3 — what neither search reaches.** A `string` segment holding a *pre-composed sub-path in a variable* — typically named `path`, `subPath`, `route`, `resource`, `endpoint` — is invisible to both searches and is the residual manual pass. Reviewing by that name set is a heuristic, not a proof, and the note must say so rather than imply a coverage it does not have.

**And the counter-direction, which the note must state so the triage does not read as pure loss:** a `string` segment holding a name, slug, code, email or free-text value is **fixed**, not broken — those are exactly the values that can restructure a URL today and cannot after. Most of the `string` population is in that row.

#### 3.2.3 Detection aids considered — none warrants shipping ahead of Unit A

The same kill-test as §5: name the legitimate consumer.

| Aid | Who it would serve | Verdict |
|---|---|---|
| A Roslyn analyzer shipped in the package, flagging a `/` in a segment argument | The 352-file population | **Rejected.** It reaches exactly the shapes the §3.2.2 searches already reach, at the cost of a new shipped artifact with its own project, packaging and tests — and it is equally blind to the variable-held sub-path, which is the half that actually needs help. An analyzer earns its keep when consumers cannot be searched; here they can, and they are two repositories in one organisation. |
| `[Obsolete]` on the old `params object[]` shape, kept one release alongside the new one | Someone wanting a compile warning before behaviour changes | **Rejected, and specifically:** with both present, `Path("a","b")` binds to the *new* overload silently while `Path(objArray)` binds to the obsolete one. The warning therefore fires on precisely the B1 calls that were already going to be compile errors, and stays silent on B2 and B3 — the population it would exist to warn. It warns the wrong people. |
| A transitional un-obsoleted `Path(params object[])` preserving the raw join | B2/B3 callers wanting the old behaviour | **Rejected.** The same binding rule means they cannot reliably reach it, and it leaves the defect live on the shape this task exists to fix. |
| A "detection release" adding the new behaviour under a different name first, flipping later | Voluntary migrators | **Rejected.** A two-release transition window (#1136 §6) that depends on 352 files migrating voluntarily — which at that size means they will not, and the flip then lands with the same silence one release later. |

**Nothing warrants shipping ahead of Unit A.** The migration aid is code search plus the §3.2.2 triage, and it costs one release-note section rather than a shipped artifact.

**Why an in-place break is acceptable here, concretely:** the package is pre-1.0, and every release in its history has shipped an announced behaviour change (redirect credential stripping, body removal from `Message`, media-type dispatch). Consumers upgrade a NuGet version deliberately and recompile. #1136's prohibition on transition windows is scoped to *"a private-monorepo + atomic-deploy environment"* and does not bind a published library — which is exactly why the release note carries weight here and would not there.

### 3.3 Null elements, and the two dot segments

**Decision: a null base or a null segment throws an argument exception naming the position. A segment whose rendered form is exactly `.` or `..` throws. An empty string is allowed.**

**Null → error, not skip, not empty.** The three options differ in what a null coming from untrusted or merely unset data addresses:

- *Empty (today)*: `Rest.Path(root, id, "orders")` with `id == null` yields `root//orders`. Measured (§2.3), `Uri` preserves `//`, so what gets addressed depends on the server's normalisation — many collapse it, and the call then hits the **collection** endpoint instead of one member's sub-resource. A `DELETE` shaped that way deletes the wrong thing, and *whether* it does is a property of the server, not of the code.
- *Skip*: yields `root/orders` — the same misaddressing, but deterministically rather than conditionally, and with the doubled separator that might have made it visible in a log now removed.
- *Error*: the request is never issued.

Skip is strictly worse than empty for exactly the reason the ask anticipated: it makes the misaddressing reliable and removes its only trace. Error is the only option under which a null cannot silently change which resource is addressed, and it is one guard.

**Empty string stays allowed, and this is not an inconsistency.** `Rest.Path(root, "users", "")` → `root/users/` is the idiom for a trailing slash, which Django-style APIs require — a named legitimate consumer. An empty string is a value the caller wrote; `null` is the shape an absent value takes by default in C# (an unassigned `string` is null, an unassigned `int` is 0) and never means "trailing slash". The guard is on the shape that means "something is missing", not on the shape that means "nothing goes here".

**`.` and `..` → error, on the §2.2 measurement.** This is an addition beyond #8320's suggested direction and it is justified by measurement rather than by caution: escaping provably does not neutralise a dot segment, `Uri` provably removes it before the request line is built, and percent-encoding the dots provably does not help either. Ship `EscapeDataString` alone and the helper's new promise — *an untrusted segment cannot restructure the URL* — is false on its first bullet. The guard matches **whole rendered segments only**; `a..b`, `...`, `a.` and `..a` are measured safe and pass. A caller who genuinely wants to walk up a level puts `..` in `baseUrl`, where it is verbatim text.

Order of operations per segment: **null check → render → dot-segment check → escape.** The check reads the rendered form because that is what reaches the URL.

### 3.4 Culture

**Decision: a segment that is `IFormattable` is rendered with `ToString(null, CultureInfo.InvariantCulture)`; everything else with `ToString()`. No format string is imposed.**

**What it applies to:** `DateTime`, `DateTimeOffset`, `DateOnly`/`TimeOnly`, `decimal`, `double`, `float`, and any consumer type implementing `IFormattable`. Measured (§2.4), it does **not** touch `string` or `bool`, neither of which is `IFormattable`, and it is a no-op for `Guid`, `int`, `TimeSpan` and `enum`, which are `IFormattable` but culture-stable.

**Is it a behaviour change for existing callers? Yes, and only on a machine whose current culture is not invariant.** A German or Turkish host sending `1234.5` in a path emits `1234,5` today and `1234.5` after; a server parsing a German decimal from a path breaks, and a server expecting a normal REST decimal is fixed. For `DateTime` the change is larger but the affected population is near-empty: an invariant/en-US host already renders `08/28/2026 13:05:09`, whose two slashes split it into three extra segments — nobody has a working `DateTime`-in-path there. A `de-DE` host renders `28.08.2026 13:05:09`, whose only illegal character is the space, which `Uri` escapes — so a lenient server *can* be accepting it today, and that caller does break.

**Rejected: mirroring `QueryParameters`' `DateTime` → `"O"` special case.** The DRY pull is real — the query half already imposes a round-trippable format on `DateTime` — and it is declined, because the two positions are not the same kind of thing. A query parameter is a *named* field whose type contract is conventionally ISO-8601; a path segment is *positional* and its format is dictated by the server's route template, which the library cannot know (`/reports/2026-08-28` and `/reports/20260828` and `/reports/1756389909` are all real). Picking one is picking wrong for someone, silently. Invariant-without-a-format leaves the unformatted case *loudly* wrong — `08%2F28%2F2026%2013%3A05%3A09` is unmistakable in a log — rather than subtly wrong, and any caller who cares was always going to format the value themselves. Flagged in §11 as the one place a different call by Toni would change one line.

### 3.5 `PathQuery` and the query half

**The path portion of `PathQuery` has the identical defect, because it does not delegate.** Both string overloads inline their own `string.Join("/", elements)` (`Rest.cs:27` and `:28`) rather than calling `Path`. Three copies of the join rule, one of which is already fixed by §3.1 and two of which would not be. `PathQuery` delegates the path portion to `Path` — a DRY fix that falls out of the change rather than being sought.

**The query half needs nothing on encoding.** `QueryParameters.ToString()` already applies `HttpUtility.UrlEncode` to every name and every value, including each item of the array branch. That asymmetry — query encoded, path not — is the strongest evidence available that the omission in `Rest` is an oversight and not a policy: the same author, in the same directory, encoded the half where the need was obvious and left the half where it was not. Form-urlencoding is also the right choice *there*, where `+`-for-space is correct; it is the wrong choice for a path segment (§3.1).

**The query half does need the culture fix.** `QueryParameters.ToString()` special-cases `DateTime` to `"O"` but renders everything else with a bare `parameter.Value.ToString()` (`QueryParameters.cs:147,148`). Measured, a `decimal` on a `de-DE` host emits `1234,5` and encodes to `1234%2c5`. The same defect, the same fix, on the same public surface — it belongs with §3.4 and ships in the same unit.

**Where the rendering rule lives.** One internal static helper, applied at three sites: the `Rest` segment, the `QueryParameters` scalar value, and the `QueryParameters` array item. **The DRY math does not force this: 3 lines × 3 sites = 9, below #1267's ~15–20 threshold.** It is extracted anyway, and the reason is stated rather than dressed up as the threshold: two public surfaces of the same library must not render the same `decimal` two different ways, and an inlined rule drifts the moment someone edits one site. The named-helper test passes in two words.

**Out of scope on the query half, deliberately:** the `{a,b}` braces the array branch emits are not legal in a query and are unencoded; `PathQuery(QueryParameters, …)` still throws on a null collection where the string overload tolerates an empty one (#8308). Neither is forced by this ask and neither has a named consumer asking — #1184.

### 3.6 One change or two

**Two units, in this order.**

**Unit A — encoding, null, dot segments, signature.** Security-shaped. One compatibility story (the §3.2 break table), one release note about one thing. Null belongs here, not with the correctness fixes, because §3.3's argument for rejecting it is a misaddressing argument, not a tidiness one.

**Unit B — invariant rendering, across `Rest` and `QueryParameters`.** Correctness-shaped, and a genuinely disjoint break population: it affects only non-invariant hosts, it changes no signature, it touches a second public type with its own test file, and a caller unaffected by A can still be broken by B and vice versa. Bundling them would produce a release note in which a consumer cannot tell which half broke them.

**A before B.** A carries the signature change and establishes the per-segment rendering step that B then modifies; reversed, B would write a rendering rule into a method A is about to re-shape, for no gain. Per #1165 this document ships in A's PR.

**The 352-file consumer population (§3.2.1) does not change the sequencing.** Unit A carries the whole break and nothing detects it earlier — §3.2.3 kills all four candidate detection aids, and the `[Obsolete]` one fails for the specific reason that it would warn B1 while staying silent on B2/B3. So there is no unit that could usefully precede A. B stays second on its own merits: no signature change, a disjoint break population, and a second public type with its own test file. **The one thing the size does change is the release note**, which §3.2.2 promotes from a courtesy to a gating deliverable of Unit A.

---

## 4. The contract after both units

| Member | Input | Output | Throws |
|---|---|---|---|
| `Path(baseUrl, segments)` | base: verbatim URL text. segments: any values | base, then each segment joined by `/`, each rendered invariantly and percent-encoded as a data string | base or a segment is null; a segment renders to `.` or `..` |
| `PathQuery(string, baseUrl, segments)` | as above, plus query text with or without a leading `?` | the `Path` result with the query appended, `?` normalised, omitted when the query is empty | as `Path` |
| `PathQuery(QueryParameters, baseUrl, segments)` | as above, query rendered by the collection | as above | as `Path`; unchanged null-collection behaviour |

**Invariants.** Every character of `baseUrl` is reproduced verbatim. No segment can introduce a `/`, `?`, `#`, `&`, `=`, `\` or space into the URL structure. No segment can be a dot segment. The rendering of a segment does not depend on the host's culture. The number of `/` separators the helper emits is exactly `segments.Length`, always — which is the property `//` and dot segments each violated.

---

## 5. Knobs considered and rejected — "name the legitimate consumer of each member"

| Tempting member | Who would legitimately call it | Verdict |
|---|---|---|
| `Path(baseUrl, bool escape, params object[])` or an `escape:` optional argument | A caller holding pre-escaped or multi-segment text — the B2/B3 population | **Rejected.** That population is already served by the `baseUrl` position (§3.2), which is verbatim by design. A knob would be a second way to do the same thing, and the wrong default would silently reintroduce the defect. |
| `EscapingMode { None, Data, Uri }` | `Uri` mode: nobody — it is obsolete API that measurably does not fix the defect (§2.1). `None`: same as above. | **Rejected.** Two of three values have no consumer and the third is the default. |
| `Rest.AllowDotSegments` | A caller wanting `..` | **Rejected.** Serve it with `baseUrl`, where `..` is verbatim text. A global static toggle on a security guard is the worst possible home for it. |
| A configurable culture or format for segments | Nobody. A wire format has one correct culture. | **Rejected.** #1136 §3 — no named operator, no environment difference, not a secret. |
| A `Rest.Segment(object)` public renderer | Nobody yet asking. | **Rejected.** #1184. The helper stays internal; it becomes public when someone names a use. |

No new public type, no new enum, no new option on `HttpOptions`. The net public surface delta is **zero new members**; three existing signatures change and one internal helper appears.

---

## 6. Risks and limits — recorded, not defended against

| Limit | Why it is not engineered around |
|---|---|
| **B2/B3 break silently at runtime across an unknown share of 352 consumer files** (§3.2.1) | No compile-time signal is available without renaming, and renaming ships the fix on a method nobody calls; all four detection aids are killed in §3.2.3. Mitigated structurally by the `baseUrl` escape hatch, and operationally by the §3.2.2 triage, which the release note must carry. This is the design's largest accepted cost, its size is **unknown rather than presumed small**, and it is named as such. |
| **`double`/`float` render differently per TFM even under invariant** (§2.4) | A property of the runtimes, not of this code. Second instance of #9965. |
| **`EscapeDataString` throws above 65519 chars on `net48` only** (§2.5) | Third instance of #9965. A 64 KiB path segment is not a shape this library serves; a guard for it would be #1136 §6 defensive code for an impossible scenario. |
| **The test project is `net8.0` only** | So neither divergence above is visible to any guard in the repo. Pre-existing; #9965 owns it. |
| **An untrusted `baseUrl` is not protected** | It determines scheme and host, so a caller placing untrusted data there has an SSRF problem that no encoding addresses. Out of scope, stated so the omission is deliberate. |
| **A base ending in `/` still yields `//`** | Today's behaviour; #8308 already records the absent slash policy; not forced by the ask. |

---

## 7. Tests (#275 — load-bearing, and each one falsifiable)

**Unit A.** Each of `/`, `?`, `#`, `&`, `=`, `\`, space and a non-ASCII character, placed in a segment, appears percent-encoded in the result and does **not** appear as a structural character in `new Uri(result).AbsolutePath`. Asserting on the built `Uri`, not only on the string, is what makes these tests about the defect rather than about `EscapeDataString` — the §2.2 measurement is the proof that a string-only assertion can pass while the URL still traverses.

- Base is verbatim: a full `http://host/api` base survives character-for-character, including its `:`, `//` and any query-free path.
- `..` and `.` as whole segments throw; `a..b`, `...`, `a.`, `..a` do not.
- A caller-supplied `%2E%2E` segment survives as `%252E%252E` and the resulting `Uri.AbsolutePath` still contains it — the pre-encoded traversal is inert.
- Null base and null segment each throw, and the exception names which position.
- Empty segment does **not** throw and yields the trailing slash.
- `PathQuery`'s path portion is byte-identical to `Path`'s for the same base and segments — the delegation is pinned, not assumed.

**Unit B.** Set `CultureInfo.CurrentCulture` to `de-DE` inside the test and assert a `decimal` and a `DateTime` render identically to the invariant case, through **both** `Rest.Path` and `QueryParameters.ToString()`. A `string` and a `bool` segment are unchanged by the invariant path — the guard that keeps the change from being wider than claimed.

**Two shapes to avoid, from this repo's own review history (#8297):** do not assert a negative as "the result does not contain `/`" — write the positive `Does.Contain("%2F")`, since the negative passes vacuously if the segment vanishes entirely. And pin the *premise* of each guard: a test asserting that `..` throws should first assert that a benign segment in the same position does not.

---

## 8. Implementation order

**Unit A — `fix/path-segment-encoding`** (ships with this document at `docs/architecture/path-segment-encoding.md`).
1. Change the three signatures to `(…, string baseUrl, params object[] segments)`.
2. Per segment: null guard → `ToString()` → reject exactly `.` / `..` → `Uri.EscapeDataString`. Base emitted verbatim, null-guarded.
3. `PathQuery`'s two string overloads delegate the path portion to `Path`; the join rule exists once.
4. XML doc comments state the split explicitly: the base is used verbatim, each segment is percent-encoded, null and dot segments throw. The signature carries the meaning; the comment confirms it (#114 §4 — no comment restating the code).
5. Release note entry naming B2 and B3 in their own words, naming `baseUrl` as the verbatim position, and **carrying the §3.2.2 triage in full** — the static-type table, the two searches, the manual-pass caveat, and the sentence saying which `string` segments are *fixed* rather than broken. At 352 consumer files this section is a gating part of the deliverable, not a courtesy: an entry without it announces a silent break and leaves 352 files to read.

**Unit B — `fix/invariant-value-rendering`** (after A merges).
1. Internal static invariant renderer: `IFormattable` → `ToString(null, InvariantCulture)`, else `ToString()`.
2. Apply at the `Rest` segment site and at both `QueryParameters.ToString()` value sites. `DateTime` keeps its existing `"O"` in `QueryParameters`; nothing there is re-litigated.
3. Release note entry naming the non-invariant-host population.

**Do not** add a knob (§5). **Do not** touch `HttpService`, the error-message surface, or the slash policy. **Do not** extend the guard to empty segments (§3.3) or to the `{a,b}` array braces (§3.5).

---

## 9. Pre-Design Checklist (#1136 §5), answered in order

**KISS / DRY / YAGNI**
- *No new type mirroring an existing one* — ✓ zero new types; §5.
- *No abstraction with one implementation* — ✓ one internal static helper, not an interface; §3.5.
- *No element justified by "we might need X later"* — ✓ every rejected knob in §5 fails the named-consumer test explicitly.
- *No deprecation period / feature flag / compatibility shim / transition window* — ✓ §3.2 rejects `[Obsolete]` + parallel method; the break is taken in place. The rule's monorepo scoping and why a published package differs is stated there rather than assumed.
- *`block_size × site_count` quoted for every inline decision* — ✓ §3.5: 3 × 3 = 9, **below** threshold, extraction taken anyway for a stated non-threshold reason.

**Existing systems first**
- *Existing surface audited* — ✓ §3.5 audits `QueryParameters.ToString()`, finds encoding already present (no duplication) and culture handling absent (the gap this fills). §3.1 audits `HttpUtility.UrlEncode` as the in-repo candidate and rejects it on measured behaviour.
- *Reason a new layer can't live on the existing surface* — n/a, no new layer.
- *New persisted data* — n/a, none.
- *Consumer chain recursed* — ✓ §1.2 Fact 1 traces `Rest.Path` → `HttpService` verb → `HttpRequestMessage` → `Uri`, which is what establishes that element 0 is a base.

**Configurability**
- *Every knob has a named operator or environment difference* — ✓ §5, zero knobs, each rejection named.
- *Telemetry-then-tune pairing* — n/a, none proposed.
- *Magic numbers stay `const`* — n/a; the only constants are the two dot-segment literals, which are RFC 3986 vocabulary.

**Less is better**
- *delete / merge / inline check on every element* — ✓ the dot-segment guard survives it on the §2.2 measurement (deleting it makes the design's own promise false); `PathQuery`'s duplicated join is deleted by merging into `Path`; the invariant helper is merged to one site from three.
- *Trade-offs named when the complex option wins* — ✓ §3.2 (signature change over new name over overload), §3.3 (error over skip over empty), §3.4 (invariant without a format), §3.6 (two units over one).
- *Radical-clean over compromise when unconsumed* — ✓ §3.2 rejects the safe-sibling method, the classic compromise shape here.
- *Reader inventory covers AST and string-literal references* — ✓ `Rest.Path` / `Rest.PathQuery` have **zero call sites in this repository** (grep across all `.cs`, tests included) and **352 files across two repositories outside it** (§3.2.1). Both halves are counted rather than assumed. There is no string-literal reference surface — these are method calls, not names resolved from strings — which is what makes the §3.2.2 static-type triage sound: the AST is the whole surface.
- *Carrier-swap table enumerates every affected member* — ✓ §4 lists all three, not a representative one.

**Data deliverables** — n/a, no SQL, schema or migration.

**Document discipline**
- *Cites #114 and #1136 as load-bearing* — ✓ header.
- *Scope inventories explicit* — ✓ §10.
- *Out-of-scope listed, not merely absent* — ✓ §10.
- *No multi-paragraph rationale for things that obviously stay* — ✓ the slash policy and the query half's encoder each get one line.
- *Predecessor doc banner* — n/a; this supersedes nothing. §2.6 states the boundary against #9939 rather than claiming supersession.

---

## 10. Scope

**In scope:** `Pooshit.Http/Paths/Rest.cs` in full; the value-rendering path of `Pooshit.Http/Paths/QueryParameters.cs`; the three public signatures; release notes for both units; tests for both units.

**Out of scope, explicitly:** `HttpService` and every open item on it (#8317, #9663, #9664, #9665, #9667, #9690) · the error-message surface · `PathQuery(QueryParameters, …)` throwing on a null collection · the `{a,b}` braces the array branch emits unencoded · the leading/trailing-slash policy · `HttpUtility.UrlEncode`'s `+`-for-space in the query, which is correct there · the `net48`-only length limit and the `double` TFM divergence, both recorded to #9965 · an untrusted `baseUrl` (SSRF) · adding an `Accept` header or anything else on the request path.

---

## 11. Open questions — both answered

Both questions this document opened have been answered (2026-08-28). They are kept here rather than deleted, because each answer is load-bearing for a decision above.

1. **Should a `DateTime` segment get `"O"`, mirroring `QueryParameters`? — ANSWERED: no, §3.4 stands.** A path segment's format is route-defined; a query parameter's is conventionally ISO-8601, so the DRY tension against the query half is the accepted cost. Invariant-without-a-format failing loudly beats a format that is subtly wrong for half of all routes. The §3.4 paragraph explaining the tension stays on the page deliberately — a reviewer will hit it, and the answer should be where they hit it.
2. **How large is the B3 population? — ANSWERED, and larger than this document originally assumed.** GitHub code search across `org:mamgoGmbH` finds **352 files** referencing `Rest.Path`, across **mamgo-backend and `gangolf`**. Fully absorbed into §3.2.1 (sizing, and what it does and does not change), §3.2.2 (the triage the release note must carry), §3.2.3 (why no detection aid ships first) and §3.6 (why the sequencing is unchanged). **No decision moved.** What moved is the weight of the B2/B3 row, which is now stated as unknown-but-large rather than presumed small, and the status of the release note, which is now a gating deliverable rather than a courtesy.

**Still genuinely open, and it does not block Unit A:** the count of *actually affected* call sites within those 352 files. It is discoverable only by running the §3.2.2 triage over the two repositories, which is migration work for the consumer side rather than design work — and the triage exists precisely so that pass is bounded.
