CREATE KEYSPACE IF NOT EXISTS octocon
WITH replication = {'class':'SimpleStrategy', 'replication_factor':1};

CREATE TABLE IF NOT EXISTS octocon.alters_by_system (
    system_id text,
    alter_id int,
    name text,
    alias text,
    PRIMARY KEY ((system_id), alter_id)
);

CREATE TABLE IF NOT EXISTS octocon.account_profiles_by_system (
    system_id text PRIMARY KEY,
    username text,
    updated_at timestamp
);

CREATE TABLE IF NOT EXISTS octocon.account_descriptions_by_system (
    system_id text PRIMARY KEY,
    description text,
    updated_at timestamp
);

CREATE TABLE IF NOT EXISTS octocon.account_encryption_by_system (
    system_id text PRIMARY KEY,
    encryption_initialized boolean,
    encryption_key_checksum text,
    updated_at timestamp
);

CREATE TABLE IF NOT EXISTS octocon.notification_tokens_by_system (
    system_id text,
    push_token text,
    inserted_at timestamp,
    PRIMARY KEY ((system_id), push_token)
);

CREATE TABLE IF NOT EXISTS octocon.notification_tokens_by_token (
    push_token text PRIMARY KEY,
    system_id text,
    inserted_at timestamp
);

CREATE TABLE IF NOT EXISTS octocon.fronting_active_by_system (
    system_id text,
    alter_id int,
    front_id text,
    comment text,
    started_at timestamp,
    PRIMARY KEY ((system_id), alter_id)
);

CREATE TABLE IF NOT EXISTS octocon.fronting_primary_by_system (
    system_id text PRIMARY KEY,
    alter_id int
);

CREATE TABLE IF NOT EXISTS octocon.tags_by_system (
    system_id text,
    tag_id text,
    name text,
    parent_tag_id text,
    PRIMARY KEY ((system_id), tag_id)
);

CREATE TABLE IF NOT EXISTS octocon.tag_alters_by_system (
    system_id text,
    tag_id text,
    alter_id int,
    PRIMARY KEY ((system_id, tag_id), alter_id)
);

CREATE TABLE IF NOT EXISTS octocon.polls_by_system (
    system_id text,
    poll_id text,
    title text,
    description text,
    poll_type text,
    data_json text,
    time_end timestamp,
    PRIMARY KEY ((system_id), poll_id)
);

CREATE TABLE IF NOT EXISTS octocon.global_journals_by_system (
    system_id text,
    entry_id text,
    title text,
    content text,
    color text,
    inserted_at timestamp,
    PRIMARY KEY ((system_id), entry_id)
);

CREATE TABLE IF NOT EXISTS octocon.global_journal_state_by_system (
    system_id text,
    entry_id text,
    pinned boolean,
    locked boolean,
    PRIMARY KEY ((system_id), entry_id)
);

CREATE TABLE IF NOT EXISTS octocon.global_journal_alters_by_system (
    system_id text,
    entry_id text,
    alter_id int,
    PRIMARY KEY ((system_id, entry_id), alter_id)
);

CREATE TABLE IF NOT EXISTS octocon.alter_journals_by_system (
    system_id text,
    entry_id text,
    alter_id int,
    title text,
    content text,
    color text,
    pinned boolean,
    locked boolean,
    inserted_at timestamp,
    PRIMARY KEY ((system_id), entry_id)
);

CREATE TABLE IF NOT EXISTS octocon.friendships_by_system (
    system_id text,
    friend_id text,
    level text,
    since timestamp,
    PRIMARY KEY ((system_id), friend_id)
);

CREATE TABLE IF NOT EXISTS octocon.friend_requests_by_sender (
    from_system_id text,
    to_system_id text,
    date_sent timestamp,
    PRIMARY KEY ((from_system_id), to_system_id)
);

CREATE TABLE IF NOT EXISTS octocon.friend_requests_by_recipient (
    to_system_id text,
    from_system_id text,
    date_sent timestamp,
    PRIMARY KEY ((to_system_id), from_system_id)
);

CREATE TABLE IF NOT EXISTS octocon.aggregate_versions_by_region (
    region_scope text,
    aggregate_type text,
    aggregate_id text,
    version bigint,
    PRIMARY KEY ((region_scope, aggregate_type), aggregate_id)
);
