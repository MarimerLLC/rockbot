# owntracks-mcp (https://github.com/de-niji/OwnTracksHermes, MIT): a read-only MCP
# server over the OwnTracks Recorder's REST API. It lets RockBot answer "where was I
# yesterday afternoon?" and similar questions. See docs/user-location.md.
#
# Upstream publishes no image, so this builds one from a pinned commit:
#   docker buildx build --platform linux/amd64 \
#     -t <registry>/owntracks-mcp:0.2.0-9f005a7 --push deploy/owntracks-mcp
#
# To upgrade, bump OWNTRACKS_MCP_REF and the tag together. Read the upstream diff
# first: this is a young single-author project, which is why it is pinned.
FROM python:3.12-slim

ARG OWNTRACKS_MCP_REF=9f005a790c38

RUN apt-get update && apt-get install -y --no-install-recommends git \
    && pip install --no-cache-dir \
        "owntracks-mcp @ git+https://github.com/de-niji/OwnTracksHermes@${OWNTRACKS_MCP_REF}" \
        "mcp>=1.10,<2" \
    && apt-get purge -y git && apt-get autoremove -y && rm -rf /var/lib/apt/lists/*

RUN useradd --uid 1000 --create-home owntracks
# Admits non-localhost Host headers that upstream's localhost-only DNS-rebinding
# protection would refuse with 421 (see serve.py).
COPY serve.py /opt/owntracks-mcp/serve.py
USER 1000
WORKDIR /home/owntracks

# The default allowlist fits a Kubernetes Service named owntracks-mcp in namespace
# owntracks. Override ALLOWED_HOSTS to match your own Service or hostname.
ENV PYTHONUNBUFFERED=1 \
    ALLOWED_HOSTS="owntracks-mcp:*,owntracks-mcp.owntracks:*,owntracks-mcp.owntracks.svc:*,owntracks-mcp.owntracks.svc.cluster.local:*"

EXPOSE 8765

ENTRYPOINT ["python", "/opt/owntracks-mcp/serve.py"]
CMD ["serve", "--transport", "streamable-http", "--host", "0.0.0.0", "--port", "8765"]
