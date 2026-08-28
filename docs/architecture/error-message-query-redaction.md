# Architectural Document: query-string redaction and body removal in `HttpServiceException` messages

> **Repo path:** `docs/architecture/error-message-query-redaction.md` (repository `telmengedar/Pooshit.Http`).
> **DiVoid:** source task **#9938** · driver analysis **#9937** · origin incident **#9559** (a Meta token found in a prod log) · consumer-side stopgap **#9702** / mamgo PR #895 · project **#2281** · repo map root **#8292** · `HttpService` **#8297** · `HttpOptions` **#8299** · `HttpServiceException` **#8300** · `HeaderDumpMode` **#9617** · redirect credential policy **#9633** · follow-up filed by this design **#9940**.
> **Sibling designs, neither superseded:** `docs/architecture/error-message-header-redaction.md` (#9117 — the header half of the same message) and `docs/architecture/redirect-credential-policy.md` (#9633 — why `SensitiveHeaders` is dual-purpose and must not be reused here).
> **Contracts cited as load-bearing:** Design Contracts **#1136** (§1 KISS/DRY/YAGNI, §2 existing systems first, §3 configurability is not free, §4 less is better, §5 checklist walked as §8 of this document, §6 anti-patterns) · Code Contracts **#114** (§0 KISS/DRY/YAGNI + the bounce rule, §13.1.1 guard axes) · **#1267** (DRY threshold math) · **#1184** (design only what was asked) · **#6836** (expose the real structures under their real names).
> **Baseline:** `master` @ `070692b`, tree clean. Package version on master: `0.10.0-preview`; this change ships `0.11.0-preview`.

---

## 1. Problem

### 1.1 The ask, verbatim (Toni, 2026-08-28)

> *"there is another gap with secret redaction - headers you implemented last time and it seems to be fine, but there is still a query surface. some calls put api tokens in query. I'm not sure how to handle it correctly, since query string keys are not as standardized as header keys."*

> *"Another minor thing while we're at it - the backend says that sometimes the body contains such secrets - the idea is to remove the body from the exception message. It should still be part of the exception, but if the application wants to do anything with it then it can look at the body which is carried by the exception and doesn't need to worry about the exception message."*

Two independent leaks into `HttpServiceException.Message` — the one string that every `logger.LogError(ex, …)` writes out by reflex. PR #5 closed the header leak in the same string and left both of these open.

### 1.2 Surface A — the request URL, verified in source at `070692b`

`DumpHeaders` is redaction-aware. The URL rendered beside it is not: `RequestUri` is interpolated **raw**, at four sites rather than one.

| # | Site | Expression |
|---|---|---|
| 1 | `HttpService.cs:225` | `$"Error sending request to '{response.RequestMessage?.RequestUri}' -> status {response.StatusCode}\n{DumpHeaders(…)}\n{responseBody}"` |
| 2 | `HttpService.cs:226` | the same message, body-less branch |
| 3 | `HttpService.cs:260` | `$"Error decoding response of '{response.RequestMessage?.RequestUri}'"` |
| 4 | `HttpService.cs:300` | `context` — `$"'{response.RequestMessage?.RequestUri}' (media type '{reported}', requested type '{typeof(T).Name}')"`, consumed at `:303` and `:312` |

Live rather than theoretical: Facebook's `paging.next` embeds `access_token` directly in the URL. PR #887 stopped the mamgo consumer *following* that URL; any `HttpServiceException` raised against such a URL still prints it verbatim. That is #9559 — a Meta system-user token in a production log — surviving in a second form.

### 1.3 Surface B — the response body

`HttpService.cs:224-226` delivers the body **twice**: structured on `.Body` (`HttpServiceException.cs:30`), where the application can inspect it and decide, and interpolated into `.Message`, which is what gets logged. The second delivery is a library decision that removes the application's choice. Only site 1 does this; `:303` and `:312` already pass the body as `body:` only and are correct as they stand.

### 1.4 Requirements

| # | Requirement |
|---|---|
| R1 | The response body never appears in `.Message`. It remains on `.Body`, unchanged, including its documented `null`-when-absent semantics. |
| R2 | A credential arriving in a query-string parameter does not reach `.Message`. |
| R3 | Non-secret parameter **values** stay legible. A log must still distinguish two failing calls to the same endpoint — this is the entire reason a name-set policy was chosen over redacting every value. |
| R4 | When the name policy misses a credential, the caller has a means to close the miss — the set is public and mutable, as `SensitiveHeaders` is. |
| R5 | Redaction keeps the parameter **name** and replaces only the **value**, exactly as the header rule does. |
| R6 | A URL the design cannot interpret is not rendered unredacted. |
| R7 | All four sites are covered, and a fifth site added later cannot silently miss the rule. |

---

## 2. Decisions already taken elsewhere — recorded, not re-opened

Two of the three questions were settled by Toni on 2026-08-28 and are recorded on #9938 with their reasoning. They are stated here because the design rests on them, not because they are being re-decided.

**T1 — the body is removed from the message outright.** No mode, no option, no redaction of it ever. A `BodyDumpMode` enum was proposed in #9937 and **withdrawn as over-engineered**: a `Full` member would exist only to re-duplicate data the caller already holds typed and unconcatenated on `.Body`, so it has no legitimate consumer. It is not symmetric with `HeaderDumpMode` — that enum earns its place because redacting named headers is a genuine *rendering* decision, whereas a body is one opaque string whose only choices are in-message or not. No redaction of the body either: Meta's *error* bodies carry no credentials and are what made #9559 diagnosable, while its *success* bodies embed `access_token`. Same vendor, same client, opposite answer — undecidable at library level, decidable by the caller.

**T2 — the query is redacted through a sensitive query-parameter **name set**, separate from `SensitiveHeaders`.** Chosen over three alternatives (redact-all-values with a `QueryDumpMode` knob; redact-all-values with no knob; drop the query entirely) because it is the only one that satisfies R3.

### 2.1 What this document exists to settle

Toni named the open problem himself: **query keys are not standardized the way header keys are.** `SensitiveHeaders` works because a fixed, well-known set of credential-bearing header names genuinely exists — eight names cover essentially all of HTTP authentication. There is no such registry for query parameters. `access_token`, `api_key`, `key`, `token`, `sig`, `client_secret` are conventions, and every vendor spells them slightly differently.

So the design owes a reasoned answer to *how a name is matched*, not merely *which names ship* — because exact-name matching transplanted from the header rule has a failure mode that is **silent**: an unlisted vendor name leaks and nothing says so. §3.2 is that answer.

---

## 3. Design

One new public member, two new private helpers, one collapsed throw. Nothing else moves.

### 3.1 Where the redaction lives — `DumpUrl`, and why the sites cannot be plumbed individually

All four sites stop interpolating `RequestUri` and instead render `{DumpUrl(response)}`. `DumpUrl` is a private instance method on `HttpService`, taking the `HttpResponseMessage` (which every one of the four sites already has in scope) and returning the string to print.

**DRY math (#1267, #1136 §1).** The redaction block — locate the query span, split it, test each name, rebuild — is ~20 lines. `block_size × site_count = 20 × 4 = 80`, far above the ~15-20 threshold. The extraction is mandatory, not stylistic. The named-helper test passes in one word: `DumpUrl`, sibling to the `DumpHeaders` that already stands beside it at site 1.

**Taking the response rather than the `Uri` is load-bearing, and it is what makes R7 enforceable.** #8297 records the precedent hazard in this exact file: the header work plumbed all eight `CheckHttpResponse` call sites correctly while the suite exercised one, and four `options`→`null` mutants survived a green suite. The remedy adopted there was a source-reading guard (`EveryStatusCheckCallSiteNamesTheOptions`). The same guard is available here only if the call sites are textually distinguishable from a raw interpolation — so with `DumpUrl(response)` at every site, **no interpolation hole in `HttpService.cs` names `RequestUri` at all**, and a guard can assert exactly that (§6.3). Had the helper taken a `Uri`, every site would still read `RequestUri` inside its braces and the guard would have to distinguish wrapped from unwrapped occurrences by regex — weaker, and weaker in the direction that already burned this file once.

**Two members, not one.** `DumpUrl` performs the rewrite; a second private predicate answers *is this parameter name a credential name*. This mirrors `DumpHeaders`/`DumpHeader` — collection helper over item predicate — and separates the **policy** (which names are credentials) from the **rendering** (how the URL is rebuilt), so the policy is testable on its own and the rewrite loop stays readable. The inline-it check (#1136 §4) was run: inlining costs ~6 lines inside a loop and merges two concerns that change for different reasons.

### 3.2 How a name is matched — plain substring, and why not segmentation or exact-name

**A query parameter is sensitive when its name *contains* any entry of the set.** No splitting, no word boundary, no anchor at either end: the entry may sit leading, medial or trailing, beside digits, beside punctuation, or inside a longer run of letters. Matching ignores case, supplied by `StringComparison.OrdinalIgnoreCase` at the comparison itself; the set's own `OrdinalIgnoreCase` comparer governs what a consumer's `Add` and `Remove` reach.

| Parameter name | Entry found | Verdict |
|---|---|---|
| `access_token` | `token`, trailing | redacted |
| `access_token_v2` | `token`, medial | redacted |
| `token_id` | `token`, leading | redacted |
| `X-Amz-Signature` | `sig` | redacted |
| `X-Amz-Security-Token` | `token` | redacted |
| `AWSAccessKeyId` | `key` | redacted |
| `apiKey` | `key`, ignoring case | redacted |
| `apikey` | `key` | redacted |
| `apikey2` | `key`, beside a digit | redacted |
| `accesskey` · `authtoken` · `secretkey` · `apitoken` | `key` / `token` | redacted |
| `client_secret` | `secret` | redacted |
| `sortkey` · `monkey` · `keyword` | `key` | **redacted — over-redaction, accepted below** |
| `author` | `auth` | **redacted — over-redaction, accepted below** |
| `assignee` · `design` | `sig` | **redacted — over-redaction, accepted below** |
| `page` · `limit` · `filter` · `format` | none | kept verbatim |

**Rejected: exact-name matching (the `SensitiveHeaders` rule transplanted).** Its failure mode, stated because the audit requires it: an unanticipated vendor spelling leaks **silently**. `SensitiveHeaders` can be exact because the header namespace has a registry and eight names exhaust it; the query namespace has none. Concretely, exact-name matching misses the single most common way a credential ends up in a URL — the pre-signed object-store URL. `X-Amz-Signature`, `X-Amz-Credential`, `X-Amz-Security-Token`, `AWSAccessKeyId`, `X-Goog-Signature` would each have to be enumerated, and Azure's SAS spells it `sig`. That shape is not hypothetical in this repo: the redirect design (#9633 §2.3) already reasons about it by name — *"a storage or CDN handoff answers with a pre-signed URL that carries its own authorisation in the query string."* Enumerating vendor query names would be a permanent maintenance surface for a namespace that has no registry — the same argument #9633 used to reject a public-suffix list.

Segment matching converts an **enumeration problem into a vocabulary problem**. Names are unbounded and vendor-specific; the words vendors build them out of are few and closed.

**Rejected: segment matching — tried, shipped in the first revision of this branch, and withdrawn.** The rule was *"a parameter is sensitive when any segment of its name is in the set"*, splitting at every non-alphanumeric character and at each lower-to-upper camel-case transition. It was chosen for precision and it delivers precision; it was also claimed here to *"dominate plain substring on both axes"* and to *"keep everything #895's anchors caught"*, and **both claims were false**. Segment matching loses recall on the all-lowercase concatenated compound, which has no boundary to split at: `secretkey`, `accesskey`, `authtoken`, `apitoken`, `bearertoken`, `privatekey`, `sessionkey`, `apikey2` and `key2` all rendered **verbatim** under it, and #895's substring `key` caught three of them. The set was patched once already to cover this shape — `apikey` and `accesstoken` shipped as whole-name entries precisely because no boundary reached them — and that patch is the tell: the rule had been converted back into the enumeration problem it existed to escape, one compound at a time, with every unanticipated compound failing **silently**. Withdrawn for that reason, not for a preference.

**No rule separates `secretkey` from `sortkey` without enumerating one of them.** This was looked for before accepting the cost below. The two names are structurally identical — a four-or-five letter run followed by `key`, no boundary, no case signal, no digit — and they differ only in that `secret` denotes a credential and `sort` does not. Every candidate collapses: an end-anchor matches both, a start-anchor matches `keyword` and neither of these, a decomposition rule splits `monkey` into `mon` + `key` as readily as `sortkey` into `sort` + `key`, and a rule that asks whether the *remainder* is a word needs a dictionary, which is enumeration with extra steps and a larger surface. The distinction is semantic, and the design has no semantics available. There is therefore a genuine trade here, and it is settled in the paragraph below rather than dissolved.

**Accepted cost, named rather than papered over: this rule over-redacts.** `sortkey`, `monkey`, `keyword`, `partition_key`, `sort_key`, `idempotency_key` and `public_key` all render `<redacted>` through `key`; `author` and `authority` through `auth`; `assignee`, `design`, `resign` and `consignment` through `sig`. `key` and `sig` are the two widest entries and neither is optional — Google's APIs authenticate with a bare `?key=` and Azure Storage SAS signs with a bare `?sig=`, the two most widespread credential-in-query parameters on the web. The cost is a **diagnostic**, and it is **visible**: an investigator reading `?keyword=<redacted>` can see exactly what happened and why. Under-redaction costs a **credential**, and it is invisible by construction — nothing in a log announces the value that was printed in full. The asymmetry is the whole argument, and it points one way.

**The knob a caller has for that cost is coarse, and that is a limit rather than a feature.** The only remedy for an unwanted redaction is `SensitiveQueryParameters.Remove(entry)`, and an entry is broad: removing `key` to make `?keyword=` legible also stops `secretkey`, `accesskey` and `AWSAccessKeyId` being redacted, and removing `sig` to make `?design=` legible drops `X-Amz-Signature` and the Azure SAS `sig` with it. A caller who removes `sig` and still wants signatures covered adds `signature` back explicitly. No per-name exclusion list is introduced for this: it would be a second enumeration surface with no named consumer (#1136 §6, YAGNI), and unlike the matching set it would fail in the under-redaction direction when it drifted.

**The percent-encoding limit the first revision recorded does not exist, and saying so is the point.** That revision stated: *"a parameter name that is still percent-encoded in the rendered URL (`access%5Ftoken`) segments to `access`, `5`, `Ftoken` and is missed."* The premise is false — the name is never still percent-encoded at that point. `DumpUrl` reads `RequestUri.ToString()`, and `Uri` normalises **unreserved** escapes (`ALPHA` / `DIGIT` / `-` `.` `_` `~`) back to their characters when it renders. A request built with `?access%5Ftoken=` renders as `?access_token=` and a request built with `?api%2Dkey=` renders as `?api-key=`, both verified end to end. The escaped spelling never reaches the matching rule under either rule, so the limit was never load-bearing and the advice it gave — *"a caller who meets one adds the encoded spelling to the set"* — would not have worked, because the encoded spelling is not what the rule is shown.

**What is left of it is narrow enough to state exactly.** An escape survives rendering only when it encodes a character outside the unreserved set, and every entry in the default set is pure ASCII letters. So the only spelling that can still break a credential word is a **reserved** character encoded *inside* the word — `access_to%2Fken`, a slash in the middle of a parameter name. That is not a spelling any writer produces. No decode step is introduced for it: the string being scanned is the same string being rendered (§3.4), and adding a decode pass would make matching operate on text the reader never sees. Recorded as a limit, not engineered around (#1136 §6).

### 3.3 The public surface — `HttpService.SensitiveQueryParameters`

A get-only `ISet<string>` backed by a `HashSet<string>` with `StringComparer.OrdinalIgnoreCase`, pre-populated with the default entries, mutable by a consumer at composition time. Deliberately the identical shape to `SensitiveHeaders` so a caller meets one concept twice rather than two concepts once — and, like it, it is read on every dump and is **not safe to mutate once the service has been used**. Its XML doc says so, as `SensitiveHeaders`' does.

**Naming (#6836).** The library's own vocabulary for this thing is `QueryParameter` with a `Name` (`Paths/QueryParameter.cs`), and Toni's words are *"query string keys"*. `SensitiveQueryParameters` is the repo's real name for the real structure, and parallels `SensitiveHeaders` — both read as *names of X treated as credentials*.

**The default set — seven entries, all credential words rather than parameter names:**

`token` · `key` · `secret` · `password` · `sig` · `auth` · `credential`

The membership rule, borrowed intact from the header design: **a word is in the default set when a parameter whose name contains it is, by that word's own meaning, a credential — not merely correlated with one.**

**A second rule follows from substring matching: no entry may contain another entry.** An entry that carries a shorter entry inside it can never produce a match the shorter one did not already produce, so it contributes nothing and misdescribes the rule to anyone reading the set. Three entries were removed on this ground when segmentation was withdrawn — `apikey` and `accesstoken`, which existed only to reach compounds that `key` and `token` now reach directly, and `signature`, which `sig` subsumes. Each removal is pinned by a probe: re-adding any of the three leaves the suite green, which is the evidence that it is dead membership rather than an argument that it is.

The consequence a caller must know is stated in §3.2: because `signature` is gone, removing `sig` removes signature coverage entirely rather than falling back to a longer entry.

**Two deliberate absences, both by the same rule:**

- **`session`.** A cookie is a session identifier by definition, which is why `Cookie` is in `SensitiveHeaders`. A query parameter named `session` is not — `?session=42` on a booking or checkout API is an opaque handle to a resource, frequently the very thing an investigator needs to see. Correlated with credentials, not a credential by definition. Excluded; a caller whose upstream puts a session secret in the query adds it.
- **`code`.** An OAuth authorization code is a one-time credential, but `code` is overwhelmingly a benign parameter — country code, error code, product code, discount code. Including it would redact the most common diagnostic parameter in the corpus to cover one specific flow. Excluded, and this is the direct analogue of the header design's `WWW-Authenticate` exclusion: the most useful field in the dump does not get redacted to cover a case the caller can name themselves.

**No per-call surface, and no `HttpOptions` member.** The header design's decision 2 applies unchanged: the mode is diagnostic verbosity and varies per call; the name set is security policy configured once at composition. Here there is no mode at all, so nothing is per-call.

**No knob, and the `BodyDumpMode` kill-test is why.** A hypothetical `QueryDumpMode` was run through the test that killed `BodyDumpMode` in #9937 — *name the legitimate consumer of each member*:

| Member | Legitimate consumer |
|---|---|
| `Full` (print the query verbatim) | None. Its only function is to put a credential back into a log. `Response.RequestMessage.RequestUri` already gives a caller the real URL by explicit access (§7 route 3). |
| `Omitted` (drop the query) | None. It destroys exactly the legibility that made this option win over the three alternatives (R3). |
| `Redacted` | The behaviour that ships unconditionally. |

Every member but the default fails, so the enum is a knob over a one-valued space. #1136 §3 independently requires a named operator or an environment difference before a knob ships; neither exists.

**And it is not on `IHttpService`.** Header design decision 3, unchanged: adding a member to a published interface is source-breaking for any external implementer, and buys nothing — this is composition-time policy, configured where the concrete type is visible anyway.

### 3.4 How the URL is rewritten — textual, in place, never re-parsed

`DumpUrl` renders the request URI's **string form** — the same `ToString()` that today's interpolation produces, so nothing outside a redacted value changes — and rewrites it as text:

1. No request message, or no request URI → **empty string**, exactly what today's null-propagating interpolation yields. No new sentinel, no new vocabulary.
2. No `?` in the string → returned unchanged. There is no query.
3. The **query span** runs from just after the first `?` to the first `#` at or after it, or to the end. Everything before the `?` and everything from the `#` onward is copied character-for-character.
4. The span is split at `&`. Each piece's **name** is the text before its first `=`; the value is everything after it. A piece with no `=` has no value and is emitted unchanged.
5. A sensitive name emits `<name>=<redacted>` — the original name text verbatim, the original value text dropped. Any other piece is emitted unchanged.
6. Prefix, `?`, rejoined span and fragment are reassembled.

**Invariant:** every character outside a redacted value is reproduced verbatim. `DumpUrl` never re-encodes, re-orders, de-duplicates or normalises anything.

**Rejected: parse the query into a name/value collection and re-serialise it** (`HttpUtility.ParseQueryString` or equivalent — available here, since `Paths/QueryParameters.cs` already uses `System.Web` on both target frameworks). Its failure mode is that **the logged URL stops being the URL that was sent**: a decode/re-encode round-trip normalises `+` against `%20`, collapses repeated names into comma-joined values, drops an empty trailing parameter, and re-escapes reserved characters by its own rules. An investigator comparing a logged URL against a vendor's access log would be comparing two different strings. A diagnostic that silently rewrites the thing it is diagnosing is worse than a coarser one that does not.

**R6 falls out by construction, not by a fallback branch.** There is no parse step, therefore no unparseable case. A relative URI, a URI with no authority, a malformed query, a bare `?`, a `#` before any `&` — all take the same textual path, and any piece the rules cannot resolve into a name/value pair is emitted with no value replaced only because it *has* no value. The one shape that hides a value from the rules is a parameter whose separator is not `&`: in `?a=1;access_token=x` the whole tail is read as the value of `a`, whose name matches nothing, so the token is rendered verbatim. That is a real leak and it is recorded as a limit rather than defended — HTML 4.01 B.2.2 once recommended `;` as an alternative separator, so it is not unattested, though it is long deprecated and this library itself only ever emits `&` (`Paths/QueryParameters.cs:153`). Closing it would mean splitting on a separator set the sender did not use, which changes what a benign value renders as; the trade was judged not worth taking for a separator no current writer emits. **Unparseable never means unredacted, because nothing is parsed** (#1136 §6: no defensive branch for a case the design's own shape has already answered).

### 3.5 The body — one throw instead of two

`CheckHttpResponse` (`:220-228`) collapses to a single `throw`, whose message is today's body-less form and whose `body:` argument carries the response body.

**One detail is not cosmetic and must be preserved:** `HttpServiceException.Body` is documented as *"null when the response had no body"* (`HttpServiceException.cs:28`), and today's two-branch shape delivers that by passing no `body:` argument on the empty branch. The collapsed throw must pass the body **when it is non-empty and `null` otherwise** — a single conditional expression, not a restored second branch. Passing `""` through would silently turn `Body` from `null` into an empty string for every bodiless error, which is a contract change nobody asked for (#1184).

Nothing else in the method moves: the status band, the stream read, and the `body:` value itself are untouched.

---

## 4. Scope

**In scope:** the four URL sites listed in §1.2; the two throws at `:224-226`; `HttpService.SensitiveQueryParameters` and the two private helpers; the coverage in §6; the version bump and the `PackageReleaseNotes` entry in §5.

**Out of scope, deliberately:**

- **`SensitiveHeaders` and everything it governs** — the header dump policy, `HeaderDumpMode`, and the cross-origin redirect strip (#9633). No entry moves between the two sets and neither set learns the other's matching rule.
- **The response body's *content***. It is never inspected, sniffed, parsed or scrubbed — T1, and the whole of Toni's principle. The library's involvement with the body ends at handing it to `.Body`.
- **Credentials in path segments.** Some APIs put a token in a path segment (`/v1/<token>/items`). This is out of scope, and it is worth saying *why* it is not merely deferred: **a path segment has no name.** Nothing in `/v1/abc123/items` declares which segment is a credential, so no name-set policy can reach it. The only mechanisms that could are value-shape guessing (rejected in §5.1 for the query, and strictly worse here) or dropping the path entirely, which erases the diagnostic that the URL exists to provide. A caller with path-borne credentials has no library-level remedy today and this design does not pretend otherwise.
- **A URL carried inside a dumped *header* value.** `Location`, `Content-Location` and `Referer` can each carry a query credential, and under `HeaderDumpMode.Redacted` their values are dumped verbatim because those names are not in `SensitiveHeaders`. This is a genuine open route in the same message — Meta's `paging.next` arrives exactly this way — and it is out of scope here because closing it means changing header *rendering*, which this task's scope explicitly excludes, and because it needs its own decision about which header names are URL-valued. Recorded in §7 route 6 and filed as **#9940** rather than folded in.
- **The other open Pooshit.Http tasks** — #9664, #8320, #8317, #9667, #9690, #9663 — which stay closed to this branch.

---

## 5. Decisions

**D1 — plain substring matching over segment matching and over exact-name.** §3.2. Segment matching was implemented first on this branch and withdrawn: it loses recall on all-lowercase concatenated compounds (`secretkey`, `accesskey`, `authtoken`, `apitoken`) with no boundary to split at, and each miss is silent. Exact-name matching fails worse and for the same reason — a silent miss of the pre-signed-URL family, the most common credential-in-query shape and one this repo has already reasoned about by name. The accepted price of substring matching is over-redaction of `sortkey`, `keyword`, `monkey`, `author`, `assignee` and `design`, which is a visible diagnostic loss rather than a silent credential loss; no rule separating those from `secretkey` exists without enumerating one side (§3.2). Reversal cost: cheap — the predicate is one private member and one expression, and the segmentation form is recorded above in full.

**D2 — the set is separate from `SensitiveHeaders`, and this is the one decision that would be actively harmful to get wrong.** Since PR #7, `SensitiveHeaders` governs **two** behaviours: error-message redaction *and* the cross-origin redirect strip (#9633 §2.2, map #9617). A word added there for query redaction — `token`, `key`, `auth`, `sig` — would silently change what travels to a redirect target, a security decision taken under a different trade-off (the *asymmetry of forgetting*, which weighed a leaked log line against a leaked credential and concluded that one list should govern both). Reusing it here would additionally import a **second matching rule** into that decision, since query names match as substrings and header names match exactly. Two lists, two names, no cross-talk — and §6 pins the absence of cross-talk in both directions.

**D3 — no value-shaped fallback: no length floor, no entropy test.** The brief asked whether one is warranted. It is not, and the reasons are concrete rather than aesthetic:

1. **It targets the wrong population.** High-entropy query values are overwhelmingly *not* credentials — opaque ids, GUIDs, hashes, and pagination cursors. Facebook's own `after=QVFIU…` cursor is a long, high-entropy, entirely non-secret value that sits beside the token this design exists to hide. Redacting it destroys precisely the legibility R3 exists to preserve.
2. **The caller cannot close it.** A name is a stable handle a consumer can add or remove; *"values longer than N with entropy above E"* offers nothing to grip per-parameter. It fails R4 by construction.
3. **It is non-deterministic across calls.** The same parameter would render redacted in one log line and legible in the next depending on the value it happened to carry — the worst possible property for a diagnostic, and one that makes a log impossible to reason about.
4. **It does not even close its own gap.** An eight-character API key, a PIN, or a short signature passes any floor that leaves ordinary ids alone.

Guessing at values is not a safety net under the name policy; it is a second, worse policy running in parallel. Reversal cost: cheap, but there is nothing here worth reversing to.

**D4 — the rewrite is textual, not a parse-and-reserialise.** §3.4. Rejected alternative's failure mode: the logged URL stops being the URL that was sent.

**D5 — the redaction placeholder is the literal `<redacted>`, the same token the header dump uses.** One message, one vocabulary; a reader learns the convention once. Rejected: a distinct query placeholder, which buys a distinction nobody needs. Rejected: dropping the whole `name=value` pair, which loses *"an `access_token` parameter was present"* — diagnostically valuable, not itself a secret, and the direct analogue of the header design's decision 4.

**D6 — version `0.10.0-preview` → `0.11.0-preview`, a minor bump.** The repo convention across four consecutive changes is *patch when behaviour is byte-identical for callers who opt into nothing, minor when it is not*. This change alters the default observable behaviour of **every** caller — the body leaves the message and query values are redacted with no opt-in — which is exactly the `0.8.0` → `0.9.0` precedent set by header redaction, and for exactly the same reason. The convention gives one answer here and it needs no argument.

**D7 — PR shape: one PR, both halves.** The global one-feature-one-PR rule (#1165) asks whether these are two units. They touch the *same two lines*: the collapse of the throws at `:224-226` and the `DumpUrl` substitution in the message those lines build, so a two-PR split has the second PR rewriting the line the first just wrote. They are also one statement to the consumer — *what reaches `.Message`* — carried by one release-note paragraph pair and one version bump. Recommended as one PR; PR decomposition is the orchestrator's call at briefing time, and this is a recommendation with its reason, not a ruling.

---

## 6. Coverage

New file `Http.Tests/HttpServiceQueryRedactionTests.cs`, following `HttpServiceHeaderRedactionTests.cs` in shape (a `SequenceHandler` over a canned error response; a `Capture` helper that throws through `Get<string>`). The body cases join `HttpServiceExceptionTests.cs`, which already owns `.Body` semantics.

**No existing test is invalidated.** Verified by reading the suite: no test asserts that the response body appears in `.Message`, and the only test URL carrying a query (`…/target?raw=1`, `HttpServiceRedirectTests`) has no sensitive segment. `HttpServiceExceptionTests.BodyNullWhenOmitted` pins the `null`-for-empty contract at constructor level, which is why §3.5 preserves it.

### 6.1 The redaction itself, with its duals (#114 §13.1.1)

| # | Case | Guards |
|---|---|---|
| 1 | `?access_token=…` on a failing call: the name is present, the value is absent, `<redacted>` is present | the fix (R2, R5) |
| 2 | **Dual —** a non-sensitive parameter beside it keeps its value **verbatim** | R3. Without it, an implementation that redacts the whole query passes every token-is-gone assertion |
| 3 | **Dual —** scheme, host, path and the status still appear in the message | R3. Without it, dropping the query wholesale passes cases 1 and 2 |
| 4 | `[TestCase]` fan: `access_token`, `X-Amz-Signature`, `X-Amz-Security-Token`, `X-Amz-Credential`, `AWSAccessKeyId`, `apiKey`, `key`, `sig`, `client_secret`, `password`, `x_auth` — all redacted | D1's coverage claim, entry by entry |
| 5 | **Dual —** `[TestCase]` fan: `sortkey`, `keyword`, `monkey`, `author`, `assignee`, `design` — all **redacted** | D1's accepted cost. Restoring a word boundary fails here rather than passing quietly, which is how the withdrawn segmentation rule is kept out |
| 6 | `?ACCESS_TOKEN=…` redacted | case-insensitivity of the name comparison |
| 7 | A caller-added entry (`vendorsecret`) is redacted; a caller-removed entry (`key`) is kept; a **cleared** set redacts nothing | R4, and that the set is the source of truth rather than a hard-coded list |
| 8 | **Separateness, both directions.** A name added only to `SensitiveHeaders` (`X-Tenant-Marker`) used as a *query* parameter stays verbatim; a word added only to `SensitiveQueryParameters` (`tenantmarker`) used as a *header* name stays verbatim in the dump | D2. This is the guard against a future edit quietly merging the two sets |

### 6.1.1 The predicate's axes, enumerated (#114 §13.1.1)

The matching rule is `∃ entry ∈ set : name contains entry, ignoring case`. Its inputs are exactly three — the **name**, the **set**, and the **comparison mode** — so the axis list below is complete by construction: the name varies by where a match sits, by case, by adjacent digits, by adjacent punctuation, and by whether it matches at all; the set varies by membership and by the casing a consumer reaches it with; the comparison varies by case-sensitivity. Every axis carries at least one case whose mutation is red.

| Axis | Cases | Mutation it kills |
|---|---|---|
| Position of the match in the name | `token_id` (leading), `access_token_v2`, `x_key_1`, `v4_signature` (medial), `access_token` (trailing) | anchoring the match at either end of the name; matching only a final segment |
| Case of the name | `ACCESS_TOKEN`, `apiKey`, `AWSAccessKeyId` | `OrdinalIgnoreCase` relaxed to `Ordinal` |
| Case of a consumer's entry | `Add("VENDORSECRET")` matches `vendorsecret`; `Remove("KEY")` reaches the shipped `key` | a comparison that folds only the name; the set's own comparer relaxed to `Ordinal` |
| Digits adjacent to the match | `key2`, `apikey2`, `sig1`, `signature_v4` | any rule treating a digit as part of a word boundary |
| Punctuation adjacent to the match, and its absence | `access_token`, `x_auth`, `client_secret` against `accesskey`, `authtoken`, `secretkey`, `apitoken`, `bearertoken`, `privatekey`, `sessionkey` | the withdrawn segmentation rule, which leaves every name in the second group legible |
| Percent-encoded separator inside the name | `access%5Ftoken` renders and redacts as `access_token`; `api%2Dkey` as `api-key` | the withdrawn limit above, by pinning that `Uri` renders the decoded form the rule actually sees |
| Whether the name matches at all | `page`, `limit`, `offset`, `filter`, `id`, `format` | redact-everything |
| Set-drivenness | added entry; removed entry; cleared set | a hard-coded fallback list; a constant predicate in either direction |
| Which text is matched | `?page=tokenholder&filter=secretsauce` stays verbatim | matching the whole `name=value` pair instead of the name, which would make redaction depend on value content (D3) |
| A name with no letters | `?=v`, `?--=v`, `?_=v` | a fault or a spurious match on an empty or punctuation-only name |

### 6.2 The rules §3.4 states, one case each

| # | Case | Expected |
|---|---|---|
| 9 | `?flag&access_token=x` | `flag` survives verbatim; the token is redacted (piece with no `=`) |
| 10 | `?sig=ab==cd&filter=a=b` | `sig` matched and its **whole** value replaced; `filter` keeps `a=b` verbatim (split at the *first* `=`) |
| 11 | `?access_token=&x=1` | renders `access_token=<redacted>`; an empty value is still a value |
| 12 | `?access_token=x#fragment` | the fragment is outside the query span and survives verbatim |
| 13 | Two sensitive parameters and one benign, in one query | both redacted, order and separators preserved |
| 14 | A URL with **no** query at all | rendered byte-identically to today |
| 14a | `?sig=x&q=a?b` | the query span starts at the **first** `?`; a later one inside a value does not move it and leave the credential ahead of it verbatim |

### 6.3 All four sites, and the fifth site that does not exist yet

`DumpUrl` has four callers after the change — one in `CheckHttpResponse` (the two throws having collapsed to one), one in `ReadResponse`'s JSON decode-failure path, and two through `context` in `DecodeUnknownMediaType`. Each is reached by a different response shape, so the fan is four integration cases, all against a URL carrying `?access_token=…` and all asserting the token is absent from `.Message`:

| # | Site | How it is reached |
|---|---|---|
| 15 | `CheckHttpResponse` | a 4xx status |
| 16 | `ReadResponse` JSON branch | `Content-Type: application/json` with a malformed body |
| 17 | `DecodeUnknownMediaType`, non-JSON-shaped | an unrecognised media type with a body that does not start `{` or `[` |
| 18 | `DecodeUnknownMediaType`, decode failure | an unrecognised media type with a JSON-shaped body the decoder rejects |

| # | Guard | Assertion |
|---|---|---|
| 19 | `EveryUrlInAMessageGoesThroughTheRedactor` — a source-reading guard over `HttpService.cs`, modelled directly on the existing `EveryStatusCheckCallSiteNamesTheOptions` | **No interpolated string in the file names `RequestUri`** (zero matches for an interpolation hole containing it), **and** `DumpUrl(response)` appears in at least three interpolation holes |
| 19a | `SourceCarriesNoLiteralShapeTheScannerCannotRead` | the file contains no raw string interpolation (`$"""`), which case 19's scanner cannot parse |

Case 19 is R7 and it is the one case the fan cannot supply: an overload or a message site added next year is structurally invisible to a hand-written fan and visible to this guard.

**The scanner's literal grammar, and why case 19a exists.** The guard reads interpolated literals with a regular expression, and a regular expression cannot parse C# string literals in general. The one it uses accepts the `$"…"`, `$@"…"` and `@$"…"` forms, tolerates an escaped `\"` and a verbatim `""`, and spans newlines, so a message site written in any of those shapes is seen. It cannot read a **raw** string literal (`$"""…"""`), whose delimiter length is variable — so rather than leave that as a silent blind spot in which case 19 would keep reporting success, case 19a fails the moment one appears in the file, and the remedy is to widen the scanner deliberately. The narrower predecessor of this scanner (`\$"[^"\n]*"`, single-line and quote-free) was verified to miss a `$@"…{RequestUri}…"` site and a multi-line one entirely; both are caught now, and both misses are recorded in the mutation matrix as the negative proof (#275) that the widening is load-bearing.

### 6.4 The body

| # | Case | Guards |
|---|---|---|
| 20 | A failing call with a body: `.Message` does **not** contain the body text; `.Body` **does** | R1 |
| 21 | **Dual —** the same call's message still carries the URL, the status and the header block | R1 without collateral damage — a "message stops carrying anything" implementation fails here |
| 22 | A failing call with an **empty** body, end to end: `.Body` is **`null`**, not `""` | §3.5's preserved contract, which is documented on the property but not pinned end to end today |

---

## 7. Escape-route audit

Every route by which a query-string credential could still reach the outside, with a verdict — the same exercise the header design ran, re-run against this surface.

| # | Route | Verdict |
|---|---|---|
| 1 | The four URL sites (§1.2) → `HttpServiceException.Message` | **Closed by this change.** The measured leak. |
| 2 | `Exception.ToString()` | **Closed by consequence.** It renders `Message` plus type, stack and inner exception; `Message` is now redacted. |
| 3 | `HttpServiceException.Response.RequestMessage.RequestUri` | **Deliberately left open**, exactly as the header design left `Response.RequestMessage.Headers` open. Reading it is an explicit act by a caller who wants the value; it does not reach a log by default, and it is the escape hatch that makes `Full`-style knobs unnecessary (§3.3). |
| 4 | `HttpServiceException.Body` | **Out of scope by T1.** The server's own payload; the library neither adds to nor classifies it. |
| 5 | `HttpServiceException`'s own default message (`HttpServiceException.cs:17`) | **Not a route.** `$"{StatusCode}: {ReasonPhrase}"` — no URL. |
| 6 | A **URL inside a dumped header value** — `Location`, `Content-Location`, `Referer` | **Open, out of scope, filed as #9940.** Under `HeaderDumpMode.Redacted` these names are not in `SensitiveHeaders`, so their values print verbatim, and Meta's `paging.next` arrives as exactly such a value. Closing it means changing header rendering and deciding which header names are URL-valued — a separate decision on a surface this task excludes. |
| 7 | The **inner exception** attached on a decode failure (`:260`, `:312`) | **Not a route the library controls.** The inner exception is the configured decoder's, and its message is the decoder's content — the same principle as the body: only the application knows what its decoder prints. The library adds no URL to it. |
| 8 | The redirect path (`HandleResponse`, `CreateRedirectRequest`) | **Not a message route.** It builds requests, not messages; a redirect that fails surfaces through route 1 and is redacted there. The cross-origin credential strip (#9633) is untouched. |
| 9 | Library logging | **Not a route.** No `ILogger`, no `Console.` anywhere in `Pooshit.Http/`. |

---

## 8. Pre-Design Checklist (#1136 §5)

Walked in the checklist's own order.

### 8.1 KISS / DRY / YAGNI

| Item | Verdict |
|---|---|
| No new type whose value-space mirrors an existing one (#114 §5.4) | **Pass.** No new enum, no new class, no new file in `Pooshit.Http/`. `SensitiveQueryParameters` is a second *instance* of an established shape, not a mirror of `SensitiveHeaders`' value-space — §5 D2 states why one set cannot serve both, and §7 case 8 pins it. |
| No new abstraction with one implementation | **Pass.** Two private methods on an existing class; no interface, no strategy, no pluggable policy. |
| Nothing justified by "we might need X later" | **Pass.** §3.3 kills `QueryDumpMode` by the same test that killed `BodyDumpMode` (#9937) — every member but the default has no legitimate consumer. §5 D3 kills the entropy fallback. §4 declines the path-segment case with a structural reason rather than deferring it vaguely. |
| No deprecation window, feature flag, compat shim | **Pass.** The change is immediate and total; there is no opt-out, which is what makes it a safe default (header design §4). |
| DRY math quoted on every inline-vs-extract decision | **Pass.** §3.1: `block_size × site_count = 20 × 4 = 80`, above the ~15-20 threshold, so the helper extracts. The one sub-decision that could have gone either way — folding the name predicate into `DumpUrl` — is decided in §3.1 on the merge/inline test with its reason stated, not on a paraphrase. |

### 8.2 Existing systems first

| Item | Verdict |
|---|---|
| Existing surface audited before adding one | **Pass.** `SensitiveHeaders` (rejected for reuse, §5 D2, with the #9633 reason); `HeaderDumpMode`/`HttpOptions` (no member added, §3.3); `Paths/QueryParameters` (a *builder* for outgoing query strings — it constructs and encodes, it does not parse an existing URL, so it is not the surface this needs; §3.4 additionally rejects parse-and-reserialise on its own merits); `Response.RequestMessage` (already the caller's escape hatch, §7 route 3). |
| A new layer names the concrete reason it cannot live on an existing surface | **N/A** — no new layer. |
| New persisted data enables a named 4-week decision | **N/A** — no data layer in this repo. |
| Consumer chain recursed on anything justified by "an existing reader projects it" | **N/A** — nothing here is justified that way. |

### 8.3 Configurability

| Item | Verdict |
|---|---|
| Every knob has a named operator or environment difference | **Pass by removal.** No knob ships. §3.3 records that no member of a `QueryDumpMode` could name a consumer, which is exactly why. The one configurable surface — the name set — is not a tuning knob but the security policy itself, and it is configurable for the reason R4 states: the namespace has no registry, so the default cannot be complete. |
| Telemetry-then-tune knobs come with a filed tuning task | **N/A** — none. |
| Magic values that need not vary stay `const` | **Pass.** `<redacted>` is the existing literal, reused (§5 D5); the matching rule has no thresholds, which is a direct consequence of D3 rejecting length and entropy. |

### 8.4 Less is better

| Item | Verdict |
|---|---|
| Can-it-be-deleted / merged / inlined run on every element | **Pass, and one element nearly failed.** *Deleted:* the deletion test was run again in the second revision and it removed something — the segment-splitting predicate went, replaced by one substring expression (§3.2). Deleting the remaining predicate leaves exact-name matching, whose named cost is a silent miss of the pre-signed-URL family, so it stays. *Merged:* the redaction is merged into the four existing message sites through one helper rather than a new rendering stage. *Inlined:* the name predicate could be inlined into `DumpUrl`; §3.1 keeps it separate for a stated reason (policy vs. rendering, and the `DumpHeaders`/`DumpHeader` precedent). |
| Trade-offs named explicitly | **Pass.** §3.2 (`key` and `sig` over-redact `sortkey`, `keyword`, `author`, `assignee`, `design`), §3.2 (the coarse removal knob; the narrowed percent-encoding limit), §3.3 (`session` and `code` excluded), §3.4 (the `;` separator leak), §4 (path segments unaddressable), §7 route 6 (the `Location` route left open), §5 D3 (no value-shape safety net). |
| Radical-clean shape where the existing surface has no consumer | **Pass.** T1's body removal is the radical-clean choice — outright removal, no mode, no compromise "truncated body" middle shape, which is the compromise-shapes anti-pattern in its exact form. |
| Reader inventories cover string literals as well as AST references | **Pass.** The four sites are string interpolations, and §6.3 case 19 is a *source-level* guard rather than an AST-level one for precisely that reason. |
| Carrier-swap tables enumerate every affected site | **Pass.** §1.2 enumerates all four, and §6.3 pins each one individually plus the fifth that does not exist yet. |

### 8.5 Data deliverables

**N/A** — no SQL, no schema, no migration; this repo has no data layer.

### 8.6 Document discipline

| Item | Verdict |
|---|---|
| Cites #114 and #1136 as load-bearing | **Pass** — header block. |
| Reader / scope inventories explicit | **Pass** — §1.2, §4. |
| Out-of-scope listed explicitly rather than merely absent | **Pass** — §4, including the path-segment question the brief asked to be settled either way. |
| No multi-paragraph rationale for things that obviously stay | **Pass.** |
| Superseded predecessors get a banner | **N/A.** `error-message-header-redaction.md` and `redirect-credential-policy.md` are **extended, not superseded** — this design changes nothing either of them decided, and §4 lists their subject matter as out of scope. Neither gets a banner. |

---

## 9. Compatibility and release notes

### 9.1 What breaks

**No signature changes.** No interface member is added, removed or re-typed; `DumpUrl` and the name predicate are private; `SensitiveQueryParameters` is additive.

| Caller shape | Change |
|---|---|
| Logs `ex.Message` and reads the response body out of it | **Broken, deliberately.** The body is gone from the message. The remedy is to log `ex.Body`, which is exactly the application-level decision this hands back. |
| Logs `ex.Message` and reads a credential out of the logged URL | **Broken, deliberately.** That is the disclosure being removed. `ex.Response.RequestMessage.RequestUri` still carries the real URL. |
| Parses parameter values out of `ex.Message` | **Broken for the ten default words.** Never a supported contract; `Response` remains available. |
| Everyone else | **Unaffected.** URL, status, header block, `.Body`, `.Response` all behave as before, and a URL with no sensitive parameter renders byte-identically. |

### 9.2 What the `PackageReleaseNotes` entry must convey

The block on `master` is **cumulative across the untagged releases** (four commits on 2026-08-26 established this) and its paragraphs are ordered by *how likely the change is to alter what a working caller observes*. Two new paragraphs are added, placed **after** the cross-origin-strip paragraph — that one can turn a `200` into a `401`, while these two change only what is written to a log — and before the media-type paragraph.

**Paragraph one — the body.** `HttpServiceException.Message` no longer contains the response body. The body is unchanged on `HttpServiceException.Body`, where it always was, and a caller who logged the message and relied on seeing the body logs `.Body` instead. There is no option and no way to put it back, and that is the point: an arbitrary vendor payload is something only the application can judge safe to write to a log, so the library stops making that judgement on its behalf. Unaffected: `.Body` itself, including that it is still `null` when the response carried no body; the url, the status and the header block in the message; every call that does not fail.

**Paragraph two — the query string.** Query parameter values whose name carries a credential word are replaced by `<redacted>` in the message. The parameter name survives and so does every other parameter's value, so two failing calls to the same endpoint still read differently in a log. The name set is `HttpService.SensitiveQueryParameters`, open for a consumer to extend or reduce — configure it before the service issues its first request, as with `SensitiveHeaders` — and it ships with `token`, `key`, `secret`, `password`, `sig`, `auth` and `credential`. A name is sensitive when it **contains** an entry anywhere inside it, compared ignoring case, with no word splitting and no anchor at either end, so `access_token`, `X-Amz-Signature`, `X-Amz-Security-Token`, `AWSAccessKeyId`, `apiKey`, `accesskey`, `secretkey`, `authtoken`, `apitoken` and `apikey2` are all covered without being listed. Add the *word*, not a parameter name: `sig` covers `x_vendor_sig` and `X-Amz-Signature` alike, whereas an entry spelled `x_vendor_sig` matches only names containing that whole string. No entry contains another — `signature`, `apikey` and `accesstoken` would each be reached by `sig`, `key` and `token` and are therefore not shipped, which matters if you reduce the set: removing `sig` removes signature coverage outright rather than falling back to a longer entry, so add `signature` back if you do. **This set is separate from `SensitiveHeaders` and neither affects the other** — `SensitiveHeaders` also governs which headers are dropped on a cross-origin redirect, and a word added for query redaction must not change what travels to a redirect target. The rule is deliberately biased toward redacting too much, because too little is silent: a parameter named `partition_key`, `sort_key`, `idempotency_key`, `sortkey`, `monkey` or `keyword` now renders `<redacted>` through `key`, `author` and `authority` through `auth`, and `assignee`, `design` and `resign` through `sig`. That costs a diagnostic and you can see it happen; the knob is coarse, since removing `key` to read `keyword` also stops `secretkey` and `AWSAccessKeyId` being redacted. The direction that carries the real risk is the other one, and it is invisible: a credential whose parameter name contains none of the seven words is printed in full, and nothing in the log says so — a vendor spelling such as `x_vendor_nonce` or a name that percent-encodes a reserved character inside the credential word itself, `access_to%2Fken`, both escape, and the remedy is to add the spelling before the service issues its first request. `session` and `code` are deliberately absent — both are far more often ordinary identifiers than credentials. Unaffected: the url's scheme, host and path; a url with no query; the status; `HttpServiceException.Body` and `HttpServiceException.Response`, from which `Response.RequestMessage.RequestUri` still yields the real url for a caller who wants it. Two limits worth naming: a url that appears inside a *header* value — a `Location` pointing at a pre-signed target, say — is dumped as part of the header block and is not redacted by this, and a query written with the long-deprecated semicolon separator, `?a=1;access_token=x`, is read as a single parameter named `a` and is not redacted either.

---

## 10. Implementation order

1. **`SensitiveQueryParameters`** on `HttpService` — the set, its seven entries, its XML doc (including the not-safe-to-mutate-once-used clause and the add-the-word-not-the-name clause). No behaviour yet.
2. **The name predicate** — the case-insensitive substring test against the set (§3.2).
3. **`DumpUrl`** — the textual rewrite (§3.4).
4. **Substitute all four sites** to render `{DumpUrl(response)}`, removing every `RequestUri` interpolation from `HttpService.cs` (§1.2, §3.1).
5. **Collapse the two throws** at `:224-226` into one, preserving the `null`-for-empty `body:` argument (§3.5). The suite is green at this point — no existing test asserts the body is in the message.
6. **Coverage** — §6.1 through §6.4, in that order. Cases 2, 3, 5, 8, 21 and 22 are the duals; without them the suite is green for implementations this design explicitly rejects. §6.1.1 enumerates the predicate's axes separately, because a dual constrains the rule only where the mutations varied (#114 §13.1.1) and the first revision of this branch shipped two surviving mutants inside the predicate for exactly that reason.
7. **Version and release notes** — `0.11.0-preview` (§5 D6) and the two paragraphs from §9.2, placed per the ordering rule stated there.

---

## 11. Open questions

None blocking. Two are cheap to change and are Toni's call:

1. **The default set's membership.** Ten words, with `session` and `code` excluded for the reasons in §3.3. If either is wanted in, or `pwd`/`passwd` added, it is one line and no design changes.
2. **Patch or minor.** §5 D6 reads the repo's own convention literally and lands on minor (`0.11.0-preview`); the convention gives an unambiguous answer here, so this is a packaging confirmation rather than an open fork.
