-- =====================
-- UDTs
-- =====================
CREATE TYPE IF NOT EXISTS nam.discord_server_settings (
    guild_id text,
    proxying_disabled boolean,
    autoproxy_mode smallint,
    latched_alter int
);

CREATE TYPE IF NOT EXISTS nam.discord_settings (
    system_tag text,
    show_system_tag boolean,
    case_insensitive_proxies boolean,
    show_pronouns boolean,
    ids_as_proxies boolean,
    silent_proxying boolean,
    use_proxy_delay boolean,
    global_autoproxy_mode smallint,
    global_latched_alter int,
    server_settings list<frozen<nam.discord_server_settings>>
);

CREATE TYPE IF NOT EXISTS nam.field (
    id uuid,
    name text,
    type smallint,
    locked boolean,
    security_level smallint
);

CREATE TYPE IF NOT EXISTS nam.alter_field (
    id uuid,
    value text
);

CREATE TYPE IF NOT EXISTS nam.server_settings_data (
    log_channel text,
    force_system_tags boolean,
    proxy_disabled_users list<text>
);

-- =====================
-- USERS TABLE
-- =====================
CREATE TABLE IF NOT EXISTS nam.users (
    id text,
    email text,
    discord_id text,
    apple_id text,
    google_id text,
    username text,
    description text,
    avatar_url text,
    lifetime_alter_count int,
    primary_front int,
    last_proxy_id smallint,
    discord_settings frozen<nam.discord_settings>,
    fields list<frozen<nam.field>>,
    salt text,
    encryption_initialized boolean,
    encryption_key_checksum text,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (id)
);

CREATE INDEX IF NOT EXISTS users_by_discord_id ON nam.users (discord_id);
CREATE INDEX IF NOT EXISTS users_by_email ON nam.users (email);
CREATE INDEX IF NOT EXISTS users_by_username ON nam.users (username);
CREATE INDEX IF NOT EXISTS users_by_apple_id ON nam.users (apple_id);
CREATE INDEX IF NOT EXISTS users_by_google_id ON nam.users (google_id);

-- =====================
-- ALTERS TABLE
-- =====================
CREATE TABLE IF NOT EXISTS nam.alters (
    user_id text,
    id smallint,
    alias text,
    name text,
    pronouns text,
    description text,
    avatar_url text,
    security_level smallint,
    extra_images list<text>,
    color text,
    discord_proxies list<text>,
    proxy_name text,
    fields list<frozen<nam.alter_field>>,
    untracked boolean,
    archived boolean,
    pinned boolean,
    last_fronted timestamp,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, id)
);

CREATE MATERIALIZED VIEW IF NOT EXISTS nam.alters_by_alias AS
    SELECT user_id, id, alias, name, pronouns, description, avatar_url,
           security_level, extra_images, color, discord_proxies, proxy_name,
           fields, untracked, archived, pinned, last_fronted, inserted_at, updated_at
    FROM nam.alters
    WHERE user_id IS NOT NULL AND alias IS NOT NULL AND id IS NOT NULL
    PRIMARY KEY ((user_id), alias, id);

-- =====================
-- TAGS TABLE
-- =====================
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

-- =====================
-- ALTER_TAGS TABLE
-- =====================
CREATE TABLE IF NOT EXISTS nam.alter_tags (
    user_id text,
    tag_id uuid,
    alter_id smallint,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, tag_id, alter_id)
);

CREATE MATERIALIZED VIEW IF NOT EXISTS nam.alter_tags_by_alter AS
    SELECT user_id, tag_id, alter_id, inserted_at, updated_at
    FROM nam.alter_tags
    WHERE user_id IS NOT NULL AND alter_id IS NOT NULL AND tag_id IS NOT NULL
    PRIMARY KEY ((user_id), alter_id, tag_id);

-- =====================
-- POLLS TABLE
-- =====================
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

-- =====================
-- GLOBAL_JOURNALS TABLE
-- =====================
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

-- =====================
-- GLOBAL_JOURNAL_ALTERS TABLE
-- =====================
CREATE TABLE IF NOT EXISTS nam.global_journal_alters (
    user_id text,
    journal_id uuid,
    alter_id smallint,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, journal_id, alter_id)
);

-- =====================
-- CURRENT_FRONTS TABLE
-- =====================
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

-- =====================
-- SERVER_SETTINGS TABLE
-- =====================
CREATE TABLE IF NOT EXISTS nam.server_settings (
    guild_id text,
    data frozen<nam.server_settings_data>,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (guild_id)
);

-- =====================
-- ALTER_JOURNALS TABLE
-- =====================
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
    SELECT user_id, id, alter_id, title, content, color, pinned, locked,
           inserted_at, updated_at
    FROM nam.alter_journals
    WHERE user_id IS NOT NULL AND alter_id IS NOT NULL AND id IS NOT NULL
    PRIMARY KEY ((user_id), alter_id, id);

-- =====================
-- FRONTS TABLE
-- =====================
CREATE TABLE IF NOT EXISTS nam.fronts (
    user_id text,
    id uuid,
    alter_id smallint,
    comment text,
    time_start timestamp,
    time_end timestamp,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, alter_id, id, time_start)
);

CREATE MATERIALIZED VIEW IF NOT EXISTS nam.fronts_by_time AS
    SELECT * FROM nam.fronts
    WHERE user_id IS NOT NULL AND time_start IS NOT NULL AND time_end IS NOT NULL
      AND id IS NOT NULL AND alter_id IS NOT NULL
    PRIMARY KEY (user_id, time_start, time_end, id, alter_id);

-- =====================
-- GLOBAL KEYSPACE TABLES
-- =====================
CREATE TABLE IF NOT EXISTS global.user_registry (
    user_id text,
    discord_id text,
    email text,
    username text,
    apple_id text,
    google_id text,
    region text,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id)
);

CREATE INDEX IF NOT EXISTS user_registry_by_discord_id ON global.user_registry (discord_id);
CREATE INDEX IF NOT EXISTS user_registry_by_email ON global.user_registry (email);
CREATE INDEX IF NOT EXISTS user_registry_by_username ON global.user_registry (username);
CREATE INDEX IF NOT EXISTS user_registry_by_apple_id ON global.user_registry (apple_id);
CREATE INDEX IF NOT EXISTS user_registry_by_google_id ON global.user_registry (google_id);

CREATE TABLE IF NOT EXISTS global.notification_tokens (
    user_id text,
    push_token text,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, push_token)
);

CREATE INDEX IF NOT EXISTS notification_tokens_by_push_token ON global.notification_tokens (push_token);

CREATE TABLE IF NOT EXISTS global.friendships (
    user_id text,
    friend_id text,
    level smallint,
    since timestamp,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, friend_id)
);

CREATE INDEX IF NOT EXISTS friendships_by_friend_id ON global.friendships (friend_id);

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

-- =====================
-- METRICS KEYSPACE
-- =====================
CREATE TABLE IF NOT EXISTS nam_nt.metrics_counts (
    key text,
    value counter,
    PRIMARY KEY (key)
);