defmodule OctoconDiscord.Proxy.InvalidAlterError do
  defexception [:message]
end

defmodule OctoconDiscord.Proxy do
  @moduledoc false

  require Logger

  import OctoconDiscord.Utils.CV2

  alias Octocon.{
    Alters,
    Messages
  }

  alias OctoconDiscord.{Cache, Utils}

  alias Nostrum.Api

  @proxy_delay_ms 850
  @proxy_delete_delay_ms @proxy_delay_ms + 300

  # https://discord.com/developers/docs/resources/channel#channel-object-channel-types
  @thread_like_channel_types [
    # ANNOUNCEMENT_THREAD
    10,
    # PUBLIC_THREAD
    11,
    # PRIVATE_THREAD
    12
  ]

  # Checks whether a message is in a thread or not
  # This uses the guild cache, but may send an API request if the guild isn't cached on this node for some reason
  defp check_thread(%Nostrum.Struct.Message{guild_id: guild_id, channel_id: channel_id}) do
    guild = Utils.get_cached_guild(guild_id)

    case Map.get(guild.threads, channel_id) do
      nil ->
        channel = Map.get(guild.channels, channel_id)

        if channel.type in @thread_like_channel_types do
          {channel.parent_id, channel.parent_id, channel_id}
        else
          # Definitely not a thread
          {channel_id, channel.parent_id, nil}
        end

      thread ->
        {thread.parent_id, thread.parent_id, thread.id}
    end
  end

  def with_proxy_prerequisites(user_id, message, fun) do
    # Cache.Proxy is also responsible for keeping track of users who don't have an account
    case Cache.Proxy.get(to_string(user_id)) do
      {:error, :no_user} ->
        # User doesn't have an Octocon account, ignore
        :no_proxy

      {:ok, proxy_data} ->
        {channel_id, parent_id, thread_id} = check_thread(message)

        # Fast path: if the channel is blacklisted, don't bother checking anything else
        unless Cache.ChannelBlacklists.blacklisted?(
                 to_string(channel_id),
                 to_string(parent_id)
               ) do
          webhook = Cache.Webhooks.get_webhook(channel_id)

          if webhook == nil do
            :no_proxy
          else
            settings_template = %Octocon.Accounts.ServerSettings{
              guild_id: to_string(message.guild_id)
            }

            server_proxy_settings =
              proxy_data.settings.server_settings
              |> Map.get(to_string(message.guild_id), nil)
              |> case do
                nil ->
                  settings_template

                server_settings ->
                  settings_template
                  |> Map.merge(server_settings)
              end

            if server_proxy_settings.proxying_disabled do
              :no_proxy
            else
              server_settings = Cache.ServerSettings.get_settings(message.guild_id)

              fun.(%{
                message: message,
                webhook: webhook,
                proxy_data:
                  proxy_data
                  |> Map.put(
                    :settings,
                    Map.merge(proxy_data.settings, %{server_settings: server_proxy_settings})
                  ),
                thread_id: thread_id,
                server_settings: server_settings
              })
            end
          end
        end
    end
  end

  # Sends a proxied message to Discord
  def send_proxy_message(
        %{
          webhook: webhook,
          message: message,
          alter: {system_id, alter_id},
          proxy_data: %{
            settings: %{
              system_tag: system_tag,
              show_system_tag: show_system_tag,
              show_pronouns: show_pronouns,
              use_proxy_delay: use_proxy_delay,
              silent_proxying: silent_proxying
            }
          },
          thread_id: thread_id,
          server_settings: server_settings
        },
        is_reproxy,
        proxy_fun \\ fn id, token, data -> Nostrum.Api.Webhook.execute(id, token, data, true) end
      ) do
    alter =
      case Alters.get_alter_by_id({:system, system_id}, {:id, alter_id}, [
             :name,
             :avatar_url,
             :pronouns,
             :color,
             :proxy_name
           ]) do
        {:ok, alter} ->
          alter

        {:error, :no_alter_id} ->
          raise OctoconDiscord.Proxy.InvalidAlterError,
                "Alter with ID #{alter_id} does not exist."
      end

    final_tag =
      cond do
        system_tag == nil -> ""
        server_settings[:force_system_tags] == true or show_system_tag == true -> " #{system_tag}"
        true -> ""
      end

    parsed_pronouns =
      if show_pronouns == false do
        ""
      else
        case alter.pronouns do
          nil -> ""
          "" -> ""
          pronouns -> " (#{pronouns})"
        end
      end

    base_name = alter.proxy_name || alter.name

    name_length = String.length(base_name)
    tag_length = String.length(final_tag)

    truncated_pronouns =
      parsed_pronouns
      |> String.slice(0..(80 - tag_length - name_length - 1))

    final_username = "#{base_name}#{truncated_pronouns}#{final_tag}"

    webhook_data = %{
      content: message.content,
      username: final_username,
      avatar_url: alter.avatar_url,
      thread_id: thread_id,
      embeds:
        case message.message_reference do
          nil ->
            nil

          %{message_id: message_id} ->
            case Api.Message.get(message.channel_id, message_id) do
              {:ok, reply} ->
                [build_reply_embed(message, reply, alter.color)]

              _ ->
                nil
            end
        end
    }

    webhook_data =
      if silent_proxying do
        Map.put(webhook_data, :flags, Bitwise.bsl(1, 12))
      else
        webhook_data
      end

    context = %{
      system_id: system_id,
      alter_id: alter_id,
      author_id: to_string(message.author.id),
      use_proxy_delay: use_proxy_delay,
      silent_proxying: silent_proxying
    }

    case message.attachments do
      [] ->
        # If we have no attachments, we're done
        send_proxy_message_raw(
          %{
            webhook: webhook,
            message: message,
            webhook_data: webhook_data,
            server_settings: server_settings,
            context: context
          },
          is_reproxy,
          proxy_fun
        )

      files ->
        # Otherwise, we need to download the files and send them along with the message
        send_proxy_message_with_files(
          %{
            webhook: webhook,
            message: message,
            webhook_data: webhook_data,
            files: files,
            server_settings: server_settings,
            context: context
          },
          is_reproxy,
          proxy_fun
        )
    end
  end

  # Sends a proxied message to Discord with files
  defp send_proxy_message_with_files(
         %{
           webhook: webhook,
           message: message,
           webhook_data: webhook_data,
           files: files,
           server_settings: server_settings,
           context: context
         },
         is_reproxy,
         proxy_fun
       ) do
    attachments =
      files
      |> Stream.filter(fn file -> file.size < 20_000_000 end)
      |> Task.async_stream(
        fn %{filename: filename, url: url} = attachment ->
          req =
            Finch.build(:get, url)
            |> Finch.request(Octocon.Finch)

          case req do
            {:ok, %{body: body}} ->
              file_data = %{name: filename, body: body}

              attachment_data =
                cond do
                  filename == "voice-message.ogg" ->
                    %{
                      filename: "voice-message.ogg",
                      content_type: "audio/ogg",
                      waveform: attachment.waveform,
                      duration_secs: attachment.duration_secs
                    }

                  String.contains?(attachment.content_type, "image") ->
                    %{
                      filename: filename,
                      content_type: attachment.content_type,
                      title: attachment.title,
                      description: attachment.description
                    }

                  true ->
                    %{
                      filename: filename,
                      content_type: attachment.content_type,
                      title: attachment.title
                    }
                end

              {:ok, {file_data, attachment_data}}

            {:error, error} ->
              {:error, error}
          end
        end,
        timeout: :timer.seconds(15),
        max_concurrency: 4
      )
      |> Stream.filter(fn
        {:ok, {:ok, _}} -> true
        _ -> false
      end)
      |> Stream.map(fn {:ok, {:ok, data}} -> data end)
      |> Enum.to_list()

    is_voice_message =
      Enum.any?(attachments, fn {%{name: name}, _} -> name == "voice-message.ogg" end)

    webhook_data =
      webhook_data
      |> Map.put(:files, Enum.map(attachments, fn {file_data, _} -> file_data end))
      |> Map.put(
        :flags,
        if attachments == [] do
          Map.get(webhook_data, :flags)
        else
          # This is a bit hacky, but it lets us tell Discord that we're sending a voice message
          if is_voice_message do
            Bitwise.|||(Bitwise.bsl(1, 12), Bitwise.bsl(1, 13))
          else
            Map.get(webhook_data, :flags)
          end
        end
      )
      |> Map.put(
        :attachments,
        attachments
        |> Enum.with_index()
        |> Enum.map(fn {{_, attachment_data}, index} -> Map.put(attachment_data, :id, index) end)
      )

    # Delegate to `send_proxy_message_raw` with the updated webhook data
    send_proxy_message_raw(
      %{
        webhook: webhook,
        message: message,
        webhook_data: webhook_data,
        server_settings: server_settings,
        context: context
      },
      is_reproxy,
      proxy_fun
    )
  end

  # Sends a proxied message to Discord with the given webhook data
  defp send_proxy_message_raw(
         %{
           webhook: webhook,
           message: message,
           webhook_data: webhook_data,
           server_settings: server_settings,
           context: context
         },
         is_reproxy,
         proxy_fun
       ) do
    webhook_task =
      Task.async(fn ->
        if !is_reproxy && context.use_proxy_delay do
          Process.sleep(@proxy_delay_ms)
        end

        result_message = proxy_fun.(webhook.id, webhook.token, webhook_data)

        case result_message do
          {:ok, %{id: message_id}} ->
            unless is_reproxy do
              spawn(fn ->
                attrs =
                  context
                  |> Map.put(:message_id, to_string(message_id))
                  |> Map.put(:timestamp, Nostrum.Snowflake.creation_time(message_id))

                Messages.insert_message(attrs)
              end)

              spawn(fn ->
                log_proxy_message(
                  message,
                  message_id,
                  server_settings
                )
              end)
            end

          {:error, error} ->
            Logger.error("Failed to send proxy message: #{inspect(error)}")
        end
      end)

    delete_task =
      Task.async(fn ->
        if !is_reproxy && context.use_proxy_delay do
          Process.sleep(@proxy_delete_delay_ms)
        end

        unless is_reproxy do
          Api.Message.delete(message.channel_id, message.id)
        end
      end)

    # Bail if Discord rate-limits us or otherwise fails to send the message
    # This is especially useful to avoid holding attachments in RAM for too long
    Task.await_many([webhook_task, delete_task], :timer.seconds(10))
  end

  # Logs a proxied message to the log channel
  # If no log channel is set, or if the server doesn't have any settings in the database, ignore
  defp log_proxy_message(_, _, nil), do: :ok
  defp log_proxy_message(_, _, %{log_channel: nil}), do: :ok

  # Otherwise, proceed with logging
  defp log_proxy_message(
         %{
           guild_id: guild_id,
           content: content,
           channel_id: channel_id,
           author: %{id: author_id, avatar: avatar_hash}
         },
         message_id,
         %{log_channel: log_channel}
       ) do
    permalink = "https://discord.com/channels/#{guild_id}/#{channel_id}/#{message_id}"

    truncated_content =
      cond do
        content == nil or content == "" ->
          "*This message did not have any text.*"

        String.length(content) > 500 ->
          content
          |> String.slice(0..500)
          |> Kernel.<>("\n...")

        true ->
          content
      end

    creation_time = Nostrum.Snowflake.creation_time(message_id) |> DateTime.to_unix()

    result =
      Api.Message.create(
        String.to_integer(log_channel),
        %{
          components: [
            container(
              [
                section(
                  [
                    text("### Message proxied"),
                    text(truncated_content)
                  ],
                  thumbnail(Utils.get_avatar_url(author_id, avatar_hash))
                ),
                separator(spacing: :large),
                text("**Author:** <@#{author_id}>"),
                text("**Sent at:** <t:#{creation_time}:F> (<t:#{creation_time}:R>)")
              ],
              %{accent_color: Utils.hex_to_int("#3F3793")}
            ),
            action_row([
              link_button(permalink, label: "Jump to message")
            ])
          ],
          allowed_mentions: :none,
          flags: cv2_flags(false) |> Bitwise.bor(Bitwise.bsl(1, 12))
        }
      )

    case result do
      {:ok, _} ->
        :ok

      {:error, error} ->
        Logger.error("Failed to log proxied message: #{inspect(error)}; #{inspect(log_channel)}")
    end
  end

  defp build_reply_embed(
         message,
         %{
           author: reply_author,
           content: reply_content,
           id: reply_id,
           channel_id: reply_channel_id
         },
         color
       )
       when is_nil(reply_content) or reply_content == "" do
    reply_embed(
      reply_author,
      "[Reply to:](https://discord.com/channels/#{message.guild_id}/#{reply_channel_id}/#{reply_id}) *This message did not have any text.*",
      color
    )
  end

  # Recreate replies as embeds
  # [TODO]: Replace with components
  defp build_reply_embed(
         message,
         %{
           author: reply_author,
           content: reply_content,
           id: reply_id,
           channel_id: reply_channel_id
         },
         color
       ) do
    truncated_content =
      if String.length(reply_content) > 75 do
        trimmed =
          reply_content
          |> String.slice(0..75)

        spoiler_count =
          trimmed
          |> String.split("||")
          |> length()
          |> then(&Kernel.-(&1, 1))

        # If the spoiler count is odd, we need to add a spoiler tag to the end
        # This ensures spoilered content isn't accidentally revealed when truncating
        trimmed <> if rem(spoiler_count, 2) == 0, do: "...", else: "||..."
      else
        reply_content
      end

    reply_embed(
      reply_author,
      "[Reply to:](https://discord.com/channels/#{message.guild_id}/#{reply_channel_id}/#{reply_id}) #{truncated_content}",
      color
    )
  end

  defp reply_embed(%{id: id, avatar: avatar, username: username}, text, color) do
    %Nostrum.Struct.Embed{
      author: %Nostrum.Struct.Embed.Author{
        icon_url: Utils.get_avatar_url(id, avatar),
        name: "#{username} ↩️"
      },
      description: text,
      color: Utils.hex_to_int(color)
    }
  end
end
