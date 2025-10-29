# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy csproj and restore dependencies
COPY ["BobFx.Core/BobFx.Core.csproj", "BobFx.Core/"]
RUN dotnet restore "BobFx.Core/BobFx.Core.csproj"

# Copy everything else and build
COPY . .
WORKDIR "/src/BobFx.Core"
RUN dotnet build "BobFx.Core.csproj" -c Release -o /app/build

# Publish stage
FROM build AS publish
RUN dotnet publish "BobFx.Core.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

# Copy published app
COPY --from=publish /app/publish .

ENTRYPOINT ["dotnet", "BobFx.Core.dll"]
