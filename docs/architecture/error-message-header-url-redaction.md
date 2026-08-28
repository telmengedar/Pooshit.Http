# Architectural Document: query redaction inside URL-valued header values

> **Repo path:** `docs/architecture/error-message-header-url-redaction.md` (repository `telmengedar/Pooshit.Http`).
> **DiVoid:** source task **#9940** · the design that found the gap **#9939** (`docs/architecture/error-message-query-redaction.md`, §7 route 6) · the task whose scope excluded it **#9938** · project **#2281** · repo map root **#8292** · `HttpService` **#8297** · `HeaderDumpMode` **#9617** · redirect credential policy **#9633** · TFM gap **#9965**.
> **Sibling designs, none superseded:** `error-message-query-redaction.md` (#9939 — the request URL half), `error-message-header-redaction.md` (#9117 — the credential-header half), `redirect-credential-policy.md` (#9633 — why `SensitiveHeaders` is dual-purpose).
> **Contracts cited as load-bearing:** Design Contracts **#1136** (§1 KISS/DRY/YAGNI, §2 existing systems first, §3 configurability is not free, §4 less is better, §5 checklist walked as §8) · Code Contracts **#114** (§0 principles + the bounce rule, §4 comments, §13.1.1 guard axes, and the 2026-08-27 addendum on prose that outlives the defect it describes) · **#1267** (DRY threshold math) · **#275** (load-bearing tests).
> **Baseline:** `master` @ `b9be2c0`, tree clean, 236 tests green. Package version on master: `0.11.0-preview`; this change ships `0.12.0-preview`.

---

## 1. Problem

After #9938 shipped, `HttpServiceException.Message` renders the request URL with credential-bearing query values replaced. The header block printed underneath it does not get the same treatment.

`DumpHeader` replaces a value only when the header **name** is in `SensitiveHeaders`. `Location` and `Referer` are not in that set — correctly, they are not credentials — so their values print verbatim. But those values are URLs, and a URL carries a query. Meta's `paging.next` embeds `access_token` in one; a `Location` pointing at a pre-signed object-store target carries `X-Amz-Signature` or a bare `sig`.

The result is one message in which the same credential, in the same textual form, is redacted on one line and printed on the next, decided by nothing more than which line it landed on.

### 1.1 Requirements

1. A URL sitting in a header value that the library renders is redacted by the same rule as the request URL.
2. The rule for *which* headers are URL-valued is stated as a rule, not a list, so a later reader can extend it correctly.
3. `HeaderDumpMode.Full` is not touched.
4. No new public surface unless a knob is genuinely earned.

---

## 2. Decisions already taken elsewhere — recorded, not re-opened

- **The word set.** `SensitiveQueryParameters` and its substring-match rule are settled by #9939 §3.2/§3.3. This document reuses them and does not re-litigate the eight words or the over-redaction they cause.
- **The rewrite is textual.** #9939 §3.4 settled that the query is rewritten in place and never re-parsed or re-serialised. That property is what makes this change possible at all (§3.5).
- **`SensitiveHeaders` is dual-purpose** (#9633) and must not be reused as a URL-valued marker: adding a name to it also strips that header from a cross-origin redirect hop.

---

## 3. Design

### 3.1 Which header names are URL-valued — the rule, and what it admits

**The rule:** a header is URL-valued when its own field definition makes the **entire field value a single URI-reference**, and the library's dump can actually reach it.

The first clause is a statement about ABNF, not about whether a URL might turn up somewhere inside the value. It is what separates a field the redactor can be pointed at safely from a structured field where the redactor is wrong (§3.4). Within the core specification it admits `Location` (RFC 9110 §10.2.2), `Content-Location` (§8.7) and `Referer` (§10.1.3), and nothing else: `Origin` serialises without a query by construction, `Alt-Svc`, `Report-To` and `Content-Security-Policy` are structured, `Refresh` is `delay; url=…` and therefore structured too.

The second clause is where a measurement removes one of the three.

**`Content-Location` is excluded because it is unreachable, and that is measured rather than reasoned.** It is a *content* header. `HttpResponseHeaders.TryAddWithoutValidation("Content-Location", …)` returns `false`; the value lives on `response.Content.Headers`. `DumpHeaders` walks `response.Headers` and `response.RequestMessage.Headers` and neither contains it. An entry for it in the set could never be compared against successfully — it would be a string no execution path can reach. #1136 §4's delete-test asks what breaks if the element is absent, and here the answer is provably nothing: adding `"Content-Location"` to the set is mutation **M8** in §6.2 and it survives the whole suite, changing no output at all.

That exclusion is pinned rather than merely written down. `ContentLocationHeader_CarryingACredential_NotDumpedBecauseContentHeadersAreNotWalked` asserts first that the response genuinely carries the header (so the guard cannot pass vacuously, #114 §13.1.1) and then that the message does not. If the dump is ever extended to content headers, that test fails and points at this section.

**Outside the core specification the rule has no closed list, and this document does not offer one.** Fields whose whole value is a single URI-reference exist beyond RFC 9110 — WebDAV's `Destination` (RFC 4918 §10.3) and `SourceMap` (with its legacy `X-SourceMap` spelling) are two; `Content-Base` and `Ping-To` / `Ping-From` are weaker instances — and that namespace is open, so any list written here would be a list of whichever examples came to mind rather than a boundary. They are out on **YAGNI**: the library offers no WebDAV or source-map affordance, none of them names a credential-bearing target in ordinary use, and no consumer is asking. A consumer who needs one covered has the `SensitiveHeaders` route of the next paragraph today, and a one-line set entry if that is ever wrong.

**Reachability is not what keeps any of them out, and the rule should not be read as if it were.** `Destination`, `SourceMap`, `Refresh`, `Content-Base`, `Ping-To` and `Origin` are all reachable — measured. The second clause discriminates exactly **one** field, `Content-Location`, which is why that exclusion earns a paragraph and a test of its own while these earn a sentence. Everything else is out on syntax or on YAGNI.

**The shipped set is therefore `Location` and `Referer`.**

**The rule cuts both ways, deliberately.** Too narrow leaks. Too wide does *not* corrupt, and it is worth being precise about why, because it sets the cost of a future addition: the redactor only ever replaces the text between an `=` and the next `&` when the name in front of that `=` carries a credential word. Handed a value that is not a URL it either leaves it alone or over-redacts one span — never mangles the rest. `LocationHeader_NonUrlValueShapedLikeAQuery_OverRedacted` pins exactly that outcome. So the real boundary the rule defends is not corruption; it is §3.4.

**A vendor header carrying a callback URL is out**, and it has an escape route that costs no new surface: put its name in `SensitiveHeaders` and the whole value is replaced. That is coarser than redacting only the query, and it also strips the header on a cross-origin hop (#9633) — both stated here so the trade is visible. It is the answer to "my `X-Callback-Url` leaks", and it is the reason §3.2 can say no to a knob.

### 3.2 The set is a private constant, not a third public set

#1136 §3's concrete rule promotes a constant to a knob when there is **a named operator who will tune it**, **an environment difference**, or **a secret**. None of the three applies.

`SensitiveHeaders` is public because credential header names are vendor-specific — nobody but the consumer knows they send `X-Vendor-Token`. `SensitiveQueryParameters` is public because the query namespace has no registry at all. Neither argument carries here: which fields are URI-valued is fixed by the HTTP specification and is identical for every consumer of this library. There is no operator who knows better than RFC 9110 whether `Location` holds a URL.

The kill-test the sibling change already ran points the same way. #9938 removed the response body from the message with **no option at all** — a security default that admits no knob. Adding a fourth public collection to `HttpService` one release later, for a list that cannot vary, would be the opposite discipline in the same file.

And the knob would be the wrong shape even if it were wanted. A consumer with a URL-bearing vendor header does not want to declare it URL-valued; they want its credential gone, which `SensitiveHeaders` already does (§3.1). The gap the knob would fill is already filled.

So: `static readonly ISet<string> urlValuedHeaders`, beside the existing `redirectExcludedHeaders`, which is the file's own precedent for a private static header-name set.

**One honest consequence:** the set's `OrdinalIgnoreCase` comparer is not load-bearing and cannot be made so through the public surface. `HttpHeaders` canonicalises the casing of known header names — `location` reads back as `Location`, `referer` as `Referer` — so no casing variant ever reaches the comparison. Mutating it to `Ordinal` survives the suite (**M3**, §6.2). It stays `OrdinalIgnoreCase` to match the two sibling sets and to stay correct if the dump ever walks a collection that preserves arbitrary casing, not because a test earns it. Recorded so nobody reads coverage into it.

### 3.3 `HeaderDumpMode.Full` does not redact — confirmed

`Full` is the explicit show-me-everything hatch. It already prints an `Authorization` bearer token verbatim. Redacting a query inside a `Location` two lines below that would make the hatch lie about itself, and would quietly turn a three-member enum into four behaviours wearing three names. `Omitted` drops the block entirely, so the question does not arise there.

**Only `Redacted` redacts.** This is not a new rule; it is the existing contract of the enum (#9617) applied to one more value.

**The asymmetry a reader will notice, stated so it is not mistaken for an oversight:** under `Full`, the request URL on the first line *is* still redacted, because `DumpUrl` is unconditional and has no knob by #9938's decision. `HeaderDumpMode` governs the header block — the URL is not a header — so "`Full` dumps every header value verbatim" remains true and complete. The two policies are separate by design, not by accident.

### 3.4 `Link` is excluded, and the reason is a demonstrated leak rather than a preference

`Link` (RFC 8288) is not a bare URL. Its value is a comma-separated list of `<URI-Reference>; param=value` entries, and the parameters may carry quoted strings containing commas and semicolons. Handling it properly means an RFC 8288 parser with quoted-string handling — real machinery, its own test surface — to find each `<…>` span and rewrite inside it.

**Half-handling it is worse than excluding it, and this is the decisive point.** The redactor takes *one* query span, from the first `?` to the end. Given a two-link value:

```
<https://api.test/x?q=a>; rel="next", <https://api.test/y?access_token=SECRET>; rel="prev"
```

the span begins at the first URL's `?`, the whole remainder splits into a single parameter named `q`, `q` carries no credential word, and the credential in the *second* URL is copied out verbatim. A paginated API emitting `rel="next"` and `rel="prev"` together — GitHub's shape — hits this on the common path. Adding `Link` to the set would therefore produce a header that *looks* protected and is not: the exact failure mode #9938's release note calls out as the dangerous direction, because it is silent.

This is demonstrated, not argued. `UrlValuedHeaderCarryingAStructuredMultiUrlValue_CredentialInTheSecondUrlSurvives` runs that value through a header that *is* in the set and asserts the credential survives. `LinkHeader_CarryingACredential_DumpedVerbatim` pins that `Link` is outside the set, and adding it is mutation **M7**, which that test kills.

`Refresh` is excluded by the same rule for a different reason: `5; url=https://…` is structured, so it fails §3.1's first clause. It happens to survive naive treatment where `Link` does not — which is precisely why the rule, and not a case-by-case judgement, decides membership. A rule with an exception is not a rule.

### 3.5 What the redactor does with a value that is not a request URL

`DumpUrl` was written against `RequestUri.ToString()` and had never been handed anything else. Four inputs are new on this path, and all four are pinned:

| Input | Behaviour | Why it is safe |
| --- | --- | --- |
| Empty value | Returned unchanged | No `?`, so the first guard returns immediately |
| Relative URL (`/object/4711?access_token=…`) | Redacted normally | The rewrite is textual and never parses, so it needs no scheme or authority |
| Value with no query | Returned unchanged | Same first guard |
| Value that is not a URL | Returned unchanged, or one span over-redacted | The rewrite only ever replaces text after an `=` whose name matched |

The textual-rewrite decision of #9939 §3.4, taken there to avoid normalising `+` against `%20`, is what makes all four fall out for free. A parsing redactor would have needed a `Uri.TryCreate` and a policy for every failure.

**One fact discovered while pinning this, worth carrying because it inverts the expectation.** For a *known* header, `HttpHeaders` parses the value and hands the dump an escaped rendering rather than the raw text: a `Location` set to `plain text` reads back as `plain%20text`. So for the two names in the set the redactor is handed a `Uri`-normalised string — the same kind of string `RequestUri.ToString()` produces — not arbitrary bytes. This narrows how far the "arbitrary text" question actually reaches, and it means the .NET Framework versus .NET 8 escaping divergence recorded against #9965 applies to this path too.

### 3.6 Per value, then join — not join, then redact

`DumpHeader` renders a multi-valued header as `string.Join("; ", header.Value)`. Redacting the joined string is the same defect as §3.4, reached from a different direction: with two `Location` values, the single query span starts inside the first and swallows the second, whose credential then prints verbatim.

This is not hypothetical — `HttpResponseHeaders` accepts two `Location` values and enumerates them as a two-element sequence (measured). So the redaction is applied **per value** and the results are joined:

```csharp
builder.AppendLine(string.Join("; ", header.Value.Select(RedactQuery)));
```

`MultiValuedLocationHeader_CredentialInTheSecondValue_StillRedacted` pins it; mutation **M4** is the join-first form and that test kills it.

The distinction against `Link` is exactly this: `Location`'s multi-valuedness lives at the header-collection level, which `header.Value` already decomposes for free. `Link`'s lives *inside a single value*, which nothing decomposes without a parser.

### 3.7 Reuse — `RedactQuery` extracted from `DumpUrl`

`DumpUrl` did two things: unwrap the response's URL, and rewrite its query. Only the second is wanted here. The rewrite is extracted to `RedactQuery(string url)` and `DumpUrl` becomes its two-line caller:

```csharp
string DumpUrl(HttpResponseMessage response) {
    string url = response.RequestMessage?.RequestUri?.ToString();
    return url == null ? string.Empty : RedactQuery(url);
}
```

**DRY math (#1267):** the rewrite is a 20-line block; inlining it at the second site gives `20 × 2 = 40`, well above the ~15-20 threshold, so extraction is mandatory rather than optional. The named-helper test passes at one word. No second redactor is written and no existing behaviour moves: `DumpUrl`'s null-response contract is preserved in the caller, where mutation **M9** shows the existing `ResponseWithoutRequestMessage_RendersEmptyUrl` still guards it.

### 3.8 Precedence — the credential set wins

A name in both `SensitiveHeaders` and the URL-valued set loses its **whole** value, not just its query. The credential set is the stronger statement, and query redaction would otherwise put most of the value back. `NameInBothSets_WholeValueRedactedRatherThanItsQuery` pins the order; mutation **M5** swaps the two branches and that test kills it.

---

## 4. Scope

**In:** `DumpHeader`'s rendering under `HeaderDumpMode.Redacted`; the private URL-valued name set; the extraction of `RedactQuery`; tests; the release note; this document.

**Out, explicitly:**

- `SensitiveHeaders`' membership or its exact-name matching rule (#9633 territory).
- `SensitiveQueryParameters`' membership or its substring rule (#9939 territory) — reused unchanged.
- The cross-origin redirect strip.
- An RFC 8288 parser for `Link` (§3.4).
- The test project's single-TFM gap (**#9965**), which this change inherits and does not widen.
- #9664, #9665, #8320, #8317, #9667, #9690, #9663.

---

## 5. Decisions

| # | Decision | Alternatives rejected | Reversal cost |
| --- | --- | --- | --- |
| D1 | URL-valued means the field's ABNF makes the whole value one URI-reference **and** the dump can reach it. Ships `Location`, `Referer` | The literal three including `Content-Location` (unreachable, measured); "any header whose value parses as a URI" (would sweep in `Link`) | One line per name |
| D2 | Private `static readonly` set, no public knob | A third public set (#1136 §3 gate fails on all three clauses); a `HeaderUrlDumpMode` enum (mirror-enum anti-pattern) | Making it public later is additive and non-breaking |
| D3 | Only `HeaderDumpMode.Redacted` redacts | Redacting under `Full` (makes the hatch lie) | One condition |
| D4 | `Link` excluded, no parser | Naive whole-value redaction (leaks, demonstrated); an RFC 8288 parser (machinery with no named consumer) | Additive when a consumer appears |
| D5 | Redact per value, then join | Join then redact (leaks on a multi-valued header, measured) | One expression |
| D6 | Extract `RedactQuery`, reuse it | A second header-specific redactor (DRY math forbids it at 40 lines) | — |

---

## 6. Coverage

### 6.1 The predicate's axes, enumerated (#114 §13.1.1)

The shipped predicate is *"replace the query inside this header's value"*. Every position it reads is an axis:

| Axis | Values | Guarded by |
| --- | --- | --- |
| A — dump mode | Redacted / Full / Omitted | `FullMode_…`, `OmittedMode_…`, all others |
| B — `SensitiveHeaders` precedence | in / not in | `NameInBothSets_…` |
| C — URL-valued membership | in / not in | `HeaderOutsideTheUrlValuedSet_…`, `LinkHeader_…` |
| D — which entry | `Location` / `Referer`, independently | `LocationHeader_…`, `RefererHeader_…` |
| E — header collection | response headers / request headers | `LocationHeader_…` / `RefererHeader_…` |
| F — value decomposition | single value / several values | `MultiValuedLocationHeader_…` |
| G — the word set that drives it | added word / cleared set | `ServiceQuerySet_AddedWord_…`, `ServiceQuerySet_Cleared_…` |
| H — value shape | absolute / relative / no query / empty / not a URL / structured | the five `LocationHeader_…` shape tests |
| I — the non-sensitive direction | benign parameter survives | `LocationHeader_BenignQueryParameter_DumpedVerbatim` |
| J — set comparer casing | ignore-case / ordinal | **unguarded and unguardable** — see §3.2 |

`RedactQuery`'s own internal axes — word matching, position in the name, casing, fragments, missing `=`, repeated parameters — are not re-tested here. They are fully guarded through `DumpUrl` by `HttpServiceQueryRedactionTests`, and the two paths call the same helper, so a second copy of that fixture would guard nothing new. What is tested here is the dispatch and the inputs `DumpUrl` could never produce.

### 6.2 Mutation results

Thirteen mutants, eleven killed. Both survivors were predicted and are recorded as findings, not omitted:

| # | Mutation | Verdict |
| --- | --- | --- |
| M1 | Drop `Location` from the set | KILLED (6) |
| M2 | Drop `Referer` from the set | KILLED (1) |
| M3 | Comparer `OrdinalIgnoreCase` → `Ordinal` | **SURVIVED** — §3.2; `HttpHeaders` canonicalises known names, so no variant reaches the set |
| M4 | Join then redact | KILLED (1) |
| M5 | URL-valued checked before `SensitiveHeaders` | KILLED (1) |
| M6 | Drop the mode guard | KILLED (19) |
| M7 | Add `Link` to the set | KILLED (1) |
| M8 | Add `Content-Location` to the set | **SURVIVED** — §3.1; this is the measurement that justifies excluding it |
| M9 | Drop `DumpUrl`'s null guard | KILLED (1) |
| M10 | Add `X-Callback-Url` to the set | KILLED (1) |
| M11 | Redact every header value regardless of the set | KILLED (2) |
| M12 | Drop the `return` after the URL-valued branch | KILLED (7) |
| M13 | Revert the feature entirely | KILLED (7) — the #275 negative proof |

---

## 7. Escape-route audit

Routes by which a credential still reaches `HttpServiceException.Message` after this change:

| # | Route | Status |
| --- | --- | --- |
| 1 | A URL in a header outside the set — `Link`, `Refresh`, a vendor callback header | **Open, by decision.** §3.4 for `Link`; §3.1 for the vendor case and its `SensitiveHeaders` escape route |
| 2 | A credential whose query-parameter name carries none of the eight words | Open, inherited from #9939 §3.2 unchanged |
| 3 | A credential in a URL **path segment** rather than its query | Open, inherited from #9939 §4 |
| 4 | `HeaderDumpMode.Full` | Open by design, §3.3 |
| 5 | A percent-encoded delimiter inside a parameter name, on .NET Framework only | Open, inherited; the concrete case for **#9965** |
| 6 | The response body | Closed by #9938 — the body no longer reaches the message |

---

## 8. Pre-Design Checklist (#1136 §5)

| Item | Verdict |
| --- | --- |
| No new type mirroring an existing one | **Pass.** No new type at all; one private field, one extracted private method |
| No abstraction with one implementation | **Pass.** None added |
| No element justified by "we might need X later" | **Pass.** `Content-Location` was dropped on exactly this ground, with a measurement (§3.1) |
| DRY math quoted | **Pass.** §3.7: `20 × 2 = 40`, above threshold, so extracted |
| Existing systems first | **Pass.** Reuses `RedactQuery`, `SensitiveQueryParameters` and `HeaderDumpMode`; the vendor-header case is served by the existing `SensitiveHeaders` (§3.1) |
| Every knob has a named operator or environment difference | **Pass.** None of the three clauses applies, so no knob ships (§3.2) |
| Constants stay `static readonly`, named clearly | **Pass.** `urlValuedHeaders`, beside `redirectExcludedHeaders` |
| Can-it-be-deleted / merged / inlined | **Pass.** Ran on the set (deleted one entry), on the knob (deleted), and on the helper (inlining fails the DRY math) |
| Trade-offs named explicitly | **Pass.** §3.1 (coarser `SensitiveHeaders` escape route), §3.2 (unpinnable comparer), §3.3 (the `Full` asymmetry), §3.4 (`Link` left leaking) |
| Out-of-scope items listed | **Pass.** §4 |
| Predecessor designs marked | **N/A.** Nothing is superseded; the four related designs remain live and are cross-referenced |

---

## 9. Compatibility and release notes

**What breaks.** Nothing typed: no public signature changes, no member added or removed, and the set is private. What changes is the text of `HttpServiceException.Message` — a `Location` or `Referer` value carrying a credential query parameter now prints with that value replaced. Anyone parsing header values out of the message was never on a supported contract; `HttpServiceException.Response` still carries the real headers.

The narrow real cost: a diagnostic. A `Location` whose query legitimately carries `?design=…` or `?sortkey=…` now renders `<redacted>` there, through the same over-redaction the word set causes everywhere (#9939 §3.2). It is visible in the log that suffers it, which is the direction that trade was already settled in.

**Prose falsified by this change, patched in the same commit** (#114 addendum, 2026-08-27). Five statements asserted the gap this change closes, and a stale *negative* claim has no natural expiry:

1. `Pooshit.Http.csproj` release notes — *"a url that appears inside a header value … is not redacted by this"*.
2. `error-message-query-redaction.md` §4 — the same claim as an out-of-scope bullet.
3. `error-message-query-redaction.md` §7 — route 6, *"Open, out of scope, filed as #9940"*.
4. `error-message-query-redaction.md` §8 — the checklist row citing *"the `Location` route left open"*.
5. `error-message-query-redaction.md` §9 — the release-note draft mirroring statement 1.

The design-doc instances are marked closed inline rather than rewritten: the reasoning was correct on its date, and only its liveness is false. The two release-note instances are corrected outright, because they describe shipped behaviour to a consumer.

---

## 10. Implementation

1. `urlValuedHeaders` beside `redirectExcludedHeaders`.
2. Extract `RedactQuery(string)` from `DumpUrl`; `DumpUrl` becomes its caller.
3. `DumpHeader` gains the URL-valued branch, after the `SensitiveHeaders` branch, applying `RedactQuery` per value.
4. `HttpServiceHeaderUrlRedactionTests` — the axes of §6.1.
5. Version to `0.12.0-preview`; new release-note paragraph; patch the five statements of §9.
