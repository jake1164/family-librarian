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
| `GET /manifest`                   | Identity, protocol version, and declared capabilities |
| `GET /health`                     | v2 health plus per-operation availability                   |
| `POST /search`                    | v2 work/edition evidence → structured candidate evidence    |
| `POST /acquire`                   | Exact candidate reference/revision/token → durable v2 job   |
| `GET /acquire/{jobId}`            | v2 state, phase, progress, interaction, or structured error |
| `GET /acquire/{jobId}/outputs`    | Describes retained outputs after completion                  |
| `GET /acquire/{jobId}/outputs/{outputId}` | Streams one file output                              |
| `POST /acquire/{jobId}/cancel`    | Best-effort cancellation                                    |
| `DELETE /acquire/{jobId}`         | Best-effort cleanup                                         |

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
