# Architectural Document: the suite runs on both shipped runtimes

> **Repo path:** `docs/architecture/test-suite-runs-on-both-shipped-runtimes.md` (repository `telmengedar/Pooshit.Http`).
> **Node:** **#14647** — this document's own DiVoid node, maintained as a byte copy of this file.
> **DiVoid:** source task **#9965** · project **#2281** · repo map root **#8292** · `HttpService` **#8297** (the BCL-adjacent hazard list, and the three reconciliation entries that already record this gap) · the measurement that surfaced it **#9964** §3 · the PR it was found under **#9938** / **#9939**.
> **Predecessors, none superseded:** **#14516** / `verb-preserving-redirect.md` (the 307/308 capability whose net48 absence this document measures) · **#9939** / `error-message-query-redaction.md` (the `Uri` divergence, recorded in its hazard list and pinned by nothing) · **#10046** / `path-segment-encoding.md` (nominated as a third carrier in §3.6 and **falsified** in §3.7).
> **Contracts cited as load-bearing:** Design Contracts **#1136** (§1 KISS/DRY/YAGNI, §2 existing systems first, §3 configurability is not free, §4 less is better, §5 checklist including the measurement-discipline rows, §6 anti-patterns) · Code Contracts **#114** §0.
> **Baseline:** `master` @ `1457df8`, tree clean. Version `0.15.1-preview`. Suite **409 green, 0 failed, 0 skipped**, `net8.0` Release — measured, §3.1.

---

## TL;DR

**The task's two hardest questions are now answered by measurement rather than by reasoning.** #9965 asked whether multi-targeting `Http.Tests` is disproportionate, and warned that running every guard on both runtimes manufactures false guards at scale. A probe against a byte-identical copy of `master` @ `1457df8` answers both:

- **The cost is three lines of project configuration and one shared constant.** `<LangVersion>latest</LangVersion>`, plus a `net48`-conditional `<Reference Include="System.Net.Http"/>` and `<PackageReference Include="System.Memory" Version="4.5.5"/>`. **2 of 23 files need a source edit to compile, at 35 mechanical sites, all the same token** — `HttpStatusCode.PermanentRedirect`, which does not exist in the `net48` enum. Nothing else in the suite is `net48`-hostile.
- **The "at scale" worry does not survive contact. `net48` discovers all 409 guards and 395 of them pass.** Exactly **14** fail, **all in one file, all from one cause**: .NET Framework's `HttpClient` disposes the request content once a send completes, so the verb-preserving 307/308 hop cannot replay a body. The false-guard hazard is 14 guards wide, not 409.

**Decision. Multi-target `Http.Tests` to `net8.0;net48`. All 409 run on both. Nothing is loosened.** The 14 become `#if NET` and are answered on the Framework side by **six** purpose-written guards — not fourteen restatements of one fact.

**The strongest argument in the file is now measured on this repo's own fixtures.** A capability the library shipped in `0.14.0-preview` is **absent on half the shipped surface**, and the suite is green. It is the first thing a `net48` run says.

**Three findings the task could not have contained.**

1. **The layer question is answered, and it answers the easy way.** The 14 failures arrive through `SequenceHandler` — a hand-written `HttpMessageHandler` that never touches a socket. So the disposal decision sits **at or above `HttpClient`**, not in the platform handler, and every existing fixture sees it. Had it been the other way, every `SequenceHandler`-based 307/308 guard would have run **green on `net48` while the capability was absent** — a guard that actively denies the defect (§9).
2. **The control could have come back negative and did not.** Every **bodyless** redirect guard passes on `net48`. So the discriminator is the body, not the status — which is what distinguishes "content disposal" from "308 is broken on Framework" (§9.2).
3. **The `Uri` family produced zero failures**, including the 21 guards in `RestPathEncodingTests` that I had nominated as a candidate before the run. The nomination was **wrong**, and the reason is the finding: the carrier is present, the **inputs never reach the divergent region**. The suite's most `Uri`-looking guard, `PercentEncodedSeparatorInName_DecodedAndRedacted`, uses only **unreserved** escapes. The reserved-escape divergence #9965 and #8297 both record is pinned by **no guard at all**, in either direction (§3.5, §3.7).

**CI.** `build-test` **stays required and stays on `ubuntu-latest`**, narrowed to `-f net8.0`. A second job **`build-test-net48` on `windows-latest` is added and is also required**. Jobs run in parallel, so the cost is the slower job's wall clock, not a sum. Mono is rejected twice over — measured (the net4x test host is a Windows PE and the NuGet host package's `lib/net462` is an empty placeholder) and reasoned (Mono's `HttpClient` and `Uri` are Mono's own, so a green Mono run would prove nothing about Framework, which is the forwarding-layer error #1136 §5 names).

**Considered and dropped: a ratchet counting `#if NETFRAMEWORK` regions.** §8.5 argues it does not earn its keep once the divergence surface is six named guards in one file, and names the mechanism that actually defends the trap.

---

## 1. Problem

`Pooshit.Http` ships `netstandard2.0;net8.0`. `Http.Tests` targets `net8.0`. All 409 guards therefore pin behaviour on exactly one of the two runtimes the package is consumed from.

For most of the suite this is harmless: the assertions are about the library's own logic over inputs the test supplied, and that logic is one source compiled twice. It stops being harmless wherever a guard's outcome passes through a BCL type — and the library's whole surface is `HttpClient`, `Uri`, `HttpContent` and string encoding, the densest such area in the BCL.

Two failure directions, not symmetric:

| direction | shape | status after §3 |
|---|---|---|
| **D1 — a capability absent on net48, suite green** | the library promises something the Framework runtime cannot do; nothing fails | **live, and now measured on this repo's fixtures.** The 307/308 body-carrying hop; 14 guards. |
| **D2 — a guard asserts a net8-only outcome** | the assertion is a claim about behaviour that differs for half of consumers | **latent, and measured to be currently empty.** Zero of 409 guards assert a divergent `Uri` outcome — because none of them supplies an input that reaches one. |

D1 is the one that matters, and it is exactly the shape #9965 describes: a guard that cannot fail on the consumer that would have broken is decoration for that consumer, and it reads like a real guard. D2 turns out to be a *gap* rather than a *wrong assertion* — nobody wrote a guard there at all.

**The requirement, stated so it can be checked:** *a behavioural difference between the two shipped runtimes must not be invisible to the suite.* Not "the suite runs twice" — that is a mechanism, chosen in §5.

## 2. Scope

### 2.1 In scope

1. Multi-targeting `Http.Tests` to `net8.0;net48` and the project configuration that requires (§3.2).
2. The `HttpStatusCode.PermanentRedirect` shim at 35 sites (§5.4).
3. Splitting the 14 measured divergences per runtime (§8.3), and the six Framework-side guards that replace them (§11.1).
4. The two `Uri` guards that do not exist in either direction (§11.2).
5. The CI change, including which status checks are required on `master` (§7).

### 2.2 Out of scope — enumerated, not omitted

| # | Out of scope | Why |
|---|---|---|
| O1 | Adding `net48` to **`Pooshit.Http`**'s `TargetFrameworks`. | Framework consumers get the `netstandard2.0` asset; that asset is the thing under test. A third shipped TFM is a packaging change with its own consumers. |
| O2 | **Fixing** any divergence this finds. | This task is the instrument. The 307/308 absence on `net48` is **pinned**, not repaired — repairing it means buffering the request content before the hop, a behaviour change to the redirect path that belongs with #8323. |
| O3 | `net462`, `net472`, or any Framework TFM other than `net48`. | `netstandard2.0`'s floor is `net461`, so the shipped surface is wider than what gets tested. One Framework TFM is the cost-effective answer; a matrix over four is #1136 §3 configurability with no named operator. `net48` is chosen because it is what #9964 and the `0.15.1-preview` release note measured. Open as Q2. |
| O4 | .NET 9 / .NET 10. | Same family. The divergence is Framework-vs-Core, not Core-vs-Core. |
| O5 | **WebAssembly.** | #8297 records a real WASM handler split (the bare client follows redirects itself). It is a **third** runtime, is not reachable by a TFM on this project, and is **not** narrowed by this change. Named so nobody reads "both shipped runtimes" as "all runtimes". |
| O6 | Mono, Wine, or any cross-platform `net48` execution. | Rejected in §7.3 on measured and reasoned grounds, not deferred. |
| O7 | Coverage collection (`coverlet.collector`) on the `net48` target. | The `net48` job runs tests. Coverage stays where it is. |
| O8 | Rewriting existing `net8.0` assertions that are correct on `net8.0`. | A guard that is right about `net8.0` keeps its identifier and its assertion; divergence is expressed by *adding* the other side. |
| O9 | A release, a version bump, or release-note text. | No production line moves. §3.4 confirms the `0.15.1-preview` note rather than contradicting it, so there is nothing to correct. |
| O10 | The three source-scanning guards. | TFM-independent (§3.8). They run twice, cost three duplicate executions and no correctness, and are left alone. |
| O11 | Making the `net48` *build* run on the Linux job. | Measured possible (§3.6) and deliberately not done: it would duplicate what the Windows job already proves, on a target the Windows job must build anyway. |

## 3. What I measured

§3.1, §3.5, §3.8 and §3.9 were run or read by me on `master` @ `1457df8`. §3.2–§3.4, §3.6 and §3.7 come from a probe against a byte-identical copy of the same tree, verified by recursive `md5sum` over `Pooshit.Http` + `Http.Tests` excluding `bin`, `obj` and `.claude`. Nothing in this section is inferred; §4 holds everything that is.

### 3.1 The baseline

`dotnet test Http.Tests/Http.Tests.csproj -c Release` → **409 passed, 0 failed, 0 skipped**, `.NETCoreApp,Version=v8.0`. One pre-existing warning, `CS8618` at `QueryParametersTests.cs(8,25)`.

The probe reproduced 409/0/0 on its copy before anything was changed. A copy that does not is a broken instrument, not a result.

The task says "~420". The measured number is **409**. I use the measured one throughout.

### 3.2 The build cost

Four configurations, each error count taken from MSBuild's authoritative summary line rather than from a grep of the log (minimal verbosity prints each error twice, inline and in the summary — a raw grep double-counts).

| configuration | errors | distinct codes |
|---|---|---|
| `<TargetFrameworks>net8.0;net48</TargetFrameworks>`, nothing else | **1** | `CS8630 ×1` — `Nullable: Enable` needs C# ≥ 8; `net48` defaults to C# 7.3 |
| `+ Microsoft.NETFramework.ReferenceAssemblies 1.0.3` | **1** | identical. **The package changes nothing** — see below |
| `+ <LangVersion>latest</LangVersion>` | **45** | `CS1069 ×33`, `CS0103 ×10`, `CS0012 ×2` — all one cause: on Framework, `System.Net.Http` is a **separate assembly**, not part of `System.dll` |
| `+ <Reference Include="System.Net.Http"/>` (net48-conditional) | **74** | `CS0518 ×39` (`ReadOnlySpan<T>` — the 39 `"…"u8` literals) · `CS0117 ×35` (`HttpStatusCode.PermanentRedirect`) |
| `+ <PackageReference Include="System.Memory" Version="4.5.5"/>` (net48-conditional) | **35** | `CS0117 ×35`, 100% `HttpStatusCode.PermanentRedirect` |

**The cheapest configuration that builds both targets with 0 errors:**

```xml
<TargetFrameworks>net8.0;net48</TargetFrameworks>
<LangVersion>latest</LangVersion>
<ImplicitUsings>enable</ImplicitUsings>   <!-- unchanged -->
<Nullable>enable</Nullable>               <!-- unchanged -->
...
<ItemGroup Condition="'$(TargetFramework)' == 'net48'">
    <Reference Include="System.Net.Http"/>
    <PackageReference Include="System.Memory" Version="4.5.5"/>
</ItemGroup>
```

Three findings inside that:

- **`Microsoft.NETFramework.ReferenceAssemblies` is already there.** The probe's `obj/project.assets.json` resolved `Microsoft.NETFramework.ReferenceAssemblies.net48/1.0.3` **before any explicit reference was added**, and removing the explicit reference at the end left both targets building. The SDK injects it because no targeting pack is installed on the machine. There is **no** `C:\Program Files (x86)\Reference Assemblies\...\.NETFramework` directory on this machine and `NETSDK1045`/`MSB3644` never appeared. The task's report that a `net48` build "worked first try using the package" is confirmed, with the correction that the package need not be named.
- **`<LangVersion>latest</LangVersion>` is mandatory and is not negotiable down.** With it removed *and* `Nullable` disabled, the build produces **45 `CS8370` errors**: file-scoped namespaces need C# 10, global usings need C# 10, nullable reference types need C# 8, primary constructors need C# 12. `net48` defaults to C# 7.3 and no other setting avoids that.
- **`System.Net.Http` is a `<Reference>`, not a `<PackageReference>`.** The NuGet package of that name is the wrong tool and is not needed.

`Microsoft.Bcl.AsyncInterfaces` and `System.Threading.Tasks.Extensions` were **not** needed; zero errors pointed at `IAsyncEnumerable`/`IAsyncDisposable`, and the build reached zero without them. `<ImplicitUsings>` must stay enabled, but that is **symmetric** — removing it breaks `net8.0` with the same 8 errors it breaks `net48` with, so it is not part of this decision.

### 3.3 The source cost to compile: 2 files, 35 sites, one token

| file | sites | cause |
|---|---|---|
| `Http.Tests/HttpServiceRedirectTests.cs` | 32 | `HttpStatusCode.PermanentRedirect` |
| `Http.Tests/HttpServiceStatusBandTests.cs` | 3 (lines 136, 137, 146) | same |

35 sites = the 35 `CS0117` errors exactly. **21 of 23 files compile on `net48` unchanged.** Every other construct the suite uses — 39 UTF-8 string literals, file-scoped namespaces, primary constructors, global usings, target-typed `new`, collection expressions, nullable annotations, `using` declarations — is resolved by project settings alone.

`HttpStatusCode.PermanentRedirect` (308) arrived in .NET Core 2.0 / netstandard2.1; Framework 4.8's enum stops at `TemporaryRedirect` (307). **This is the same wall the library already hit**: #8297 records `HttpService`'s private `const HttpStatusCode permanentRedirect = (HttpStatusCode)308`, with a doc comment whose whole job is to make a tidy-up back to the framework symbol look wrong. The test project now inherits the same constraint. §5.4 is the answer.

### 3.4 The `net48` run: 409 discovered, 395 pass, 14 fail

```
dotnet test Http.Tests/Http.Tests.csproj -c Release
  net8.0 : failed 0,  passed 409, skipped 0, total 409
  net48  : failed 14, passed 395, skipped 0, total 409
```

`net48` **discovers and executes the full suite** — same 409, nothing silently skipped. Host: the SDK's `TestHostNetFramework`.

**All 14 failures are in `HttpServiceRedirectTests.cs`, and all 14 are one cause.** On .NET Framework, `HttpClient` disposes the request content object once a send completes, so the verb-preserving hop cannot replay the body.

**Nine assert the capability works** and get `HttpServiceException: … the request body cannot be sent a second time`:

`Post307_HopRepeatsPostWithSameBody` · `Post308_HopRepeatsPostWithSameBody` · `Post308_BodyBytesAreIdenticalOnBothHops` · `Post308_ContentTypeRidesTheHop` · `Post308_CrossOrigin_AuthorizationStrippedWhileBodyRides` · `Post308_ExpectContinueSurvivesTheBodyCarryingHop` · `Post308_UrlProcessorIsAppliedBeforeUriResolution` · `Post308_SupersededRedirectResponseIsDisposed` · `Post308_NoLocation_UrlProcessorSynthesisesTarget_HopUsesIt`

**Five assert a specific failure mode and get a different one first:**

| guard | expected | actually arrived on net48 |
|---|---|---|
| `Post308_NonSeekableStreamBody_ThrowsHttpServiceException` | inner-inner `Is.TypeOf<InvalidOperationException>` | `ObjectDisposedException` (`StreamContent`) — a *subclass*, which `Is.TypeOf` rejects |
| `Post308_HopFailsWithAnUnrelatedTransportError_DisposesTheSupersededResponse` | `HttpRequestException` | `HttpServiceException: … cannot be sent a second time` — **disposal pre-empts the injected transport error** |
| `Post308_HopFailsWithAnUnrelatedTransportError_IsNotReportedAsAnUnreplayableBody` | `HttpRequestException` | same |
| `Post308_ExhaustionSignalledThreeLevelsDeep_IsTranslated` | `HttpRequestException` | `ObjectDisposedException` (`SingleUseContent`) |
| `Post308_ExhaustionSignalledAtTheOutermostLevel_IsTranslated` | `Is.TypeOf<InvalidOperationException>` | `HttpRequestException → ObjectDisposedException` (`StringContent`) |

Two things fall out of that second table and neither is obvious:

- **`ObjectDisposedException` derives from `InvalidOperationException`**, which is why `IsConsumedContentFailure`'s chain walk still translates correctly and nine of the fourteen produce the library's own exception. The failures are the tests' **exact-type** assertions, not the library mis-handling anything.
- **On Framework, an unrelated transport error on a body-carrying hop is unobservable.** Disposal always wins first. That is a genuine behavioural property, it is nowhere in the docs, and §11.1 pins it.

**This independently reproduces the `0.15.1-preview` release note** (*"HttpClient disposes the request content once a send completes … a buffered body and a seekable stream fail exactly as a non-seekable one does"*) on this repo's own fixtures. §2.2 O9 stands: nothing in the note needs correcting.

### 3.5 The `Uri` guard everyone would assume covers this does not

`HttpServiceQueryRedactionTests.PercentEncodedSeparatorInName_DecodedAndRedacted` is the suite's only guard on percent-encoded parameter names. Its cases are `access%5Ftoken` and `api%2Dkey` — **`%5F` is `_`, `%2D` is `-`, both unreserved**, and #9964 §3 measured unreserved escapes as normalising identically on both runtimes.

It passes on `net48`. And the reserved-escape divergence — `?access_to%2Fken=` rendering `?access_to/ken=` on Framework and `?access_to%2Fken=` on .NET 8 — **is pinned by nothing anywhere in the repo, in either direction.** Neither is the concrete leak #8297 records from it: `?access_token%26x=SECRET` splits into two parameters on Framework and prints the secret.

Both #9965 and #8297 name this divergence in prose. Neither produced a guard. §11.2 creates them.

### 3.6 The Linux build is possible and the Linux run is not

**Build — measured reference graph, reasoned conclusion.** A `-v detailed` `net48` build resolves **every** compile reference out of the NuGet cache, with **zero** hits on any machine-installed targeting-pack directory: 12 assemblies from `microsoft.netframework.referenceassemblies.net48/1.0.3`, plus `system.memory`, `system.buffers`, `system.numerics.vectors`, `system.runtime.compilerservices.unsafe`, `nunit/lib/net45`, `pooshit.json/lib/netstandard2.0`, and the project's own `netstandard2.0` output. There are no `.resx` files in either project, so no `AL.exe` step. **Reasoned from that graph:** `dotnet build -f net48` on a Linux runner should succeed. Not executed on Linux.

**Run — measured artefacts, reasoned conclusion.** The net4x test hosts in the SDK are Windows PE executables, and `microsoft.testplatform.testhost/17.8.0`'s `lib/net462/` contains only the `_._` placeholder — the net4x host is not carried by the package at all. **Reasoned:** a `net48` assembly needs a CLR implementing .NET Framework 4.8; Linux has none. Whether VSTest's net4x host runs under current Mono is **not known and not claimed** (§4).

### 3.7 Measurements and hypotheses I discarded, and what made them wrong

Recorded because the trap is invisible in what survives (#1136 §5), and three of these are mine.

| discarded | what made it wrong |
|---|---|
| ***"`RestPathEncodingTests` is a high-suspicion divergence candidate."*** Before the run, I nominated it on reading: `Rest.cs:35` escapes every path segment with `Uri.EscapeDataString`, and two of its 21 guards read outcomes **off a constructed `Uri`** (`Path_SegmentCarriesStructuralCharacter_DoesNotRestructureUrl` asserts `url.AbsolutePath.Split('/')`; `Path_SegmentCarriesPreEncodedDotSegment_IsInert` asserts `%2E%2E` inertness). | **Falsified by the run: all 21 pass on `net48`.** The carrier is real — the nomination was not wrong about *where* a divergence could live. It was wrong that a divergence lives there, because the `TestCase` inputs (`/ ? # & = \ space ä`) are all outside the region where the two `EscapeDataString` implementations differ. **This is the same shape as §3.5 and it is the reason §8.2 exists**: carrier is necessary, input decides. Recorded as a negative result because "we looked and it does not diverge" is information the next author would otherwise re-derive. |
| **`Post308_NonSeekableStreamBody_ThrowsHttpServiceException` predictions — wrong twice.** First I predicted it would pass *vacuously green* (a guard asserting failure, on a runtime where the whole family fails). Reading it, I corrected to "red at its `handler.Requests` count assertion". | **Both wrong.** It fails at its **exact-type** assertion — `Is.TypeOf<InvalidOperationException>` rejecting an `ObjectDisposedException`. The *outcome* (red, not vacuous) was right for the wrong reason. **The hazard the first prediction was chasing is real and survives as §8.4 clause 4a**: several sibling guards *do* pass on `net48` for a reason unrelated to what they pin, and §8.4 names them. |
| **Mine: *"`LoopbackServer` uses `HttpListener`, so the `net48` job may need a URL ACL or elevation."*** I briefed the probe on that premise and it answered M8 against it. | **Wrong premise.** `LoopbackServer` uses `TcpListener` on `IPAddress.Loopback` port 0 and hand-writes the response head; `grep -rn "HttpListener"` across both projects returns zero. The answer built on my premise is discarded with it. **What stands is measured instead:** the 38 guards in the two files that use `LoopbackServer` all passed on `net48`, in an **unelevated** session, with no urlacl reservation. |
| **Mine: *"the three source-scanning guards will break on multi-target, because they resolve the source relative to the output directory."*** | **Falsified by reading** (§3.8). Kept because it is the obvious worry and deleting it guarantees the next author has it too. |

### 3.8 The three source-scanning guards are TFM-independent

`ExactlyOneCallSiteNamesACompletionOption`, `EveryStatusCheckCallSiteNamesTheOptions` and `SourceCarriesNoLiteralShapeTheScannerCannotRead` read `HttpService.cs` from disk. All three resolve the path through **`[CallerFilePath]`**, a compile-time constant. They do not walk up from the assembly's output directory, and they passed on both targets.

### 3.9 Language and BCL features in the test sources

| feature | count | relevance |
|---|---|---|
| UTF-8 string literals (`"…"u8`) | **39 uses in 7 files** | the entire `CS0518 ×39` block; resolved by `System.Memory` |
| primary constructors | present (`RestPathEncodingTests.RenderedSegment`) | C# 12; needs `LangVersion latest` |
| collection expressions (`[]`) | present (`SequenceHandler.Drain`) | C# 12; array target, no runtime type required |
| raw string literals / `record` / range / index | **0** | none |
| files relying on `ImplicitUsings` | **1** (`QueryParametersTests.cs`) | symmetric across TFMs, not part of this decision |

## 4. What I reasoned

Kept separate from §3 deliberately.

- **Mono's fidelity** as a stand-in for Framework's `HttpClient` and `Uri` (§7.3). Reasoned from the fact that Mono ships its own BCL implementations. Nothing was run on Mono, and specifically: whether VSTest's net4x host runs under current Mono, and whether the Linux SDK layout ships a `TestHostNetFramework` directory at all, are **unknown and not claimed**.
- **The Linux `net48` build succeeding** (§3.6) — reasoned from a measured reference graph, not observed on Linux.
- **GitHub Actions Windows-runner wall clock** relative to `ubuntu-latest` (§7.2). I did not time a run in this repo.
- **That `<LangVersion>latest</LangVersion>` resolves identically on CI's pinned SDK.** The probe ran on **SDK 10.0.203, `win-arm64`**; CI pins `dotnet-version: '8.0.x'` on x64. Every C# feature the suite uses is ≤ C# 12, which `latest` yields on an 8.0.4xx SDK — but that is reasoning across two axes the measurement did not cross, and it is Q1.

## 5. The decision

**`Http.Tests` becomes `<TargetFrameworks>net8.0;net48</TargetFrameworks>`. All 409 guards compile and run on both. The 14 measured divergences are split per runtime, each side asserting what its runtime actually does.**

The classification lives in the **test source**, next to the guard it describes, and nowhere else. Not in a link list, not in a build file, not in a documentation table that drifts.

### 5.1 Why the whole suite and not a chosen subset

The task warns, correctly, that running all 409 on both is the tempting default and manufactures the false-guard hazard at scale: every legitimately divergent guard becomes a red someone "fixes" by loosening, and a guard loosened to pass on two runtimes pins less than it did on one.

Two answers, and the second is now measured.

**The hazard is a property of how a red is disposed of, not of how many guards run.** Reds arrive in one triage pass, done once, against a written rule (§8.4) that names three dispositions and forbids the fourth. That is governable.

**And "at scale" is 14.** Not 409, not a distribution across the suite — **14 guards, one file, one cause.** The scale premise the warning rests on is measured away. What remains is a bounded, comprehensible triage that one person does in one sitting.

The alternative hazard is not bounded. Any mechanism that runs a subset must *choose* the subset, and **§3.5 and §3.7 together are the proof the choice cannot be made by reading**: the guard everyone would have put in the subset is runtime-independent, the divergence it appears to cover is guarded nowhere, and a whole file I nominated on carrier analysis turned out not to diverge at all. A subset chosen from that reasoning would have been confidently wrong in three directions at once — and a wrong subset is silent, because the omitted members are exactly where an unknown carrier hides. **That is #9965's own defect, moved from the TFM level to the file level.**

### 5.2 Why the split is `#if`, and what each side must contain

A divergent guard is answered by **a differently named guard on the other side**, never by one guard with a weakened assertion:

- the `net8.0` side keeps its identifier and its assertion, wrapped in `#if NET`;
- the `net48` side is a **new, separately named** guard under `#if NETFRAMEWORK`, whose name says the runtime, and whose assertion is a **positive statement of what Framework does** — not `Assert.Pass()`, not a relaxed matcher, not a try/catch that accepts either.

Two consequences, both the point:

1. A future loosening becomes the **deletion of an `#if NETFRAMEWORK` block**, visible in a diff. A loosened single assertion is invisible.
2. The Framework guard's name is greppable and carries its runtime, so a coverage row can name an identifier that says which runtime it pins (#1136 §6).

`#if` is not an independent shape — it is expressible only *because* the project multi-targets. §6.2 records why the task's third option resolves into this one.

### 5.3 Nine guards do not become nine arms

The nine guards in §3.4's first group pin nine different properties **of a hop that does not happen on `net48`**: the body repeats, the bytes match, the content type rides, the credential is stripped, `Expect: 100-continue` survives, the `UrlProcessor` runs first, the superseded response is disposed. Asserting each of those separately on Framework would be **nine restatements of one fact** — the hop raises — which is #1136 §1 DRY at its plainest.

**So the nine become `#if NET` and the Framework side gets six purpose-written guards** (§11.1): the fact itself, the shape-irrelevance claim that distinguishes Framework's *disposal* from Core's *consumption*, the 307 arm, the bodyless control, the pre-emption property from §3.4's second table, and the clause-4a discriminator. Six guards that each say something, rather than fourteen that say one thing fourteen times.

### 5.4 The 35-site shim

**One shared constant in `Http.Tests/TestSupport`, unconditional, used by both targets.** `internal const HttpStatusCode PermanentRedirect = (HttpStatusCode)308`, with a doc comment naming why it is not the framework symbol — mirroring the doc comment #8297 records on the library's own private `permanentRedirect`. The 35 sites become one named identifier.

**Why not inline `((HttpStatusCode)308)` at 35 sites.** Two arguments and the second is the stronger:

- The math, per #1136 §1: `block_size × site_count = 1 × 35 = 35`, above the ~15–20 threshold. The rule's trivial-sequence exemption covers three near-identical lines, not thirty-five.
- **DRY with the library's own decision on the same value.** `HttpService` already chose a named constant with an explanatory comment for exactly this symbol, for exactly this reason. Spraying the raw cast 35 times across the tests would be the repo contradicting itself about the same number, and it would put a magic number in the one place a reader looks to find out what the library means by 308.

**Why not make the library's constant `internal` plus `InternalsVisibleTo`.** It adds an attribute to a shipped assembly so that a test can avoid four lines. The value, not the logic, is what is shared; a test-side constant duplicates one integer and no behaviour. Rejected on #1136 §4.

**Note the consequence for the library's own guard.** #8297 records that the doc comment is the first line of defence against a tidy-up back to `HttpStatusCode.PermanentRedirect` and that **CI is the second, because `build-test` compiles both library targets**. After this change the *test* project also compiles on `net48`, so a tidy-up in the tests is caught by the same mechanism. The comment on the new constant is what makes it look wrong to the person about to make it.

### 5.5 The per-guard escape hatch, and its limit

Where splitting a guard is genuinely disproportionate — the arrange is elaborate and the divergence is incidental to what it pins — the guard may run on `net8.0` only, via `#if NET`, **with a `[Description]` naming the divergence and the runtime it is not asserting on**, in the style this suite already uses throughout.

That is strictly better than a loosened guard, because it is legible: a reader sees a guard that declines to speak about `net48`, not one that appears to speak about both and pins neither. It is strictly worse than a split. **It is not a budget** — §3.4 says the whole population is 14, and the six in §11.1 cover them, so no use of this hatch is anticipated. Every use is a line in the PR body.

## 6. Alternatives, and why each lost

### 6.1 A separate `Http.Tests.Framework` project sharing sources — REJECTED

Linked files or a shared project, carrying only the runtime-sensitive sources.

- **Granularity mismatch.** The sharing unit is a file; the classification unit is a guard. The 14 divergences sit inside a 62-guard file. Linking it brings 62; not linking it brings none. There is no file-level answer that is right.
- **#1136 §2 Form 2, parallel layer.** A second csproj, a second CI invocation, a second package list, a link list nobody maintains.
- **It reintroduces the defect, and this is decisive.** A guard added tomorrow to a non-linked file never runs on `net48`, and **nobody sees a red, because there is nothing to be red.** #9965 exists because a divergence was invisible to the suite. This shape makes divergence invisible again, one level up, and adds a build file that makes the invisibility look deliberate.
- **And it costs more than what it replaces.** §3.2 measured the multi-target at three project lines; a second project is more work for strictly less coverage.

### 6.2 Per-guard conditional compilation without multi-targeting — NOT A SHAPE

`#if NETFRAMEWORK` in a project that targets no Framework TFM compiles to nothing on every build. There is no configuration in which it runs a guard on `net48`. It is absorbed into §5.2 as the *expression* of the chosen shape.

### 6.3 A small `net48`-only probe project with hand-written divergence guards — REJECTED, and it was the strongest competitor

Not the task node's fallback: this shape *executes* assertions on `net48` — a new project, `net48` only, no source sharing, a dozen guards written from scratch against the `netstandard2.0` asset.

Before §3 it was genuinely attractive under #1136 §1 and §4: it would cost nothing on the existing 409. **§3.2 removes the cost advantage and §3.7 removes the correctness case.**

- **Cost.** The multi-target is three project lines plus one constant. A new project is a csproj, a package list, a CI step, and every fixture it needs re-created or shared — strictly more than what it was supposed to avoid.
- **Correctness.** It makes the *known* divergences visible. #9965 exists because the known divergence was found **by hand, during a QA re-review, by someone verifying a design claim rather than reasoning about it** (#9964 §3). This shape guarantees the next one is also found by hand. And §3.7 is the evidence that reading is not enough: I nominated a 21-guard file on carrier analysis and the run falsified it, while the actual divergence sat in a file whose guards look nothing like `Uri` code.

### 6.4 The task node's documentation-only fallback — REJECTED

*"Name the runtime-dependent guards and record which TFM each was measured on."* It was offered against a feasibility blocker that has since been measured away, and #9965 says so itself. I am not restating that argument. Two of my own:

- **§3.5 and §3.7 falsify the premise it rests on.** The fallback assumes the runtime-dependent guards can be **named by reading**. Reading nominated a file that does not diverge and missed the file that does; and the suite's most `Uri`-dependent-looking guard is runtime-independent. A document produced that way would have been wrong in both directions and would have read as compliant.
- **It cannot express D1 at all.** The 307/308 absence is not a guard asserting the wrong thing — it is a capability with **no** guard on the runtime where it is absent. A document recording "these guards were measured on net8" says nothing about a guard that does not exist. The defect that most needs closing is invisible to the remedy.

## 7. CI — what the required check becomes

### 7.1 The answer

```
build-test          ubuntu-latest    REQUIRED   (existing check name, kept)
  dotnet build Pooshit.Http/Pooshit.Http.csproj -c Release      unchanged
  dotnet test  Http.Tests/Http.Tests.csproj -c Release -f net8.0   ← gains -f

build-test-net48    windows-latest   REQUIRED   (new check name)
  dotnet test  Http.Tests/Http.Tests.csproj -c Release -f net48
```

**`build-test` stays required and stays on `ubuntu-latest`. `build-test-net48` is added on `windows-latest` and is also required.** Branch protection on `master` names both.

Two mechanical notes:

- The existing `dotnet test` step has **no `-f`**. Once the project multi-targets, it tries to run both targets and the `net48` run fails on Linux (§3.6). **`-f net8.0` is not optional**, and omitting it is the most likely way this lands broken.
- The library build step is unchanged. It already compiles both library TFMs and is the guard that catches a `netstandard2.0` break — #8297 records why it is its own step: with `netstandard2.0` broken, `dotnet test` alone reported 340 passed, exit 0.

### 7.2 Why two jobs rather than moving `build-test` to Windows

Moving the single job to `windows-latest` makes **every** PR wait on the slower runner for **every** failure, including the ones that have nothing to do with `net48`. Two jobs run in parallel: wall clock is `max(ubuntu, windows)`, not the sum, so the marginal cost is the Windows provisioning delta on the job that would have finished second anyway — while the fast Linux gate still fails fast on breakages that are not runtime-specific.

It also keeps the `netstandard2.0` **build** check on the runner it is on today, which is the check that has already caught a real class of defect.

Reasoned, not measured (§4): I did not time a `windows-latest` run in this repo.

### 7.3 Why the `net48` check is required, and why Mono is not the answer

**Why required.** A `net48` job that runs but does not block is a check on the runtime that has the absent capability, which nobody is obliged to read. That is the same shape as a guard that cannot fail — the thing #9965 exists to remove — with a green tick on it. **If it is worth adding it is worth blocking on.** After this lands, `net48` is green, so the check gates a green suite from day one; a red appears only when someone introduces a divergence, which is precisely the event that must not merge silently.

**Why not Mono**, which would keep everything on `ubuntu-latest`:

- **Measured:** the net4x test host is a Windows PE shipped in the SDK layout, and `microsoft.testplatform.testhost/17.8.0`'s `lib/net462/` is the `_._` placeholder — the host is not in the package. There is no supported route for `dotnet test -f net48` on Linux, and whether VSTest's host runs under current Mono is unknown and not claimed (§4).
- **Reasoned, and it is the argument that would still bite if the host problem were solved:** Mono implements its **own** `HttpClient`, `HttpContent` and `Uri`. This task asks *"what does .NET Framework 4.8 do"*; a Mono run answers *"what does Mono do"*. The content-disposal behaviour in §3.4 is a property of one specific `HttpClient` implementation, and there is no reason Mono's shares it. **A green Mono run would be indistinguishable from a green Framework run while proving nothing about Framework** — a correct measurement of an adjacent subject, which is the failure #1136 §5 names and which this repo has already paid for once by probing `HttpClientHandler` when the decision lived in `BrowserHttpHandler` (#14627).

Rejected, not deferred (§2.2 O6).

## 8. The guard classification

### 8.1 The carriers

**A guard is a candidate for runtime-dependence iff its asserted value passes through a BCL type between the library's own computation and the assertion.** Three carriers, enumerable rather than judged:

| carrier | what passes through | known divergence | measured result on this suite |
|---|---|---|---|
| **C1 — `Uri` rendering** | `Uri.ToString()`, `AbsolutePath`, `Query`, `EscapeDataString` | reserved escapes (`%2F %3F %26 %3D %3A %40 %2B`) decode on Framework and are preserved on .NET 8 (#9964 §3) | **zero failures.** No guard supplies an input in the divergent region (§3.5, §3.7). The divergence is real and **unguarded**. |
| **C2 — `HttpHeaders` round-trip** | `TryAddWithoutValidation` acceptance; the `Uri` normalisation `HttpHeaders` applies to registered values (#8297: `Location: plain text` reads back `plain%20text`) | C1 through a second door | **zero failures** across 40 header-redaction guards |
| **C3 — `HttpClient` / `HttpContent` lifetime** | whether a content object survives a send; response disposal; `ResponseHeadersRead` handling | Framework `HttpClient` disposes request content after send | **14 failures, all of them.** §3.4 |

Everything with none of these three asserts a value the library computed from a literal the test supplied. That is 395 of 409, measured.

### 8.2 How an implementer tells them apart — and the honest limit

**The carrier list is derivable statically. The divergent subset is not, and this suite is the proof in both directions.**

`PercentEncodedSeparatorInName_DecodedAndRedacted` carries C1 in the plainest possible way and is runtime-independent, because its inputs are unreserved escapes (§3.5). `RestPathEncodingTests` carries C1 through `Uri.EscapeDataString` and reads outcomes off a constructed `Uri`, and all 21 of its guards pass (§3.7). Meanwhile the 14 that do diverge sit in a file whose assertions are about request counts, methods and header sets — nothing that looks like BCL-adjacent code.

**Carrier is necessary; it is not sufficient. Sufficiency depends on the literal the guard supplies as input**, and there is no static predicate over test sources that decides "is this input in the divergent region of this BCL behaviour" without already encoding the divergence you are trying to discover.

So the method is two stages with different authorities:

**Stage 1 — the static pass produces a superset, by grep, in about an hour.** Starting points, not measurements (#1136 §6: a grep predicate must name the behaviour, not a token that co-occurs with it):

| carrier | starting grep | what a miss looks like |
|---|---|---|
| C1 | `RequestUri`, `RequestedUris`, `new Uri(`, `AbsolutePath`, `Rest.Path`, assertions on `exception.Message` containing `http` | a guard asserting on a URL the test built as a plain string never touches `Uri` — a false positive, cheap. A URL reaching the message through a helper the grep does not name — a **false negative**, which only stage 2 catches. |
| C2 | `.Headers.`, `TryAddWithoutValidation`, `GetValues`, `TryGetValues`, `DumpHeaders` | same |
| C3 | `Disposed`, `Dispose`, `308`, `307`, `RequestBodies`, `HttpCompletionOption`, `ProbeContent`, `SingleUseContent`, `NonSeekableStream` | same |

**Stage 2 — the run narrows the superset and is the only thing that can find a fourth carrier.** Whatever stage 1 said, a guard whose outcome differs is runtime-dependent.

**So: can the classification be found without running? No.** Stage 1 would have flagged `PercentEncodedSeparatorInName_DecodedAndRedacted` and `RestPathEncodingTests` (both wrong) and said nothing about the reserved-escape case (because there is no guard there to classify). Stage 1's value is that it says where to look first and what a red is likely to mean; stage 2 produces the answer.

**For this repo, stage 2 has already been run and §3.4 is its output.** The implementer does not repeat the discovery — they reproduce it as a check that the probe's configuration transfers (Q1), and then act on the 14.

### 8.3 The measured classification

| group | guards | disposition |
|---|---|---|
| **runtime-independent** | **395** | run on both, unchanged, no annotation. This is the default and it needs no ceremony. |
| **C3, capability absent** | **9** (§3.4 group 1) | `#if NET`. Answered on Framework by the four guards in §11.1 rows 1–4, not by nine arms (§5.3). |
| **C3, failure attribution differs** | **5** (§3.4 group 2) | `#if NET`. Answered on Framework by §11.1 rows 5–6. |
| **C1/C2, divergence exists but no guard does** | **0 existing** | §11.2 creates four. |

**One security property worth stating, because its absence would have been a serious finding.** `Post308_CrossOrigin_AuthorizationStrippedWhileBodyRides` is among the 9 and goes dark on `net48`. The cross-origin credential-strip property itself does **not** go dark: its bodyless siblings `SendWithAuthorizationHeader_CrossOriginRedirect_CredentialIsStrippedWhileOtherHeadersSurvive` and `GetWithTokenProvider_CrossOriginRedirect_AuthorizationIsStripped` **pass on `net48`** (measured — they are not among the 14). So `#if NET`-ing the body-carrying one loses a case, not a guarantee.

### 8.4 The triage rule — for the reds this produced, and for the next one

**A difference between the two targets is one of exactly four things, and only the fourth is forbidden.**

1. **A carrier §8.1 names.** The guard is runtime-dependent. **Split it** per §5.2 — and per §5.3, do not assume one arm per red.
2. **A carrier §8.1 does *not* name.** *First* name the new carrier and add it to §8.1 in this document — **the rule's incompleteness is the finding, and it is worth more than the guard that produced it.** *Then* split.
3. **A test-infrastructure artefact** — a compile difference, a fixture assumption, a package resolution problem. Fix the infrastructure. **This is the only case where a test changes to accommodate a runtime, and it must be argued rather than assumed.** The tell: if the guard never reached its assertion it is infrastructure; if it reached its assertion and got a different answer it is 1 or 2. *(For the 14 measured here: all reached their assertions. None is infrastructure.)*
4. **Loosening the assertion so it passes on both. Forbidden.** A guard loosened to pass on two runtimes pins less than it did on one, and it is invisible afterwards — the diff shows a relaxed matcher, not a lost guarantee. If outcomes differ, the guard is answered by a guard on the other side. If that is disproportionate for a particular guard, §5.5 applies and is recorded.

**Clause 4a — a green on `net48` is not evidence of sameness, and this suite has live instances.** A guard that asserts a **failure** can pass on a runtime where *everything in its family* fails, for a reason unrelated to what it pins.

The measured instances: `Post308_NonSeekableStreamBody_FailureMessageRedactsTheTargetQuery` and `Post308_NonSeekableStreamBody_LeavesTheSupersededResponseUndisposed` **pass on `net48`** — but on `net48` a *buffered* body produces the same failure, so neither guard still discriminates non-seekability, which is the premise its name states. They are green and they pin less than their names claim.

**The disposition is a discriminator, not a re-read of the assertion**: §11.1 row 2 asserts that a buffered body fails identically on Framework, which makes the loss of discrimination explicit rather than silent. Same rule for `Post308_WithoutLocation_ThrowsAndSendsNoSecondRequest`, `Post308_UnstampedResponse_ThrowsInsteadOfDowngrading`, `Post308_ResponseStampedWithoutRequestUri_ThrowsInsteadOfFailingToResolve` and `Post308_UrlProcessorReturnsNull_FailsWithNoTarget` — all green on `net48`, all asserting a failure on a body-carrying hop. Each needs one question answered during implementation: *does its guard fire before the send?* If yes it is still discriminating; if no it is vacuous and gets the row-2 treatment.

§3.7 records that my first prediction about this clause named the wrong guard. The clause is right; my instance was not.

### 8.5 Considered and dropped: a ratchet on `#if NETFRAMEWORK` regions

The obvious mechanical defence against clause 4 is a guard asserting that the number of `#if NETFRAMEWORK` regions across `Http.Tests` is at least N — DRY with three guards of that shape already in the repo (`EveryStatusCheckCallSiteNamesTheOptions` and siblings).

**Dropped, on #1136 §4.** What breaks if it is absent?

- The Framework-side surface after this lands is **six named guards in one file** (§11.1). A deletion is plainly visible in a diff; the ratchets it would imitate exist to catch a fan of call sites growing silently across a 500-line production file, which is not this shape.
- The ratchet is set from today's count and **does nothing about a future guard**, which is where the real hazard lives: the loosening the task fears happens when someone adds a guard tomorrow and meets a red. A number fixed today is silent then.
- Its own limits would have to be stated and are substantial: adding an arm passes silently, gutting an arm's body passes, and it counts a **spelling** rather than a behaviour — the caveat #1136 §6 attaches to greppability.

**The mechanism that actually defends the trap is the required `build-test-net48` check** (§7.3): a new divergence cannot merge silently, because the job goes red and blocks. That is a real gate, it costs nothing extra, and it is already in the design. The durable statement of what to do with that red is §8.4, cited from a `[Description]` on each Framework-side guard — the convention this suite already uses on almost every test.

## 9. The layer control

### 9.1 The question, and the measured answer

The divergence was known before this design. **Which layer decides it was not**, and the two branches produce different coverage rows:

| if the decision is at… | then… |
|---|---|
| `HttpClient` (above the handler) | every existing `SequenceHandler` fixture sees it; the Framework guards reuse the fixtures the suite already has |
| the platform `HttpClientHandler` (below `HttpClient`) | **`SequenceHandler` never sees it.** Every `SequenceHandler`-based 307/308 guard would run **green on `net48`** and report the capability as *present* on a runtime where it is absent — a guard that does not merely fail to catch the defect but actively denies it. Every C3 guard would have to go over a real socket. |

**Measured: the first.** All 14 failures arrive through `SequenceHandler`, a hand-written `HttpMessageHandler` that never opens a socket — two of the stack traces terminate inside `SequenceHandler.cs`. The disposal is applied at or above `HttpClient`, so the existing fixtures are sufficient and §11.1's rows use them.

This is the layer trap this repo has already paid for once: probing `HttpClientHandler` for property support showed every setter working, because that handler *forwards*, and the decision lived in `BrowserHttpHandler` (#14627). **`SequenceHandler` is structurally a forwarding layer for this class of question** — it is an `HttpMessageHandler`, it sits *below* `HttpClient`, and it cannot observe anything `HttpClient` does after `SendAsync` returns. That it *does* see this one is a fact about where the disposal lives, not a general licence.

### 9.2 The control, which could have come back negative

The finding "content disposal" is only distinguishable from "308 is broken on Framework" by a case where a hop **does** succeed on `net48`.

**Measured: every bodyless redirect guard passes.** `AbsoluteLocationResolvesToAbsoluteUrl`, `RelativeLocationResolvesAgainstRequestUri`, `Get308_BodylessHop_StillDropsBodyDescriptor`, `Post301_HopIsIssuedAsGetWithoutBody`, `Post303_HopIsIssuedAsGetWithoutBody`, `GetWithTokenProvider_SameOriginRedirect_AuthorizationReachesBothHops` and the rest of the bodyless family are not among the 14. **Redirect following works on `net48`. The body is the discriminator.**

Had those failed too, the finding would have been "308 is broken on Framework" and everything downstream of it would change. They did not, and that is what makes the release note's account confirmed rather than assumed.

A second control in the same shape: `PercentEncodedSeparatorInName_DecodedAndRedacted` runs on both and passes. If it ever goes red on `net48`, the reserved/unreserved account behind §11.2 is wrong and the `Uri` divergence is wider than recorded (§11.2 row 4).

### 9.3 A note on the metric

The cost figure this design reports is **409 guards × 2 targets**. That number is in the **churn** metric, not the defect's, and presenting it as the benefit would be the error #1136 §5 names.

**The defect is measured in guards that can fail on only one of the two shipped runtimes — 409 of 409 today.** The mechanical test: if the two runtimes did not diverge at all, "409 × 2" would read exactly the same while the defect's metric read zero. The execution count sits *next to* the question.

The figure **in** the defect's metric, now that the run has been taken: **14 guards pin a capability that does not exist on half the shipped surface, and 4 more divergences are pinned by nothing in either direction** (§11.2). After this lands, 409 of 409 can fail on the runtime they describe, and the 14 are answered by six guards that assert what Framework actually does.

## 10. Risks

| # | risk | mitigation |
|---|---|---|
| R1 | A future red is silenced by loosening rather than split. | §8.4 clause 4 forbids it; §5.2 makes a loosening a visible `#if` deletion; the required `build-test-net48` check (§7.3) means the red must be dealt with at all. §8.5 explains why a ratchet is not the answer. |
| R2 | The probe's configuration does not transfer to CI's pinned SDK (10.0.203/arm64 measured, 8.0.x/x64 deployed). | Q1. Phase 1 reproduces §3.2 under `8.0.x` before anything else is spent; pin `Microsoft.NETFramework.ReferenceAssemblies` explicitly rather than relying on the SDK's implicit injection, which was only measured on SDK 10. |
| R3 | The `net48` job is flaky on a Windows runner. | `LoopbackServer` binds `TcpListener` on port 0 and its 38 guards passed on `net48` **unelevated** (§3.7). If flakiness appears, fix it — do **not** demote the check to non-required, which is §7.3's rejected option arriving by the back door. |
| R4 | Clause 4a's other candidates (§8.4) turn out vacuous and are missed. | Each is named individually in §8.4 with the one question that resolves it. Six names, not a category. |
| R5 | A contributor meets a `net48` red and deletes the **target** rather than splitting the guard. | The required `build-test-net48` check makes removing the target a branch-protection change, not a csproj edit. |
| R6 | Framework-side arms rot and drift from what Framework does. | Every arm is a real assertion running on every PR; drift is a red, not silence. This is the property §6.1 and §6.3 lack. |
| R7 | `#if` blocks make the test source harder to read. | Accepted, and bounded: 14 `#if NET` wrappers and six Framework guards, in one of 23 files. The alternatives that avoid them reintroduce the defect. |

## 11. Coverage

Rows name the test identifier they pin. Where the design precedes the test, the row names the identifier **the implementer must create**.

### 11.1 C3 — the measured divergence

Six Framework-side guards replace fourteen `#if NET` wrappers (§5.3). All use `SequenceHandler`, per §9.1.

| # | identifier | arm | pins |
|---|---|---|---|
| 1 | `Post308_NetFramework_BufferedBody_HopIsNotFollowed` | **create**, `#if NETFRAMEWORK` | a `string` body 308 under `FollowRedirects = true` raises `HttpServiceException` naming the unreplayable body |
| 2 | `Post308_NetFramework_SeekableStreamBody_FailsLikeANonSeekableOne` | **create**, `#if NETFRAMEWORK` | the body's **shape is irrelevant** on Framework — the claim that distinguishes Framework's *disposal* from Core's *consumption*, and the **clause-4a discriminator** for the two `NonSeekableStreamBody` guards that pass vacuously (§8.4) |
| 3 | `Post307_NetFramework_BufferedBody_HopIsNotFollowed` | **create**, `#if NETFRAMEWORK` | the 307 arm, separately — 307 and 308 reach the preserving arm by different constants |
| 4 | `Post308_NetFramework_BodylessHop_StillFollows` | **create**, `#if NETFRAMEWORK` | **the control** (§9.2). A 308 with no body **is** followed on Framework. Already measured green through the existing bodyless family; this row makes it a deliberate guard rather than an incidental pass, so a future regression reads as "the control broke". |
| 5 | `Post308_NetFramework_UnrelatedTransportError_IsPreemptedByDisposal` | **create**, `#if NETFRAMEWORK` | disposal wins before an injected transport error can surface — the property behind failures 11 and 12 (§3.4), which is documented nowhere |
| 6 | `Post308_NetFramework_ConsumedContentSignal_ArrivesAsObjectDisposedException` | **create**, `#if NETFRAMEWORK` | the translated inner is an `ObjectDisposedException`, a **subclass** of the `InvalidOperationException` `IsConsumedContentFailure` walks for — which is why translation still works. Behind failures 10, 13 and 14. |
| 7 | the nine guards of §3.4 group 1 | **existing**, become `#if NET` | unchanged on `net8.0`; identifiers, assertions and `[Description]`s untouched |
| 8 | the five guards of §3.4 group 2 | **existing**, become `#if NET` | as above |
| 9 | `Post308_NonSeekableStreamBody_FailureMessageRedactsTheTargetQuery`, `Post308_NonSeekableStreamBody_LeavesTheSupersededResponseUndisposed` | **existing**, run on **both**, unchanged | green on `net48` and no longer discriminating; row 2 is what makes that explicit. Named here so the pair is not read as full coverage on Framework. |
| 10 | `SendWithAuthorizationHeader_CrossOriginRedirect_CredentialIsStrippedWhileOtherHeadersSurvive`, `GetWithTokenProvider_CrossOriginRedirect_AuthorizationIsStripped` | **existing**, run on **both**, unchanged | the cross-origin credential-strip property **retains Framework coverage** after its body-carrying sibling goes `#if NET` (§8.3). Measured: both pass on `net48`. |

### 11.2 C1 — `Uri` reserved-escape rendering

Zero guards exist here today, in either direction (§3.5).

| # | identifier | arm | pins |
|---|---|---|---|
| 1 | `ReservedEscapeInParameterName_NetFramework_DecodesAndSplitsTheQuery` | **create**, `#if NETFRAMEWORK` | `?access_to%2Fken=` renders as `?access_to/ken=` on Framework |
| 2 | `ReservedEscapeInParameterName_Net8_IsPreservedAndRedacted` | **create**, `#if NET` | the same input renders `%2F` and redacts on .NET 8 — the explicit other side of the same divergence |
| 3 | `SeparatorEscapeInParameterName_NetFramework_DefeatsRedaction` | **create**, `#if NETFRAMEWORK` | `?access_token%26x=SECRET` splits and prints the secret on Framework. #8297 **describes** this hazard; nothing asserts it. Asserting it converts prose into a guard that goes red if it is ever fixed. |
| 4 | `PercentEncodedSeparatorInName_DecodedAndRedacted` | **existing**, runs on **both**, unchanged | **control** for rows 1–3 (§9.2): unreserved escapes normalise identically. Measured green on `net48`. If it ever goes red there, the reserved/unreserved account is wrong and the divergence is wider than recorded. |

### 11.3 Measured negative — no row

`RestPathEncodingTests` (21 guards, `Uri.EscapeDataString` + two guards reading off a constructed `Uri`) was nominated as a C1 candidate and **all 21 pass on `net48`** (§3.7). **No guard is created.** The negative result is recorded in §3.7 rather than as a row, because a guard asserting "these two runtimes agree here" pins nothing anyone is trying to change and would be #1136 §6 defensive code for a case that does not exist.

### 11.4 The mechanism

| identifier | arm | pins |
|---|---|---|
| `TestSupport/Statuses.PermanentRedirect` | **create**, unconditional | one named constant for `(HttpStatusCode)308`, replacing 35 raw sites (§5.4). Not a guard; named here because it is the only new production-shaped artefact in the change. |
| `build-test` / `build-test-net48` | CI, not a test | §7.1. Verified in branch-protection settings on `master`, which is a repository setting the PR cannot land — Q3. |

## 12. Implementation phases

**Phase 1 — reproduce §3.2 on CI's SDK.** Apply the three project lines under **`8.0.x` on x64**, not on the SDK the probe used. Confirm both targets build with 0 errors and that `Microsoft.NETFramework.ReferenceAssemblies` resolves — **pin it explicitly**, since its implicit injection was only measured on SDK 10.0.203. If this does not reproduce, stop and report before any source work. (Q1.)

**Phase 2 — the shim.** Add `TestSupport/Statuses.cs` per §5.4 and replace the 35 sites. Both targets compile; `net8.0` still 409 green. **No assertion changes in this phase** — a mechanical token replacement that also touched an assertion would be indistinguishable from a behaviour change in review.

**Phase 3 — run both and confirm §3.4.** Expect 409/409 discovered, `net8.0` 409 green, `net48` 395/14 with exactly the 14 named. **A different set is a finding, not a variance**: apply §8.4 clause 2 and amend §8.1 of this document before proceeding.

**Phase 4 — the split.** Wrap the 14 in `#if NET`. Add the six Framework guards of §11.1 and the three new `Uri` guards of §11.2 rows 1–3. Answer the six clause-4a questions in §8.4 and record each answer in the PR body. Both targets green.

**Phase 5 — CI.** Add `-f net8.0` to the existing test step; add the `build-test-net48` job. **Branch protection is a repository setting outside the PR** — name it explicitly in the PR body so it is not forgotten (Q3).

**Phase 6 — reconcile.** Resync this document's DiVoid node (#11228), and reconcile **#8297**, which currently records the single-TFM test project as an open item in **three separate** reconciliation entries and can now strike it.

## 13. Open questions

**Q1 — does §3.2's configuration transfer to CI's SDK?** The probe ran on **SDK 10.0.203, `win-arm64`**; CI pins **`8.0.x`** on x64. Two axes the measurement did not cross. Every C# feature the suite uses is ≤ C# 12, which `latest` yields on an 8.0.4xx SDK — but the implicit `Microsoft.NETFramework.ReferenceAssemblies` injection is SDK behaviour and was measured on one SDK only. **Falsifier: phase 1 builds clean under `8.0.x`.** Mitigated by pinning the package explicitly regardless. *Does not need Toni — it needs phase 1.*

**Q2 — `net48`, or a lower Framework TFM?** §2.2 O3 scopes this to `net48` because that is what #9964 and the `0.15.1-preview` note measured. `netstandard2.0`'s floor is `net461`, so the shipped surface is wider than what gets tested. **Needs Toni: is there a known consumer below 4.8?** If not, `net48` is the right single choice and O3 stands as written.

**Q3 — branch protection.** Adding `build-test-net48` as a required check on `master` is a repository setting only Toni can change; the PR cannot land it. **Needs Toni**, and it is the single step that separates §7.1 from §7.3's rejected option. Until it is set, the job runs and advises but does not gate.

**Q4 — should the 14 `#if NET` guards keep their `net8.0`-only status, or is the `0.14.0-preview` capability worth making work on Framework?** Out of scope here (O2) and the answer is not obvious: buffering the request content before a body-carrying hop would restore the capability on Framework at the cost of holding the body in memory on **both** runtimes. That is a design question for #8323, not a test question. **Needs Toni: file it, or leave the capability documented as Core-only?**

## 14. Design Contracts (#1136) §5 discharge

**KISS / DRY / YAGNI**
- No new type mirroring an existing one. The one new artefact is a four-line constant (§5.4), justified by math (`1 × 35 = 35`, above threshold) **and** by DRY with the library's own decision on the same value.
- No new abstraction with one implementation.
- No element justified by "we might need X later": every guard in §11 pins a divergence that is **measured** (§3.4, §3.5). §11.3 explicitly creates **no** guard where the measurement came back negative.
- No deprecation period, feature flag, compatibility shim or transition window.
- A ratchet was considered and **dropped** with the reasoning stated (§8.5), rather than added because it looked like diligence.

**Existing systems first**
- Audited: `Http.Tests` exists, references the library, and already has `SequenceHandler` and `LoopbackServer`. The design adds a TFM to it rather than a project beside it — §6.1 rejects the new-layer shape on #1136 §2 Form 2 grounds **and** on measured cost.
- No new persisted data. No field justified by "the existing reader projects it".

**Configurability**
- No new config knob. The three project properties are required settings with measured failure modes (§3.2), not tunables.
- No telemetry-then-tune compound.

**Less is better**
- Can-it-be-deleted run on every element; §8.5 is where it removed one, and §5.3 is where it turned fourteen arms into six guards.
- No "this guard is now unreachable" deletion anywhere — the design deletes no guard, and §11.1 rows 7–8 keep every existing identifier and assertion intact.
- Trade-offs named explicitly: §6.3 is the closest competitor and loses on measured cost **and** a stated requirement.
- Radical-clean applies: where outcomes differ, no compromise assertion accepts both. That compromise is §8.4 clause 4 and is forbidden by name.
- No reader-inventory or carrier-swap table applies.

**Data deliverables** — none.

**Document discipline**
- Cites #114 and #1136 as load-bearing, in the header block.
- Scope inventory explicit: §2.2, eleven enumerated exclusions.
- Out-of-scope items listed, not merely absent.
- No multi-paragraph rationale for anything that obviously stays.
- Supersedes nothing. Three predecessors named in the header with the relation stated, including one this document **falsifies** (§3.7, `path-segment-encoding.md` as a divergence candidate).
- **Every coverage row names a test identifier** (§11), marked **create** or **existing**; §11.3 states why a row is absent rather than leaving it absent.
- **Findable in the graph**: filed as a `documentation` node rooted at the project and linked to #9965.
- **The header block names its own node id** on its own line, beside the repo path.

**Measurement discipline**
- **Every figure in the metric the defect is in** — §9.3. The defect's metric is *guards that can fail on only one of two shipped runtimes*; "409 × 2" is named as a **churn** figure and disclaimed as a proxy, with the mechanical test applied (if the runtimes did not diverge, 409 × 2 would read the same while the defect's metric read zero). The figure in the right metric is stated as **14 measured + 4 unguarded**, not as an execution count.
- **Discarded measurements recorded with what made them wrong** — §3.7, four of them, three mine: a whole file nominated as divergent and falsified by the run; two successive wrong predictions about the same guard; and a wrong premise (`HttpListener`) that invalidated an answer I had commissioned. The error-count method is also stated (§3.2: MSBuild's summary line, because a raw grep of the log double-counts) — the instrument, not just the result.
- **A control where the measurement could be taken at more than one layer** — §9 in full. The layer is `HttpClient` vs the platform handler; `SequenceHandler` is identified as a **forwarding layer** for this class of question **by construction**, and the measurement says which layer answered (the failures arrive through it, so the decision is above it). **Two controls, and both could have come back negative**: the bodyless family passing is what separates "content disposal" from "308 is broken on Framework" (§9.2), and `PercentEncodedSeparatorInName_DecodedAndRedacted` passing is what holds up the reserved/unreserved account behind §11.2.
- Grep predicates in §8.2 are named as **starting points producing a superset**, with what a false negative looks like stated per carrier — per #1136 §6's caveat that a predicate must name the behaviour, not a token that co-occurs with it. §3.7 and §3.5 are the two live proofs that carrier-by-reading over-selects.
