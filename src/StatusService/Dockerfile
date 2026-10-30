# Build context = repository root:  docker build -f src/StatusService/Dockerfile --build-arg DOTNET_MAJOR=10 .
ARG DOTNET_MAJOR=10

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_MAJOR}.0 AS build
ARG DOTNET_MAJOR
WORKDIR /src
# Copy only project files first so "dotnet restore" is cached until a dependency changes.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Contracts/Contracts.csproj src/Contracts/
COPY src/Platform/Platform.csproj src/Platform/
COPY src/StatusService/StatusService.csproj src/StatusService/
RUN dotnet restore src/StatusService/StatusService.csproj -p:DotNetMajor=${DOTNET_MAJOR}
COPY src/Contracts src/Contracts
COPY src/Platform src/Platform
COPY src/StatusService src/StatusService
RUN dotnet publish src/StatusService/StatusService.csproj -c Release -o /app --no-restore -p:DotNetMajor=${DOTNET_MAJOR} -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_MAJOR}.0 AS final
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "StatusService.dll"]
