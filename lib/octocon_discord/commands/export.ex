defmodule OctoconDiscord.Commands.Export do
  @moduledoc false

  use OctoconDiscord.Commands

  @behaviour Nosedrum.ApplicationCommand

  alias Octocon.Accounts

  @impl Nosedrum.ApplicationCommand
  def description, do: "Exports your Octocon data."

  @impl Nosedrum.ApplicationCommand
  def command(%{data: %{options: options}} = interaction) do
    %{
      id: discord_id
      # avatar: avatar_hash
    } = interaction.user

    discord_id = to_string(discord_id)
    system_identity = {:discord, discord_id}

    # credo:disable-for-next-line
    if not Accounts.user_exists?(system_identity) do
      error_component("You do not have an Octocon account.")
    else
      data = Octocon.Accounts.gather_export_data(system_identity)
      format = get_command_option(options, "format")

      data =
        case format do
          "pk" ->
            data |> Octocon.Accounts.format_pk_export()

          "full" ->
            data |> Octocon.Accounts.format_full_export()
        end

      {:ok, channel} = Nostrum.Api.User.create_dm(Integer.parse(discord_id) |> elem(0))

      Nostrum.Api.Message.create(channel.id, %{
        content: """
        # Octocon data export
        Your export data is attached as a JSON file to this message.

        #{if format == "pk" do
          "Use the `pk;import` command in PluralKit to import this data into your PluralKit account."
        else
          "This data is not currently importable to any other platform. It is intended to be used in the event that another platform implements Octocon imports."
        end}
        """,
        files: [
          %{
            name: "octocon_export_#{format}.json",
            body: data
          }
        ]
      })

      success_component("Check your DMs.")
    end
  end

  @impl Nosedrum.ApplicationCommand
  def type, do: :slash

  @impl Nosedrum.ApplicationCommand
  def options,
    do: [
      %{
        name: "format",
        description: "The format to export your data in.",
        type: :string,
        required: true,
        choices: [
          %{name: "PluralKit", value: "pk"},
          %{name: "Full JSON", value: "full"}
        ]
      }
    ]
end
