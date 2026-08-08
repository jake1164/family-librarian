# Family Librarian agent guide

All agents working in this repository, including Codex, must read and follow this
file before proposing, changing, or reviewing code.

## Technology baseline

- Target the latest stable supported .NET release and its matching latest stable C# language version. At the time this instruction was written, that is `.NET 10` / `net10.0` and `C# 14` (and .NET 10 is an LTS release).
- Before changing .NET, C#, ASP.NET Core, Blazor, EF Core, Identity, or Docker behavior, consult the current official documentation on Microsoft Learn (`learn.microsoft.com`) and apply the documented security and framework best practices.
- Build the browser application as Blazor WebAssembly. Keep the ASP.NET Core host/API responsible for PostgreSQL, Identity, OIDC, authorization, metadata-provider credentials, and all other secrets. Client-side authorization is presentation only; every protected operation must be authorized by the host API.
- Use MudBlazor for application UI components. Preserve accessibility, responsive behavior, and semantic HTML rather than relying on visual components alone.

## Documentation and dependencies

- Before adding, upgrading, configuring, or using a library, query the Context7 MCP server for its current documentation and compatibility guidance. This includes MudBlazor, EF Core/Npgsql, PostgreSQL-related libraries, test libraries, and any new dependency.
- If Context7 is unavailable, say so in the work summary and use the library's official documentation as the fallback. Do not invent current APIs or version compatibility.
- Prefer official Microsoft Learn documentation for .NET/C# framework behavior even when Context7 has a summary.
- Pin production dependencies to reviewed compatible versions; do not use floating versions.

## Architecture and security

- Keep the system a modular monolith: domain and application logic must not depend on Blazor, EF Core, or vendor SDKs.
- Do not send connection strings, provider credentials, OIDC client secrets, access tokens, or database entities to the WebAssembly client.
- Use cookie-based browser authentication where suitable. Local Identity must work without Authentik; generic OIDC is optional and Authentik is a documented/tested target, not a requirement.
- Apply authorization in server-side application/API handlers, validate all input, use anti-forgery protection for cookie-authenticated state changes, and keep audit/status transitions explicit.

## Delivery workflow

- The application runs through Docker Compose for development and self-hosted deployment. Keep the default runtime limited to the application host/API and PostgreSQL unless a planned slice requires more.
- Build locally through Compose. Release images publish to `ghcr.io/jake1164/family-librarian`; keep image tags immutable and publish through the repository's GitHub Actions credentials.
- Run the relevant build, test, migration, and Compose health checks before reporting implementation work complete.

## Scope discipline

- Read `README.md` and the relevant documents in `docs/` before changing design-sensitive code.
- Do not add acquisition automation, file scanning, Audiobookshelf delivery, device delivery, notifications, or AI features to the initial request/catalog slice unless explicitly requested.
