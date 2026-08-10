# VS Code Run & Debug — Family Librarian

Added `.vscode/launch.json`, `.vscode/tasks.json`, `.vscode/settings.json`,
`.vscode/extensions.json`, `compose.debug.yaml`; extended `.env.example`.

## Where credentials live

`.env` (git-ignored) is the single local credential store, feeding **both** Docker
Compose and the VS Code debugger via `launch.json`'s `envFile`. No secret appears
in `launch.json`, and no `UserSecretsId` is added to any csproj.

`.env` carries two naming styles side by side:

- `POSTGRES_PASSWORD`, `BOOTSTRAP_ADMIN_*` — `${NAME}` interpolation in `compose.yaml`.
- `BootstrapAdmin__Email`, `BootstrapAdmin__Password` — ASP.NET Core configuration
  keys (`__` maps to `:`), read only by the debugger.

Each side ignores the other's keys; both Compose files were verified to still parse.
Avoid `$` and `#` in values — Compose interpolates `$` in `.env`, and both parsers
can treat `#` as a comment.

### Bootstrap admin password policy

Set in `src/FamilyLibrarian.Infrastructure/DependencyInjection.cs`: at least 12
characters with a digit, a lowercase letter, an uppercase letter, and a
non-alphanumeric character. A password that violates it makes startup throw
`Unable to create the bootstrap administrator: ...` from `IdentityInitializer`.
`.env.example` states the full policy and ships a placeholder that satisfies it,
so the first F5 succeeds — but it is a known value and must be changed.

## Why a separate debug database

`compose.yaml` deliberately does not publish PostgreSQL to the host, so a
debugger-launched process cannot reach it. `compose.debug.yaml` runs an isolated
PostgreSQL (Compose project `family-librarian-debug`, own volume) on
`localhost:5432` with the credentials already in `appsettings.Development.json`.
It never starts as part of `docker compose up`.

## Launch configurations

| Name | Purpose |
| --- | --- |
| `Full stack: server + Blazor WASM` (compound) | Server and client breakpoints together. |
| `.NET: Launch Family Librarian (server)` | Server-side only. Fastest loop. |
| `Blazor WASM: Attach to running app` | Attach half of the compound; not for standalone use. |
| `.NET: Debug EF migrations (--migrate)` | Steps through the `--migrate` branch, then exits. |
| `.NET: Attach to process` | Attach to a Compose container or `dotnet watch`. |

The `blazorwasm` debug type supports only `env`, not `envFile` (verified against
the C# extension's contributed schema). So the server is started by the `coreclr`
config — which does support `envFile` — and the WASM debugger attaches to it, the
form documented at
<https://learn.microsoft.com/aspnet/core/blazor/debug#attach-to-an-existing-visual-studio-code-debugging-session>.

App listens on `https://localhost:7080` and `http://localhost:5080` (HTTP 307s to HTTPS).

## Tasks

`build` (default), `test`, `clean`, `watch`, `dev: check .env`, `dev-db: up|down|reset`,
`db: apply migrations`, `dev: prepare`, `compose: up --build`, `compose: down`.

`dev: prepare` (the preLaunchTask) runs: check `.env` → start debug DB → migrate → build.
It fails fast with a copy-paste remedy if `.env` is missing, because `envFile` errors
on a missing file.

## Prerequisites

- `cp .env.example .env`, then edit the values.
- `dotnet dev-certs https --trust`.
- Docker running.

## Fixed: unauthenticated API calls returned HTML instead of 401

`ApiAuthenticationStateProvider.GetAuthenticationStateAsync` crashed the render with
`JsonException: ExpectedStartOfValueNotFound, <`.

Cause: .NET 10 returns 401 rather than redirecting only for endpoints carrying
`IApiEndpointMetadata`, which is inferred from typed return signatures. The handlers
in `Program.cs` are declared `Task<IResult>` returning `Results.Ok(...)`, so no
metadata was attached, the cookie handler redirected to `/Account/Login`, that route
does not exist, `MapFallbackToFile` answered with `index.html`, and the client
deserialized `<!DOCTYPE html>` as JSON. The client's `catch` only handles
`HttpRequestException` for 401/403, so the `JsonException` escaped.

Fix in `DependencyInjection.cs`: `ConfigureApplicationCookie` overrides
`OnRedirectToLogin`/`OnRedirectToAccessDenied` to set 401/403. Correct here because
the app has no server-rendered pages — every route is the SPA or an API.

Alternative considered: convert each handler to typed results
(`Results<Ok<CurrentUserResponse>, UnauthorizedHttpResult>`) so the framework infers
the metadata natively. More idiomatic for .NET 10, but touches every endpoint
signature and still leaves non-API routes redirecting to a page that does not exist.

## Outstanding: client-side breakpoints need one source change

`src/FamilyLibrarian.Web/Program.cs` never calls `UseWebAssemblyDebugging()`. That
middleware hosts the debug proxy Blazor WebAssembly debugging depends on; the
required `Microsoft.AspNetCore.Components.WebAssembly.Server` package is already
referenced. Server-side debugging works today; **client-side breakpoints will not
bind until this is added**, after the `var app = builder.Build();` line:

```csharp
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
```

Left unapplied — it is a change to Program.cs beyond the requested scope.

## Fixed: container attach failed on Apple Silicon

`tasks.json` hard-coded `VSDBG_HOST_DIR` to `~/.vsdbg/linux-x64/latest` for every
non-Windows host. Compose builds the image for the host's native platform, so an
arm64 Mac gets an arm64 container, and the x86-64 debugger bind-mounted at
`/remote_debugger` died with
`rosetta error: failed to open elf at /lib64/ld-linux-x86-64.so.2`. Windows was
unaffected because its container is linux/amd64.

`compose: reuse container` and `compose: force rebuild` now select `linux-arm64`
or `linux-x64` from `uname -m`, and fail with a `getvsdbg.sh` command if that RID
isn't installed. Both run through `bash -lc`, which also puts Docker Desktop's
`/usr/local/bin/docker` on `PATH` — a `type: process` task does not get it when
VS Code is launched from the Dock. Windows keeps its own PowerShell branch.

## Fixed: Data Protection keys died with the container

The key ring defaulted to the file provider, which logged
`Storing keys in a directory '/home/app/.aspnet/DataProtection-Keys' that may not
be persisted outside of the container`, and the four first-chance
`CryptographicException`s in the debug console came with it. Every
`--force-recreate` discarded the keys and invalidated auth cookies.

`AppDbContext` now implements `IDataProtectionKeyContext`, and
`AddInfrastructure` calls `PersistKeysToDbContext<AppDbContext>()`, so the key
ring lives in `identity.data_protection_keys` (migration
`20260810222849_AddDataProtectionKeys`). A database rather than a mounted volume
because a named volume at that path would be created root-owned while the
container runs as `$APP_UID`, and a bind mount would need per-OS host paths.

`SetApplicationName("FamilyLibrarian")` is required, not cosmetic: the key
discriminator otherwise derives from the content root path — `/app` in the
container, an OS-specific absolute path under the debugger — so with a shared
key ring a cookie issued by one host would fail to decrypt in the other.

Still logged: `No XML encryptor configured. Key ... may be persisted to storage
in unencrypted form.` The keys sit unencrypted in a database only this app can
reach. Encrypting at rest needs a certificate and is left undone deliberately.

## Known: the Compose stack has no local account

`compose.yaml` passes neither `Authentication__EnableLocal` nor
`BootstrapAdmin__*`, so `identity.users` is empty in the container stack and
there is nothing to log in as — only the debugger's `envFile` supplies them.
Unrelated to the debugger work above; noted because it blocks any end-to-end
auth check against the containers.

## Verified

- `dotnet build FamilyLibrarian.slnx` — 0 warnings, 0 errors.
- `--migrate` applied `20260808152234_InitialIdentity`.
- App bound both URLs; `/health/ready` → `Healthy` 200, `/` → 200, HTTP → 307.
- `BootstrapAdmin__*` **as environment variables** created the admin; `POST /api/auth/login`
  → 204, `/api/v1/me` → 200 with roles `["User","Admin"]`, `/api/v1/admin/ping` → 200.
- Both Compose files pass `docker compose config` with the extended `.env`.
- The `.env.example` placeholder password passes the Identity policy: clean startup,
  login → 204.
- Debug database volume reset afterward, so it holds no test account.
- After the cookie fix: unauthenticated `/api/v1/me` and `/api/v1/admin/ping` → 401
  with 0 redirects and an empty body; SPA root still 200 `text/html`. Build stayed
  at 0 warnings / 0 errors.
