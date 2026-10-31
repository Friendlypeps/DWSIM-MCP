# Runtime image for the DWSIM-MCP headless server. Derived from DWSIM 10's packaging/docker/Dockerfile.
#
# The app is published self-contained (the .NET runtime is bundled), so the base image only needs the
# native OS dependencies. The build context holds a per-architecture publish at publish-mcp/amd64 and
# publish-mcp/arm64; buildx sets TARGETARCH to pick the matching one (scripts/publish.sh stages them).

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble
ARG TARGETARCH
ARG VERSION=dev
ARG UPSTREAM=v10.2.12

LABEL org.opencontainers.image.source="https://github.com/friendlypeps/dwsim-mcp" \
      org.opencontainers.image.title="DWSIM-MCP" \
      org.opencontainers.image.description="DWSIM 10 headless Model Context Protocol server with extended tools (columns, kinetics, flash, BIPs, adjust, sweeps, reflection)" \
      org.opencontainers.image.licenses="GPL-3.0-or-later" \
      org.opencontainers.image.version="${VERSION}" \
      io.friendlypeps.dwsim.upstream="${UPSTREAM}"

# SkiaSharp (the engine touches it at start-up) needs fontconfig; ICU already ships in runtime-deps.
RUN apt-get update \
 && apt-get install -y --no-install-recommends libfontconfig1 \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /opt/dwsim-mcp
COPY publish-mcp/${TARGETARCH}/ ./
RUN chmod +x /opt/dwsim-mcp/dwsim-mcp

# Same entrypoint as the official image: bind every interface, port 5901; extra flags (--token secret)
# appended after the image name are passed straight through.
EXPOSE 5901
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s CMD ["/opt/dwsim-mcp/dwsim-mcp", "--version"]
ENTRYPOINT ["/opt/dwsim-mcp/dwsim-mcp", "--http", "--host", "0.0.0.0", "--port", "5901"]
