# Simple Dockerfile for Interfold.Api

# Runtime stage only: expects published output in /publish
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY publish/ .

ENTRYPOINT ["dotnet", "Interfold.Api.dll"]
