# GOOSED. — Demo Script (about 2 minutes)

Best demo space: a room or corridor with a clear 3-4 m run and at least one wall or large piece of
furniture the goose can be steered around. Sound on, volume up, haptics on. A laptop on the table open on the Goose Board
(`https://goose-brain.mohammad-rashid7337.workers.dev/board`, see BOOTH.md) so the queue watches the goose's brain live.

1. **Open the app.** Cream launch screen, then the title: "GOOSED. — Steal the egg. Get goosed." Tap **START**.
   (First launch: the camera permission prompt, then the three-step HOW IT WORKS card. Tap GOT IT.)
2. **Scan the floor.** Move the phone slowly over the floor and toward a wall or the couch for 5-10 seconds.
   Soft cream dots appear on the floor; on the iPhone 15 Pro LiDAR meshing is also running silently in the background.
   The screen advances to placement as soon as a decent patch of floor exists.
3. **Place the nest.** A soft ring and a translucent ghost of the nest follow the floor under the screen centre. Tap.
   The twig nest appears with the egg glowing warmly in it, the dots fade away, and you should see the nest's
   shadow on your real floor.
4. **Grab the egg.** Tap the egg (or **TAKE EGG**). "BREAKFAST TIME!", the egg flies into your hand ("GOT IT. LET'S GO COOK."),
   then it slips and drops in slow motion, cracking on the floor in front of you ("OOPS. THERE GOES BREAKFAST."):
   shell pieces, yolk and the white spreading on your floor. Keep looking where you are looking: "THE GOOSE HEARD THAT."
5. **Here it comes.** "HERE COMES KEVIN." The goose flies in low along your line of sight, wings beating (you feel each beat),
   squawking, and lands 2-3 m in front of you. It stands there, glares at you and **speaks** (the subtitle pill shows the line). **It will not move until you have actually
   looked at it** — if you turned away, the edge locator (goose icon + arrow + distance) points to it.
6. **TURN AROUND AND RUN!** Turn 180 degrees and walk or jog away, phone facing forward. The dark HUD shows the
   survival timer, honks, best, and the goose-o-meter; the locator sits on the screen edge in the goose's direction
   and drops to the bottom with BEHIND YOU when it is behind you. Behind you it also sounds muffled.
7. **Let the goose chase from behind.** Your heartbeat starts (you feel it), honks get more frequent, the edges of
   the screen darken and redden. Say out loud that you can't see it yet.
8. **Turn the phone around.** The goose is right there in the room, casting a shadow on your floor, head locked on the
   camera. Walk around a wall or the couch: the goose steers around it instead of clipping through, and honks
   angrily when it has to reroute.
8b. **Yell at it.** Shout at the phone ("GO AWAY, KEVIN!"). It flinches (feathers, RUDE.), backs off, then dashes back at you.
    Two seconds later it answers what you actually said. Say out loud that it heard you.
    Now shout its **name** ("KEVIN!"): it stops dead, snaps its head to the camera, one dramatic honk, "...WHAT." on the subtitle
    pill, then it comes for you. Try "SORRY!": "APOLOGY NOT ACCEPTED." (Apple on-device speech, nothing leaves the phone.)
8c. **Bread.** As the goose closes in, the round bread button (bottom right) grows and pulses. Tap it once: the slice of toast flies past
    the goose, it detours, eats it (OM NOM., crumbs) and comes back angrier with a dash. One roll per round.
8d. **Dodge.** When it crouches to lunge, sidestep half a metre: DODGED! (hit-stop, feathers) and a sore-loser line. DODGES count on the HUD.
9. **Escalation.** After ~8 seconds the goose starts **flap-dashing**: short low hops that cover a couple of metres
   in a blink (it never lands closer than 1.8 m). The hops get more frequent, the animation faster, feathers start
   flying. After ~8 seconds of chase it can also **lunge** (crouch, flap, feathers, whoosh, big haptic, lens punch).
   After 30 seconds it enters **RAGE MODE**: dashes every 3 seconds, honk bursts, a distant flock joins in.
10. **Get caught.** Slow-motion tackle with a giant "HONK.", the picture desaturates, heavy haptic thud and rumble,
    then four sad descending honks and a random game-over title with your survival time, honks survived and best time.
    A new best gets a NEW BEST! stamp, a flock cheer and a success haptic.
10b. **Or outlast it.** Survive 45 s and the goose gives up: THE GOOSE HAS GIVEN UP, it flops and sulks, the win card
    ("YOU OUTLASTED KEVIN") and a sore-loser line. (`GooseGameManager.outlastSeconds` is the demo knob.)
10c. **The photo.** The card lists the run (DASHES SURVIVED, LUNGES SURVIVED, BREAD, RAGE REACHED). Tap **SHARE**: the slow-motion
    tackle frame with the GOOSED. stamp opens in the share sheet; AirDrop it to the judge. Or **PHOTO WITH KEVIN**: the card slides
    away, walk around the goose (it turns to follow you, flaps, honks), frame it, tap the shutter, share.
10d. **The board.** Type a name, tap **POST TO BOARD**: "#2 ON THE BOARD". Point at the laptop: the run appears on the Goose Board
    within two seconds, and the right-hand panel is this phone's goose: its memory, grudge and the events of the round streaming in.
11. **Restart.** Tap **RUN AGAIN**: "THIS MEANS WAR." The goose remembers: the grudge line under the title, and its intro quotes
    last time. The steal beat runs faster now and a tap skips ahead. **MOVE NEST** lets you pick a new spot. Show that BEST persists.
12. **Sentry.** Open the `game.round` trace from the phone: phase spans, frame measurements, the Worker's `gen_ai.chat` span with
    token counts and the ElevenLabs span in the same trace; then the Logs view filtered on `frame.spike` / `yell.`.

Talking points while it runs:
- The goose is a Cloudflare Agent: its memory (name, grudge, what you shouted) lives in a Durable Object; OpenAI writes every line;
  ElevenLabs voices it; the phone falls back to an offline bank so the demo never depends on wifi.
- Sentry is how we tuned it: the render benchmark, the frame-spike logs with goose/AR context, the adaptive quality ladder.
- The goose is a real AR object in world space, not a UI overlay. It casts real shadows and reflects the real room
  (ARKit environment probes); the virtual light follows the room's brightness and colour temperature.
- Every sound is something that exists in the room: real goose recordings, synthesized foley, your own heartbeat.
  No music, on purpose.
- Haptics are Core Haptics patterns: wing beats, footsteps you can feel within 3 m, the heartbeat, the tackle.
- Avoidance uses the LiDAR scene mesh + detected walls as invisible colliders and simple capsule-cast steering.
- Everything works offline; the only saved data is the best time, your board name and the sound/haptics toggles.
- The Goose Board is the same Worker: a second Durable Object holds today's runs; the page polls the playing phone's memory.
