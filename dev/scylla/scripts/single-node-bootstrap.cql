CREATE KEYSPACE IF NOT EXISTS global WITH replication = {'class': 'SimpleStrategy', 'replication_factor': 1};
CREATE KEYSPACE IF NOT EXISTS nam WITH replication = {'class': 'SimpleStrategy', 'replication_factor': 1};

CREATE TABLE IF NOT EXISTS global.users (
    id text PRIMARY KEY,
    username text,
    primary_front int,
    inserted_at timestamp,
    updated_at timestamp
);

CREATE TABLE IF NOT EXISTS global.user_registry (
    user_id text PRIMARY KEY,
    region text,
    discord_id text,
    apple_id text,
    google_id text,
    email text,
    username text,
    inserted_at timestamp,
    updated_at timestamp
);

CREATE INDEX IF NOT EXISTS user_registry_discord_id ON global.user_registry (discord_id);
CREATE INDEX IF NOT EXISTS user_registry_email ON global.user_registry (email);
CREATE INDEX IF NOT EXISTS user_registry_username ON global.user_registry (username);
CREATE INDEX IF NOT EXISTS user_registry_apple_id ON global.user_registry (apple_id);
CREATE INDEX IF NOT EXISTS user_registry_google_id ON global.user_registry (google_id);

CREATE TABLE IF NOT EXISTS global.notification_tokens (
    user_id text,
    push_token text,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, push_token)
);

CREATE INDEX IF NOT EXISTS notification_tokens_by_token ON global.notification_tokens (push_token);

CREATE TABLE IF NOT EXISTS global.friendships (
    user_id text,
    friend_id text,
    level smallint,
    since timestamp,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, friend_id)
);

CREATE TABLE IF NOT EXISTS global.friend_requests (
    from_id text,
    to_id text,
    date_sent timestamp,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (from_id, to_id)
);

CREATE INDEX IF NOT EXISTS friend_requests_by_to_id ON global.friend_requests (to_id);

CREATE TABLE IF NOT EXISTS global.aggregate_versions_by_region (
    region_scope text,
    aggregate_type text,
    aggregate_id text,
    version bigint,
    PRIMARY KEY ((region_scope, aggregate_type), aggregate_id)
);

CREATE TABLE IF NOT EXISTS nam.alters (
    user_id text,
    id smallint,
    name text,
    alias text,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, id)
);

CREATE TABLE IF NOT EXISTS nam.tags (
    user_id text,
    id uuid,
    parent_tag_id uuid,
    name text,
    description text,
    color text,
    security_level smallint,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, id)
);

CREATE TABLE IF NOT EXISTS nam.alter_tags (
    user_id text,
    tag_id uuid,
    alter_id smallint,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, tag_id, alter_id)
);

CREATE TABLE IF NOT EXISTS nam.polls (
    user_id text,
    id uuid,
    title text,
    description text,
    type smallint,
    data text,
    time_end timestamp,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, id)
);

CREATE TABLE IF NOT EXISTS nam.current_fronts (
    user_id text,
    alter_id smallint,
    id uuid,
    comment text,
    time_start timestamp,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, alter_id)
);

CREATE TABLE IF NOT EXISTS nam.fronts (
    user_id text,
    id uuid,
    alter_id smallint,
    comment text,
    time_start timestamp,
    time_end timestamp,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, id, time_start)
);

CREATE MATERIALIZED VIEW IF NOT EXISTS nam.fronts_by_time AS
SELECT * FROM nam.fronts
WHERE user_id IS NOT NULL AND time_start IS NOT NULL AND time_end IS NOT NULL AND id IS NOT NULL
PRIMARY KEY (user_id, time_start, time_end, id);

CREATE TABLE IF NOT EXISTS nam.global_journals (
    user_id text,
    id uuid,
    title text,
    content text,
    color text,
    pinned boolean,
    locked boolean,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, id)
);

CREATE TABLE IF NOT EXISTS nam.global_journal_alters (
    user_id text,
    global_journal_id uuid,
    alter_id smallint,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, global_journal_id, alter_id)
);

CREATE TABLE IF NOT EXISTS nam.alter_journals (
    user_id text,
    id uuid,
    alter_id smallint,
    title text,
    content text,
    color text,
    pinned boolean,
    locked boolean,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, id, alter_id)
);

CREATE MATERIALIZED VIEW IF NOT EXISTS nam.alter_journals_by_alter AS
SELECT * FROM nam.alter_journals
WHERE user_id IS NOT NULL AND alter_id IS NOT NULL AND id IS NOT NULL
PRIMARY KEY (user_id, alter_id, id);

CREATE TABLE IF NOT EXISTS nam.settings_fields (
    user_id text,
    id uuid,
    position int,
    name text,
    value text,
    updated_at timestamp,
    PRIMARY KEY (user_id, id)
);
