-- Migration: 002_create_auth_tokens.sql
-- Purpose: Track issued deep-link JWS tokens for revocation capability
-- Created: 2026-04-04

CREATE TABLE IF NOT EXISTS auth_tokens (
    jti TEXT PRIMARY KEY,
    system_id TEXT NOT NULL,
    issued_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    expires_at TIMESTAMPTZ NOT NULL,
    revoked_at TIMESTAMPTZ
);

-- Index for efficient lookups by system ID (e.g., find all tokens for a user)
CREATE INDEX IF NOT EXISTS idx_auth_tokens_system_id ON auth_tokens(system_id);

-- Index for cleanup queries (e.g., find expired tokens to delete)
CREATE INDEX IF NOT EXISTS idx_auth_tokens_expires_at ON auth_tokens(expires_at);

-- Index revoked rows for fast revocation/cleanup scans.
-- NOTE: Partial index predicates must be immutable, so avoid CURRENT_TIMESTAMP here.
CREATE INDEX IF NOT EXISTS idx_auth_tokens_revoked_at_not_null ON auth_tokens(revoked_at)
    WHERE revoked_at IS NOT NULL;

-- Add comment for documentation
COMMENT ON TABLE auth_tokens IS 'Stores issued JWS token metadata for revocation and audit purposes. jti (JWT ID) is the primary key. revoked_at tracks manual revocation; cleanup job removes entries where revoked_at IS NOT NULL OR expires_at < now().';
COMMENT ON COLUMN auth_tokens.jti IS 'The unique JWT ID (jti) claim from the token.';
COMMENT ON COLUMN auth_tokens.system_id IS 'The system_id (subject) this token was issued for.';
COMMENT ON COLUMN auth_tokens.issued_at IS 'Timestamp when token was issued.';
COMMENT ON COLUMN auth_tokens.expires_at IS 'Timestamp when token naturally expires (from JWT exp claim).';
COMMENT ON COLUMN auth_tokens.revoked_at IS 'Timestamp when token was explicitly revoked, if any. NULL = active/never revoked.';
