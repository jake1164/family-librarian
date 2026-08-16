FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Directory.Build.props", "Directory.Packages.props", "./"]
COPY ["samples/FamilyLibrarian.SampleProvider/FamilyLibrarian.SampleProvider.csproj", "samples/FamilyLibrarian.SampleProvider/"]
RUN dotnet restore "samples/FamilyLibrarian.SampleProvider/FamilyLibrarian.SampleProvider.csproj"

COPY ["samples/FamilyLibrarian.SampleProvider/", "samples/FamilyLibrarian.SampleProvider/"]
RUN dotnet publish "samples/FamilyLibrarian.SampleProvider/FamilyLibrarian.SampleProvider.csproj" --configuration Release --no-restore --output /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
# Optional: set to require a matching "Authorization: Bearer <token>" header on
# every request. Leave unset for an open (LAN-only) reference deployment.
ENV SAMPLE_PROVIDER_API_KEY=""
COPY --from=build /app/publish .
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "FamilyLibrarian.SampleProvider.dll"]
