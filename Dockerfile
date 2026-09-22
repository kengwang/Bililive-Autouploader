FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore BililiveAutoUploader.slnx
RUN dotnet publish src/BililiveAutoUploader.Web/BililiveAutoUploader.Web.csproj -c Release -o /app/publish --no-restore
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
VOLUME ["/app/data", "/recordings"]
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "BililiveAutoUploader.Web.dll"]
