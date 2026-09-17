# External Provider HTTP Protocol Reference

**Status:** Protocol version **2** — the target wire contract for new
external providers (Anna's Archive, Usenet/Newznab, and any future
provider). This document is the spec to build against; it has no dependency
on the corresponding Family Librarian code existing yet. Protocol version 1
— the contract actually shipped as of `feature/alpha-5` — is preserved
verbatim in **Appendix A** below for the existing sample provider and until
the client/server code here is upgraded to match this document.

**What changed from v1, in one paragraph:** v1 modeled a single author, a
single ISBN, a binary health check, and a hard 90-second acquisition budget
ending in exactly one file. v2 adds structured multi-author/series/
identifier evidence, pagination, real protocol-version negotiation,
provider-instance identity, an idempotent long-lived job model with a small
lifecycle state plus an open-string phase (including a `waiting`/
user-interaction state for browser-gated or otherwise stalled acquisitions),
structured retryable errors, multiple generic outputs with checksums and
retention, explicit cancel-vs-cleanup semantics, richer coarse health, and a
namespaced extension point — all designed to be additive going forward
rather than requiring another full version bump.

**Audience:** someone building a standalone HTTP service (any language, any
runtime — a Docker container is the expected shape) that Family Librarian
will register as an *external provider* and call over the network.

A complete, working, cross-checked reference implementation lives at
[`samples/FamilyLibrarian.SampleProvider`](../samples/FamilyLibrarian.SampleProvider) —
read its source if anything here is ambiguous; that project's own conformance
tests (`ExternalProviderClientTests`) run the real client in this repository
against it. **As of this writing the sample provider and the real client
still speak protocol version 1** (Appendix A) — this document describes the
target they are being upgraded to, not their current behavior.

---

## 1. The trust model

Unchanged from v1. Family Librarian never runs your code. It only ever sends
HTTP requests to your service and reads HTTP responses. Your service, in
turn, never receives Family Librarian's database, other providers'
credentials, or the destination library — it receives only the request's
work/edition metadata, and returns candidate **evidence** and, eventually,
one or more outputs.

Concretely:

- You run as a separate process — a container, VM, or any host reachable over
  HTTP from Family Librarian's server. There is no in-process plugin model.
- Family Librarian treats your search results with real skepticism: it
  independently re-verifies your candidates' evidence (title/author/
  identifiers) before ever calling `/acquire`. A plausible-looking wrong
  match does not get fetched silently — either it corroborates strongly
  enough to auto-acquire, or an administrator must explicitly confirm it
  first.
- You supply evidence; Family Librarian decides identity. You produce
  outputs; Family Librarian decides how those outputs enter its trusted
  validation pipeline (malware scanning, archive safety, real file-type
  validation, and the rest). Nothing you report replaces that pipeline.
- An admin registers you by base URL (plus an optional API key) through
  **Admin → External providers**.

---

## 2. Base URL and routing

Unchanged from v1. The admin-entered base URL is used as-is; every endpoint
below is a path relative to it. Every call — manifest, health, search, and
every step of acquire — goes to the same base URL and is routed identically
per your manifest's declared `egressPolicy` (§4).

---

## 3. Authentication

Unchanged from v1. If the admin configured an API key for your registration,
every request carries `Authorization: Bearer <api-key>`. Reject
unauthenticated/mismatched requests with `401`.

---

## 4. `GET /manifest`

Called on registration, whenever an admin clicks "Test Connection," and
periodically thereafter per the registration's recheck schedule.

Response:

```json
{
  "protocolVersions": ["2"],
  "protocolVersion": "2",
  "instanceId": "b3f2c9de-...-stable-across-restarts",
  "id": "your-provider-id",
  "name": "Your Provider Name",
  "version": "1.0.0",
  "capabilities": {
    "mediaTypes": ["ebook"],
    "operations": ["search", "acquire"],
    "features": ["pagination", "checksums", "outputs", "waiting-interaction", "idempotency"]
  },
  "outputRetentionSeconds": 86400,
  "egressPolicy": "NORMAL"
}
```

| Field | Required | Notes |
|---|---|---|
| `protocolVersions` | **yes** | Array of version strings you support, e.g. `["2"]` or `["1", "2"]` during a transition. Family Librarian computes the highest value present in both its own supported set and yours. If there is no overlap, Family Librarian refuses to call `/search` or `/acquire` at all — it will not silently guess a version — but will still call `/manifest`/`/health` for diagnostics and surface the mismatch to the admin. |
| `protocolVersion` | no | Deprecated single-string form, kept for a transitional read by older clients. Set it equal to the highest value in `protocolVersions`. Defaults to `"1"` if both fields are omitted, for backward compatibility with a v1 manifest. |
| `instanceId` | recommended | A string identifying *this deployed instance*, stable across ordinary restarts (persist it to disk/env, don't regenerate it per process start). Distinct from `id`/`providerId`, which identifies the provider *software*. Lets Family Librarian detect that a container has been replaced mid-job (a new `instanceId` under the same `id`) rather than assuming an old job reference is still valid. Omitting it is tolerated — Family Librarian just can't detect instance replacement for you. |
| `id`, `name`, `version` | no | `id` is the provider software/type identity (e.g. `anna`, `newznab-generic`), not this specific deployment. Default to empty string if omitted. Purely informational (shown in the admin UI). |
| `capabilities` | no | A structured object: `mediaTypes` (open list, e.g. `ebook`/`audiobook`), `operations` (open list, e.g. `search`/`acquire`), `features` (open list — declare the ones from the example that you actually support: `pagination`, `checksums`, `outputs`, `waiting-interaction`, `idempotency`; unrecognized feature strings are tolerated and ignored). All three lists may be empty or omitted; Family Librarian does not gate which endpoints it calls based on this today, but declare accurately anyway — that is expected to matter more as capability-based gating lands. A legacy flat array (`["ebook", "search", "acquire"]`, the v1 shape) is also tolerated and parsed as best-effort `mediaTypes`/`operations`. |
| `outputRetentionSeconds` | no | How long you guarantee a completed job's outputs remain fetchable after completion, if you don't specify a per-job `retention.expiresAt` (§8a). Omit if you have no fixed policy. |
| `egressPolicy` | no | Unchanged from v1: one of `NORMAL` (default), `PRIVATE_REQUIRED`, `CUSTOM_PROXY`. |

Once a version is negotiated, every subsequent call (health, search, every
acquire step) carries:

```
Family-Librarian-Protocol-Version: 2
```

A provider that only ever declares `protocolVersions: ["1"]` never receives
this header — Family Librarian falls back to speaking v1 (Appendix A) to it
entirely.

---

## 5. `GET /health`

Response body now carries real information instead of relying on the status
code alone:

```json
{
  "status": "degraded",
  "operations": {
    "search": "available",
    "acquire": "unavailable"
  }
}
```

| Field | Required | Notes |
|---|---|---|
| `status` | no (defaults to `healthy` if body omitted and status is `2xx`) | One of `healthy`, `degraded`, `unhealthy`. Coarse overall judgment — a provider is free to report this loosely; Family Librarian treats `operations` as the more specific signal when the two disagree. |
| `operations.search` / `operations.acquire` | no | One of `available`, `degraded`, `unavailable`. Missing keys are treated as inheriting `status`. This split exists because a provider's local search can keep working while its upstream acquisition path is down — for example, a metadata-index provider (Anna's Archive-shaped) whose local database answers searches fine while the upstream member/download path is rate-limited or offline. Report it accurately; don't collapse to a single boolean. |

A bare `2xx` with no body (the v1 shape) is still accepted and treated as
`{"status": "healthy"}`. Called as part of "Test Connection" and on the
registration's recheck schedule — not polled continuously in the background.

---

## 6. `POST /search`

Request:

```json
{
  "requestId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "mediaType": "ebook",
  "work": {
    "title": "Debt of Honor",
    "subtitle": null,
    "authors": [
      { "name": "Tom Clancy", "role": "author" }
    ],
    "series": [
      { "name": "Jack Ryan", "position": "6" }
    ],
    "language": "en",
    "identifiers": [
      { "scheme": "isbn13", "value": "9780000000000" }
    ]
  },
  "constraints": {
    "languages": ["en"],
    "formats": ["epub", "azw3"],
    "excludeCollections": true
  },
  "pagination": {
    "limit": 50,
    "cursor": null
  }
}
```

| Field | Required | Notes |
|---|---|---|
| `mediaType` | yes | Always `"ebook"` or `"audiobook"` (lowercase) today; treat as an open string going forward. |
| `work.title` | yes | |
| `work.authors` | no (array, possibly empty) | Each entry `{name, role}`. `role` is an open string (`author`, `editor`, `narrator`, `translator`, `contributor`, `other`, ...) — an unrecognized role must not break your parsing. Do not assume exactly one author. |
| `work.series` | no (array, possibly empty) | Each entry `{name, position}`. `position` is a flexible string — expect `"6"`, `"1.5"`, `"0"`, `"prequel"`, `"novella"`, etc., never a constrained numeric. A book may belong to more than one series. |
| `work.language` | no | A BCP-47-style tag (`en`, `en-US`, `fr`, ...) when known. |
| `work.identifiers` | no (array, possibly empty) | Each entry `{scheme, value}`. `scheme` is an open string — recognized examples include `isbn13`, `isbn10`, `asin`, `md5`, `openlibrary`, `hardcover`, `goodreads`, but treat it as extensible; an unrecognized scheme must be safely ignored, not rejected. Replaces v1's fixed `identifiers.isbn13` field — a request with no known identifiers omits the array or sends it empty. |
| `constraints` | no | Provider-side filtering hints (`languages`, `formats`, `excludeCollections`, etc.) — entirely optional to honor. Family Librarian validates results independently regardless of what you filter. You may report which you actually applied via a top-level `appliedConstraints: ["language", "format"]` in the response; omitting it is fine. |
| `pagination.limit` / `pagination.cursor` | no | Cursor-based. Omit `pagination` entirely (or support only `limit`) if you don't implement paging — see the response shape below for what that looks like. |

Response:

```json
{
  "candidates": [
    {
      "providerReference": "abc123",
      "candidateRevision": null,
      "acquireToken": null,

      "work": {
        "title": "Debt of Honor",
        "subtitle": null,
        "authors": [{ "name": "Tom Clancy", "role": "author" }],
        "series": [{ "name": "Jack Ryan", "position": "6" }]
      },
      "edition": {
        "language": "en",
        "publicationYear": 1994,
        "publisher": "Putnam",
        "identifiers": [{ "scheme": "isbn13", "value": "9780000000000" }]
      },
      "release": {
        "name": "Tom.Clancy.Debt.of.Honor.RETAIL.EPUB",
        "format": "epub",
        "sizeBytes": 1452821,
        "isCollection": false,
        "partCount": 1,
        "isSample": false,
        "isAbridged": null,
        "isUnabridged": null,
        "qualityTags": ["retail"],
        "ageDays": null
      },

      "extensions": {}
    }
  ],
  "nextCursor": "opaque-token",
  "totalEstimate": 342
}
```

| Field | Required | Notes |
|---|---|---|
| `providerReference` | **yes** | A candidate missing this is silently dropped by the client. Must be an opaque, stable string you can resolve again in `/acquire` — it does not need to mean anything to Family Librarian, and it does **not** need to be a download URL. It is fine for it to be a content hash (Anna's Archive: MD5), a release GUID (Newznab), or any other stable identifier you can look up later. |
| `candidateRevision` | no | Opaque version marker for this candidate's underlying record. See §8's staleness-conflict behavior. Most providers can omit this — see the note there. |
| `acquireToken` | no | Opaque state you want carried forward unchanged to `/acquire`, for a provider that cannot cheaply re-derive everything from `providerReference` alone at acquire time (e.g. a stateless scraper that captured ephemeral session data during search). A provider backed by its own local index (Anna's Archive-shaped) or a queryable-by-GUID indexer (Newznab-shaped) typically does not need this — it can simply re-resolve by `providerReference` when `/acquire` is called. Family Librarian stores and returns this value unchanged, never inspects or logs it. |
| `work`, `edition`, `release` | no, but populate what you have | Structured evidence, not a trusted verdict — Family Librarian performs its own match decision using this evidence (§7). `work`/`edition` mirror the request shape (structured authors/series/identifiers). `release` carries release-level facts distinct from the canonical book: original release/file name, format, size, whether it's a multi-book collection or a sample, part count, abridged/unabridged status, free-form quality tags, and age. This is what lets Family Librarian reject a 20-book collection when one book was requested, a sample, or a mismatched abridgement, without you having to make that judgment yourself. |
| `extensions` | no | Namespaced provider-specific data — see §11. |

| Envelope field | Required | Notes |
|---|---|---|
| `candidates` | yes (array, possibly empty) | **No matches:** return `{ "candidates": [] }` with `200 OK` — still exactly correct and expected, no distinct "not found" status. **Ambiguity:** if you can't tell which of several results is right, return all of them rather than guessing one; Family Librarian's re-verification only ever trusts a single, corroborated candidate. |
| `nextCursor` | no | Present when more results exist beyond `limit`; pass it back as the next request's `pagination.cursor`. Omit or `null` when there are no more results. |
| `totalEstimate` | no | May be omitted entirely if you don't know or don't want to compute it. |

---

## 7. How your results get used before `/acquire`

Unchanged in principle from v1, now with richer evidence to work with.
Family Librarian never fetches purely on your say-so. After `/search`
returns, it independently checks your candidates' evidence — identifiers
first, then title/author/series/language — against what was actually
requested, and assigns its own internal decision (auto-accept, requires
review, or reject) with an explainable evidence list. A provider-side
ranking score, if you supply one via `extensions`, may help you order
results but is never treated as Family Librarian's match confidence.

Implication for you: the more accurately and completely you populate
`work`/`edition`/`release` evidence, the more often a correct result can be
used without friction. There's no benefit to guessing a single "best" result
when you're unsure — return everything plausible (§6's ambiguity note).

---

## 8. `POST /acquire` and the job lifecycle

Request:

```json
{
  "requestId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "candidateReference": "abc123",
  "candidateRevision": null,
  "acquireToken": null,
  "mediaType": "ebook"
}
```

Header, **required**:

```
Idempotency-Key: 6f2c1e6a-...-client-generated-uuid
```

Family Librarian generates one key per logical acquisition attempt and
resends the same value on any retry of that same attempt (e.g. after a lost
response). If you have already accepted this key, return the existing job's
current representation — same status code as the original accept — rather
than starting a second, duplicate acquisition. Remember a key for at least
as long as you retain the job/outputs it produced. If the same key is
reused for a request that is materially different from the one you first
accepted it for, reject with `409`.

`candidateRevision` and `acquireToken`, if you returned them from `/search`,
are sent back unchanged. If you never return them, you will never receive
them here either, and nothing about this section applies to you.

### Staleness: `candidateRevision` mismatch

If `candidateRevision` is present and no longer matches your current record
for that candidate, respond synchronously:

```
409 Conflict
Content-Type: application/problem+json
```

```json
{
  "code": "CANDIDATE_CHANGED",
  "message": "The candidate has changed since it was returned by search.",
  "retryable": false
}
```

`retryable: false` here means *don't retry this exact acquire call with this
stale revision* — it does **not** mean the requested book is unobtainable.
Family Librarian's own behavior on receiving this is to invalidate the
candidate and re-search, not give up. The same `CANDIDATE_CHANGED` code is
equally valid inside an async job's `error` object (below) if you only
discover the staleness after already accepting the job. This entire
mechanism is opt-in: if you never send `candidateRevision`, you are not
expected to detect or report this condition at all.

An unrecognized `candidateReference` (stale or never valid) that isn't a
revision conflict should still return a plain `404`.

### Accepting the job

Any `2xx` status containing a `jobId`:

```json
{ "jobId": "a1b2c3", "state": "queued" }
```

`202 Accepted` is the convention — use it unless you have a reason not to.

### Polling: `GET /acquire/{jobId}`

```json
{
  "jobId": "a1b2c3",
  "state": "running",
  "phase": "downloading",

  "progress": {
    "percent": 62.4,
    "bytesCompleted": 628000000,
    "bytesTotal": 1000000000,
    "message": null
  }
}
```

| Field | Required | Notes |
|---|---|---|
| `state` | yes | One of exactly six values: `queued`, `running`, `waiting`, `completed`, `failed`, `cancelled`. Small and closed on purpose — Family Librarian validates this set strictly. Case-insensitive. |
| `phase` | no | An **open string** describing what's actually happening — `resolving`, `downloading`, `repairing`, `extracting`, `user-interaction`, or anything else meaningful to you. Never validated against a fixed list on either side; an unrecognized phase is simply displayed as-is. This is how a torrent provider can expose `checking`, a Usenet provider can expose `repairing`/`extracting`, and a browser-automation provider can expose `browser-queue`, all without changing the protocol. |
| `interaction` | required when `state = waiting` and the wait is on the user | `{type, message, expiresAt, resumeSupported, actionUrl}`. `type` is an open string (`browser`, `login`, `mfa`, `captcha`, `approval`, `device-code`, `other`, ...). `actionUrl` is where a human completes the step; `resumeSupported: true` means you'll pick the job back up automatically once they do — Family Librarian does not send you a separate "resume" call. Never put a provider cookie or authenticated session token in this object; you own your own session state. |
| `progress` | no | `percent`/`bytesCompleted`/`bytesTotal`/`message`, any or all of which may be omitted if you don't know them. |
| `error` | present when `state = failed` | See below. |

There is no fixed end-to-end time budget. A job may legitimately run for
minutes or hours (torrent, Usenet repair/extract, a slow browser-gated
download, a large audiobook) — design for that rather than assuming a short
window. Each individual HTTP call (including this one) still has its own
short transport timeout (§9) independent of how long the *job* takes overall.

**Polling cadence:** respond with a `Retry-After` header (seconds) and/or a
body-level `pollAfterSeconds` when you know your own job doesn't need to be
checked again immediately — e.g. `2` for an active direct download, `30`
for a browser waiting-queue, much longer for a step waiting on a human.
Family Librarian enforces its own reasonable minimum/maximum bounds around
whatever you suggest; it will not poll faster than a sane floor even if you
ask for less, and will not wait indefinitely even if you ask for more.

### Structured failure

```json
{
  "state": "failed",
  "error": {
    "code": "UPSTREAM_RATE_LIMITED",
    "message": "Provider temporarily rate limited the request.",
    "retryable": true,
    "retryAfterSeconds": 300,
    "details": {}
  }
}
```

`code` is an open string on the wire — Family Librarian recognizes at least:
`NOT_FOUND`, `AUTH_REQUIRED`, `AUTH_FAILED`, `RATE_LIMITED`,
`UPSTREAM_UNAVAILABLE`, `NETWORK_FAILURE`, `DOWNLOAD_FAILED`,
`HASH_MISMATCH`, `UNSUPPORTED_FORMAT`, `CONTENT_UNAVAILABLE`, `TIMEOUT`,
`CANCELLED`, `CANDIDATE_CHANGED`, `PROVIDER_INTERNAL_ERROR` — but an
unrecognized code must still parse cleanly; treat the vocabulary above as
the currently-understood subset, not a closed enum. `retryable` and
`retryAfterSeconds` tell Family Librarian whether and when retrying the
*same* acquisition attempt is worth it; they say nothing about the
underlying book request's viability beyond that one attempt.

### HTTP status alignment

Prefer real HTTP semantics over a bespoke scheme:

| Status | Meaning |
|---|---|
| `401` | Authentication failure |
| `403` | Authorization failure |
| `404` | Unknown candidate/job |
| `409` | Candidate changed / idempotency-key conflict |
| `429` | Rate limited |
| `503` | Temporarily unavailable |

For any synchronous failure of this kind, prefer
`Content-Type: application/problem+json` with a body containing at least
`code` (from the vocabulary above), so a client that wants more than the
bare status code can get it without guessing.

---

## 8a. Outputs

Once `state = completed`, fetch the job's outputs:

```
GET /acquire/{jobId}/outputs
GET /acquire/{jobId}/outputs/{outputId}
```

`GET .../outputs` response:

```json
{
  "outputs": [
    {
      "id": "primary",
      "kind": "file",
      "role": "ebook",
      "filename": "Debt of Honor.epub",
      "contentType": "application/epub+zip",
      "sizeBytes": 1452821,
      "checksums": [
        { "algorithm": "sha256", "value": "..." }
      ],
      "retention": { "expiresAt": null }
    }
  ]
}
```

| Field | Required | Notes |
|---|---|---|
| `kind` | yes | `file` (bytes you serve directly via `GET .../outputs/{outputId}`), `uri` (e.g. a magnet link — set `uriScheme`, no bytes to fetch from you), or `descriptor` (bytes representing a pointer for another system to act on, e.g. a `.nzb` or `.torrent` file — served the same way as `file`). Prefer returning descriptor/file bytes over an arbitrary external URL where you reasonably can; it keeps the trust boundary simple and avoids handing Family Librarian a URL to blindly fetch. |
| `role` | no | An open string — `primary`, `ebook`, `audio-part`, `cover`, `metadata`, `checksum`, `archive`, `supplementary`, `torrent`, `usenet-descriptor`, `other`, or anything else meaningful. An unrecognized role must not break the client. |
| `filename` / `contentType` / `sizeBytes` | no, but populate for `file`/`descriptor` kinds | |
| `uriScheme` | no, required in practice for `uri` kind | e.g. `magnet` |
| `checksums` | no (array, possibly empty) | `{algorithm, value}` pairs, extensible — not a fixed field per hash type. Family Librarian independently computes its own checksum of whatever it actually downloads; yours is a cross-check, not a substitute. Checksums prove file identity/integrity, not book identity. |
| `retention` | no | `expiresAt`, if this specific output has a different retention window than the manifest-level `outputRetentionSeconds`. |

A job may report more than one output — an ebook plus a cover plus a
metadata sidecar, or the individual tracks of an audiobook, or a `.torrent`
descriptor alongside nothing else. This replaces v1's assumption of exactly
one file per job. A legacy single-artifact `GET /acquire/{jobId}/artifact`
endpoint may still be exposed for a transitional period if convenient, but a
v2-negotiated client always prefers `/outputs`.

---

## 8b. Cancel vs. cleanup

```
POST /acquire/{jobId}/cancel
```

Means: try to stop active work. The job may still transition to `cancelled`
or, if it was too far along to stop cleanly, `failed` — either is
acceptable, and Family Librarian does not require this call to succeed
synchronously.

```
DELETE /acquire/{jobId}
```

Means: Family Librarian is finished with this job; release whatever you were
retaining for it (downloaded artifacts, browser session state, temporary
directories, downloader entries). The job and its outputs may no longer be
queryable afterward. This is distinct from cancel — a completed job that
Family Librarian has already fetched outputs from still gets a `DELETE` to
release your copy.

Both are idempotent and best-effort: respond `204 No Content` whether or not
there was anything to actually do, and expect repeat calls.

---

## 9. Timeouts and size limits, at a glance

| What | Limit |
|---|---|
| Any single HTTP call (manifest/health/search/acquire-POST/job-status GET/outputs GET/cancel/delete) | 20 seconds |
| Manifest/search/job-status/outputs JSON response body | 10 MB |
| Acquire job, end to end | No fixed budget — bounded only by your own declared/implied retention. Design for jobs that may run minutes to hours. |
| Poll cadence | Driven by your `Retry-After`/`pollAfterSeconds` hint, within Family Librarian's own sane floor/ceiling |
| Idempotency-Key retention | Remember a key at least as long as you retain the job/outputs it produced |
| Artifact/output download | No size cap in the HTTP client itself (downstream validation/quarantine steps apply their own limits) |

---

## 10. Error handling summary

Superseded by §8's structured-error and HTTP-status-alignment content — that
is the authoritative behavior now. In short: synchronous failures should use
the HTTP status table in §8 plus a `code`-bearing `problem+json` body where
practical; asynchronous job failures use the `error` object in the job
status response. Unknown `code` values, unknown `phase` strings, and unknown
JSON fields anywhere in this protocol must always be tolerated, never treated
as a parse failure.

---

## 11. Extensions and additive-compatibility rules

Provider-specific data that doesn't fit anywhere above goes in a namespaced
`extensions` object — available on search candidates, the manifest, and job
status — rather than as a new top-level field or an unstructured shared
`metadata` dumping ground:

```json
{
  "extensions": {
    "net.familylibrarian.anna": { "collection": "example", "originalPath": "..." },
    "org.newznab": { "categoryIds": [7020] }
  }
}
```

Use a reverse-DNS-ish namespace as convention, not an enforced format.

**The hard rule:** unknown namespaces, unknown fields, unknown enum/role/
scheme/phase values, and unknown extension content must always be tolerated
— round-tripped or safely ignored, never a parse failure — by both sides.
And `extensions` content must **never** be used by Family Librarian to
influence a matching or trust decision. It is inert diagnostic/provider data,
not a signal the core protocol depends on. If something in `extensions`
turns out to be broadly useful across provider types, it can graduate to a
standardized field in a future additive revision without anyone's existing
integration breaking.

This additive-compatibility principle applies throughout the whole document:
unknown JSON fields, missing optional fields, unknown capability/phase/role/
scheme/output-role/health-component values must always be tolerated. Only a
genuinely incompatible wire-semantics change should ever require another
major protocol version bump.

---

## 12. What's intentionally not covered here

Deferred, not because they're unimportant forever, but because nothing in
the two providers this revision was designed against (Anna's Archive,
Usenet/Newznab) actually requires them yet, and building them speculatively
risks guessing wrong:

- **A public `/metrics` endpoint or a protocol-specific distributed-tracing
  scheme.** Conventional operational instrumentation (Prometheus/
  OpenTelemetry-shaped) is fine to run on your own side, but is not part of
  this wire contract, and Family Librarian does not need to receive it. In
  particular, a metadata-index provider's own internal search/import stage
  timing (useful for e.g. an eventual SQLite-vs-PostgreSQL decision) is
  entirely that provider's own implementation concern.
- **Batch search or batch acquisition.**
- **HTTP Range/resumable transfer** for large file outputs.
- **Full collection-member enumeration** (a provider *may* still describe
  that a release is a collection via `release.isCollection`/`partCount` —
  just not required to list every member's own title/author).
- **A generic provider-configuration-schema or administrative-actions API.**
  Expose your own `managementUrl`/`documentationUrl` if you have an admin
  surface; Family Librarian will link to it rather than modeling it.
- **Provider concurrency-limit negotiation.**

Each of these has a reachable path in later via the `extensions` namespace
or an additive field if real usage ever justifies it — nothing here forecloses
them, they're just not required now.

---

# Appendix A: Protocol version 1 (legacy)

Frozen, reference-only. This is the contract actually implemented by
`samples/FamilyLibrarian.SampleProvider` and `ExternalProviderClient` as of
this writing; it will be retired once both are upgraded to protocol version
2 above.

## A.1 `GET /manifest`

Response:

```json
{
  "protocolVersion": "1",
  "id": "your-provider-id",
  "name": "Your Provider Name",
  "version": "1.0.0",
  "capabilities": ["ebook", "search", "acquire"],
  "egressPolicy": "NORMAL"
}
```

| Field | Required | Notes |
|---|---|---|
| `protocolVersion` | no | Defaults to `"1"` if omitted. Not enforced — a mismatched value is stored and displayed to the admin but does not block calls. |
| `id`, `name`, `version` | no | Default to empty string if omitted. Purely informational. |
| `capabilities` | no | Free-form strings, purely informational — not used to gate which endpoints are called. |
| `egressPolicy` | no | One of `NORMAL` (default), `PRIVATE_REQUIRED`, `CUSTOM_PROXY`. Anything else (including lowercase) is treated as `NORMAL`. |

## A.2 `GET /health`

Any `2xx` means healthy; anything else (or a connection failure) means
unhealthy. No required body.

## A.3 `POST /search`

Request:

```json
{
  "requestId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "mediaType": "ebook",
  "work": {
    "title": "Example Book",
    "authors": ["Example Author"],
    "identifiers": { "isbn13": "9780000000000" }
  }
}
```

Response:

```json
{
  "candidates": [
    {
      "providerReference": "abc123",
      "title": "Example Book",
      "author": "Example Author",
      "format": "epub",
      "sizeBytes": 123456789,
      "metadata": {}
    }
  ]
}
```

| Field | Required | Notes |
|---|---|---|
| `providerReference` | **yes** | A candidate missing this is silently dropped. Opaque, stable, resolvable again in `/acquire`. |
| `title` | no (defaults to `""`) | Used directly in match verification. |
| `author` | no | Same. |
| `format`, `sizeBytes` | no | Informational only. |
| `metadata` | no | Opaque JSON, round-tripped as a string; not interpreted. |

No matches → `{ "candidates": [] }` with `200 OK`. Ambiguity → return all
plausible candidates rather than guessing one.

## A.4 `POST /acquire`

Request:

```json
{
  "requestId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "candidateReference": "abc123",
  "mediaType": "ebook"
}
```

`candidateReference` is exactly the string returned from `/search`.

Response: any `2xx` status containing a `jobId`:

```json
{ "jobId": "a1b2c3", "status": "InProgress" }
```

`202 Accepted` is the convention. An unrecognized `candidateReference`
returns `404`.

### Polling: `GET /acquire/{jobId}`

```json
{ "jobId": "a1b2c3", "status": "InProgress" }
```

`status` is one of `InProgress`, `Completed`, or `Failed`
(case-insensitive). On `Failed`, an optional `failureReason` string is
surfaced as the error message:

```json
{ "jobId": "a1b2c3", "status": "Failed", "failureReason": "Upstream source is down." }
```

Family Librarian polls every **2 seconds**, for up to **90 seconds total**
from the initial `/acquire` call. If the job hasn't reached `Completed`
(or `Failed`) within that budget, Family Librarian gives up, best-effort
sends `DELETE /acquire/{jobId}`, and reports a timeout.

### `GET /acquire/{jobId}/artifact`

Called once `status` is `Completed`. Response: the file as a binary stream,
`200 OK`, with `Content-Type` set to the real MIME type and
`Content-Disposition: attachment; filename="..."` set to the filename
Family Librarian will use downstream (omitting it falls back to
`{candidateReference}.bin`, which is unlikely to pass content-type
validation). The file must be genuinely valid for its declared format —
validation inspects actual bytes, not just headers/extension.

### `DELETE /acquire/{jobId}`

Best-effort cancellation. Respond `204 No Content` regardless of whether the
underlying work actually stopped. Never used to fetch a partial file.

## A.5 Timeouts and size limits

| What | Limit |
|---|---|
| Any single HTTP call | 20 seconds |
| Manifest/search/job-status JSON response body | 10 MB |
| Acquire job, end to end | 90 seconds total budget |
| Poll interval while `InProgress` | 2 seconds |
| Artifact download | No size cap in the HTTP client itself |

## A.6 Error handling summary

| Situation | What to return | What happens |
|---|---|---|
| No search matches | `200` + `{ "candidates": [] }` | Shows no external option for that provider. |
| Unknown `candidateReference` on acquire | `404` | Hard failure; no job created. |
| Acquire job fails after being accepted | `status: "Failed"` (+ optional `failureReason`) | Surfaces `failureReason`; no artifact fetched. |
| Any other unexpected failure | Any non-`2xx` | Hard failure; status code not otherwise distinguished. |
| Malformed/missing required JSON field | — | Also a hard failure. |

## A.7 What v1 intentionally didn't cover

- Automated discovery/catalog install (`manifestUrl`-based one-click
  install) — prototyped, on hold.
- Capability-based call gating and protocol-version negotiation — both
  fields existed on the manifest but were informational only. (Resolved in
  protocol version 2, above.)
