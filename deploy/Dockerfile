# syntax=docker/dockerfile:1
# Build context: the repository root (this file lives in deploy/).
#   docker build -f deploy/Dockerfile -t photo-clippy .
# Multi-arch: docker buildx build --platform linux/amd64,linux/arm64 -f deploy/Dockerfile .
# To build and push to your own registry use deploy/publish-image.sh.

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /build

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/PhotoClippy.Contracts/*.csproj src/PhotoClippy.Contracts/
COPY src/PhotoClippy.Core/*.csproj src/PhotoClippy.Core/
COPY src/PhotoClippy.Imaging/*.csproj src/PhotoClippy.Imaging/
COPY src/PhotoClippy.Plugins.Auth/*.csproj src/PhotoClippy.Plugins.Auth/
COPY src/PhotoClippy.Plugins.Clip/*.csproj src/PhotoClippy.Plugins.Clip/
COPY src/PhotoClippy.Plugins.Color/*.csproj src/PhotoClippy.Plugins.Color/
COPY src/PhotoClippy.Plugins.Composition/*.csproj src/PhotoClippy.Plugins.Composition/
COPY src/PhotoClippy.Plugins.External/*.csproj src/PhotoClippy.Plugins.External/
COPY src/PhotoClippy.Plugins.Lenses/*.csproj src/PhotoClippy.Plugins.Lenses/
COPY src/PhotoClippy.Plugins.Metadata/*.csproj src/PhotoClippy.Plugins.Metadata/
COPY src/PhotoClippy.Plugins.Recommendation/*.csproj src/PhotoClippy.Plugins.Recommendation/
COPY src/PhotoClippy.Plugins.Reporting/*.csproj src/PhotoClippy.Plugins.Reporting/
COPY src/PhotoClippy.Cli/*.csproj src/PhotoClippy.Cli/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet restore src/PhotoClippy.Cli/PhotoClippy.Cli.csproj -a $TARGETARCH

COPY src/ src/
COPY data/ data/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet publish src/PhotoClippy.Cli/PhotoClippy.Cli.csproj -c Release -a $TARGETARCH \
      --no-restore --self-contained false -p:UseAppHost=true -o /app \
 && { test -f /app/wwwroot/_framework/blazor.web.js || { echo "blazor.web.js missing from publish output; the web UI would stay blank" >&2; exit 1; }; }

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
