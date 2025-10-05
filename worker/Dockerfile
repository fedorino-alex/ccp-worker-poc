# Use the official .NET 8 SDK image for building
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution file
COPY ccp-poc.sln ./
COPY ccp/ccp.csproj ./ccp/
COPY shared/shared.csproj ./shared/
COPY worker/worker.csproj ./worker/

# Restore dependencies - restore solution first, then specific project
RUN dotnet restore "ccp-poc.sln"

# Copy the rest of the source code
COPY . ./

# Build the application
WORKDIR /src/worker
RUN dotnet publish -c Development -o /app/publish

# Use the official .NET 8 runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Copy the published application from the build stage
COPY --from=build /app/publish .

# Set the entry point
ENTRYPOINT ["dotnet", "worker.dll"]