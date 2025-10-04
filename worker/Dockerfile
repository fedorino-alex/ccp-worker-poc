# Use the official .NET 8 SDK image for building
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution file
COPY ccp-poc.sln ./

# Copy project files for restore (better Docker layer caching)
COPY worker/worker.csproj ./worker/
COPY shared/shared.csproj ./shared/

# Restore dependencies
RUN dotnet restore "./worker/worker.csproj"

# Copy the rest of the source code
COPY worker/ ./worker/
COPY shared/ ./shared/

# Build the application
WORKDIR /src/worker
RUN dotnet publish -c Development -o /app/publish --no-restore

# Use the official .NET 8 runtime image
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app

# Copy the published application from the build stage
COPY --from=build /app/publish .

# Set the entry point
ENTRYPOINT ["dotnet", "worker.dll"]