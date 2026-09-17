# External Provider HTTP Protocol Reference

**Status:** Reflects shipped behavior on `feature/alpha-5`, protocol version `1`.
**Audience:** someone building a standalone HTTP service (any language, any
runtime — a Docker container is the expected shape) that Family Librarian will
register as an *external provider* and call over the network.

This is the precise wire contract. For the design rationale behind the
provider-capability model in general, see
[03-provider-api-contracts.md §5](03-provider-api-contracts.md#5-acquisition-provider) —
but treat *this* document as authoritative for the actual bytes on the wire;
the other one is a draft written before this contract was implemented and has
drifted from it in places (no `series`/`seriesPosition` fields are actually
sent, for example).

A complete, working, cross-checked reference implementation lives at
[`samples/FamilyLibrarian.SampleProvider`](../samples/FamilyLibrarian.SampleProvider) —
read its source if anything here is ambiguous; that project's own conformance
tests (`ExternalProviderClientTests`) run the real client in this repository
against it.

---

## 1. The trust model

Family Librarian never runs your code. It only ever sends HTTP requests to
your service and reads HTTP responses. Your service, in turn, never receives
Family Librarian's database, other providers' credentials, or the destination
library — it receives only the request's title/author/ISBN, and returns
candidates and (eventually) a file.

Concretely:

- You run as a separate process — a container, VM, or any host reachable over
  HTTP from Family Librarian's server. There is no in-process plugin model
  today.
- Family Librarian treats your search results with real skepticism: it
  independently re-verifies your candidates' title/author (and, when it has
  one, the request's own ISBN-13) before ever calling `/acquire`. A
  plausible-looking wrong match does not get fetched silently — an
  administrator must explicitly confirm it first. See §7.
- An admin registers you by base URL (plus an optional API key) through
  **Admin → External providers**. There is currently no automated
  discovery/install mechanism — that is on hold pending a broader design
  question, not something you need to support.

---

## 2. Base URL and routing

The admin-entered base URL is used as-is; every endpoint below is a path
relative to it (e.g. `https://provider.example.test/api/v2` + `/manifest` →
`https://provider.example.test/api/v2/manifest`). Your base URL may include a
path prefix.

Every call — manifest, health, search, and every step of acquire — goes to
the same base URL and is routed identically per your manifest's declared
`egressPolicy` (§4). There is no per-request routing choice.

---

## 3. Authentication

If the admin configured an API key for your registration, every request
carries:

```
Authorization: Bearer <api-key>
```

If no key was configured, the header is omitted. Whether and how you enforce
this is entirely up to you — reject unauthenticated/mismatched requests with
`401`.

---

## 4. `GET /manifest`

Called on registration and whenever an admin clicks "Test Connection."

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
| `protocolVersion` | no | Defaults to `"1"` if omitted. Not currently enforced — a mismatched value is stored and displayed to the admin but does not block calls. |
| `id`, `name`, `version` | no | Default to empty string if omitted. Purely informational (shown in the admin UI). |
| `capabilities` | no | Free-form strings, purely informational today — not used to gate which endpoints Family Librarian calls. Declare them anyway; capability-based gating is expected to matter more later. |
| `egressPolicy` | no | One of `NORMAL` (default), `PRIVATE_REQUIRED`, `CUSTOM_PROXY` — see below. Anything else (including a lowercase value) is treated as `NORMAL`. |

`egressPolicy` is *your* declared requirement for how Family Librarian must
reach you — not a request-scoped choice. `NORMAL` means Family Librarian
connects directly. `PRIVATE_REQUIRED`/`CUSTOM_PROXY` mean every call to you
(manifest, health, search, and every acquire step) is routed through a
server-side-configured gateway/proxy instead, and if that gateway is
unavailable, calls to you fail closed rather than silently falling back to a
direct connection. (The gateway/VPN design itself is documented separately and
is currently being revisited — declare the policy your deployment actually
needs; the enforcement behavior described here is what's shipped.)

## 5. `GET /health`

Response: any `2xx` means healthy; anything else (or a connection failure)
means unhealthy. No required body. Called as part of "Test Connection," not
polled continuously in the background.

---

## 6. `POST /search`

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

- `mediaType` is always `"ebook"` or `"audiobook"` (lowercase).
- `authors` is always an array (possibly empty) — do not assume exactly one.
- `identifiers.isbn13` is `null` when Family Librarian doesn't know an ISBN
  for this request. Treat a missing key the same as a `null` value.

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
| `providerReference` | **yes** | A candidate missing this is silently dropped by the client. Must be an opaque, stable string you can resolve again in `/acquire` — it does not need to mean anything to Family Librarian. |
| `title` | no (defaults to `""`) | Used directly in match verification (§7) — return the real title. |
| `author` | no | Same — return the real primary author if you have one. |
| `format`, `sizeBytes` | no | Informational only today. |
| `metadata` | no | Opaque JSON, round-tripped as a string; not currently interpreted by Family Librarian. |

**No matches:** return `{ "candidates": [] }` with `200 OK`. There is no
distinct "not found" status for search — an empty array is exactly correct
and expected.

**Ambiguity:** if you can't tell which of several results is right, return
all of them rather than guessing one. Family Librarian's own re-verification
(§7) will only ever trust a single, corroborated candidate — returning
several plausible ones is treated more safely than confidently returning one
wrong one.

There is currently no per-candidate identifier field beyond `providerReference`
— you cannot attach your own ISBN to an individual candidate on the wire today.

---

## 7. How your results get used before `/acquire`

Family Librarian never fetches purely on your say-so. After `/search`
returns, it independently checks your candidates' `title`/`author` (and the
request's own known ISBN-13, if any) against what was actually requested:

- If Family Librarian knows an ISBN-13 for the request **and** your search
  returned exactly one candidate **and** that candidate's title/author
  plausibly corroborate the request, the match is trusted immediately — no
  administrator action needed.
- Every other outcome (title/author-only match, multiple candidates, no
  corroboration, or no match at all) requires an administrator to explicitly
  confirm before Family Librarian will call `/acquire` for it.

Implication for you: the more accurately you populate `title`/`author`, the
more often a correct result can be used without friction. There's no benefit
to guessing a single "best" result when you're unsure — see the ambiguity
note in §6.

---

## 8. `POST /acquire`

Request:

```json
{
  "requestId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "candidateReference": "abc123",
  "mediaType": "ebook"
}
```

`candidateReference` is exactly the string you returned from `/search`.

Response: any `2xx` status containing a `jobId`:

```json
{ "jobId": "a1b2c3", "status": "InProgress" }
```

`202 Accepted` is the convention (and what the reference implementation
returns) — Family Librarian only requires `2xx` plus a `jobId` field, but use
`202` unless you have a reason not to.

An unrecognized `candidateReference` should return `404` — Family Librarian
treats any non-`2xx` response as a hard failure and does not currently parse
or surface an error-body `message` field anywhere in its own UI, but returning
one anyway (e.g. `{ "message": "Unknown candidateReference." }`) helps anyone
debugging with `curl`.

### Polling: `GET /acquire/{jobId}`

```json
{ "jobId": "a1b2c3", "status": "InProgress" }
```

`status` is one of `InProgress`, `Completed`, or `Failed` (case-insensitive).
On `Failed`, an optional `failureReason` string is surfaced as the error
message on the Family Librarian side:

```json
{ "jobId": "a1b2c3", "status": "Failed", "failureReason": "Upstream source is down." }
```

Family Librarian polls this endpoint every **2 seconds**, for up to **90
seconds total** from the initial `/acquire` call. If your job hasn't reached
`Completed` (or `Failed`) within that budget, Family Librarian gives up,
best-effort sends `DELETE /acquire/{jobId}` (§below), and reports a timeout to
its own caller. Design your job to either finish or fail within that window,
or expect the request to time out.

### `GET /acquire/{jobId}/artifact`

Called once `status` is `Completed`. Response: the file as a binary stream,
`200 OK`, with:

- `Content-Type` set to the real MIME type of the file.
- `Content-Disposition: attachment; filename="..."` (or `filename*=`) — the
  filename Family Librarian will use downstream. If omitted, Family Librarian
  falls back to `{candidateReference}.bin`, which is unlikely to pass its own
  file-type validation — always set this header.

The file must be a genuinely valid file of the declared format — Family
Librarian's own content-type/extension validation inspects actual bytes
(magic numbers), not just headers or the extension, on the way into
quarantine.

### `DELETE /acquire/{jobId}`

Best-effort cancellation, called when Family Librarian gives up on a job
(caller cancellation or the 90-second timeout). Respond `204 No Content`
whether or not you actually managed to stop the underlying work — Family
Librarian does not wait on or retry this call, and never uses a partial file
from a job it cancelled.

---

## 9. Timeouts and size limits, at a glance

| What | Limit |
|---|---|
| Any single HTTP call (manifest/health/search/acquire-POST/job-status GET) | 20 seconds |
| Manifest/search/job-status JSON response body | 10 MB |
| Acquire job, end to end (submit → `Completed`/`Failed`) | 90 seconds total budget |
| Poll interval while `InProgress` | 2 seconds |
| Artifact download | No size cap in the HTTP client itself (downstream validation/quarantine steps apply their own limits) |

---

## 10. Error handling summary

Family Librarian has no structured error-code scheme yet — only HTTP status
and, for job failures, `failureReason`:

| Situation | What to return | What Family Librarian does |
|---|---|---|
| No search matches | `200` + `{ "candidates": [] }` | Shows no external option for that provider. |
| Unknown `candidateReference` on acquire | `404` | Treated as a hard failure (`HttpRequestException`); no job is created. |
| Acquire job fails after being accepted | `status: "Failed"` (+ optional `failureReason`) from the status endpoint | Surfaces `failureReason` as the error message; no artifact is fetched. |
| Any other unexpected failure | Any non-`2xx` | Treated as a hard failure; the specific status code isn't currently distinguished beyond success/failure. |
| Malformed/missing JSON where a field is required (e.g. no `jobId` on acquire) | — | Also a hard failure on the Family Librarian side. |

---

## 11. What's intentionally not covered here

- **Automated discovery/catalog install.** A `manifestUrl`-based one-click
  install flow was prototyped but is currently on hold pending a decision
  about whether providers might someday run as sandboxed in-process plugins
  instead of separate services — that would change what "install" should
  mean. Manual registration by base URL (this document) is unaffected and
  remains the supported path.
- **Capability-based call gating and protocol-version negotiation.** Both
  fields exist on the manifest today but are informational only, as noted
  above.
