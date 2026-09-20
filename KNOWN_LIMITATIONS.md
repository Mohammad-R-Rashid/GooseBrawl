- **Results / photo / board / speech pass (2026-09-20) was verified in the Editor mock (smoke test, `Library/ShareShots` PNGs, the
  board against `wrangler dev` and the deployed Worker); not yet on the phone.** Still to verify on the iPhone 15 Pro: the share
  sheet (`GooseShare.mm`, needs the `NSPhotoLibraryAddUsageDescription` the post-build step now writes), the capture at the tackle
  (`ScreenCapture.CaptureScreenshotAsTexture` at the end of the slow-motion frame), the iOS keyboard over the board row, and the
  on-device speech recogniser (`GooseSpeech.mm`: needs the Dictation language assets; the `speech.warmup` log says `available`).
  If speech misbehaves in a loud hall, the yell still works exactly as before (the recogniser only adds the name / apology beats).
- **Voice + brain + Sentry pass (this session) was built and verified in the Editor mock through the smoke test (offline and
  against a local `wrangler dev` Worker in mock mode) and the Unity iOS export; no phone was connected.** Still to verify on the
  iPhone 15 Pro: the microphone prompt and shout detection with the phone's own speaker (the honk gate), speaker volume while
  the mic session is active (`Force IOS Speakers When Recording` is on; if the goose gets quiet, turn `YellDetector.yellEnabled`
  off), the Sentry native iOS layer in the Xcode build, real frame measurements (draw calls read 0 in the Editor), and the benchmark.
- **No OpenAI key by choice**: the brain runs in mock-text mode (the 84-line script, deterministic), so lines never react to what
  you shouted and the goose's name comes from the persona bank. The ElevenLabs voices are live (free tier, 10k characters/month;
  the deployed Worker caches every line for both voices in R2 so the script costs about 2.8k characters once; `POST /warm`
  after a redeploy or a line change). `gpt-5.6-luna` / `gpt-transcribe`
  were never exercised with a real key.
- **Sentry is enabled** (Unity + Worker, one project). The Uptime monitor on `/health` must be created in the Sentry UI. The
  ElevenLabs key was shared in a chat transcript: rotate it after the event. Sentry Profiling and Session Replay do not exist for Unity; the frame-level data
  comes from `PerfProbe` through Tracing and Logs. Adaptive quality only steps down, and only on the device.
- **The bread button** renders the slice of toast through an extra camera on the `UIBread` layer (added by Setup / the `layers` remote command);
  without that layer it falls back to a flat warm disc.
- **Bread never uses physics**: it lands on the far side of the goose on free floor (falls back to beside it); on a cluttered
  scan it may land inside something the goose then walks around. The goose ignores catches while it eats (by design).
- **Release builds only**: development builds show Unity's console overlay on errors and cost frame time; `Goose Brawl > Build iOS
  Xcode Project (Release)` + `xcodebuild -configuration Release` is the shipping path. Performance defaults are MSAA 2x,
  1024 shadow map, 8 m shadow distance, LiDAR scene reconstruction OFF (the goose steers against the detected wall
  planes), environment probes OFF; the benchmark (docs/PERFORMANCE_BENCHMARK.md) measures the heavier settings.
- **The shout is amplitude-based**: any loud burst 15 dB over the room floor (and above -20 dBFS) counts; clapping works too.
# Known Limitations

- **This polish pass (rename to GOOSED., graphics, sound, haptics, gameplay, UI) was built and verified in the Editor mock,
  through the smoke test, the Unity iOS export and a signed Xcode build (`Builds/DerivedData/.../GOOSED.app`); the phone was not
  connected at the end of the session, so the install and the device pass (shadow catcher against LiDAR depth, Core Haptics feel, environment probes,
  light estimation, MSAA/post-processing cost at 60 fps) needs a run on the iPhone 15 Pro. Fallbacks: raise the shadow
  catcher (`ShadowCatcher.lift`), turn off `ARBootstrapper.enableOcclusion` / `enableEnvironmentProbes`, or lower Bloom
  in `Assets/Settings/GoosedPostFX.asset`.

- **Device testing.** The Development build was compiled with Xcode, installed and launched on the paired iPhone 15 Pro
  from this environment. The device console confirmed: ARKit providers registered, the AR session initialized, scene
  meshing and environment-depth occlusion active, the OS font loaded, the 16 honk recordings loaded and the goose
  spawning with all bones and animation clips resolved. Feel, timing and comedy on the device were not judged by the
  author (no eyes on the screen). Signing: the Apple ID stored in Xcode has invalid keychain credentials, so automatic
  provisioning against Apple fails; the build was signed with the existing Xcode-managed wildcard profile of team
  7D742L5CU8 (`DEVELOPMENT_TEAM=7D742L5CU8 CODE_SIGN_STYLE=Automatic`, no `-allowProvisioningUpdates`). The build that is
  currently installed still carries the URP template bundle id; Setup now sets `com.goosebrawl.eggsnatcher`, so the next
  Unity export uses the proper id.
- **Ultra-wide (0.5x) camera.** Verified on the iPhone 15 Pro: ARKit world tracking offers 22 video formats and none use
  the ultra-wide capture device, so the 0.5x view is not available to AR sessions. `ARCameraSelector` would pick it if a
  future iOS exposes it; today it selects 1920x1440 at 60 fps (4:3, the widest field of view available) and logs the list.
- **Nest model.** The nest is procedural (lathe bowl + one combined mesh of ~80 twig strands with a generated normal map);
  drop a real model into `Assets/Art/Nest/` and re-run Setup to use it instead.
- **Fly-in path.** The goose's entrance flies a straight line from the distance toward the nest at about 1.3 m height; it
  is shortened when a scanned wall is in the way but it does not steer around furniture while airborne.
- **Obstacle avoidance is local steering, not pathfinding.** The goose can get caught in dead ends or behind long
  walls; it will stop, honk, back up and try wider angles, but it will not plan a route around a building.
- **Unscanned geometry is invisible to the goose.** Anything you did not point the phone at has no collider yet,
  so scan the demo area (especially the wall or furniture you want it to avoid) before stealing the egg.
- **Floor height is fixed at nest placement.** Stairs and split levels are not supported; the goose stays on the
  nest floor (it follows small scanned bumps within 25 cm).
- **Occlusion** is enabled with the fastest environment depth mode and only works on LiDAR devices; the goose may
  poke through thin real objects. Turn `ARBootstrapper.enableOcclusion` off if it causes flicker.
- **Tracking loss** pauses the goose and shows a "Move phone slowly" hint; if ARKit relocalizes far away the goose
  keeps its old world position (it never teleports), which can put it further away than expected.
- **Animation clips** are matched by name and played through a Playables mixer with cross-fades; there is no
  root motion and no blend tree, so foot sliding at odd speeds is possible. Head/wing procedural motion is layered
  in world space and can look slightly off if the model's rest pose is unusual.
- **Audio**: the goose honks are sliced automatically out of flock recordings, so a few slices contain two overlapping
  birds. Everything else is synthesized at 44.1 kHz (`ProceduralAudio`); it is physically motivated but still synthetic.
  Spatialization is Unity's panning plus a "behind you" low-pass; there is no HRTF plugin, so on the phone speaker
  direction comes from the locator, the muffling and the haptics rather than from stereo cues.
- **Haptics** use Core Haptics (transients, continuous events, authored patterns) with a UIKit fallback on device and
  `Handheld.Vibrate` on Android; the Editor only counts pulses.
- **Editor mock** is a flat room with boxes; it cannot reproduce AR tracking quality, meshing noise or lighting.
- **LiDAR scene reconstruction is off by default** (`ARBootstrapper.enableEnvironmentMeshing`): every chunk update
  cooked a MeshCollider on the main thread and produced visible hitches. Walls come from ARKit's vertical planes
  instead, which appear a little later and only for flat, well-lit walls; furniture in the middle of a room is not a
  collider for the goose any more. The benchmark config `mesh_on` measures the cost if it is ever wanted back.
- **No pooling for the dust particle system** beyond reusing one system per goose; there is only ever one goose.
- The TextMeshPro essential resources are imported by Setup; if that import fails the UI silently uses legacy
  `UnityEngine.UI.Text`, which looks a little softer.
