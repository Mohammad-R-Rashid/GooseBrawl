# GOOSED. at the booth: the Goose Board

The Goose Board is a web page served by the goose's own Worker. Left: today's leaderboard (every run posted from the
results card, rolling 24 h). Right: the **live brain** of whichever phone is playing right now, straight out of its
Durable Object (name, title, grudge, rounds, best/last, last shout, and the round's events sliding in). It polls every
1.5 s and needs nothing but a browser.

## Deployed (use this at the table)

Open on the laptop, full screen (`F11` / `⌃⌘F`):

```
https://goose-brain.mohammad-rashid7337.workers.dev/board
```

The phone build already talks to this Worker (`Assets/Resources/GooseBrainUrl.txt`), so every POST TO BOARD from the
results card lands here. `?device=<deviceId>` pins the brain panel to one phone (the id is in `/board/data`).

Redeploy after a Worker change:

```bash
cd backend/goose-brain && npm run check && npm test && npx wrangler deploy
```

## Local (development, or if the venue wifi dies)

```bash
cd backend/goose-brain && npx wrangler dev --port 8787 --var MOCK_AI:1
```

Then open `http://localhost:8787/board`. `MOCK_AI:1` means bank lines and no third-party calls, so no keys are needed.
The Unity Editor prefers this local Worker automatically when it is up (it probes `localhost:8787/health` on play), so
the Editor's results card posts to the local board and the title ticker reads from it. The phone can only reach it
if it is on the same network and you point `GooseBrainUrl.txt` at your Mac's IP (`http://<mac-ip>:8787`), then
re-export; for the demo, use the deployed Worker instead.

Seed a couple of rows to check the layout:

```bash
curl -s -X POST localhost:8787/board/run -H 'content-type: application/json' -d '{"deviceId":"dev1","name":"Mo","seconds":23.4,"outcome":"GOOSED","gooseName":"KEVIN","honks":12,"dodges":3}'
```

```bash
curl -s -X POST localhost:8787/agents/goose-brain/dev1/session -H 'content-type: application/json' -d '{"gamesPlayed":0}'
```

The first fills the table; the second names dev1's goose and makes the brain panel follow it.

## What the phone shows

- Results card: a name field (A-Z, 12 characters, remembered between rounds) and **POST TO BOARD**, answering
  `#3 ON THE BOARD`. Hidden when the Worker is unreachable; nothing else changes.
- Title screen: **TOP GEESE TODAY**, the top three, only when the Worker answers.

## Reset the board

There is no reset route on purpose (the endpoint is open). Runs older than 24 hours fall off the page by themselves,
so a board seeded the night before is clean by judging time. To clear it sooner, rename the global instance in
`src/server.ts` and `src/agent.ts` (`idFromName("global")` -> `"global-2"`) and redeploy: a fresh Durable Object, empty table.
