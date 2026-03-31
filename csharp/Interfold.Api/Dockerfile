# Simple Dockerfile for Interfold.Api

# Runtime stage only: expects published output in /publish
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY publish/ .

# Expose default port
EXPOSE 8080

# Set environment variables (optional, can be overridden)
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "Interfold.Api.dll"]
