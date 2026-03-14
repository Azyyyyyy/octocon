defmodule OctoconDiscord.Consumer do
  @moduledoc false

  @behaviour Nostrum.Consumer
  require Logger

  import OctoconDiscord.Utils.{
    Components,
    CV2
  }

  alias OctoconDiscord.{
    Commands,
    Components
  }

  alias OctoconDiscord.Events.MessageCreate

  @commands %{
    # "register" => Commands.Register,
    "help" => Commands.Help,
    "system" => Commands.System,
    "settings" => Commands.Settings,
    "alter" => Commands.Alter,
    "tag" => Commands.Tag,
    "autoproxy" => Commands.Autoproxy,
    "global-autoproxy" => Commands.GlobalAutoproxy,
    "danger" => Commands.Danger,
    "bot-info" => Commands.BotInfo,
    "friend" => Commands.Friend,
    "admin" => Commands.Admin,
    "front" => Commands.Front,
    "export" => Commands.Export,
    # "octo" => Commands.Octo,
    "❓ Who is this?" => Commands.Messages.WhoIsThis,
    "🔔 Ping account" => Commands.Messages.PingAccount,
    "❌ Delete proxied message" => Commands.Messages.DeleteProxiedMessage,
    "🔄 Reproxy message" => Commands.Messages.Reproxy
  }

  def handle_event({:READY, %{shard: {this_shard, shard_count}}, _ws_state}) do
    unless :persistent_term.get(:commands_initialized, false) do
      Logger.info(
        "First shard is READY; bulk-registering all application commands (#{map_size(@commands)})..."
      )

      scope = Application.get_env(:octocon, :nostrum_scope)

      Enum.each(@commands, fn {name, module} ->
        Nosedrum.Storage.Dispatcher.queue_command(name, module)
      end)

      case Nosedrum.Storage.Dispatcher.process_queue(scope) do
        {:ok, _} ->
          Logger.info("Registered all commands!")

        {:error, e} ->
          Logger.error("Failed to register all commands: #{inspect(e)}")
      end

      :persistent_term.put(:commands_initialized, true)
    end

    OctoconDiscord.StatusUpdater.bump({this_shard, shard_count})

    :ok
  end

  def handle_event({:INTERACTION_CREATE, %{type: type} = interaction, _ws_state})
      when type in [3, 5] do
    Components.dispatch(interaction)
  rescue
    e ->
      create_error_response(interaction)
      reraise e, __STACKTRACE__
  end

  def handle_event({:INTERACTION_CREATE, %{type: 4} = interaction, _ws_state}) do
    Nostrum.Api.Interaction.create_response(
      interaction,
      %{
        type: 8,
        data: %{
          choices: OctoconDiscord.Autocomplete.dispatch(interaction)
        }
      }
    )
  rescue
    e ->
      Nostrum.Api.Interaction.create_response(interaction, %{
        type: 8,
        data: %{
          choices: []
        }
      })

      reraise e, __STACKTRACE__
  end

  def handle_event({:INTERACTION_CREATE, interaction, _ws_state}) do
    Nosedrum.Storage.Dispatcher.handle_interaction(interaction)
  rescue
    e ->
      create_error_response(interaction)
      reraise e, __STACKTRACE__
  end

  def handle_event({:MESSAGE_CREATE, data, _ws_state}) do
    # IO.inspect(data, limit: :infinity)
    MessageCreate.handle(data)
  end

  def handle_event(_data) do
    :ok
  end

  defp create_error_response(interaction) do
    Nostrum.Api.Interaction.create_response(interaction, %{
      type: 4,
      data: %{
        components: [
          error_component_raw("An error occurred while processing your command.")
        ],
        flags: cv2_flags()
      }
    })
  end

  # def handle_event({:MESSAGE_DELETE, data, _ws_state}) do
  #  MessageDelete.handle(data)
  # end

  # def handle_event({:MESSAGE_UPDATE, data, _ws_state}) do
  # MessageUpdate.handle(data)
  # end

  # def handle_event({:MESSAGE_REACTION_ADD, data, _ws_state}) do
  #  MessageReactionAdd.handle(data)
  # end
end
