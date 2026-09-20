FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY NuGet.Config ./
COPY TomCat.Api/TomCat.Api.csproj TomCat.Api/
RUN dotnet restore TomCat.Api/TomCat.Api.csproj --configfile NuGet.Config

COPY TomCat.Api/ TomCat.Api/
RUN dotnet publish TomCat.Api/TomCat.Api.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    PORT=8080 \
    Storage__Directory=/data \
    AllowedHosts=*

COPY --from=build /app/publish .

EXPOSE 8080
ENTRYPOINT ["dotnet", "TomCat.Api.dll"]
