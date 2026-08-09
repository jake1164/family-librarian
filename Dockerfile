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

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
COPY --from=build /app/publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "FamilyLibrarian.Web.dll"]
