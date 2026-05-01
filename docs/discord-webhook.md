# Discord webhook delivery

Foundry delivers dependency notifications to Discord via a webhook. The bot (`bot/foundry_bot.py`) is independent; this delivery path posts directly to a channel webhook URL.

## Create a webhook in your Discord server

1. Open your Discord server, go to **Server Settings → Integrations → Webhooks**.
2. Click **New Webhook**, give it a name (e.g., `Foundry Alerts`), choose the target channel.
3. Click **Copy Webhook URL**. It looks like:
   `https://discord.com/api/webhooks/12345678/AbCdEfGhIjKlMnOpQrSt`

## Configure the URL

The URL belongs in `foundry.settings.local.json` (gitignored), not in the committed `foundry.settings.json`. Create or update it alongside the settings file:

```json
{
  "discordWebhookUrl": "https://discord.com/api/webhooks/YOUR_ID/YOUR_TOKEN"
}
```

The committed `foundry.settings.json` keeps `"discordWebhookUrl": ""` as a placeholder. Both files are merged at startup; the local file wins on any key present in both.

## Verify delivery is working

**Startup log** — if the URL is empty, Foundry logs once at startup:
```
Discord delivery disabled: no webhook URL configured.
```
If the URL is set, no startup message appears; the worker polls silently.

**Delivery log** — successful delivery logs at Debug level:
```
Delivered notification {Id} to Discord.
```
Failures log at Warning (5xx, network) or Error (4xx).

**NotificationStore** — the `/api/notifications` endpoint (or direct LiteDB inspection) shows each notification's `DeliveredAt` and `DeliveredTo` fields. `DeliveredTo = "discord"` means delivered successfully. `DeliveredTo = "discord:failed"` means the delivery was abandoned after 3 attempts.

## Retry behaviour

The worker polls every 30 seconds. On transient failures (5xx or network errors), the notification is retried. After 3 failed attempts the notification is marked `discord:failed` and will not be retried further.
