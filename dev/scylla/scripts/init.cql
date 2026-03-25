CREATE KEYSPACE IF NOT EXISTS nam WITH replication = {'class': 'org.apache.cassandra.locator.NetworkTopologyStrategy', 'nam': '1'} AND durable_writes = true AND tablets = {'enabled': false};
CREATE KEYSPACE IF NOT EXISTS eur WITH replication = {'class': 'org.apache.cassandra.locator.NetworkTopologyStrategy', 'nam': '1', 'eur': '1'} AND durable_writes = true AND tablets = {'enabled': false};
CREATE KEYSPACE IF NOT EXISTS sam WITH replication = {'class': 'org.apache.cassandra.locator.NetworkTopologyStrategy', 'nam': '1'} AND durable_writes = true AND tablets = {'enabled': false};
CREATE KEYSPACE IF NOT EXISTS sas WITH replication = {'class': 'org.apache.cassandra.locator.NetworkTopologyStrategy', 'nam': '1', 'sas': '1'} AND durable_writes = true AND tablets = {'enabled': false};
CREATE KEYSPACE IF NOT EXISTS eas WITH replication = {'class': 'org.apache.cassandra.locator.NetworkTopologyStrategy', 'nam': '1', 'eas': '1'} AND durable_writes = true AND tablets = {'enabled': false};
CREATE KEYSPACE IF NOT EXISTS ocn WITH replication = {'class': 'org.apache.cassandra.locator.NetworkTopologyStrategy', 'nam': '1', 'ocn': '1'} AND durable_writes = true AND tablets = {'enabled': false};

CREATE KEYSPACE IF NOT EXISTS gdpr WITH replication = {'class': 'org.apache.cassandra.locator.NetworkTopologyStrategy', 'eur': '1'} AND durable_writes = true AND tablets = {'enabled': false};
CREATE KEYSPACE IF NOT EXISTS nam_nt WITH replication = {'class': 'org.apache.cassandra.locator.NetworkTopologyStrategy', 'nam': '1'} AND durable_writes = true AND tablets = {'enabled': false};
CREATE KEYSPACE IF NOT EXISTS global WITH replication = {'class': 'org.apache.cassandra.locator.NetworkTopologyStrategy', 'nam': '1', 'eur': '1', 'sas': '1', 'eas': '1', 'ocn': '1'} AND durable_writes = true AND tablets = {'enabled': false};
CREATE KEYSPACE IF NOT EXISTS dummy WITH replication = {'class': 'org.apache.cassandra.locator.SimpleStrategy', 'replication_factor': '1'} AND durable_writes = true AND tablets = {'enabled': false};
