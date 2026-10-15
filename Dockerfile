# Multi-stage build for .NET 10 Modular Monolith
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src

# Copy solution and project definitions
COPY . .
RUN dotnet restore src/Agent360.API/Agent360.API.csproj

# Build and Publish
RUN dotnet publish src/Agent360.API/Agent360.API.csproj -c Release -o /app/publish --no-restore

# Final runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview AS final
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Agent360.API.dll"]
