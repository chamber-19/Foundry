"""
Foundry Discord Bot.

Connects to the local Foundry broker API, exposes operator slash commands, and
posts dependency notifications to the configured alerts channel.
"""

import asyncio
import json
import logging
import os
import sys
from pathlib import Path
from typing import Any

try:
    import aiohttp
    import discord
    from discord import app_commands
except ImportError:
    print("Missing dependencies. Install with: pip install -r requirements.txt")
    sys.exit(1)

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s")
logger = logging.getLogger("foundry_bot")

CONFIG_PATH = os.environ.get("FOUNDRY_BOT_CONFIG", "bot_config.json")
DEFAULT_BROKER_URL = "http://127.0.0.1:57420"
MAX_DISCORD_OUTPUT_LENGTH = 1900


def load_config() -> dict[str, Any]:
    """Load bot configuration from JSON file."""
    if not os.path.exists(CONFIG_PATH):
        logger.warning("Config file %s not found. Using defaults.", CONFIG_PATH)
        return {}
    with open(CONFIG_PATH, "r", encoding="utf-8") as f:
        return json.load(f)


config = load_config()
BROKER_URL = config.get("broker_url", DEFAULT_BROKER_URL).rstrip("/")
TOKEN = config.get("token") or os.environ.get("FOUNDRY_DISCORD_TOKEN")
GUILD_ID = config.get("guild_id") or os.environ.get("FOUNDRY_BOT_GUILD_ID", "0")
CHANNEL_IDS = config.get("channel_ids", {})
NOTIFICATION_POLL_SECONDS = int(
    config.get("notification_poll_seconds")
    or os.environ.get("FOUNDRY_NOTIFICATION_POLL_SECONDS", "30")
)

intents = discord.Intents.default()
bot = discord.Client(intents=intents)
tree = app_commands.CommandTree(bot)
guild = discord.Object(id=int(GUILD_ID))
http_session: aiohttp.ClientSession | None = None
notification_task: asyncio.Task[None] | None = None

REPO_ROOT = os.environ.get("FOUNDRY_REPO_ROOT", str(Path(__file__).parent.parent))

if GUILD_ID == "0":
    logger.warning("No guild_id configured. Set guild_id in bot_config.json or FOUNDRY_BOT_GUILD_ID env var.")


@bot.event
async def on_ready():
    global http_session, notification_task
    if http_session is None or http_session.closed:
        http_session = aiohttp.ClientSession(timeout=aiohttp.ClientTimeout(total=15))
    await tree.sync(guild=guild)
    if notification_task is None or notification_task.done():
        notification_task = asyncio.create_task(deliver_pending_notifications())
    logger.info("Foundry bot connected as %s; slash commands synced", bot.user)


async def broker_get(path: str) -> dict[str, Any]:
    session = require_session()
    async with session.get(f"{BROKER_URL}{path}") as resp:
        return await parse_broker_response(resp)


async def broker_post(path: str, payload: dict[str, Any] | None = None) -> dict[str, Any]:
    session = require_session()
    async with session.post(f"{BROKER_URL}{path}", json=payload) as resp:
        return await parse_broker_response(resp)


async def parse_broker_response(resp: aiohttp.ClientResponse) -> dict[str, Any]:
    try:
        data = await resp.json()
    except Exception:
        text = await resp.text()
        data = {"detail": text}

    if resp.status >= 400:
        detail = data.get("detail") or data.get("error") or str(data)
        raise RuntimeError(f"broker returned {resp.status}: {detail}")
    return data


def require_session() -> aiohttp.ClientSession:
    if http_session is None or http_session.closed:
        raise RuntimeError("HTTP session is not ready yet.")
    return http_session


def get_alert_channel_id() -> int | None:
    raw = (
        CHANNEL_IDS.get("alerts")
        or config.get("alerts_channel_id")
        or os.environ.get("FOUNDRY_ALERTS_CHANNEL_ID")
    )
    try:
        return int(raw)
    except (TypeError, ValueError):
        return None


async def get_alert_channel() -> discord.abc.Messageable | None:
    channel_id = get_alert_channel_id()
    if channel_id is None:
        return None

    channel = bot.get_channel(channel_id)
    if channel is not None:
        return channel

    try:
        fetched = await bot.fetch_channel(channel_id)
        if isinstance(fetched, discord.abc.Messageable):
            return fetched
    except Exception as exc:
        logger.warning("Could not fetch alerts channel %s: %s", channel_id, exc)
    return None


async def deliver_pending_notifications():
    await bot.wait_until_ready()
    while not bot.is_closed():
        try:
            channel = await get_alert_channel()
            if channel is None:
                await asyncio.sleep(NOTIFICATION_POLL_SECONDS)
                continue

            data = await broker_get("/api/notifications?pending=true&limit=10")
            notifications = data.get("notifications", [])
            for notification in notifications:
                embed = build_notification_embed(notification)
                await channel.send(embed=embed)
                await broker_post(
                    f"/api/notifications/{notification['id']}/delivered",
                    {"deliveredTo": str(get_alert_channel_id())},
                )
        except Exception as exc:
            logger.warning("Notification delivery loop failed: %s", exc)

        await asyncio.sleep(NOTIFICATION_POLL_SECONDS)


def build_notification_embed(notification: dict[str, Any]) -> discord.Embed:
    category = notification.get("category", "info")
    color = {
        "blocked": 0xC0392B,
        "risky": 0xE67E22,
        "needs-review": 0xF1C40F,
        "info": 0x3498DB,
    }.get(category, 0x3498DB)

    embed = discord.Embed(
        title=notification.get("title", "Foundry notification")[:256],
        description=(notification.get("body") or "")[:4096],
        color=color,
    )
    embed.add_field(name="Category", value=category, inline=True)
    embed.add_field(name="Severity", value=notification.get("severity", "unknown"), inline=True)
    embed.add_field(name="Repository", value=notification.get("repository", "unknown"), inline=False)
    source_url = notification.get("sourceUrl")
    if source_url:
        embed.add_field(name="Source", value=source_url, inline=False)
    return embed


@tree.command(name="health", description="Check Foundry broker health", guild=guild)
async def health(interaction: discord.Interaction):
    try:
        data = await broker_get("/health")
        await interaction.response.send_message(f"Foundry health: **{data.get('status', 'unknown')}**")
    except Exception as exc:
        await interaction.response.send_message(f"Health check failed: {exc}")


@tree.command(name="status", description="Get Foundry broker status", guild=guild)
async def status(interaction: discord.Interaction):
    try:
        data = await broker_get("/api/state")
        broker = data.get("broker", {})
        provider = data.get("provider", {})
        dependencies = data.get("dependencyMonitor", {})
        await interaction.response.send_message(
            "**Foundry Status**\n"
            f"Broker: `{broker.get('baseUrl', BROKER_URL)}`\n"
            f"Provider ready: `{provider.get('ready', False)}`\n"
            f"Dependency repos: `{dependencies.get('repositoryCount', 0)}`\n"
            f"Pending alerts: `{dependencies.get('pendingNotificationCount', 0)}`"
        )
    except Exception as exc:
        await interaction.response.send_message(f"Status check failed: {exc}")


@tree.command(name="jobs", description="List recent Foundry jobs", guild=guild)
async def list_jobs(interaction: discord.Interaction):
    try:
        data = await broker_get("/api/jobs")
        jobs = data.get("jobs", [])[:5]
        if not jobs:
            await interaction.response.send_message("No recent jobs.")
            return
        lines = [f"- `{j['id'][:8]}` - {j['type']} - **{j['status']}**" for j in jobs]
        await interaction.response.send_message("**Recent Jobs:**\n" + "\n".join(lines))
    except Exception as exc:
        await interaction.response.send_message(f"Failed to list jobs: {exc}")


@tree.command(name="deps", description="Poll GitHub dependency PRs and alerts now", guild=guild)
async def deps(interaction: discord.Interaction):
    await interaction.response.defer()
    try:
        data = await broker_post("/api/dependencies/poll")
        await interaction.followup.send(
            "**Dependency Poll Complete**\n"
            f"Repos checked: `{data.get('repositoriesChecked', 0)}`\n"
            f"Dependabot PRs: `{data.get('pullRequestsSeen', 0)}`\n"
            f"Dependabot alerts: `{data.get('alertsSeen', 0)}`\n"
            f"Created: `{data.get('notificationsCreated', 0)}` Updated: `{data.get('notificationsUpdated', 0)}`"
        )
    except Exception as exc:
        await interaction.followup.send(f"Dependency poll failed: {exc}")


@tree.command(name="alerts", description="List Foundry dependency notifications", guild=guild)
@app_commands.describe(pending="Only show undelivered notifications")
async def alerts(interaction: discord.Interaction, pending: bool = True):
    try:
        data = await broker_get(f"/api/notifications?pending={str(pending).lower()}&limit=10")
        notifications = data.get("notifications", [])
        if not notifications:
            scope = "pending" if pending else "recent"
            await interaction.response.send_message(f"No {scope} notifications.")
            return

        lines = []
        for notification in notifications:
            source = notification.get("sourceUrl") or "no source URL"
            lines.append(
                f"- **{notification.get('category', 'info')}** "
                f"{notification.get('title', 'notification')} - {source}"
            )

        text = "\n".join(lines)
        if len(text) > MAX_DISCORD_OUTPUT_LENGTH:
            text = text[:MAX_DISCORD_OUTPUT_LENGTH] + "\n..."
        await interaction.response.send_message(text)
    except Exception as exc:
        await interaction.response.send_message(f"Failed to list alerts: {exc}")


@tree.command(name="models", description="List configured and installed Ollama models", guild=guild)
async def models(interaction: discord.Interaction):
    try:
        data = await broker_get("/api/models")
        installed = data.get("installedModels", [])
        visible = ", ".join(installed[:12]) if installed else "none reported"
        await interaction.response.send_message(
            "**Foundry Models**\n"
            f"Provider: `{data.get('provider', 'unknown')}`\n"
            f"Chat: `{data.get('chatModel', 'unset')}`\n"
            f"Embeddings: `{data.get('embeddingModel', 'unset')}`\n"
            f"Installed: {visible}"
        )
    except Exception as exc:
        await interaction.response.send_message(f"Failed to list models: {exc}")


async def run_script(
    interaction: discord.Interaction,
    script_name: str,
    description: str,
    script_dir: str = "automation",
    extra_args: list[str] | None = None,
):
    """Run a PowerShell script as an async subprocess with deferred interaction."""
    await interaction.response.defer()
    script_path = os.path.join(REPO_ROOT, "scripts", script_dir, script_name)
    cmd = ["pwsh", "-NoProfile", "-File", script_path] + (extra_args or [])
    try:
        process = await asyncio.create_subprocess_exec(
            *cmd,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
            cwd=REPO_ROOT,
        )
        stdout, stderr = await asyncio.wait_for(process.communicate(), timeout=600)
        output = stdout.decode()[-MAX_DISCORD_OUTPUT_LENGTH:] if stdout else "No output"
        if stderr:
            logger.warning("Script %s wrote stderr: %s", script_name, stderr.decode()[-500:])
        await interaction.followup.send(f"**{description} complete:**\n```\n{output}\n```")
    except asyncio.TimeoutError:
        await interaction.followup.send(f"{description} timed out after 10 minutes")
    except Exception as exc:
        await interaction.followup.send(f"{description} failed: {exc}")


@tree.command(name="commands", description="List Foundry bot commands", guild=guild)
async def list_commands(interaction: discord.Interaction):
    embed = discord.Embed(title="Foundry commands", color=0x3498DB)
    embed.add_field(name="/health", value="Check broker health", inline=False)
    embed.add_field(name="/status", value="Get broker and dependency monitor status", inline=False)
    embed.add_field(name="/jobs", value="List recent jobs", inline=False)
    embed.add_field(name="/deps", value="Poll dependency PRs and alerts now", inline=False)
    embed.add_field(name="/alerts", value="List dependency notifications", inline=False)
    embed.add_field(name="/models", value="List configured and installed Ollama models", inline=False)
    embed.add_field(name="/commands", value="List bot commands", inline=False)
    await interaction.response.send_message(embed=embed)


if __name__ == "__main__":
    if not TOKEN:
        logger.error("No Discord token configured. Set FOUNDRY_DISCORD_TOKEN or add 'token' to bot_config.json.")
        sys.exit(1)
    bot.run(TOKEN)
