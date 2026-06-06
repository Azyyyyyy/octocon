-- Unified keyspace creation template for Octocon.
--
-- This file is rendered by ScyllaMigrationService at startup.
--
-- Placeholders:
--   {{KEYSPACE}}                Regional keyspace name (nam, eur, sam, sas, eas, ocn, gdpr)
--   {{KEYSPACE_REPLICATION}}    Replication map for current regional keyspace
--   {{GLOBAL_REPLICATION}}      Replication map for global keyspace
--   {{NAM_NT_REPLICATION}}      Replication map for nam_nt metrics keyspace

CREATE KEYSPACE IF NOT EXISTS {{KEYSPACE}} WITH replication = {{KEYSPACE_REPLICATION}} AND durable_writes = true;
CREATE KEYSPACE IF NOT EXISTS global WITH replication = {{GLOBAL_REPLICATION}} AND durable_writes = true;
CREATE KEYSPACE IF NOT EXISTS nam_nt WITH replication = {{NAM_NT_REPLICATION}} AND durable_writes = true;
CREATE KEYSPACE IF NOT EXISTS dummy WITH replication = {'class': 'SimpleStrategy', 'replication_factor': '1'} AND durable_writes = true;

ALTER KEYSPACE {{KEYSPACE}} WITH replication = {{KEYSPACE_REPLICATION}};
ALTER KEYSPACE global WITH replication = {{GLOBAL_REPLICATION}};
ALTER KEYSPACE nam_nt WITH replication = {{NAM_NT_REPLICATION}};
ALTER KEYSPACE dummy WITH replication = {'class': 'SimpleStrategy', 'replication_factor': '1'};
