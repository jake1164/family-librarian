<!-- Codex: Read and follow the repository's AGENTS.md before proposing or changing code. -->

# Family Librarian Planning Documents

This repository contains the current design documents for **Family Librarian**, a self-hosted family ebook and audiobook request-management platform.

## Documents

1. [Product & Architecture Specification](docs/01-product-architecture-spec.md)
2. [Domain Model & Workflow Specification](docs/02-domain-workflows.md)
3. [Provider & API Contract Design](docs/03-provider-api-contracts.md)
4. [Project Name Decision (archived shortlist)](docs/05-project-name-options.md)
5. [Deployment, Backup, and Recovery](docs/06-deployment-and-recovery.md)
6. [UI Conventions](docs/07-ui-conventions.md)

These documents are intended to be living specifications and should be updated as technical spikes and implementation decisions resolve open questions.

## Development startup

Family Librarian targets .NET 10/C# 14 and runs locally through Docker Compose.

```bash
cp .env.example .env
# Edit .env with strong local development passwords.
docker compose up --build
```

The application is then available at `http://localhost:8080`. Compose applies the
checked-in EF Core migration and creates the configured bootstrap administrator on
the first successful start.

## Signing in

There is no sign-up page. The first account is created from the two values you
put in `.env`; everyone else joins by invitation from an administrator:

```bash
ADMIN_EMAIL=you@example.test
ADMIN_PASSWORD=Your-Local-Dev-Pass1!
```

Sign in at `http://localhost:8080/login` with exactly those values. The password
policy requires at least 8 characters; there is no digit/case/symbol requirement.
Five failed attempts lock the account for 15 minutes.

This bootstrap check runs on every start but creates the account **only while
no administrator exists**. So:

- Adding or changing `ADMIN_*` after an admin already exists has no
  effect — the existing account keeps its original password.
- Leaving them blank on the very first start seeds the `User` and `Admin` roles
  but no account, leaving nothing to sign in with. Fill them in and restart; the
  first-admin creation will then run.

To check which account exists, or to confirm it actually ran:

```bash
docker exec family-librarian-postgres-1 \
  psql -U family_librarian -d family_librarian -c 'SELECT "Email" FROM identity.users;'
```

If that returns no rows, no account was created — check that `.env` has both
`ADMIN_*` values and restart the stack. To start over completely, use the
force-rebuild debug configuration below, which drops the database volume.

If you lose the administrator password and no other administrator exists, there
is no self-service recovery: the bootstrap will not re-run. Another administrator
can reset it from the Accounts page, which is a good reason to invite a second
one.

## OIDC and Authentik

The default development and self-hosted stack uses local Identity only; it does
not require Authentik or any other identity provider. Generic OIDC is an
implemented, optional integration. Authentik is a documented/tested target, not
a runtime dependency—the same design supports any standards-compliant OIDC
provider.

When OIDC is enabled, it supplements rather than replaces local sign-in so an
IdP outage or configuration error cannot remove the administrator's **IdP-outage
recovery path**. This does not provide self-service recovery for a lost sole
administrator password; the local-account recovery limits above still apply.
Family Librarian maps validated claims from the configured issuer to its own
`User` and `Admin` roles; it does not put Authentik-specific roles into the
domain model.

Use a separate OIDC client registration, ID/secret, and redirect/sign-out URI for
development, staging, and production. These registrations may be on one shared
Authentik server. The public callback URI must be browser-reachable, and the
configured issuer's discovery, token, and signing-key endpoints must also be
reachable by the Family Librarian host/container. Keep client secrets in the
environment's secret store or local developer-secret mechanism, never in the
repository or browser configuration.

Developers do not need their own Authentik installation. Local Identity is the
normal path; OIDC work may use a shared development client or a disposable IdP.
The ordinary automated test suite uses controlled test identities and must never
depend on Authentik or a reachable OIDC service. A separate opt-in OIDC
integration suite using a disposable provider remains the production-confidence
test to add.

## Adding family members

Go to **Accounts** (administrators only), enter an email address, and create an
invite link. **The link is shown once and cannot be retrieved afterwards** — only
a hash of it is stored — so copy it before leaving the page. If you lose it,
withdraw the invitation and issue another.

Send the link however you normally reach that person. Following it lets them set
their own name and password, which creates their account and signs them in from
then on. Invitations work once, expire after seven days, and can be withdrawn
before they are used.

The page separates **Pending invitations** from a collapsed **Past invitations**
list, so what is still outstanding stays readable. If a link is lost or has
expired, use **New link** on that invitation: it issues a fresh token and
withdraws the old one, so the mislaid link stops working immediately. Inviting an
address that already has an outstanding invitation does the same thing.

Two optional settings, both validated at startup:

```bash
Invitations__LifetimeDays=7                 # 1-90
Invitations__RedemptionAttemptsPerMinute=10 # 1-10000
```

The second one rate-limits the redemption endpoint, which is anonymous by
necessity. Everyone behind a shared public address counts as one caller, so raise
it if a household trips the limit.

There is deliberately no self-registration: for a household, an invitation *is*
the approval, and open signup would add an unauthenticated account-creation
endpoint plus a queue of strangers to sift through. The optional SMTP outbound
provider can send selected request-status messages once configured, but it does
not yet send invitations. Until a separately designed invitation-email workflow
exists, the copy-paste link is the invitation delivery mechanism and remains the
fallback afterwards.

From the same page you can disable or re-enable an account and grant or remove
the administrator role. Disabling takes effect immediately, including on any
session that account already has open. You cannot disable or demote your own
account, and the last remaining administrator cannot be removed — the bootstrap
only runs while no administrator exists, so that would leave no way back in.

### What you can do once signed in

Search the catalog, open a book, save it to the family catalog, then request it as
an ebook, an audiobook, or both, and follow it under **My requests** — where you
can withdraw your interest or ask again. Ordinary requests for the same book
share one request and one acquisition per format. Each participant sees their
own formats and private note; librarians can see all participants. Withdrawing
leaves the shared request open for everyone else. Asking again joins an existing
shared request, or reopens the previous request when no current one exists.

For a different language, edition, narrator, accessibility requirement, or an
unsuitable existing copy, choose the version difference and describe what is
needed. These requests go to librarian review and remain excluded from automatic
acquisition and bulk rechecks. Version details guide human selection; they do
not add automatic edition/language matching. Historical overlapping requests
found during upgrade retain their IDs/files/history and are held for review.

For ebooks, the built-in public-domain Project Gutenberg source is enabled by
default after its local RDF catalogue has finished its first sync: when it finds one high-confidence title-and-author match, Family
Librarian automatically downloads it, applies the security and identity checks,
and sends a clean verified copy to CWA. **My requests** and the book page refresh
while open so the requester can follow safe, plain-language progress without
seeing provider diagnostics.

Administrators also get:

- **Queue**, to review family requests and act on them (add a note, mark
  needs-review/unavailable, or cancel). A persistent in-app admin alert and the
  Queue navigation label show any requests that need review;
- **Metadata providers**, for enabling book-information providers and storing a
  Google Books key;
- **Sources**, for reviewing the built-in Project Gutenberg source and configuring
  external acquisition sources, their private network, and a per-source manual,
  daily, or weekly recheck schedule. The page also shows the latest safe
  automatic-source failure directly, so an operator does not have to trace a
  request timeline to discover it. Project Gutenberg searches use the daily RDF
  catalogue imported into PostgreSQL; actual ebook downloads use configured mirrors.
  Source failures remain visible to administrators but
  do not prevent Work or request pages from loading;
- **Security scans**, for the latest 25, 50 (default), or 100 imported/acquired
  files, including completed scans and deleted-file records. The shared
  SignalR connection delivers admin-only scan updates as they happen; the page shows start,
  completion, and individual check timestamps and reloads after reconnecting.
  Retry, identity review, and deletion actions remain available where appropriate.
  Confirmed malware bytes are deleted automatically; clean, matching EPUB imports
  proceed automatically;
- **Publishing settings**, for configuring CWA and Audiobookshelf; and
- **Publishing activity**, for reviewing each handoff after an approved file was
  sent to either destination, with a **Recheck** action for anything not yet
  confirmed.

The browser maintains one authenticated SignalR connection per tab. My requests,
book request status, admin Queue/Tasks/request details, Security scans, Publishing
activity, source catalog progress, the notification tray, and navigation indicators
subscribe to it instead of polling. The shared connection indicator shows when
updates are unavailable and offers **Refresh**. Initial connection failures retry
with capped backoff; reconnection reloads the open views to recover missed updates.
Data and actions still use the existing authorized HTTP APIs. Private request
updates go only to request participants and current admins; personal notifications remain
private, and source/security/publishing diagnostics remain admin-only.

Each admin request detail page includes an append-only provider-activity
timeline, including no-match, found-candidate, blocked, failed, and acquired
outcomes. External sources default to **Manual**; an admin can opt a source into
daily or weekly discovery rechecks. Those rechecks only surface candidates for
librarian review — they never download an external file automatically.

In VS Code, the Run and Debug selector provides three Compose-backed container
modes:

- `Containers: Debug Web GUI (Docker, refresh application)` preserves the
  database and support containers, but always recreates the web container from
  the newly built application image. It refreshes the local
  `family-librarian:debug-base` image only when that image is missing or was
  built on a previous UTC date.
- `Containers: Debug Web GUI (Docker, force image rebuild)` rebuilds the cached
  prerequisite base and the full debug application image while preserving the
  PostgreSQL and application-data volumes.
- `Containers: Debug Web GUI (Docker, force rebuild / fresh start)` explicitly
  does the same forced image refresh and also removes the database volume. It
  deletes all local users, catalog records, and requests, so use it only when a
  clean database is intended.

Ending any Docker debug session stops the Compose stack but preserves its
containers and database. The next reuse launch starts those same containers.
Use `compose: down (preserve data)` only when you also want to remove the
stopped containers.

All three preLaunch tasks apply `compose.debug-attach.yaml` on top of `compose.yaml`,
which builds the application container from the Dockerfile's `debug` stage. That
stage copies the latest application onto a reusable `debug-base` stage containing
`vsdbg` at `/remote_debugger`. The base is tagged locally and refreshed at most
once per UTC day during ordinary reuse launches. Application source changes sit
above it, so they no longer rerun `apt-get` or download the debugger. A force
image rebuild bypasses the prerequisite cache and refreshes both layers.

This replaced an earlier approach that bind-mounted the host's own `~/.vsdbg`
folder into the container. It worked, but VS Code still ran its copy check on
attach and, when it decided to copy, wrote the entire debugger back out through
that mount: thousands of small files crossing the Docker Desktop filesystem
bridge, which took minutes on Windows. Baking it into a layer removes the copy,
and with it the host-architecture detection the tasks used to need — the stage is
built for the same platform as the container that runs it.

All three attach configurations set `netCore.debuggerPath` to `/remote_debugger/vsdbg`,
and that setting is **required, not cosmetic**. The Containers extension only
takes the fast path when it is present: left unset, the extension downloads vsdbg
to the host, probes the container for it, and prompts *"Attaching to container
requires .NET debugger in the container. Do you want to copy the debugger to the
container?"* — the copy that took minutes. With the path set, it skips acquiring,
probing, and copying entirely and pipes straight to the debugger already in the
image. Baking the debugger in without setting this path is not enough; you need
both.

The debug image is tagged `family-librarian:debug`, separate from
`family-librarian:dev`, and the `debug` stage is declared *before* `final` in the
Dockerfile so that a plain `docker build` — and therefore every release image —
can never pick up the debugger. The matching `compose:` tasks remain available
under **Tasks: Run Task** when you want to start the stack without a debugger.

If a debug session ever hangs with no terminal output, stop it with **Shift+F5**
and check what the container is actually running:

```bash
docker ps --filter name=family-librarian-web --format "{{.Image}}"
```

It must say `family-librarian:debug`. If it says `family-librarian:dev`, the
preLaunch task did not complete, so the container has no debugger in it and the
attach will stall or prompt.

The `watch` task and the server debug launch both load server-side configuration
from the ignored `.env` file, including provider credentials.

### Cross-platform development

The VS Code tasks and launch configurations support Windows, macOS, and Linux
hosts. Install Docker Desktop (Windows/macOS) or Docker Engine (Linux), and use
Linux containers. Container debugging uses the Microsoft Container Tools and C#
extensions; their debugger matches the Linux architecture selected by Docker
(including Apple Silicon).

For Blazor WebAssembly client-side breakpoints, install Google Chrome. Safari
isn't supported by the Blazor VS Code debugger. The `Full stack: server + Blazor
WASM` configuration must run from VS Code on the host OS; Microsoft doesn't
support this client-side debugging scenario from a VS Code Remote WSL session.
Server-side and Docker-container debugging continue to work from WSL.

## Tests

The test baseline uses MSTest 4.3.3 with .NET 10's Microsoft Testing Platform.
Run the full suite with:

```bash
dotnet test --solution FamilyLibrarian.slnx
```

Web integration tests share a disposable PostgreSQL container with a separate
database per class. Their connection strings disable pooling so closed
connections do not retain server slots across classes. Production connections
retain their normal pooling configuration.

### Opt-in browser E2E

The request-to-queue browser test runs against a separately started, **clean**
Compose deployment. After building the test project, install Chromium once using
the Playwright script copied to its output directory:

```bash
pwsh tests/FamilyLibrarian.Web.Tests/bin/Debug/net10.0/playwright.ps1 install chromium
```

Start `docker compose up --build` with a bootstrap administrator, then provide
the public base URL and those administrator credentials only as process
environment variables. The test creates a temporary invited family member,
requests a book through the UI, and verifies that the administrator can review
it in the Queue. It is intentionally inconclusive unless all three variables
are set, so everyday unit and host-integration runs do not require a browser or
credentials.

```bash
FAMILY_LIBRARIAN_E2E_BASE_URL=http://localhost:8080 \
FAMILY_LIBRARIAN_E2E_ADMIN_EMAIL=admin@example.test \
FAMILY_LIBRARIAN_E2E_ADMIN_PASSWORD='replace-with-bootstrap-password' \
dotnet test --project tests/FamilyLibrarian.Web.Tests/FamilyLibrarian.Web.Tests.csproj
```

`FamilyLibrarian.Domain.Tests` begins by enforcing the domain dependency boundary.
Add focused unit tests beside the layer they exercise; use disposable PostgreSQL
integration tests for persistence behavior rather than EF Core's in-memory provider.

### Shared live-update browser regression

With a compatible Playwright Chromium installed, run the isolated regression:

```bash
FAMILY_LIBRARIAN_LIVE_BROWSER_TESTS=1 dotnet test \
  --project tests/FamilyLibrarian.Web.Tests/FamilyLibrarian.Web.Tests.csproj \
  --filter 'FullyQualifiedName~LiveUpdatesBrowserTests'
```

It starts a disposable PostgreSQL database and a local Kestrel test host, verifies
one WebSocket across client-side navigation, then disconnects the browser and
checks that reconnect restores a missed notification. It does not use lab or
production credentials. `FAMILY_LIBRARIAN_E2E_CHROMIUM_EXECUTABLE` optionally points
to an existing compatible Chromium executable instead of Playwright's default.
