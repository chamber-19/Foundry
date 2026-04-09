"""
Foundry Discord Bot — sole operator interface for the Foundry ML pipeline.

Connects to the Foundry broker API and posts results/alerts to Discord channels.
Slash commands are registered to a single guild on startup.
"""

import json
import logging
import os
import sys

try:
    import discord
    from discord import app_commands
    import aiohttp
except ImportError:
    print("Missing dependencies. Install with: pip install -r requirements.txt")
    sys.exit(1)

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s")
logger = logging.getLogger("foundry_bot")

CONFIG_PATH = os.environ.get("FOUNDRY_BOT_CONFIG", "bot_config.json")
DEFAULT_BROKER_URL = "http://127.0.0.1:57420"


def load_config() -> dict:
    """Load bot configuration from JSON file."""
    if not os.path.exists(CONFIG_PATH):
        logger.warning("Config file %s not found. Using defaults.", CONFIG_PATH)
        return {}
    with open(CONFIG_PATH, "r", encoding="utf-8") as f:
        return json.load(f)


config = load_config()
BROKER_URL = config.get("broker_url", DEFAULT_BROKER_URL)
TOKEN = config.get("token") or os.environ.get("FOUNDRY_DISCORD_TOKEN")
GUILD_ID = config.get("guild_id") or os.environ.get("FOUNDRY_BOT_GUILD_ID", "0")

intents = discord.Intents.default()
bot = discord.Client(intents=intents)
tree = app_commands.CommandTree(bot)
guild = discord.Object(id=int(GUILD_ID))
http_session: aiohttp.ClientSession | None = None

if GUILD_ID == "0":
    logger.warning("No guild_id configured. Set guild_id in bot_config.json or FOUNDRY_BOT_GUILD_ID env var.")


@bot.event
async def on_ready():
    global http_session
    if http_session is None or http_session.closed:
        http_session = aiohttp.ClientSession(timeout=aiohttp.ClientTimeout(total=10))
    await tree.sync(guild=guild)
    logger.info("Foundry bot connected as %s — slash commands synced", bot.user)


@tree.command(name="health", description="Check Foundry broker health", guild=guild)
async def health(interaction: discord.Interaction):
    try:
        async with http_session.get(f"{BROKER_URL}/health") as resp:
            data = await resp.json()
        await interaction.response.send_message(f"**Foundry Health**: {data.get('status', 'unknown')}")
    except Exception as e:
        await interaction.response.send_message(f"❌ Health check failed: {e}")


@tree.command(name="status", description="Get Foundry pipeline status", guild=guild)
async def status(interaction: discord.Interaction):
    try:
        async with http_session.get(f"{BROKER_URL}/api/state") as resp:
            data = await resp.json()
        ml = data.get("ml", {})
        await interaction.response.send_message(
            f"**ML Pipeline**: {'Enabled' if ml.get('enabled') else 'Disabled'}\n"
            f"**Summary**: {ml.get('summary', 'N/A')}"
        )
    except Exception as e:
        await interaction.response.send_message(f"❌ Status check failed: {e}")


PIPELINE_CHOICES = [
    app_commands.Choice(name="pipeline", value="pipeline"),
    app_commands.Choice(name="embeddings", value="embeddings"),
    app_commands.Choice(name="export", value="export"),
    app_commands.Choice(name="index", value="index"),
]


@tree.command(name="run", description="Trigger an ML pipeline run", guild=guild)
@app_commands.describe(pipeline_type="Pipeline type to run")
@app_commands.choices(pipeline_type=PIPELINE_CHOICES)
async def run_pipeline(interaction: discord.Interaction, pipeline_type: app_commands.Choice[str] = None):
    selected = pipeline_type.value if pipeline_type else "pipeline"
    endpoint_map = {
        "pipeline": "/api/ml/pipeline",
        "embeddings": "/api/ml/embeddings",
        "export": "/api/ml/export-artifacts",
        "index": "/api/ml/index-knowledge",
    }
    endpoint = endpoint_map.get(selected)
    if not endpoint:
        await interaction.response.send_message(
            f"Unknown pipeline type: `{selected}`. Use: {', '.join(endpoint_map.keys())}"
        )
        return

    try:
        async with http_session.post(f"{BROKER_URL}{endpoint}") as resp:
            data = await resp.json()
        job_id = data.get("jobId", "N/A")
        await interaction.response.send_message(f"✅ Job queued: `{job_id}` (type: {selected})")
    except Exception as e:
        await interaction.response.send_message(f"❌ Failed to trigger {selected}: {e}")


@tree.command(name="jobs", description="List recent jobs", guild=guild)
async def list_jobs(interaction: discord.Interaction):
    try:
        async with http_session.get(f"{BROKER_URL}/api/jobs") as resp:
            data = await resp.json()
        jobs = data.get("jobs", [])[:5]
        if not jobs:
            await interaction.response.send_message("No recent jobs.")
            return
        lines = [f"• `{j['id'][:8]}` — {j['type']} — **{j['status']}**" for j in jobs]
        await interaction.response.send_message("**Recent Jobs:**\n" + "\n".join(lines))
    except Exception as e:
        await interaction.response.send_message(f"❌ Failed to list jobs: {e}")


if __name__ == "__main__":
    if not TOKEN:
        logger.error("No Discord token configured. Set FOUNDRY_DISCORD_TOKEN or add 'token' to bot_config.json.")
        sys.exit(1)
    bot.run(TOKEN)
