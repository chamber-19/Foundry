"""
Foundry Discord Bot — sole operator interface for the Foundry ML pipeline.

Connects to the Foundry broker API and posts results/alerts to Discord channels.
Commands are sent via Discord messages and forwarded to the broker.
"""

import json
import logging
import os
import sys

try:
    import discord
    from discord.ext import commands
    import requests
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

intents = discord.Intents.default()
intents.message_content = True
bot = commands.Bot(command_prefix="!", intents=intents)


@bot.event
async def on_ready():
    logger.info("Foundry bot connected as %s", bot.user)


@bot.command(name="health")
async def health(ctx):
    """Check Foundry broker health."""
    try:
        resp = requests.get(f"{BROKER_URL}/health", timeout=10)
        data = resp.json()
        await ctx.send(f"**Foundry Health**: {data.get('status', 'unknown')}")
    except Exception as e:
        await ctx.send(f"❌ Health check failed: {e}")


@bot.command(name="status")
async def status(ctx):
    """Get Foundry pipeline status."""
    try:
        resp = requests.get(f"{BROKER_URL}/api/state", timeout=10)
        data = resp.json()
        ml = data.get("ml", {})
        await ctx.send(
            f"**ML Pipeline**: {'Enabled' if ml.get('enabled') else 'Disabled'}\n"
            f"**Summary**: {ml.get('summary', 'N/A')}"
        )
    except Exception as e:
        await ctx.send(f"❌ Status check failed: {e}")


@bot.command(name="run")
async def run_pipeline(ctx, pipeline_type: str = "pipeline"):
    """Trigger an ML pipeline run. Usage: !run [pipeline|embeddings|export|index]"""
    endpoint_map = {
        "pipeline": "/api/ml/pipeline",
        "embeddings": "/api/ml/embeddings",
        "export": "/api/ml/export-artifacts",
        "index": "/api/ml/index-knowledge",
    }
    endpoint = endpoint_map.get(pipeline_type)
    if not endpoint:
        await ctx.send(f"Unknown pipeline type: `{pipeline_type}`. Use: {', '.join(endpoint_map.keys())}")
        return

    try:
        resp = requests.post(f"{BROKER_URL}{endpoint}", timeout=10)
        data = resp.json()
        job_id = data.get("jobId", "N/A")
        await ctx.send(f"✅ Job queued: `{job_id}` (type: {pipeline_type})")
    except Exception as e:
        await ctx.send(f"❌ Failed to trigger {pipeline_type}: {e}")


@bot.command(name="jobs")
async def list_jobs(ctx):
    """List recent jobs."""
    try:
        resp = requests.get(f"{BROKER_URL}/api/jobs", timeout=10)
        data = resp.json()
        jobs = data.get("jobs", [])[:5]
        if not jobs:
            await ctx.send("No recent jobs.")
            return
        lines = [f"• `{j['id'][:8]}` — {j['type']} — **{j['status']}**" for j in jobs]
        await ctx.send("**Recent Jobs:**\n" + "\n".join(lines))
    except Exception as e:
        await ctx.send(f"❌ Failed to list jobs: {e}")


if __name__ == "__main__":
    if not TOKEN:
        logger.error("No Discord token configured. Set FOUNDRY_DISCORD_TOKEN or add 'token' to bot_config.json.")
        sys.exit(1)
    bot.run(TOKEN)
