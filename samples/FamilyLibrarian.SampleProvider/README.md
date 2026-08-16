# Family Librarian Sample Provider

A minimal, working reference implementation of Family Librarian's external-provider
protocol (M13). It runs out-of-process — a separate container Family Librarian talks
to over plain HTTP — and never receives the application database, other providers'
credentials, or the destination library. It exists to prove the protocol is real and
implementable, and to give a third party a working starting point in any language.

Two canned public-domain candidates ("Pride and Prejudice", "Frankenstein"), an
optional shared-secret bearer check, and a genuinely asynchronous `/acquire` (a
3-second simulated delay before the job reports `Completed`) so a client has to do
real polling, not just call a synchronous stub.

## The protocol

| Method & path                    | Purpose                                                  |
|-----------------------------------|-----------------------------------------------------------|
| `GET /manifest`                   | Identity, protocol version, declared capabilities, declared egress policy |
| `GET /health`                     | 200 when usable                                            |
| `POST /search`                    | `{ requestId, mediaType, work: { title, authors[], identifiers } }` → `{ candidates: [...] }` |
| `POST /acquire`                   | `{ requestId, candidateReference, mediaType }` → `202 { jobId, status }` |
| `GET /acquire/{jobId}`            | `{ jobId, status: InProgress\|Completed\|Failed, failureReason? }` |
| `GET /acquire/{jobId}/artifact`   | Binary stream, once `status` is `Completed`                |
| `DELETE /acquire/{jobId}`         | Best-effort cancellation                                    |

`egressPolicy` in the manifest is `NORMAL` (default), `PRIVATE_REQUIRED`, or
`CUSTOM_PROXY` — the provider's own declared requirement for how Family Librarian
must route every call to it (search *and* acquire), not a per-request choice.

An optional `Authorization: Bearer <token>` header carries the scoped API key
Family Librarian was given for this registration — checked here only if
`SAMPLE_PROVIDER_API_KEY` is set.

The artifact this provider returns is a genuinely valid (if trivial) EPUB — a real
`PK\x03\x04`-signed ZIP whose first entry is an uncompressed `mimetype` file
containing `application/epub+zip` — so it survives Family Librarian's own
content-type/extension validation on the way into quarantine, the same as any real
provider's file must.

## Running it

```bash
dotnet run --project samples/FamilyLibrarian.SampleProvider
```

or, from the repository root (the build needs `Directory.Build.props` from there):

```bash
docker build -f samples/FamilyLibrarian.SampleProvider/Dockerfile -t family-librarian-sample-provider .
docker run -p 8081:8080 family-librarian-sample-provider
```

Then register it in Family Librarian at **Admin → External providers** with base
URL `http://localhost:8081` (or the container's address on your Compose network),
Test Connection, enable it, and search for "Pride and Prejudice" or "Frankenstein".
