FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

COPY *.slnx .
COPY Heimdall/*.csproj ./Heimdall/
COPY OneObfuscator/. ./OneObfuscator/
RUN dotnet restore

COPY Heimdall/. ./Heimdall/
WORKDIR /src/Heimdall
RUN dotnet publish -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled-extra AS runtime
WORKDIR /app

COPY --from=build /app/publish .

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0

USER $APP_UID

ENTRYPOINT ["dotnet", "Heimdall.dll"]