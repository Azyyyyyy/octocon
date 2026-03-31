# Simple Dockerfile for Interfold.Api

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and restore as distinct layers
COPY csharp/ ./
RUN dotnet restore ./Interfold.Api/Interfold.Api.csproj

# Copy everything else and build
WORKDIR /src/Interfold.Api
RUN dotnet publish -c Release -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Expose default port
EXPOSE 8080

# Set environment variables (optional, can be overridden)
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "Interfold.Api.dll"]
