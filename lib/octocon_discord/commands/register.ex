defmodule OctoconDiscord.Commands.Register do
  @moduledoc false

  use OctoconDiscord.Commands

  @behaviour Nosedrum.ApplicationCommand

  alias Octocon.Accounts

  @impl Nosedrum.ApplicationCommand
  def description, do: "Creates a system under your Discord account."

  @impl Nosedrum.ApplicationCommand
  def command(interaction) do
    error_component(
      "Octocon is shutting down. Sign-ups are disabled. Please see our Discord for more information: https://discord.com/invite/octocon"
    )
  end

  @impl Nosedrum.ApplicationCommand
  def type, do: :slash

  # @impl Nosedrum.ApplicationCommand
  # def options, do: []
end
