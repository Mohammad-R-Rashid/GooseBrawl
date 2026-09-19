# GOOSED. — Demo Script (about 2 minutes)

Best demo space: a room or corridor with a clear 3-4 m run and at least one wall or large piece of
furniture the goose can be steered around. Sound on, volume up, haptics on.

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
5. **Here it comes.** The goose flies in low along your line of sight, wings beating (you feel each beat), squawking,
   and lands 2-3 m in front of you. It stands there and glares at you. **It will not move until you have actually
   looked at it** — if you turned away, the edge locator (goose icon + arrow + distance) points to it.
6. **TURN AROUND AND RUN!** Turn 180 degrees and walk or jog away, phone facing forward. The dark HUD shows the
   survival timer, honks, best, and the goose-o-meter; the locator sits on the screen edge in the goose's direction
   and drops to the bottom with BEHIND YOU when it is behind you. Behind you it also sounds muffled.
7. **Let the goose chase from behind.** Your heartbeat starts (you feel it), honks get more frequent, the edges of
   the screen darken and redden. Say out loud that you can't see it yet.
8. **Turn the phone around.** The goose is right there in the room, casting a shadow on your floor, head locked on the
   camera. Walk around a wall or the couch: the goose steers around it instead of clipping through, and honks
   angrily when it has to reroute.
9. **Escalation.** After ~8 seconds the goose starts **flap-dashing**: short low hops that cover a couple of metres
   in a blink (it never lands closer than 1.8 m). The hops get more frequent, the animation faster, feathers start
   flying. After ~8 seconds of chase it can also **lunge** (crouch, flap, feathers, whoosh, big haptic, lens punch).
   After 30 seconds it enters **RAGE MODE**: dashes every 3 seconds, honk bursts, a distant flock joins in.
10. **Get caught.** Slow-motion tackle with a giant "HONK.", the picture desaturates, heavy haptic thud and rumble,
    then four sad descending honks and a random game-over title with your survival time, honks survived and best time.
    A new best gets a NEW BEST! stamp, a flock cheer and a success haptic.
11. **Restart.** Tap **RUN AGAIN**: the egg is back in the same nest; the steal beat runs faster now and a tap skips ahead.
    **MOVE NEST** lets you pick a new spot. Show that BEST persists.

Talking points while it runs:
- The goose is a real AR object in world space, not a UI overlay. It casts real shadows and reflects the real room
  (ARKit environment probes); the virtual light follows the room's brightness and colour temperature.
- Every sound is something that exists in the room: real goose recordings, synthesized foley, your own heartbeat.
  No music, on purpose.
- Haptics are Core Haptics patterns: wing beats, footsteps you can feel within 3 m, the heartbeat, the tackle.
- Avoidance uses the LiDAR scene mesh + detected walls as invisible colliders and simple capsule-cast steering.
- Everything works offline; the only saved data is the best time and the sound/haptics toggles.
