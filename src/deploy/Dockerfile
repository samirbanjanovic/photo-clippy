# syntax=docker/dockerfile:1
# Build context: src
#   docker build -f deploy/Dockerfile -t photo-clippy .
# Multi-arch: docker buildx build --platform linux/amd64,linux/arm64 -f deploy/Dockerfile .

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY PhotoClippy.Contracts/*.csproj PhotoClippy.Contracts/
COPY PhotoClippy.Core/*.csproj PhotoClippy.Core/
COPY PhotoClippy.Imaging/*.csproj PhotoClippy.Imaging/
COPY PhotoClippy.Plugins.Auth/*.csproj PhotoClippy.Plugins.Auth/
COPY PhotoClippy.Plugins.Clip/*.csproj PhotoClippy.Plugins.Clip/
COPY PhotoClippy.Plugins.Color/*.csproj PhotoClippy.Plugins.Color/
COPY PhotoClippy.Plugins.Composition/*.csproj PhotoClippy.Plugins.Composition/
COPY PhotoClippy.Plugins.External/*.csproj PhotoClippy.Plugins.External/
COPY PhotoClippy.Plugins.Metadata/*.csproj PhotoClippy.Plugins.Metadata/
COPY PhotoClippy.Plugins.Recommendation/*.csproj PhotoClippy.Plugins.Recommendation/
COPY PhotoClippy.Plugins.Reporting/*.csproj PhotoClippy.Plugins.Reporting/
COPY PhotoClippy.Cli/*.csproj PhotoClippy.Cli/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet restore PhotoClippy.Cli/PhotoClippy.Cli.csproj -a $TARGETARCH

COPY . .
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet publish PhotoClippy.Cli/PhotoClippy.Cli.csproj -c Release -a $TARGETARCH \
      --no-restore --self-contained false -p:UseAppHost=true -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# ExifTool is optional: enable it as a second metadata extractor for exotic raws
# (CRW, X3F, MRW) with PHOTO_CLIPPY_PLUGINS__METADATA__1=builtin.metadata.exiftool.
RUN apt-get update \
 && apt-get install -y --no-install-recommends libimage-exiftool-perl \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app /opt/photo-clippy
RUN ln -s /opt/photo-clippy/photo-clippy /usr/local/bin/photo-clippy \
 && mkdir -p /data && chown app:app /data

# PHOTO_CLIPPY_HOME holds the cache, models, reports and the photo-clippy.json written by the web UI.
# SERVE__BROWSEROOTS confines the UI's folder picker to the mounted libraries (the UI cannot change it).
ENV PHOTO_CLIPPY_HOME=/data \
    PHOTO_CLIPPY_SERVE__URLS=http://0.0.0.0:8080 \
    PHOTO_CLIPPY_SERVE__BROWSEROOTS__0=/photos \
    DOTNET_gcServer=0

USER app
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["photo-clippy"]
# /photos seeds the library on first start; afterwards the folders saved in Settings are used.
CMD ["serve", "/photos"]
