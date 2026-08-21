# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution and project files
COPY ["Tenko.sln", "./"]
COPY ["src/TenkoServer/TenkoServer.csproj", "src/TenkoServer/"]
COPY ["src/Tenko.Native/Tenko.Native.csproj", "src/Tenko.Native/"]
COPY ["src/ScanViewer/ScanViewer.csproj", "src/ScanViewer/"]
COPY ["tests/Tenko.Tests/Tenko.Tests.csproj", "tests/Tenko.Tests/"]

# Restore dependencies
RUN dotnet restore "src/TenkoServer/TenkoServer.csproj"

# Copy full source
COPY ["src/TenkoServer/", "src/TenkoServer/"]

# Build & Publish
WORKDIR "/src/src/TenkoServer"
RUN dotnet publish "TenkoServer.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
EXPOSE 8080
EXPOSE 443

COPY --from=build /app/publish .
# Ensure data directory exists
RUN mkdir -p /app/data

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "TenkoServer.dll"]
