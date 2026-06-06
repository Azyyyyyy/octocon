#!/bin/bash
set -euxo pipefail

mix deps.get

"$@"
