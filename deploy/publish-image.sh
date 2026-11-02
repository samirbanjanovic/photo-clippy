#!/usr/bin/env bash
# Build the Photo Clippy container image and publish it to the homelab registry.
#
#   deploy/publish-image.sh                 build linux/amd64, smoke test, push :<version> :sha-<rev> :latest
#   deploy/publish-image.sh --dry-run       print every command without running anything
#   deploy/publish-image.sh --no-push       build and smoke test locally only
#
# Run it from anywhere; the build context is always the repository root.
# Works with the bash 3.2 that ships with macOS.
set -euo pipefail

REGISTRY="${PHOTO_CLIPPY_REGISTRY:-docker.home.arpa}"
IMAGE="${PHOTO_CLIPPY_IMAGE:-photo-clippy}"
PLATFORM="${PHOTO_CLIPPY_PLATFORM:-linux/amd64}"
PUSH=1
LATEST=1
SMOKE=1
PREFLIGHT=1
DRY_RUN=0
EXTRA_TAGS=()

usage() {
  cat <<EOF
Usage: ${0##*/} [options]

Builds deploy/Dockerfile and pushes it to the registry as
  <registry>/<image>:<version>   version from Directory.Build.props
  <registry>/<image>:sha-<rev>   git revision, suffixed -dirty when the tree has changes
  <registry>/<image>:latest      unless --no-latest

Options:
  -r, --registry HOST   registry host            (default: ${REGISTRY}, env PHOTO_CLIPPY_REGISTRY)
  -i, --image NAME      image name               (default: ${IMAGE}, env PHOTO_CLIPPY_IMAGE)
  -p, --platform LIST   target platform(s)       (default: ${PLATFORM}, env PHOTO_CLIPPY_PLATFORM)
                        Use linux/amd64 for an x86-64 lab host, linux/arm64 for an ARM one.
                        A comma list such as linux/amd64,linux/arm64 builds a multi-arch
                        image straight into the registry (no local load or smoke test).
  -t, --tag TAG         add an extra tag; repeatable
      --no-latest       do not tag or push :latest
      --no-push         build and smoke test locally, do not contact the registry
      --no-smoke        skip running the built image (--version) before pushing
      --skip-preflight  do not probe https://<registry>/v2/ before building
  -n, --dry-run         print the commands instead of running them
  -h, --help            show this help

Registry login (only if the registry requires it): set REGISTRY_USERNAME and
REGISTRY_PASSWORD; the password is passed on stdin, never on the command line.

TLS: the lab registry uses a certificate Docker does not trust by default. Either put
the CA at ~/.docker/certs.d/<registry>/ca.crt, or add "<registry>" to
"insecure-registries" in the Docker daemon settings (Docker Desktop: Settings > Docker
Engine) and restart Docker. See the README "Publishing to the lab registry" section.
EOF
}

die() { echo "error: $*" >&2; exit 1; }
info() { echo "==> $*"; }
warn() { echo "warning: $*" >&2; }

while [ $# -gt 0 ]; do
  case "$1" in
    -r|--registry)  [ $# -ge 2 ] || die "$1 needs a value"; REGISTRY="$2"; shift 2 ;;
    -i|--image)     [ $# -ge 2 ] || die "$1 needs a value"; IMAGE="$2"; shift 2 ;;
    -p|--platform)  [ $# -ge 2 ] || die "$1 needs a value"; PLATFORM="$2"; shift 2 ;;
    -t|--tag)       [ $# -ge 2 ] || die "$1 needs a value"; EXTRA_TAGS+=("$2"); shift 2 ;;
    --no-latest)    LATEST=0; shift ;;
    --no-push)      PUSH=0; shift ;;
    --no-smoke)     SMOKE=0; shift ;;
    --skip-preflight) PREFLIGHT=0; shift ;;
    -n|--dry-run)   DRY_RUN=1; shift ;;
    -h|--help)      usage; exit 0 ;;
    *)              usage >&2; die "unknown option: $1" ;;
  esac
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
DOCKERFILE="${ROOT}/deploy/Dockerfile"
[ -f "${DOCKERFILE}" ] || die "Dockerfile not found at ${DOCKERFILE}"

# Strip a scheme or trailing slash so --registry https://host/ still works.
REGISTRY="${REGISTRY#http://}"; REGISTRY="${REGISTRY#https://}"; REGISTRY="${REGISTRY%/}"
[ -n "${REGISTRY}" ] || die "registry must not be empty"

MULTI_ARCH=0
case "${PLATFORM}" in *,*) MULTI_ARCH=1 ;; esac
if [ "${MULTI_ARCH}" = 1 ] && [ "${PUSH}" = 0 ]; then
  die "a multi-platform build can only be pushed; use a single --platform with --no-push"
fi

VERSION="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "${ROOT}/Directory.Build.props" | head -n 1)"
[ -n "${VERSION}" ] || die "could not read <Version> from Directory.Build.props"

REVISION="nogit"
if git -C "${ROOT}" rev-parse --short=12 HEAD >/dev/null 2>&1; then
  REVISION="$(git -C "${ROOT}" rev-parse --short=12 HEAD)"
  if [ -n "$(git -C "${ROOT}" status --porcelain 2>/dev/null)" ]; then
    REVISION="${REVISION}-dirty"
  fi
fi

REPO="${REGISTRY}/${IMAGE}"
TAGS=("${VERSION}" "sha-${REVISION}")
[ "${LATEST}" = 1 ] && TAGS+=("latest")
if [ ${#EXTRA_TAGS[@]} -gt 0 ]; then
  for t in "${EXTRA_TAGS[@]}"; do TAGS+=("$t"); done
fi
PRIMARY="${REPO}:${TAGS[0]}"

run() {
  printf '+'; printf ' %q' "$@"; printf '\n'
  if [ "${DRY_RUN}" = 0 ]; then "$@"; fi
}

info "image      ${REPO}"
info "tags       ${TAGS[*]}"
info "platform   ${PLATFORM}"
info "context    ${ROOT}"
[ "${DRY_RUN}" = 1 ] && info "dry run: nothing below is executed"

if [ "${DRY_RUN}" = 0 ]; then
  command -v docker >/dev/null 2>&1 || die "docker is not on PATH"
  docker info >/dev/null 2>&1 || die "cannot reach the Docker daemon; start Docker Desktop (or the docker service) and retry"
  docker buildx version >/dev/null 2>&1 || die "docker buildx is required"
fi

# Fail fast, before a long build, if nothing answers as a registry on the other end.
if [ "${PUSH}" = 1 ] && [ "${PREFLIGHT}" = 1 ] && [ "${DRY_RUN}" = 0 ]; then
  info "checking https://${REGISTRY}/v2/"
  if ! command -v curl >/dev/null 2>&1; then
    warn "curl not found, skipping the registry check"
  else
    code="$(curl -ksS -o /dev/null -w '%{http_code}' --max-time 8 "https://${REGISTRY}/v2/" 2>/dev/null || true)"
    case "${code}" in
      200|401) ;;
      404) die "https://${REGISTRY}/v2/ answered 404. The reverse proxy is up but nothing is serving the registry API on that host; is the registry stack running? (--skip-preflight to override)" ;;
      000|"") die "cannot reach https://${REGISTRY}/v2/. Check DNS and that the host is up. (--skip-preflight to override)" ;;
      *)   die "https://${REGISTRY}/v2/ answered HTTP ${code}, which is not a Docker registry. (--skip-preflight to override)" ;;
    esac
    if ! curl -sS -o /dev/null --max-time 8 "https://${REGISTRY}/v2/" 2>/dev/null; then
      warn "this machine does not trust the certificate for ${REGISTRY}. The push fails with an x509 error"
      warn "unless Docker trusts it: add the CA to ~/.docker/certs.d/${REGISTRY}/ca.crt or list ${REGISTRY}"
      warn "under insecure-registries in the Docker daemon settings."
    fi
  fi
fi

if [ "${PUSH}" = 1 ] && [ -n "${REGISTRY_USERNAME:-}" ] && [ -n "${REGISTRY_PASSWORD:-}" ]; then
  info "logging in to ${REGISTRY} as ${REGISTRY_USERNAME}"
  if [ "${DRY_RUN}" = 1 ]; then
    echo "+ docker login ${REGISTRY} --username ${REGISTRY_USERNAME} --password-stdin"
  else
    printf '%s' "${REGISTRY_PASSWORD}" | docker login "${REGISTRY}" --username "${REGISTRY_USERNAME}" --password-stdin
  fi
fi

TAG_ARGS=()
for t in "${TAGS[@]}"; do TAG_ARGS+=(-t "${REPO}:${t}"); done

BUILD_ARGS=(
  buildx build
  --platform "${PLATFORM}"
  -f "${DOCKERFILE}"
  --label "org.opencontainers.image.title=Photo Clippy"
  --label "org.opencontainers.image.version=${VERSION}"
  --label "org.opencontainers.image.revision=${REVISION}"
)
BUILD_ARGS+=("${TAG_ARGS[@]}")

if [ "${MULTI_ARCH}" = 1 ]; then
  info "building and pushing ${PLATFORM}"
  run docker "${BUILD_ARGS[@]}" --push "${ROOT}"
else
  info "building ${PLATFORM}"
  run docker "${BUILD_ARGS[@]}" --load "${ROOT}"

  if [ "${SMOKE}" = 1 ]; then
    info "smoke test: ${PRIMARY} --version"
    run docker run --rm --platform "${PLATFORM}" "${PRIMARY}" --version
  fi

  if [ "${PUSH}" = 1 ]; then
    for t in "${TAGS[@]}"; do
      info "pushing ${REPO}:${t}"
      run docker push "${REPO}:${t}"
    done
  fi
fi

if [ "${DRY_RUN}" = 1 ]; then
  info "dry run complete"
elif [ "${PUSH}" = 1 ]; then
  info "published ${REPO}: ${TAGS[*]}"
  echo "    pull on the lab host:  docker pull ${PRIMARY}"
else
  info "built ${REPO}: ${TAGS[*]} (not pushed)"
fi
