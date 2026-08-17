FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Directory.Build.props", "Directory.Packages.props", "FamilyLibrarian.slnx", "./"]
COPY ["src/FamilyLibrarian.Domain/FamilyLibrarian.Domain.csproj", "src/FamilyLibrarian.Domain/"]
COPY ["src/FamilyLibrarian.Contracts/FamilyLibrarian.Contracts.csproj", "src/FamilyLibrarian.Contracts/"]
COPY ["src/FamilyLibrarian.Application/FamilyLibrarian.Application.csproj", "src/FamilyLibrarian.Application/"]
COPY ["src/FamilyLibrarian.Infrastructure/FamilyLibrarian.Infrastructure.csproj", "src/FamilyLibrarian.Infrastructure/"]
COPY ["src/FamilyLibrarian.Web.Client/FamilyLibrarian.Web.Client.csproj", "src/FamilyLibrarian.Web.Client/"]
COPY ["src/FamilyLibrarian.Web/FamilyLibrarian.Web.csproj", "src/FamilyLibrarian.Web/"]
RUN dotnet restore "src/FamilyLibrarian.Web/FamilyLibrarian.Web.csproj"

COPY . .
RUN dotnet publish "src/FamilyLibrarian.Web/FamilyLibrarian.Web.csproj" --configuration Release --no-restore --output /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime-base
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
# Baked in (while still root) so a fresh named volume mounted at this path on
# first run inherits this ownership instead of root:root — the security
# pipeline's storage zones live here, and the app runs as the non-root
# $APP_UID user below.
RUN mkdir -p /data/family-librarian && chown -R $APP_UID:$APP_UID /data/family-librarian
EXPOSE 8080
ENTRYPOINT ["dotnet", "FamilyLibrarian.Web.dll"]

# Debug-only prerequisites are deliberately built before any application output
# is copied. Source changes therefore rebuild the application layer without
# rerunning apt or downloading vsdbg. The local VS Code tasks build and tag this
# target as family-librarian:debug-base, refreshing it at most once per UTC day.
# DEBUG_BASE_REFRESH participates in the RUN cache key; changing it explicitly
# refreshes the runtime parent, package index, packages, and debugger download.
FROM runtime-base AS debug-base
ARG DEBUG_BASE_REFRESH=manual
LABEL org.family-librarian.debug-base.refresh-date="${DEBUG_BASE_REFRESH}"
USER root
RUN echo "Refreshing debug prerequisites for ${DEBUG_BASE_REFRESH}" \
    && apt-get update \
    && apt-get install -y --no-install-recommends curl unzip \
    && curl -sSL https://aka.ms/getvsdbgsh | bash /dev/stdin -v latest -l /remote_debugger \
    && apt-get purge -y --auto-remove curl unzip \
    && rm -rf /var/lib/apt/lists/*
USER $APP_UID

# The debug application is only the cached prerequisite base plus the latest
# published app. Used exclusively by compose.debug-attach.yaml.
#
# This exists so attaching does not have to copy the debugger in. VS Code's copy
# writes thousands of small files, and when the target was a Windows bind mount
# every one of them crossed the Docker Desktop filesystem bridge — which is what
# made attaching take minutes. Baked into a layer, it is already there.
#
# Declared BEFORE `final` deliberately: the last stage in a Dockerfile is what a
# bare `docker build` produces, and that must never be the stage carrying a
# debugger. Release builds get `final`; only an explicit `target: debug` gets this.
FROM debug-base AS debug
COPY --from=build /app/publish .
USER $APP_UID

FROM runtime-base AS runtime
COPY --from=build /app/publish .

FROM runtime AS final
USER $APP_UID
