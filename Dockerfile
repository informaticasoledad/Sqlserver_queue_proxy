# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY TDSQueue.Proxy.csproj ./
RUN dotnet restore ./TDSQueue.Proxy.csproj

COPY . ./
RUN dotnet publish ./TDSQueue.Proxy.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish ./

# Default: production profile inside container
ENV DOTNET_ENVIRONMENT=Production

# Proxy listener + health/admin port
EXPOSE 14330
EXPOSE 18080

ENTRYPOINT ["dotnet", "TDSQueue.Proxy.dll"]
