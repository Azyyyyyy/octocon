defmodule Octocon.Workers.PluralKitImportWorker do
  @moduledoc """
  An Oban worker that imports alters from PluralKit into a system with the given ID.

  ## Arguments

  - `system_id` (binary): The ID of the system to import alters into.
  - `pk_token` (binary): The PluralKit API token to use for the request.
  """

  import Octocon.Utils.Import

  require Logger

  alias Octocon.{
    Accounts,
    Alters,
    Alters.Alter,
    Tags.AlterTag,
    Tags.Tag
  }

  alias OctoconWeb.Uploaders.Avatar

  @pk_endpoint URI.parse("https://api.pluralkit.me/v2/")

  def perform(%{
        "system_id" => system_id,
        "pk_token" => pk_token
      }) do
    Logger.info("Performing PluralKit import for user #{system_id}")

    user_region = Octocon.UserRegistryCache.get_region({:system, system_id})

    user = Accounts.get_user!({:system, system_id})
    start_count = user.lifetime_alter_count + 1

    {:ok, %{body: self_body}} = send_pk_request(:get, "/systems/@me", pk_token)

    %{
      "description" => description,
      "tag" => system_tag
    } = Jason.decode!(self_body)

    {alters, alter_associations, avatars} =
      get_alters(pk_token, user_region, system_id, start_count)

    alter_count = length(alters)

    tags = get_tags(pk_token, user_region, system_id)
    alter_tags = generate_alter_tags(tags, alter_associations, user_region, system_id)

    alters
    |> Enum.map(fn {_alter, query} -> query end)
    |> execute_batch()

    tags
    |> Enum.map(fn {{_tag, query}, _members} -> query end)
    |> execute_batch()

    alter_tags
    |> execute_batch()

    Accounts.update_user(
      user,
      %{
        lifetime_alter_count: user.lifetime_alter_count + alter_count,
        description: default_if_empty(description, 3000, user.description)
      }
    )

    existing_system_tag =
      (user.discord_settings || %Octocon.Accounts.DiscordSettings{})
      |> Map.get(:system_tag, nil)

    Accounts.update_discord_settings(
      user,
      %{
        system_tag:
          default_if_empty(
            system_tag,
            20,
            existing_system_tag
          )
      }
    )

    OctoconWeb.Endpoint.broadcast!("system:#{system_id}", "pk_import_complete", %{
      alter_count: alter_count
    })

    OctoconDiscord.Utils.send_dm(
      {:system, system_id},
      "Import complete (PluralKit)",
      "#{alter_count} alters have been successfully imported from PluralKit. They have been assigned IDs #{start_count} - #{start_count + alter_count - 1}. It may take a while longer for their avatars to be processed.\n\n**Note:** This process should only be completed once; doing it again will result in duplicate alters."
    )

    spawn(fn ->
      system_identity = {:system, system_id}
      OctoconDiscord.Cache.Proxy.invalidate(system_identity)
      OctoconDiscord.Autocomplete.invalidate_all(system_identity)
    end)

    spawn(fn ->
      Task.async_stream(
        avatars,
        fn {avatar_url, avatar_scope} ->
          case Octocon.ClusterUtils.run_on_sidecar(
                 fn -> Avatar.store({avatar_url, avatar_scope}) end,
                 timeout: 10_000
               ) do
            {:ok, _} ->
              octo_url = Avatar.url({"primary.webp", avatar_scope}, :primary)

              Alters.update_alter(
                {:system, avatar_scope.system_id},
                {:id, avatar_scope.alter_id},
                %{avatar_url: octo_url}
              )

            _ ->
              # Avatar doesn't exist; stale reference on PK's end?
              :ok
          end
        end,
        max_concurrency: 2,
        ordered: false,
        timeout: :timer.seconds(10),
        on_timeout: :kill_task
      )
      |> Stream.run()
    end)

    :ok
  rescue
    e ->
      Logger.error("Error importing PluralKit alters")
      Logger.error(Exception.format(:error, e, __STACKTRACE__))

      OctoconWeb.Endpoint.broadcast!("system:#{system_id}", "pk_import_failed", %{})

      {:error, e}
  end

  defp get_alters(pk_token, user_region, system_id, start_count) do
    {:ok, %{body: alters_body}} = send_pk_request(:get, "/systems/@me/members", pk_token)

    {alters, uuids, avatars} =
      Jason.decode!(alters_body)
      |> Stream.with_index(start_count)
      |> Stream.map(fn {alter, index} ->
        parse_alter(system_id, alter, index)
      end)
      |> Stream.map(fn {alter, uuid, avatar} ->
        {{alter, alter_to_insert_query(alter, user_region)}, uuid, avatar}
      end)
      |> Enum.reduce({[], [], []}, fn
        {alter, uuid, nil}, {alters, uuids, avatars} ->
          {[alter | alters], [uuid | uuids], avatars}

        {alter, uuid, avatar}, {alters, uuids, avatars} ->
          {[alter | alters], [uuid | uuids], [avatar | avatars]}
      end)

    alter_associations =
      uuids
      |> Enum.zip(Enum.map(alters, fn {alter, _query} -> alter.id end))
      |> Enum.into(%{})

    {alters, alter_associations, avatars}
  end

  defp get_tags(pk_token, user_region, system_id) do
    {:ok, %{body: groups_body}} =
      send_pk_request(:get, "/systems/@me/groups?with_members=true", pk_token)

    Jason.decode!(groups_body)
    |> Stream.map(fn group -> parse_tag(system_id, group) end)
    |> Enum.map(fn {tag, members} ->
      {
        {tag, tag_to_insert_query(tag, user_region)},
        members
      }
    end)
  end

  defp generate_alter_tags(tags, alter_associations, user_region, system_id) do
    tags
    |> Enum.map(fn {{tag, _}, members} ->
      {
        tag.id,
        Enum.map(members, fn member -> Map.get(alter_associations, member) end)
      }
    end)
    |> Enum.map(fn {tag_id, member_ids} ->
      member_ids
      |> Enum.map(fn alter_id ->
        %AlterTag{
          user_id: system_id,
          alter_id: alter_id,
          tag_id: tag_id,
          inserted_at: NaiveDateTime.utc_now(:second) |> naive_datetime_to_datetime(),
          updated_at: NaiveDateTime.utc_now(:second) |> naive_datetime_to_datetime()
        }
        |> alter_tag_to_insert_query(user_region)
      end)
    end)
    |> List.flatten()
  end

  defp send_pk_request(method, endpoint, token) do
    uri = URI.append_path(@pk_endpoint, endpoint)

    res =
      Finch.build(method, uri, [
        {"Content-Type", "application/json"},
        {"User-Agent", "Octocon/pkimport; contact = contact@octocon.app"},
        {"Authorization", token}
      ])
      |> Finch.request(Octocon.Finch)

    res
  end

  defp parse_alter(system_id, alter, id) do
    {
      %Alter{
        user_id: system_id,
        id: id,
        name: default_if_empty(alter["name"], 80, "Unnamed alter"),
        proxy_name: default_if_empty(alter["display_name"], 80),
        discord_proxies:
          if(alter["proxy_tags"],
            do:
              alter["proxy_tags"]
              |> Enum.map(fn tag -> (tag["prefix"] || "") <> "text" <> (tag["suffix"] || "") end),
            else: nil
          ),
        pronouns: default_if_empty(alter["pronouns"], 50),
        description: default_if_empty(alter["description"], 2000),
        alias: nil,
        pinned: false,
        archived: false,
        untracked: false,
        last_fronted: nil,
        color: parse_color(alter["color"]),
        fields: [],
        security_level: 3,
        inserted_at: NaiveDateTime.utc_now(:second) |> naive_datetime_to_datetime(),
        updated_at: NaiveDateTime.utc_now(:second) |> naive_datetime_to_datetime()
      },
      alter["uuid"],
      if alter["avatar_url"] do
        random_id = Nanoid.generate(30)

        {
          alter["avatar_url"],
          %{
            system_id: system_id,
            alter_id: id,
            random_id: random_id
          }
        }
      else
        nil
      end
    }
  end

  defp parse_tag(system_id, tag) do
    {%Tag{
       user_id: system_id,
       id: Ecto.UUID.generate(),
       name: default_if_empty(tag["name"], 100, "Unnamed tag"),
       description: default_if_empty(tag["description"], 1000),
       color: parse_color(tag["color"]),
       security_level: 3,
       parent_tag_id: nil,
       inserted_at: NaiveDateTime.utc_now(:second) |> naive_datetime_to_datetime(),
       updated_at: NaiveDateTime.utc_now(:second) |> naive_datetime_to_datetime()
     }, tag["members"]}
  end

  defp default_if_empty(string, max, default \\ nil)

  defp default_if_empty(nil, _max, default), do: default
  defp default_if_empty(string, _max, default) when string == "", do: default
  defp default_if_empty(string, max, _default), do: string |> String.slice(0..max)

  defp parse_color(nil), do: nil
  defp parse_color(color), do: "#" <> color
end
