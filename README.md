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

## Tests

The test baseline uses MSTest 4.3.3 with .NET 10's Microsoft Testing Platform.
Run the full suite with:

```bash
dotnet test --solution FamilyLibrarian.slnx
```

`FamilyLibrarian.Domain.Tests` begins by enforcing the domain dependency boundary.
Add focused unit tests beside the layer they exercise; use disposable PostgreSQL
integration tests for persistence behavior rather than EF Core's in-memory provider.
