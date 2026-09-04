# Family Librarian agent guide

All agents working in this repository, including Codex, must read and follow this
file before proposing, changing, or reviewing code.

## Technology baseline

- Target the latest stable supported .NET release and its matching latest stable C# language version. At the time this instruction was written, that is `.NET 10` / `net10.0` and `C# 14` (and .NET 10 is an LTS release).
- Before changing .NET, C#, ASP.NET Core, Blazor, EF Core, Identity, or Docker behavior, consult the current official documentation on Microsoft Learn (`learn.microsoft.com`) and apply the documented security and framework best practices.
- Build the browser application as Blazor WebAssembly. Keep the ASP.NET Core host/API responsible for PostgreSQL, Identity, OIDC, authorization, metadata-provider credentials, and all other secrets. Client-side authorization is presentation only; every protected operation must be authorized by the host API.
- Use MudBlazor for application UI components. Preserve accessibility, responsive behavior, and semantic HTML rather than relying on visual components alone.
- Before adding or editing a status/media-type chip (or any small recurring UI element), read `docs/07-ui-conventions.md`. Its rule: chip color always means status, media type is conveyed by icon, and the mapping lives once in `FamilyLibrarian.Web.Client/Theme/MediaTypeVisuals.cs` behind the `FormatStatusChip`/`RequestStatusChip`/`MediaTypeChip` components — never re-derive a status-to-color switch inline in a page.

## Documentation and dependencies

- When a task needs an external tool or service, first inspect the available `mcpjungle` MCP tools and prefer them when they provide the required capability. `mslearn` and `context7` are available through that bridge.
- For Microsoft/.NET/C#/ASP.NET Core/Blazor/EF Core topics, use the configured `mcpjungle` Microsoft Learn tools to confirm current official documentation before implementing or advising.
- Before adding, upgrading, configuring, or using a library, resolve it and retrieve its current documentation through the `mcpjungle` Context7 tools. This includes MudBlazor, EF Core/Npgsql, PostgreSQL-related libraries, test libraries, and any new dependency.
- If Context7 is unavailable, say so in the work summary and use the library's official documentation as the fallback. Do not invent current APIs or version compatibility.
- Prefer official Microsoft Learn documentation for .NET/C# framework behavior even when Context7 has a summary.
- Pin production dependencies to reviewed compatible versions; do not use floating versions.

## Architecture and security

- Keep the system a modular monolith: domain and application logic must not depend on Blazor, EF Core, or vendor SDKs.
- `Program.cs` is host wiring only: configuration, the middleware pipeline, and one `Map<Area>Endpoints()` call per feature area. Route handlers, request/response mapping, and per-area route groups belong in `src/FamilyLibrarian.Web/Endpoints/<Area>Endpoints.cs` as `internal static` extension methods on `IEndpointRouteBuilder` — never as local functions in `Program.cs`. This file reached 2,200 lines and 128 handlers once; adding "just one" handler back is how that happens again. Keep each area's route prefix, role requirement, and anti-forgery filter together in its own class so the area's authorization posture reads in one place.
- Remove a project reference when the last real use of it goes away. An unused `using` is now a build error (see `.editorconfig`), which catches the usual symptom, but the dead `ProjectReference` in the `.csproj` it appeared to justify is not detected by anything — check the project file too.
- Do not send connection strings, provider credentials, OIDC client secrets, access tokens, or database entities to the WebAssembly client.
- Use cookie-based browser authentication where suitable. Local Identity must work without Authentik; generic OIDC is optional and Authentik is a documented/tested target, not a requirement.
- Apply authorization in server-side application/API handlers, validate all input, use anti-forgery protection for cookie-authenticated state changes, and keep audit/status transitions explicit.

## Delivery workflow

- Do not be a yes-person. Give clear, evidence-based feedback and push back when a request conflicts with the architecture, security, current documentation, or the project's stated goals. Explain the trade-off and offer a safer or more maintainable alternative where possible.
- Never create a Git commit without first asking the user for an explicit yes-or-no confirmation in the current conversation. A general request to implement, finish, publish, or push changes is not commit authorization.
- The application runs through Docker Compose for development and self-hosted deployment. Keep the default runtime limited to the application host/API and PostgreSQL unless a planned slice requires more.
- Build locally through Compose. Release images publish to `ghcr.io/jake1164/family-librarian`; keep image tags immutable and publish through the repository's GitHub Actions credentials.
- At the end of every relevant source change, run the appropriate build and tests. The project must build with zero warnings and zero errors, and all relevant tests must complete successfully before reporting the work complete. Run migration and Compose health checks whenever the change affects them.
- Docker-backed `family-librarian-lab` verification runs on `toontown-int-srv2` in `/opt/family-librarian-lab`. Use that host for real Compose integration tests when the local environment lacks Docker; for example, run `./lab run --test-group abs --case ABS-05` from that directory. Do not copy uncommitted files into the shared lab: the selected product branch and the lab checkout must contain the intended committed changes first.
- The Microsoft Learn documentation step above is required *before* writing framework code, not after. A refactor is not an exemption: choosing a base type, interface, receiver type, or API overload is a framework decision even when no behavior changes. State plainly in the work summary whether the check ran — if it did not, say so rather than implying it did.

## AI working files

- Write every agent-generated working file to `.ai_docs/` at the repository root. This covers implementation plans, spikes, design notes and decision records produced during a session, migration or refactor checklists, progress and status trackers, investigation write-ups, and any other scratch markdown an agent creates for its own tracking.
- Never place these files at the repository root, in `docs/`, or beside the code they describe. `docs/` is curated, human-owned project documentation; `.ai_docs/` is agent scratch space.
- `.ai_docs/` is gitignored and is not part of the shipped product. Do not reference it from `README.md`, `docs/`, or source comments, and do not treat anything in it as authoritative over `README.md`, `docs/`, or this file.
- When a document in `.ai_docs/` matures into something the project should keep, propose promoting it into `docs/` explicitly rather than silently moving it.

### Planning and closure discipline

- `.ai_docs/master-delivery-plan.md` is the single authoritative inventory of active, deferred, and blocked work. It is a delivery tracker, not a replacement for the curated product specifications in `docs/`.
- Before creating a new active AI plan or beginning a new implementation slice, add or update its master-plan entry with an identifier, status, scope/exit criterion, and a link to any detailed plan. A detailed plan without a master entry is not an active commitment.
- Keep detailed plans narrow: they may explain decisions and acceptance evidence, but must not become independent backlogs. Update the master entry when scope, status, or completion evidence changes.
- Move a plan to `.ai_docs/done/` only after its stated acceptance criterion is met. Move plans superseded by consolidation to `.ai_docs/archive/`; do not call them done. Retain completed-plan evidence there for later audit.
- At the end of any implementation slice, reconcile the master plan and affected curated documentation. An unchecked plan item may not be silently abandoned: explicitly mark it active, deferred (with prerequisite), blocked, or out of scope.

## Scope discipline

- Read `README.md` and the relevant documents in `docs/` before changing design-sensitive code.
- Do not add acquisition automation, file scanning, Audiobookshelf delivery, device delivery, notifications, or AI features to the initial request/catalog slice unless explicitly requested.
