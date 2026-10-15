# Production container for the non-AOT ASP.NET Core application.
# Build context: repository root
# Example:
# docker build -f src/<GeneratedProject>.API/Dockerfile -t <generated-project>-api:latest .

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Copy the complete repository because the API references shared and module projects.
# The repository .dockerignore excludes bin/ and obj/ folders.
COPY . .

RUN dotnet restore "src/Agent360.API/Agent360.API.csproj"

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
ARG OPENAPI_GENERATE_DOCUMENTS=false
RUN dotnet publish "src/Agent360.API/Agent360.API.csproj" \
    --configuration $BUILD_CONFIGURATION \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false \
    /p:OpenApiGenerateDocuments=$OPENAPI_GENERATE_DOCUMENTS

FROM base AS final
WORKDIR /app
ENV RuntimeConfiguration__Validate=true
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Agent360.API.dll"]
