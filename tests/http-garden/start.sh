#!/bin/bash

# The Garden expects a target on 0.0.0.0:443 over TLS, using the certificate
# pair the soil image generated at /app/garden.crt.

set -euo pipefail

exec /app/target/HermodGarden
