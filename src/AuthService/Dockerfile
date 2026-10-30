# Build context = repository root:  docker build -f src/AuthService/Dockerfile --build-arg DOTNET_MAJOR=10 .
ARG DOTNET_MAJOR=10

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_MAJOR}.0 AS build
ARG DOTNET_MAJOR
WORKDIR /src
# Copy only project files first so "dotnet restore" is cached until a dependency changes.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Contracts/Contracts.csproj src/Contracts/
COPY src/Platform/Platform.csproj src/Platform/
COPY src/AuthService/AuthService.csproj src/AuthService/
RUN dotnet restore src/AuthService/AuthService.csproj -p:DotNetMajor=${DOTNET_MAJOR}
COPY src/Contracts src/Contracts
COPY src/Platform src/Platform
COPY src/AuthService src/AuthService
RUN dotnet publish src/AuthService/AuthService.csproj -c Release -o /app --no-restore -p:DotNetMajor=${DOTNET_MAJOR} -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_MAJOR}.0 AS final
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "AuthService.dll"]
