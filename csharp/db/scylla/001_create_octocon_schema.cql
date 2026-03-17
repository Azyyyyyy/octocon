-- Create regional keyspaces
CREATE KEYSPACE IF NOT EXISTS nam WITH replication = {'class':'SimpleStrategy', 'replication_factor':1};
CREATE KEYSPACE IF NOT EXISTS eur WITH replication = {'class':'SimpleStrategy', 'replication_factor':1};
CREATE KEYSPACE IF NOT EXISTS ocn WITH replication = {'class':'SimpleStrategy', 'replication_factor':1};
CREATE KEYSPACE IF NOT EXISTS eas WITH replication = {'class':'SimpleStrategy', 'replication_factor':1};
CREATE KEYSPACE IF NOT EXISTS sam WITH replication = {'class':'SimpleStrategy', 'replication_factor':1};
CREATE KEYSPACE IF NOT EXISTS sas WITH replication = {'class':'SimpleStrategy', 'replication_factor':1};
CREATE KEYSPACE IF NOT EXISTS gdpr WITH replication = {'class':'SimpleStrategy', 'replication_factor':1};

-- Create global keyspace
CREATE KEYSPACE IF NOT EXISTS global WITH replication = {'class':'SimpleStrategy', 'replication_factor':1};

-- Create metrics keyspace
CREATE KEYSPACE IF NOT EXISTS nam_nt WITH replication = {'class':'SimpleStrategy', 'replication_factor':1};

-- ============================================================================
-- REGIONAL KEYSPACE TYPES AND TABLES (repeated for each region)
-- ============================================================================

-- DISCORD SERVER SETTINGS UDT for each region
CREATE TYPE IF NOT EXISTS nam.discord_server_settings (
    guild_id text,
    proxying_disabled boolean,
    autoproxy_mode smallint,
    latched_alter int
);

CREATE TYPE IF NOT EXISTS eur.discord_server_settings (
    guild_id text,
    proxying_disabled boolean,
    autoproxy_mode smallint,
    latched_alter int
);

CREATE TYPE IF NOT EXISTS ocn.discord_server_settings (
    guild_id text,
    proxying_disabled boolean,
    autoproxy_mode smallint,
    latched_alter int
);

CREATE TYPE IF NOT EXISTS eas.discord_server_settings (
    guild_id text,
    proxying_disabled boolean,
    autoproxy_mode smallint,
    latched_alter int
);

CREATE TYPE IF NOT EXISTS sam.discord_server_settings (
    guild_id text,
    proxying_disabled boolean,
    autoproxy_mode smallint,
    latched_alter int
);

CREATE TYPE IF NOT EXISTS sas.discord_server_settings (
    guild_id text,
    proxying_disabled boolean,
    autoproxy_mode smallint,
    latched_alter int
);

CREATE TYPE IF NOT EXISTS gdpr.discord_server_settings (
    guild_id text,
    proxying_disabled boolean,
    autoproxy_mode smallint,
    latched_alter int
);

-- DISCORD SETTINGS UDT for each region
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

CREATE TYPE IF NOT EXISTS eur.discord_settings (
    system_tag text,
    show_system_tag boolean,
    case_insensitive_proxies boolean,
    show_pronouns boolean,
    ids_as_proxies boolean,
    silent_proxying boolean,
    use_proxy_delay boolean,
    global_autoproxy_mode smallint,
    global_latched_alter int,
    server_settings list<frozen<eur.discord_server_settings>>
);

CREATE TYPE IF NOT EXISTS ocn.discord_settings (
    system_tag text,
    show_system_tag boolean,
    case_insensitive_proxies boolean,
    show_pronouns boolean,
    ids_as_proxies boolean,
    silent_proxying boolean,
    use_proxy_delay boolean,
    global_autoproxy_mode smallint,
    global_latched_alter int,
    server_settings list<frozen<ocn.discord_server_settings>>
);

CREATE TYPE IF NOT EXISTS eas.discord_settings (
    system_tag text,
    show_system_tag boolean,
    case_insensitive_proxies boolean,
    show_pronouns boolean,
    ids_as_proxies boolean,
    silent_proxying boolean,
    use_proxy_delay boolean,
    global_autoproxy_mode smallint,
    global_latched_alter int,
    server_settings list<frozen<eas.discord_server_settings>>
);

CREATE TYPE IF NOT EXISTS sam.discord_settings (
    system_tag text,
    show_system_tag boolean,
    case_insensitive_proxies boolean,
    show_pronouns boolean,
    ids_as_proxies boolean,
    silent_proxying boolean,
    use_proxy_delay boolean,
    global_autoproxy_mode smallint,
    global_latched_alter int,
    server_settings list<frozen<sam.discord_server_settings>>
);

CREATE TYPE IF NOT EXISTS sas.discord_settings (
    system_tag text,
    show_system_tag boolean,
    case_insensitive_proxies boolean,
    show_pronouns boolean,
    ids_as_proxies boolean,
    silent_proxying boolean,
    use_proxy_delay boolean,
    global_autoproxy_mode smallint,
    global_latched_alter int,
    server_settings list<frozen<sas.discord_server_settings>>
);

CREATE TYPE IF NOT EXISTS gdpr.discord_settings (
    system_tag text,
    show_system_tag boolean,
    case_insensitive_proxies boolean,
    show_pronouns boolean,
    ids_as_proxies boolean,
    silent_proxying boolean,
    use_proxy_delay boolean,
    global_autoproxy_mode smallint,
    global_latched_alter int,
    server_settings list<frozen<gdpr.discord_server_settings>>
);

-- FIELD UDT for each region
CREATE TYPE IF NOT EXISTS nam.field (
    id uuid,
    name text,
    type smallint,
    locked boolean,
    security_level smallint
);

CREATE TYPE IF NOT EXISTS eur.field (
    id uuid,
    name text,
    type smallint,
    locked boolean,
    security_level smallint
);

CREATE TYPE IF NOT EXISTS ocn.field (
    id uuid,
    name text,
    type smallint,
    locked boolean,
    security_level smallint
);

CREATE TYPE IF NOT EXISTS eas.field (
    id uuid,
    name text,
    type smallint,
    locked boolean,
    security_level smallint
);

CREATE TYPE IF NOT EXISTS sam.field (
    id uuid,
    name text,
    type smallint,
    locked boolean,
    security_level smallint
);

CREATE TYPE IF NOT EXISTS sas.field (
    id uuid,
    name text,
    type smallint,
    locked boolean,
    security_level smallint
);

CREATE TYPE IF NOT EXISTS gdpr.field (
    id uuid,
    name text,
    type smallint,
    locked boolean,
    security_level smallint
);

-- ALTER FIELD UDT for each region
CREATE TYPE IF NOT EXISTS nam.alter_field (
    id uuid,
    value text
);

CREATE TYPE IF NOT EXISTS eur.alter_field (
    id uuid,
    value text
);

CREATE TYPE IF NOT EXISTS ocn.alter_field (
    id uuid,
    value text
);

CREATE TYPE IF NOT EXISTS eas.alter_field (
    id uuid,
    value text
);

CREATE TYPE IF NOT EXISTS sam.alter_field (
    id uuid,
    value text
);

CREATE TYPE IF NOT EXISTS sas.alter_field (
    id uuid,
    value text
);

CREATE TYPE IF NOT EXISTS gdpr.alter_field (
    id uuid,
    value text
);

-- ============================================================================
-- USERS TABLE for each region
-- ============================================================================

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

CREATE TABLE IF NOT EXISTS eur.users (
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
    discord_settings frozen<eur.discord_settings>,
    fields list<frozen<eur.field>>,
    salt text,
    encryption_initialized boolean,
    encryption_key_checksum text,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (id)
);

CREATE TABLE IF NOT EXISTS ocn.users (
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
    discord_settings frozen<ocn.discord_settings>,
    fields list<frozen<ocn.field>>,
    salt text,
    encryption_initialized boolean,
    encryption_key_checksum text,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (id)
);

CREATE TABLE IF NOT EXISTS eas.users (
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
    discord_settings frozen<eas.discord_settings>,
    fields list<frozen<eas.field>>,
    salt text,
    encryption_initialized boolean,
    encryption_key_checksum text,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (id)
);

CREATE TABLE IF NOT EXISTS sam.users (
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
    discord_settings frozen<sam.discord_settings>,
    fields list<frozen<sam.field>>,
    salt text,
    encryption_initialized boolean,
    encryption_key_checksum text,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (id)
);

CREATE TABLE IF NOT EXISTS sas.users (
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
    discord_settings frozen<sas.discord_settings>,
    fields list<frozen<sas.field>>,
    salt text,
    encryption_initialized boolean,
    encryption_key_checksum text,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (id)
);

CREATE TABLE IF NOT EXISTS gdpr.users (
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
    discord_settings frozen<gdpr.discord_settings>,
    fields list<frozen<gdpr.field>>,
    salt text,
    encryption_initialized boolean,
    encryption_key_checksum text,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (id)
);

-- USERS indexes
CREATE INDEX IF NOT EXISTS users_by_discord_id ON nam.users (discord_id);
CREATE INDEX IF NOT EXISTS users_by_email ON nam.users (email);
CREATE INDEX IF NOT EXISTS users_by_username ON nam.users (username);
CREATE INDEX IF NOT EXISTS users_by_apple_id ON nam.users (apple_id);
CREATE INDEX IF NOT EXISTS users_by_google_id ON nam.users (google_id);

CREATE INDEX IF NOT EXISTS users_by_discord_id ON eur.users (discord_id);
CREATE INDEX IF NOT EXISTS users_by_email ON eur.users (email);
CREATE INDEX IF NOT EXISTS users_by_username ON eur.users (username);
CREATE INDEX IF NOT EXISTS users_by_apple_id ON eur.users (apple_id);
CREATE INDEX IF NOT EXISTS users_by_google_id ON eur.users (google_id);

CREATE INDEX IF NOT EXISTS users_by_discord_id ON ocn.users (discord_id);
CREATE INDEX IF NOT EXISTS users_by_email ON ocn.users (email);
CREATE INDEX IF NOT EXISTS users_by_username ON ocn.users (username);
CREATE INDEX IF NOT EXISTS users_by_apple_id ON ocn.users (apple_id);
CREATE INDEX IF NOT EXISTS users_by_google_id ON ocn.users (google_id);

CREATE INDEX IF NOT EXISTS users_by_discord_id ON eas.users (discord_id);
CREATE INDEX IF NOT EXISTS users_by_email ON eas.users (email);
CREATE INDEX IF NOT EXISTS users_by_username ON eas.users (username);
CREATE INDEX IF NOT EXISTS users_by_apple_id ON eas.users (apple_id);
CREATE INDEX IF NOT EXISTS users_by_google_id ON eas.users (google_id);

CREATE INDEX IF NOT EXISTS users_by_discord_id ON sam.users (discord_id);
CREATE INDEX IF NOT EXISTS users_by_email ON sam.users (email);
CREATE INDEX IF NOT EXISTS users_by_username ON sam.users (username);
CREATE INDEX IF NOT EXISTS users_by_apple_id ON sam.users (apple_id);
CREATE INDEX IF NOT EXISTS users_by_google_id ON sam.users (google_id);

CREATE INDEX IF NOT EXISTS users_by_discord_id ON sas.users (discord_id);
CREATE INDEX IF NOT EXISTS users_by_email ON sas.users (email);
CREATE INDEX IF NOT EXISTS users_by_username ON sas.users (username);
CREATE INDEX IF NOT EXISTS users_by_apple_id ON sas.users (apple_id);
CREATE INDEX IF NOT EXISTS users_by_google_id ON sas.users (google_id);

CREATE INDEX IF NOT EXISTS users_by_discord_id ON gdpr.users (discord_id);
CREATE INDEX IF NOT EXISTS users_by_email ON gdpr.users (email);
CREATE INDEX IF NOT EXISTS users_by_username ON gdpr.users (username);
CREATE INDEX IF NOT EXISTS users_by_apple_id ON gdpr.users (apple_id);
CREATE INDEX IF NOT EXISTS users_by_google_id ON gdpr.users (google_id);

-- ============================================================================
-- ALTERS TABLE for each region
-- ============================================================================

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

CREATE TABLE IF NOT EXISTS eur.alters (
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
    fields list<frozen<eur.alter_field>>,
    untracked boolean,
    archived boolean,
    pinned boolean,
    last_fronted timestamp,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, id)
);

CREATE TABLE IF NOT EXISTS ocn.alters (
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
    fields list<frozen<ocn.alter_field>>,
    untracked boolean,
    archived boolean,
    pinned boolean,
    last_fronted timestamp,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, id)
);

CREATE TABLE IF NOT EXISTS eas.alters (
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
    fields list<frozen<eas.alter_field>>,
    untracked boolean,
    archived boolean,
    pinned boolean,
    last_fronted timestamp,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, id)
);

CREATE TABLE IF NOT EXISTS sam.alters (
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
    fields list<frozen<sam.alter_field>>,
    untracked boolean,
    archived boolean,
    pinned boolean,
    last_fronted timestamp,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, id)
);

CREATE TABLE IF NOT EXISTS sas.alters (
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
    fields list<frozen<sas.alter_field>>,
    untracked boolean,
    archived boolean,
    pinned boolean,
    last_fronted timestamp,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, id)
);

CREATE TABLE IF NOT EXISTS gdpr.alters (
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
    fields list<frozen<gdpr.alter_field>>,
    untracked boolean,
    archived boolean,
    pinned boolean,
    last_fronted timestamp,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, id)
);

-- ALTERS indexes
CREATE INDEX IF NOT EXISTS alters_by_alias ON nam.alters ((user_id), alias);
CREATE INDEX IF NOT EXISTS alters_by_alias ON eur.alters ((user_id), alias);
CREATE INDEX IF NOT EXISTS alters_by_alias ON ocn.alters ((user_id), alias);
CREATE INDEX IF NOT EXISTS alters_by_alias ON eas.alters ((user_id), alias);
CREATE INDEX IF NOT EXISTS alters_by_alias ON sam.alters ((user_id), alias);
CREATE INDEX IF NOT EXISTS alters_by_alias ON sas.alters ((user_id), alias);
CREATE INDEX IF NOT EXISTS alters_by_alias ON gdpr.alters ((user_id), alias);

-- ============================================================================
-- TAGS TABLE for each region
-- ============================================================================

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

CREATE TABLE IF NOT EXISTS eur.tags (
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

CREATE TABLE IF NOT EXISTS ocn.tags (
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

CREATE TABLE IF NOT EXISTS eas.tags (
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

CREATE TABLE IF NOT EXISTS sam.tags (
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

CREATE TABLE IF NOT EXISTS sas.tags (
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

CREATE TABLE IF NOT EXISTS gdpr.tags (
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

-- ============================================================================
-- ALTER_TAGS TABLE for each region
-- ============================================================================

CREATE TABLE IF NOT EXISTS nam.alter_tags (
    user_id text,
    tag_id uuid,
    alter_id smallint,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, tag_id, alter_id)
);

CREATE TABLE IF NOT EXISTS eur.alter_tags (
    user_id text,
    tag_id uuid,
    alter_id smallint,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, tag_id, alter_id)
);

CREATE TABLE IF NOT EXISTS ocn.alter_tags (
    user_id text,
    tag_id uuid,
    alter_id smallint,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, tag_id, alter_id)
);

CREATE TABLE IF NOT EXISTS eas.alter_tags (
    user_id text,
    tag_id uuid,
    alter_id smallint,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, tag_id, alter_id)
);

CREATE TABLE IF NOT EXISTS sam.alter_tags (
    user_id text,
    tag_id uuid,
    alter_id smallint,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, tag_id, alter_id)
);

CREATE TABLE IF NOT EXISTS sas.alter_tags (
    user_id text,
    tag_id uuid,
    alter_id smallint,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, tag_id, alter_id)
);

CREATE TABLE IF NOT EXISTS gdpr.alter_tags (
    user_id text,
    tag_id uuid,
    alter_id smallint,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, tag_id, alter_id)
);

-- ALTER_TAGS indexes
CREATE INDEX IF NOT EXISTS alter_tags_by_alter ON nam.alter_tags ((user_id), alter_id);
CREATE INDEX IF NOT EXISTS alter_tags_by_alter ON eur.alter_tags ((user_id), alter_id);
CREATE INDEX IF NOT EXISTS alter_tags_by_alter ON ocn.alter_tags ((user_id), alter_id);
CREATE INDEX IF NOT EXISTS alter_tags_by_alter ON eas.alter_tags ((user_id), alter_id);
CREATE INDEX IF NOT EXISTS alter_tags_by_alter ON sam.alter_tags ((user_id), alter_id);
CREATE INDEX IF NOT EXISTS alter_tags_by_alter ON sas.alter_tags ((user_id), alter_id);
CREATE INDEX IF NOT EXISTS alter_tags_by_alter ON gdpr.alter_tags ((user_id), alter_id);

-- ============================================================================
-- POLLS TABLE for each region
-- ============================================================================

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

CREATE TABLE IF NOT EXISTS eur.polls (
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

CREATE TABLE IF NOT EXISTS ocn.polls (
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

CREATE TABLE IF NOT EXISTS eas.polls (
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

CREATE TABLE IF NOT EXISTS sam.polls (
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

CREATE TABLE IF NOT EXISTS sas.polls (
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

CREATE TABLE IF NOT EXISTS gdpr.polls (
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

-- ============================================================================
-- GLOBAL_JOURNALS TABLE for each region
-- ============================================================================

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

CREATE TABLE IF NOT EXISTS eur.global_journals (
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

CREATE TABLE IF NOT EXISTS ocn.global_journals (
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

CREATE TABLE IF NOT EXISTS eas.global_journals (
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

CREATE TABLE IF NOT EXISTS sam.global_journals (
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

CREATE TABLE IF NOT EXISTS sas.global_journals (
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

CREATE TABLE IF NOT EXISTS gdpr.global_journals (
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

-- ============================================================================
-- GLOBAL_JOURNAL_ALTERS TABLE for each region
-- ============================================================================

CREATE TABLE IF NOT EXISTS nam.global_journal_alters (
    user_id text,
    global_journal_id uuid,
    alter_id smallint,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, global_journal_id, alter_id)
);

CREATE TABLE IF NOT EXISTS eur.global_journal_alters (
    user_id text,
    global_journal_id uuid,
    alter_id smallint,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, global_journal_id, alter_id)
);

CREATE TABLE IF NOT EXISTS ocn.global_journal_alters (
    user_id text,
    global_journal_id uuid,
    alter_id smallint,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, global_journal_id, alter_id)
);

CREATE TABLE IF NOT EXISTS eas.global_journal_alters (
    user_id text,
    global_journal_id uuid,
    alter_id smallint,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, global_journal_id, alter_id)
);

CREATE TABLE IF NOT EXISTS sam.global_journal_alters (
    user_id text,
    global_journal_id uuid,
    alter_id smallint,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, global_journal_id, alter_id)
);

CREATE TABLE IF NOT EXISTS sas.global_journal_alters (
    user_id text,
    global_journal_id uuid,
    alter_id smallint,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, global_journal_id, alter_id)
);

CREATE TABLE IF NOT EXISTS gdpr.global_journal_alters (
    user_id text,
    global_journal_id uuid,
    alter_id smallint,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, global_journal_id, alter_id)
);

-- ============================================================================
-- ALTER_JOURNALS TABLE for each region
-- ============================================================================

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

CREATE TABLE IF NOT EXISTS eur.alter_journals (
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

CREATE TABLE IF NOT EXISTS ocn.alter_journals (
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

CREATE TABLE IF NOT EXISTS eas.alter_journals (
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

CREATE TABLE IF NOT EXISTS sam.alter_journals (
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

CREATE TABLE IF NOT EXISTS sas.alter_journals (
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

CREATE TABLE IF NOT EXISTS gdpr.alter_journals (
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

-- ALTER_JOURNALS indexes
CREATE INDEX IF NOT EXISTS alter_journals_by_alter ON nam.alter_journals ((user_id), alter_id);
CREATE INDEX IF NOT EXISTS alter_journals_by_alter ON eur.alter_journals ((user_id), alter_id);
CREATE INDEX IF NOT EXISTS alter_journals_by_alter ON ocn.alter_journals ((user_id), alter_id);
CREATE INDEX IF NOT EXISTS alter_journals_by_alter ON eas.alter_journals ((user_id), alter_id);
CREATE INDEX IF NOT EXISTS alter_journals_by_alter ON sam.alter_journals ((user_id), alter_id);
CREATE INDEX IF NOT EXISTS alter_journals_by_alter ON sas.alter_journals ((user_id), alter_id);
CREATE INDEX IF NOT EXISTS alter_journals_by_alter ON gdpr.alter_journals ((user_id), alter_id);

-- ============================================================================
-- FRONTS TABLE for each region
-- ============================================================================

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

CREATE TABLE IF NOT EXISTS eur.fronts (
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

CREATE TABLE IF NOT EXISTS ocn.fronts (
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

CREATE TABLE IF NOT EXISTS eas.fronts (
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

CREATE TABLE IF NOT EXISTS sam.fronts (
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

CREATE TABLE IF NOT EXISTS sas.fronts (
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

CREATE TABLE IF NOT EXISTS gdpr.fronts (
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

-- ============================================================================
-- CURRENT_FRONTS TABLE for each region
-- ============================================================================

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

CREATE TABLE IF NOT EXISTS eur.current_fronts (
    user_id text,
    alter_id smallint,
    id uuid,
    comment text,
    time_start timestamp,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, alter_id)
);

CREATE TABLE IF NOT EXISTS ocn.current_fronts (
    user_id text,
    alter_id smallint,
    id uuid,
    comment text,
    time_start timestamp,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, alter_id)
);

CREATE TABLE IF NOT EXISTS eas.current_fronts (
    user_id text,
    alter_id smallint,
    id uuid,
    comment text,
    time_start timestamp,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, alter_id)
);

CREATE TABLE IF NOT EXISTS sam.current_fronts (
    user_id text,
    alter_id smallint,
    id uuid,
    comment text,
    time_start timestamp,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, alter_id)
);

CREATE TABLE IF NOT EXISTS sas.current_fronts (
    user_id text,
    alter_id smallint,
    id uuid,
    comment text,
    time_start timestamp,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, alter_id)
);

CREATE TABLE IF NOT EXISTS gdpr.current_fronts (
    user_id text,
    alter_id smallint,
    id uuid,
    comment text,
    time_start timestamp,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (user_id, alter_id)
);

-- ============================================================================
-- GLOBAL KEYSPACE TABLES
-- ============================================================================

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

CREATE TABLE IF NOT EXISTS nam.channel_blacklists (
    channel_id text,
    guild_id text,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (channel_id)
);

CREATE INDEX IF NOT EXISTS channel_blacklists_by_guild_id ON nam.channel_blacklists (guild_id);

CREATE TYPE IF NOT EXISTS nam.server_settings_data (
    log_channel text,
    force_system_tags boolean,
    proxy_disabled_users list<text>
);

CREATE TABLE IF NOT EXISTS nam.server_settings (
    guild_id text,
    data frozen<nam.server_settings_data>,
    inserted_at timestamp,
    updated_at timestamp,
    PRIMARY KEY (guild_id)
);

CREATE TABLE IF NOT EXISTS global.aggregate_versions_by_region (
    region_scope text,
    aggregate_type text,
    aggregate_id text,
    version bigint,
    PRIMARY KEY ((region_scope, aggregate_type), aggregate_id)
);

CREATE MATERIALIZED VIEW IF NOT EXISTS nam.fronts_by_time AS
    SELECT * FROM nam.fronts
    WHERE user_id IS NOT NULL AND time_start IS NOT NULL AND time_end IS NOT NULL AND id IS NOT NULL
    PRIMARY KEY (user_id, time_start, time_end, id);

CREATE MATERIALIZED VIEW IF NOT EXISTS eur.fronts_by_time AS
    SELECT * FROM eur.fronts
    WHERE user_id IS NOT NULL AND time_start IS NOT NULL AND time_end IS NOT NULL AND id IS NOT NULL
    PRIMARY KEY (user_id, time_start, time_end, id);

CREATE MATERIALIZED VIEW IF NOT EXISTS ocn.fronts_by_time AS
    SELECT * FROM ocn.fronts
    WHERE user_id IS NOT NULL AND time_start IS NOT NULL AND time_end IS NOT NULL AND id IS NOT NULL
    PRIMARY KEY (user_id, time_start, time_end, id);

CREATE MATERIALIZED VIEW IF NOT EXISTS eas.fronts_by_time AS
    SELECT * FROM eas.fronts
    WHERE user_id IS NOT NULL AND time_start IS NOT NULL AND time_end IS NOT NULL AND id IS NOT NULL
    PRIMARY KEY (user_id, time_start, time_end, id);

CREATE MATERIALIZED VIEW IF NOT EXISTS sam.fronts_by_time AS
    SELECT * FROM sam.fronts
    WHERE user_id IS NOT NULL AND time_start IS NOT NULL AND time_end IS NOT NULL AND id IS NOT NULL
    PRIMARY KEY (user_id, time_start, time_end, id);

CREATE MATERIALIZED VIEW IF NOT EXISTS sas.fronts_by_time AS
    SELECT * FROM sas.fronts
    WHERE user_id IS NOT NULL AND time_start IS NOT NULL AND time_end IS NOT NULL AND id IS NOT NULL
    PRIMARY KEY (user_id, time_start, time_end, id);

CREATE MATERIALIZED VIEW IF NOT EXISTS gdpr.fronts_by_time AS
    SELECT * FROM gdpr.fronts
    WHERE user_id IS NOT NULL AND time_start IS NOT NULL AND time_end IS NOT NULL AND id IS NOT NULL
    PRIMARY KEY (user_id, time_start, time_end, id);

-- ============================================================================
-- METRICS KEYSPACE
-- ============================================================================

CREATE TABLE IF NOT EXISTS nam_nt.metrics_counts (
    key text,
    value counter,
    PRIMARY KEY (key)
);
