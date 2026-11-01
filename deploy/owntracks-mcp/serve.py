"""Launch owntracks-mcp with DNS-rebinding protection that admits in-cluster callers.

The upstream server builds FastMCP with the default host 127.0.0.1, so mcp 1.x
switches on DNS-rebinding protection allowing only localhost Host headers. Its
--host flag is applied later and doesn't change that, so every request through the
Kubernetes Service would get 421 "Invalid Host header".

FastMCP reads settings.transport_security when it builds the HTTP app at run time,
so replacing it here, before main() runs, is enough. Protection stays on; the
allowlist is the localhost defaults plus ALLOWED_HOSTS (comma-separated host:port
patterns, `*` for any port).
"""

import os
import sys

from mcp.server.transport_security import TransportSecuritySettings
from owntracks_mcp import server

extra = [h.strip() for h in os.environ.get("ALLOWED_HOSTS", "").split(",") if h.strip()]
server.mcp.settings.transport_security = TransportSecuritySettings(
    enable_dns_rebinding_protection=True,
    allowed_hosts=["127.0.0.1:*", "localhost:*", "[::1]:*", *extra],
    allowed_origins=["http://127.0.0.1:*", "http://localhost:*", "http://[::1]:*"],
)

server.main(sys.argv[1:])
