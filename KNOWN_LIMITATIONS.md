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
- **Mesh density / performance** were chosen conservatively (density 0.5, no normals, 4 concurrent mesh jobs).
  Very large spaces will accumulate many mesh colliders; restarting the app clears them.
- **No pooling for the dust particle system** beyond reusing one system per goose; there is only ever one goose.
- The TextMeshPro essential resources are imported by Setup; if that import fails the UI silently uses legacy
  `UnityEngine.UI.Text`, which looks a little softer.
