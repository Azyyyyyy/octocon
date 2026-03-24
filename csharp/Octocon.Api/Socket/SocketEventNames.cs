namespace Octocon.Api.Socket;

public static class SocketEventNames
{
    public static class Alters
    {
        public const string Created = "alter_created";
        public const string Updated = "alter_updated";
        public const string Deleted = "alter_deleted";
    }

    public static class Tags
    {
        public const string Created = "tag_created";
        public const string Updated = "tag_updated";
        public const string Deleted = "tag_deleted";
    }

    public static class Polls
    {
        public const string Created = "poll_created";
        public const string Updated = "poll_updated";
        public const string Deleted = "poll_deleted";
    }

    public static class Journals
    {
        public const string GlobalCreated = "global_journal_entry_created";
        public const string GlobalUpdated = "global_journal_entry_updated";
        public const string GlobalDeleted = "global_journal_entry_deleted";
        public const string AlterCreated = "alter_journal_entry_created";
        public const string AlterUpdated = "alter_journal_entry_updated";
        public const string AlterDeleted = "alter_journal_entry_deleted";
    }

    public static class Friendships
    {
        public const string Added = "friend_added";
        public const string Removed = "friend_removed";
        public const string Trusted = "friend_trusted";
        public const string Untrusted = "friend_untrusted";
        public const string RequestSent = "friend_request_sent";
        public const string RequestReceived = "friend_request_received";
        public const string RequestRemoved = "friend_request_removed";
    }

    public static class Settings
    {
        public const string FieldsUpdated = "fields_updated";
        public const string UsernameUpdated = "username_updated";
        public const string SelfUpdated = "self_updated";
        public const string AccountDeleted = "account_deleted";
        public const string AltersWiped = "alters_wiped";
        public const string EncryptedDataWiped = "encrypted_data_wiped";
        public const string DiscordAccountUnlinked = "discord_account_unlinked";
        public const string AppleAccountUnlinked = "apple_account_unlinked";
        public const string DiscordAccountLinked = "discord_account_linked";
        public const string GoogleAccountLinked = "google_account_linked";
        public const string AppleAccountLinked = "apple_account_linked";
        public const string AccountLinked = "account_linked";
    }
}