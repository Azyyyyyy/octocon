defmodule OctoconDiscord.GuildCache do
  @moduledoc """
  An Mnesia-based cache for guilds.

  Modified to only cache specific keys of the guild struct.
  """

  use Supervisor

  @behaviour Nostrum.Cache.GuildCache

  @base_table_name :nostrum_guilds

  alias Nostrum.{
    Bot,
    Cache.GuildCache,
    Struct.Channel,
    Struct.Emoji,
    Struct.Guild,
    Struct.Sticker,
    Util
  }

  @doc "Start the supervisor."
  def start_link(opts) do
    Supervisor.start_link(__MODULE__, opts)
  end

  @impl Supervisor
  @doc "Set up the cache's Mnesia table."
  def init(opts) do
    # credo:disable-for-next-line
    table_name = :"#{@base_table_name}_#{Keyword.fetch!(opts, :name)}"

    table_options = [
      attributes: [:id, :data],
      record_name: record_name()
    ]

    case :mnesia.create_table(table_name, table_options) do
      {:atomic, :ok} -> :ok
      {:aborted, {:already_exists, _tab}} -> :ok
    end

    Supervisor.init([], strategy: :one_for_one)
  end

  @doc "Retrieve the Mnesia table name used for the cache."
  @spec table :: atom()
  def table do
    # credo:disable-for-next-line
    :"#{@base_table_name}_#{Bot.fetch_bot_name()}"
  end

  defp record_name, do: :nostrum_guild

  @doc "Drop the table used for caching."
  @spec teardown() :: {:atomic, :ok} | {:aborted, term()}
  def teardown, do: :mnesia.delete_table(table())

  @doc "Clear any objects in the cache."
  @spec clear() :: :ok
  def clear do
    {:atomic, :ok} = :mnesia.clear_table(table())
    :ok
  end

  @impl GuildCache
  @doc "Get a guild from the cache."
  @spec get(Guild.id()) :: {:ok, Guild.t()} | {:error, :not_found}
  def get(guild_id) do
    :mnesia.activity(:sync_transaction, fn ->
      case :mnesia.read(table(), guild_id) do
        [{_tag, _id, guild}] -> {:ok, guild}
        [] -> {:error, :not_found}
      end
    end)
  end

  @impl GuildCache
  @doc since: "0.10.0"
  @spec all() :: Enumerable.t(Guild.t())
  def all do
    ms = [{{:_, :_, :"$1"}, [], [:"$1"]}]

    Stream.resource(
      fn -> :mnesia.select(table(), ms, 100, :read) end,
      fn items ->
        case items do
          {matches, cont} ->
            {matches, :mnesia.select(cont)}

          :"$end_of_table" ->
            {:halt, nil}
        end
      end,
      fn _cont -> :ok end
    )
  end

  # Used by dispatch

  @impl GuildCache
  @doc "Create a guild from upstream data."
  @spec create(map()) :: Guild.t()
  def create(payload) do
    guild = Guild.to_struct(payload) |> strip_guild()
    record = {record_name(), guild.id, guild}
    writer = fn -> :mnesia.write(table(), record, :write) end
    :ok = :mnesia.activity(:sync_transaction, writer)
    guild
  end

  @impl GuildCache
  @doc "Update the given guild in the cache."
  def update(payload) do
    new_guild = Guild.to_struct(payload) |> strip_guild()

    old_guild =
      :mnesia.activity(:sync_transaction, fn ->
        case :mnesia.read(table(), new_guild.id, :write) do
          [{_tag, _id, old_guild} = entry] ->
            updated_guild = Guild.merge(old_guild, new_guild)

            :mnesia.write(table(), put_elem(entry, 2, updated_guild), :write)
            old_guild

          [] ->
            nil
        end
      end)

    {old_guild, new_guild}
  end

  @impl GuildCache
  @doc "Remove the given guild from the cache."
  @spec delete(Guild.id()) :: Guild.t() | nil
  def delete(guild_id) do
    :mnesia.activity(:sync_transaction, fn ->
      case :mnesia.read(table(), guild_id, :write) do
        [{_tag, _guild_id, guild}] ->
          :mnesia.delete(table(), guild_id, :write)
          guild

        _ ->
          nil
      end
    end)
  end

  @impl GuildCache
  @doc "Create the given channel for the given guild in the cache."
  @spec channel_create(Guild.id(), map()) :: Channel.t()
  def channel_create(guild_id, channel) do
    new_channel = Channel.to_struct(channel) |> Map.take([:id, :type, :parent_id])

    update_guild!(guild_id, fn guild ->
      new_channels = Map.put(guild.channels, channel.id, new_channel)
      {%{guild | channels: new_channels}, :ok}
    end)

    new_channel
  end

  @impl GuildCache
  @doc "Delete the channel from the given guild in the cache."
  @spec channel_delete(Guild.id(), Channel.id()) :: Channel.t() | :noop
  def channel_delete(guild_id, channel_id) do
    old_channel =
      update_guild!(guild_id, fn guild ->
        {popped, new_channels} = Map.pop(guild.channels, channel_id)
        {%{guild | channels: new_channels}, popped}
      end)

    if old_channel, do: old_channel, else: :noop
  end

  @doc "Update the channel on the given guild in the cache."
  @impl GuildCache
  @spec channel_update(Guild.id(), map()) :: {Channel.t() | nil, Channel.t()}
  def channel_update(guild_id, channel) do
    update_guild!(guild_id, fn guild ->
      {old, new, new_channels} =
        upsert(guild.channels, channel.id, channel |> Map.take([:id, :type, :parent_id]))

      {%{guild | channels: new_channels}, {old, new}}
    end)
  end

  @impl GuildCache
  @doc "Update the emoji list for the given guild in the cache."
  @spec emoji_update(Guild.id(), [map()]) :: {[Emoji.t()], [Emoji.t()]}
  def emoji_update(_guild_id, payload) do
    casted = Util.cast(payload, {:list, {:struct, Emoji}})
    {[], casted}
  end

  @impl GuildCache
  @doc "Update the sticker list for the given guild in the cache."
  @spec stickers_update(Guild.id(), [map()]) :: {[Sticker.t()], [Sticker.t()]}
  def stickers_update(_guild_id, stickers) do
    casted = Util.cast(stickers, {:list, {:struct, Sticker}})
    {[], casted}
  end

  @impl GuildCache
  @doc "Create the given role in the given guild in the cache."
  @spec role_create(Guild.id(), map()) :: {Guild.id(), Role.t()}
  def role_create(guild_id, payload) do
    update_guild!(guild_id, fn guild ->
      permissions = payload.permissions |> String.to_integer()
      new_role = %{id: payload.id, permissions: permissions}
      {_old, new, new_roles} = upsert(guild.roles, payload.id, new_role)
      {%{guild | roles: new_roles}, {guild_id, new}}
    end)
  end

  @doc "Delete the given role from the given guild in the cache."
  @impl GuildCache
  @spec role_delete(Guild.id(), Role.id()) :: {Guild.id(), Role.t()} | :noop
  def role_delete(guild_id, role_id) do
    update_guild!(guild_id, fn guild ->
      {popped, new_roles} = Map.pop(guild.roles, role_id)
      result = if popped, do: {guild_id, popped}, else: :noop
      {%{guild | roles: new_roles}, result}
    end)
  end

  @impl GuildCache
  @doc "Update the given role in the given guild in the cache."
  @spec role_update(Guild.id(), map()) :: {Guild.id(), Role.t() | nil, Role.t()}
  def role_update(guild_id, role) do
    update_guild!(guild_id, fn guild ->
      permissions = role.permissions |> String.to_integer()
      new_role = %{id: role.id, permissions: permissions}
      {old, new_role, new_roles} = upsert(guild.roles, role.id, new_role)
      new_guild = %{guild | roles: new_roles}
      {new_guild, {guild_id, old, new_role}}
    end)
  end

  @impl GuildCache
  @doc "Update guild voice states with the given voice state in the cache."
  @spec voice_state_update(Guild.id(), map()) :: {Guild.id(), [map()]}
  def voice_state_update(guild_id, _payload) do
    {guild_id, []}
  end

  @doc "Increment the guild member count by one."
  @doc since: "0.7.0"
  @impl GuildCache
  @spec member_count_up(Guild.id()) :: true
  def member_count_up(guild_id) do
    # May not be `update_guild!` for the case where guild intent is off.
    update_guild(guild_id, fn guild ->
      {%{guild | member_count: guild.member_count + 1}, true}
    end)

    true
  end

  @doc "Decrement the guild member count by one."
  @doc since: "0.7.0"
  @impl GuildCache
  @spec member_count_down(Guild.id()) :: true
  def member_count_down(guild_id) do
    # May not be `update_guild!` for the case where guild intent is off.
    update_guild(guild_id, fn guild ->
      {%{guild | member_count: guild.member_count - 1}, true}
    end)

    true
  end

  defp update_guild(guild_id, updater) do
    :mnesia.activity(
      :sync_transaction,
      fn ->
        case :mnesia.read(table(), guild_id, :write) do
          [{_tag, _id, old_guild} = entry] ->
            {new_guild, result} = updater.(old_guild)
            :mnesia.write(table(), put_elem(entry, 2, new_guild), :write)
            result

          [] ->
            nil
        end
      end
    )
  end

  defp update_guild!(guild_id, updater) do
    :mnesia.activity(
      :sync_transaction,
      fn ->
        [{_tag, _id, old_guild} = entry] = :mnesia.read(table(), guild_id, :write)
        {new_guild, result} = updater.(old_guild)
        :mnesia.write(table(), put_elem(entry, 2, new_guild), :write)
        result
      end
    )
  end

  defp strip_guild(%Nostrum.Struct.Guild{
         id: id,
         name: name,
         member_count: member_count,
         owner_id: owner_id,
         roles: roles,
         threads: threads,
         channels: channels
       }) do
    channels =
      channels
      |> Enum.map(fn {id, channel} ->
        {id, if(channel == nil, do: nil, else: Map.take(channel, [:id, :type, :parent_id]))}
      end)
      |> Map.new()

    threads =
      threads
      |> Enum.map(fn {id, thread} ->
        {id, if(thread == nil, do: nil, else: Map.take(thread, [:id, :type, :parent_id]))}
      end)
      |> Map.new()

    roles =
      roles
      |> Enum.map(fn {id, role} ->
        {id, if(role == nil, do: nil, else: Map.take(role, [:id, :permissions]))}
      end)
      |> Map.new()

    %{
      id: id,
      name: name,
      member_count: member_count,
      owner_id: owner_id,
      roles: roles,
      threads: threads,
      channels: channels
    }
  end

  @impl GuildCache
  @doc "Wrap queries in a transaction."
  def wrap_query(fun) do
    :mnesia.activity(:sync_transaction, fun)
  end

  defp upsert(map, key, new) do
    if Map.has_key?(map, key) do
      old = Map.get(map, key)

      new =
        old
        |> Map.merge(new)

      new_map = Map.put(map, key, new)

      {old, new, new_map}
    else
      {nil, new, Map.put(map, key, new)}
    end
  end
end
