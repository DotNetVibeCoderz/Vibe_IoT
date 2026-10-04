# Edge gateway image (linux-x64 / linux-arm64). Build: docker build -t iotcom-worker .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
# Serial devices: run with --device=/dev/ttyUSB0 and add the user to the dialout group.
USER $APP_UID
ENTRYPOINT ["dotnet", "IoTComWorker.dll"]
