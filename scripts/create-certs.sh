#!/usr/bin/env bash
set -e

# Example usage
# ./create-certs.sh \
#   "$ROOT_NAME" \
#   "$DOMAINS" \
#   "$OUTPUT_PATH" \
#   "$PASSWORD" \
#   "$YEARS"

ROOT_NAME="$1"
DOMAINS="$2"
OUTPUT_PATH="$3"
PASSWORD="$4"
YEARS="$5"

DOMAINS_ARRAY=(${DOMAINS//,/ })

ROOT_KEY="${OUTPUT_PATH}/rootCA.key"
ROOT_CRT="${OUTPUT_PATH}/rootCA.crt"
LEAF_KEY="${OUTPUT_PATH}/leaf.key"
LEAF_CSR="${OUTPUT_PATH}/leaf.csr"
LEAF_CRT="${OUTPUT_PATH}/leaf.crt"

echo "=== Generating Root CA (OpenSSL) ==="

openssl req -x509 -newkey rsa:2048 -days $((YEARS * 365)) \
  -keyout "$ROOT_KEY" \
  -out "$ROOT_CRT" \
  -nodes \
  -subj "/CN=${ROOT_NAME}" \
  -addext "basicConstraints=critical,CA:TRUE,pathlen:0" \
  -addext "keyUsage=critical,keyCertSign"

# SAN block
SAN=""
for d in "${DOMAINS_ARRAY[@]}"; do SAN+="DNS:${d},"; done
SAN="${SAN::-1}"

echo "=== Generating Leaf Certificate ==="

openssl req -new -nodes \
  -keyout "$LEAF_KEY" \
  -out "$LEAF_CSR" \
  -subj "/CN=${DOMAINS_ARRAY[0]}"

openssl x509 -req \
  -in "$LEAF_CSR" \
  -CA "$ROOT_CRT" \
  -CAkey "$ROOT_KEY" \
  -CAcreateserial \
  -out "$LEAF_CRT" \
  -days $((YEARS * 365)) \
  -extfile <(printf "subjectAltName=%s\nkeyUsage=digitalSignature,keyEncipherment" "$SAN")

# Export as PFX
openssl pkcs12 -export \
  -inkey "$LEAF_KEY" \
  -in "$LEAF_CRT" \
  -certfile "$ROOT_CRT" \
  -out "${OUTPUT_PATH}/${ROOT_NAME}.pfx" \
  -password pass:"$PASSWORD"

echo "Certificates generated."