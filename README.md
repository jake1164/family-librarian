<!-- Codex: Read and follow the repository's AGENTS.md before proposing or changing code. -->

# Family Librarian Planning Documents

This repository contains the current design documents for **Family Librarian**, a self-hosted family ebook and audiobook request-management platform.

## Documents

1. [Product & Architecture Specification](docs/01-product-architecture-spec.md)
2. [Domain Model & Workflow Specification](docs/02-domain-workflows.md)
3. [Provider & API Contract Design](docs/03-provider-api-contracts.md)
4. [V1 Roadmap, Technical Spikes & Backlog](docs/04-v1-roadmap-and-spikes.md)
5. [Initial Implementation Plan](docs/planning/initial-implementation-plan.md)
6. [Project Name Decision (archived shortlist)](docs/05-project-name-options.md)

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

In VS Code, the Run and Debug selector provides the same two Compose-backed
container modes:

- `Containers: Debug Web GUI (Docker, reuse container)` starts the existing
  containers without force-recreating them. It builds if no usable image exists.
- `Containers: Debug Web GUI (Docker, force rebuild / fresh start)` explicitly
  removes the database volume before rebuilding. It deletes all local users,
  catalog records, and requests, so use it only when a clean database is
  intended.

Ending either Docker debug session stops the Compose stack but preserves its
containers and database. The next reuse launch starts those same containers.
Use `compose: down (preserve data)` only when you also want to remove the
stopped containers.

Both preLaunch tasks apply `compose.debug-attach.yaml` on top of `compose.yaml`,
which bind-mounts the `vsdbg` debugger VS Code already downloaded to
`~/.vsdbg/linux-x64/latest` (`%USERPROFILE%\.vsdbg\linux-x64\latest` on
Windows) into the application container at `/remote_debugger`, guaranteeing
it's present before VS Code's own copy-into-container check runs — that check
is unreliable on Windows hosts on its own. VS Code may still prompt to copy
the debugger; accept it (it's a harmless no-op re-copy onto the same mount).
The matching `compose:` tasks remain available under **Tasks: Run Task** when
you want to start the stack without a debugger.

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

`FamilyLibrarian.Domain.Tests` begins by enforcing the domain dependency boundary.
Add focused unit tests beside the layer they exercise; use disposable PostgreSQL
integration tests for persistence behavior rather than EF Core's in-memory provider.
