# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

RUN apt-get update && apt-get install -y --no-install-recommends git ca-certificates \
 && rm -rf /var/lib/apt/lists/*

COPY *.slnx .
COPY Heimdall/*.csproj ./Heimdall/

RUN git -c http.extraheader="Authorization: token ${GITEA_TOKEN}" \
    clone --depth 1 https://forge.obfuscator.fr/OneSuite/OneObfuscator.git /src/OneObfuscator \
 && git -c http.extraheader="Authorization: token ${GITEA_TOKEN}" \
    clone --depth 1 https://forge.obfuscator.fr/OneSuite/Hydronium.git /src/OneObfuscator/Hydronium \
 && rm -rf /src/OneObfuscator/.git /src/OneObfuscator/Hydronium/.git

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