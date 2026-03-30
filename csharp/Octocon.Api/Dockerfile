# Simple Dockerfile for Octocon.Api

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and restore as distinct layers
COPY csharp/ ./
RUN dotnet restore ./Octocon.Api/Octocon.Api.csproj

# Copy everything else and build
WORKDIR /src/Octocon.Api
RUN dotnet publish -c Release -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Expose default port
EXPOSE 8080

# Set environment variables (optional, can be overridden)
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "Octocon.Api.dll"]
