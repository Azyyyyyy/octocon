# Simple Dockerfile for Interfold.Api

# Runtime stage only: expects published output in /publish
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY publish/ .

# Expose HTTP and HTTPS ports
EXPOSE 5100
EXPOSE 5101

# Set environment variables (optional, can be overridden)
ENV ASPNETCORE_URLS=http://+:5100;https://+:5101

ENTRYPOINT ["dotnet", "Interfold.Api.dll"]
