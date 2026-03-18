namespace Octocon.Contracts.Operations;

public static class OperationIds
{
    // Phase B canonical – used by the HTTP API
    public const string SettingsUsernameUpdate = "cmd.settings.username.update";
    public const string SettingsDescriptionUpdate = "cmd.settings.description.update";
    public const string SettingsPushTokenAdd = "cmd.settings.push_token.add";
    public const string SettingsPushTokenRemove = "cmd.settings.push_token.remove";
    public const string SettingsEncryptionRecover = "cmd.settings.encryption.recover";
    public const string SettingsEncryptionReset = "cmd.settings.encryption.reset";
    public const string SettingsLinkToken = "qry.settings.link_token";
    public const string SettingsAvatarUpload = "cmd.settings.avatar.upload";
    public const string SettingsAvatarDelete = "cmd.settings.avatar.delete";
    public const string SettingsImportPk = "cmd.settings.import.pk";
    public const string SettingsImportSp = "cmd.settings.import.sp";
    public const string SettingsAuthUnlinkDiscord = "cmd.settings.auth.unlink_discord";
    public const string SettingsAuthUnlinkEmail = "cmd.settings.auth.unlink_email";
    public const string SettingsAuthUnlinkApple = "cmd.settings.auth.unlink_apple";
    public const string SettingsAccountDelete = "cmd.settings.account.delete";
    public const string SettingsAltersWipe = "cmd.settings.alters.wipe";
    public const string SettingsFieldCreate = "cmd.settings.field.create";
    public const string SettingsFieldUpdate = "cmd.settings.field.update";
    public const string SettingsFieldDelete = "cmd.settings.field.delete";
    public const string SettingsFieldRelocate = "cmd.settings.field.relocate";

    public const string QueryFrontMonth = "qry.front.month";
    public const string QueryFrontBetween = "qry.front.between";
    public const string QueryFrontGet = "qry.front.get";
    public const string QuerySystemHeartbeat = "qry.system.heartbeat";
    public const string QuerySystemPublicGet = "qry.system.public.get";
    public const string QueryAlterPublicList = "qry.alter.public.list";
    public const string QueryAlterPublicGet = "qry.alter.public.get";
    public const string QueryTagPublicList = "qry.tag.public.list";
    public const string QueryTagPublicGet = "qry.tag.public.get";
    public const string QueryFrontPublicList = "qry.front.public.list";
    public const string QuerySystemPublicBatch = "qry.system.public.batch";
    public const string QueryAuthOAuthRequest = "qry.auth.oauth.request";
    public const string AuthOAuthCallback = "cmd.auth.oauth.callback";
    public const string QueryAuthLinkRequest = "qry.auth.link.request";
    public const string AuthLinkCallback = "cmd.auth.link.callback";

    // Legacy CLI name retained for backward compatibility with idempotency records
    public const string AccountUsernameUpdate = "cmd.account.username.update";

    public const string AlterCreate = "cmd.alter.create";
    public const string AlterUpdate = "cmd.alter.update";
    public const string AlterDelete = "cmd.alter.delete";
    public const string AlterAvatarUpload = "cmd.alter.avatar.upload";
    public const string AlterAvatarDelete = "cmd.alter.avatar.delete";
    public const string FrontStart = "cmd.front.start";
    public const string FrontEnd = "cmd.front.end";
    public const string FrontBulkUpdate = "cmd.front.bulk_update";
    public const string FrontSet = "cmd.front.set";
    public const string FrontPrimary = "cmd.front.primary";
    public const string FrontDelete = "cmd.front.delete";
    public const string FrontCommentUpdate = "cmd.front.comment.update";
    public const string TagCreate = "cmd.tag.create";
    public const string FriendDelete = "cmd.friend.delete";
    public const string FriendTrust = "cmd.friend.trust";
    public const string FriendUntrust = "cmd.friend.untrust";
    public const string FriendRequestSend = "cmd.friend_request.send";
    public const string FriendRequestCancel = "cmd.friend_request.cancel";
    public const string FriendRequestAccept = "cmd.friend_request.accept";
    public const string FriendRequestReject = "cmd.friend_request.reject";
    public const string TagUpdate = "cmd.tag.update";
    public const string TagDelete = "cmd.tag.delete";
    public const string TagAttachAlter = "cmd.tag.attach_alter";
    public const string TagDetachAlter = "cmd.tag.detach_alter";
    public const string TagSetParent = "cmd.tag.set_parent";
    public const string TagRemoveParent = "cmd.tag.remove_parent";
    public const string PollCreate = "cmd.poll.create";
    public const string PollUpdate = "cmd.poll.update";
    public const string PollDelete = "cmd.poll.delete";
    public const string JournalGlobalCreate = "cmd.journal.global.create";
    public const string JournalGlobalUpdate = "cmd.journal.global.update";
    public const string JournalGlobalDelete = "cmd.journal.global.delete";
    public const string JournalGlobalLock = "cmd.journal.global.lock";
    public const string JournalGlobalUnlock = "cmd.journal.global.unlock";
    public const string JournalGlobalPin = "cmd.journal.global.pin";
    public const string JournalGlobalUnpin = "cmd.journal.global.unpin";
    public const string JournalGlobalAttachAlter = "cmd.journal.global.attach_alter";
    public const string JournalGlobalDetachAlter = "cmd.journal.global.detach_alter";
    public const string JournalAlterCreate = "cmd.journal.alter.create";
    public const string JournalAlterUpdate = "cmd.journal.alter.update";
    public const string JournalAlterDelete = "cmd.journal.alter.delete";
    public const string JournalAlterLock = "cmd.journal.alter.lock";
    public const string JournalAlterUnlock = "cmd.journal.alter.unlock";
    public const string JournalAlterPin = "cmd.journal.alter.pin";
    public const string JournalAlterUnpin = "cmd.journal.alter.unpin";
    public const string SettingsEncryptionSetup = "cmd.settings.encryption.setup";
}
