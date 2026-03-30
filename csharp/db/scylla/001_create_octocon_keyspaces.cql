-- Keyspace creation for Octocon single-node setup
-- Run this file FIRST before any schema or table creation!

CREATE KEYSPACE IF NOT EXISTS nam WITH replication = {'class':'SimpleStrategy', 'replication_factor':1};
CREATE KEYSPACE IF NOT EXISTS global WITH replication = {'class':'SimpleStrategy', 'replication_factor':1};
CREATE KEYSPACE IF NOT EXISTS nam_nt WITH replication = {'class':'SimpleStrategy', 'replication_factor':1};
