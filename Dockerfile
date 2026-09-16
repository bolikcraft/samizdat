FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Сначала только проекты: restore кэшируется отдельным слоем и не повторяется при правке кода.
COPY Directory.Build.props ./
COPY src/Samizdat.Core/Samizdat.Core.csproj src/Samizdat.Core/
COPY src/Samizdat.Server/Samizdat.Server.csproj src/Samizdat.Server/
RUN dotnet restore src/Samizdat.Server/Samizdat.Server.csproj

# Тема и языковые пакеты встраиваются в Samizdat.Core из корня репозитория.
COPY themes/ themes/
COPY lang/ lang/
COPY src/Samizdat.Core/ src/Samizdat.Core/
COPY src/Samizdat.Server/ src/Samizdat.Server/
RUN dotnet publish src/Samizdat.Server/Samizdat.Server.csproj -c Release -o /app --no-restore

# Только -extra: в прочих chiseled и alpine нет ICU, и сервер молча работал бы в invariant-культуре.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra
WORKDIR /app
COPY --from=build /app ./
ENV Samizdat__DataRoot=/data \
    ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "Samizdat.Server.dll"]
