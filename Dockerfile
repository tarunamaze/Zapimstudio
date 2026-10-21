FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Zapim.Marketing.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build --chown=app:app /out ./
RUN mkdir -p /data/assets /data/keys && chown -R app:app /data
USER app
ENV ASPNETCORE_HTTP_PORTS=8080 StoragePath=/data
EXPOSE 8080
ENTRYPOINT ["dotnet", "Zapim.Marketing.dll"]
