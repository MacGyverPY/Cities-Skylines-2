# GODs Disaster Control — Error and Fix Ledger

This file records confirmed build/runtime failures and their fixes so later
changes do not repeat them.

## 2026-10-11 — Missile gets its own transparent impact crater

- **Confirmed:** v0.13.0 missile model, fins and flight presentation look good.
- **Asset:** `Images/Scorched Crater Blast Ring.png` is embedded into the DLL.
  The supplied 1254×1254 PNG already carries transparency; residual pixels at
  or below 2% alpha are forced fully transparent to prevent a background haze.
- **Size:** the missile crater is 28.5 meters across, exactly 25% smaller than
  the 38-meter meteor impact mark.
- **Lifecycle:** it reuses the proven non-colliding, network-safe impact quad
  and 45-second hold plus 15-second fade/cleanup behavior.

## 2026-10-11 — Meteor Strike completed; Missile Strike Batch 0.13A begins

- **Meteor final status:** complete. The textured meteor reaches terrain and
  networks, carries the accepted Plan-B particle trail, applies 100-meter
  destruction, ignites an outer fire ring, and leaves a transparent timed
  impact mark. No native carrier or landscaping-overlap path is active.
- **Missile first implementation:** v0.13.0 adds one procedural white missile
  per click with a cylinder body, rounded nose, four front canards, four rear
  triangular fins, and isolated propulsion fire/smoke particles.
- **Damage boundary:** direct destruction is limited to 24 meters; native fires
  are limited to four surviving buildings/trees in the 24–42 meter ring.
- **Targets:** buildings, vehicles, citizens and trees use the already-proven
  native destruction/death cleanup paths. The missile visual creates no ECS
  object, collision, placement request or quadtree entry.

## 2026-10-11 — Impact quad rendered processed alpha as opaque white

- **Observed in:** v0.12.6. The impact artwork appeared at the correct position
  and size, but retained a solid white rectangular background.
- **Cause:** the quad selected `Particles/Standard Unlit`. In CS2 that shader
  rendered the ordinary MeshRenderer without the particle vertex-stream setup,
  so the material remained visually opaque despite the runtime alpha key.
- **Fix under test:** v0.12.7 prioritizes `Sprites/Default`, followed by legacy
  and unlit transparent shaders. These shader paths read per-pixel PNG alpha on
  a normal quad. The already-working white-to-alpha conversion is retained.

## 2026-10-11 — Meteor impact artwork uses an isolated visual quad

- **Asset:** `Images/SmMulch01-reference.png` is embedded in the mod DLL so the
  deployed build does not depend on an external image path.
- **Problem avoided:** native landscaping decals appeared only on terrain and
  produced overlap warnings near roads/networks.
- **Fix under test:** v0.12.6 creates a non-colliding Unity quad 0.12 meters
  above the exact impact surface. It should display on terrain, roads and
  sidewalks without entering CS2's placement or quadtree systems.
- **Image cleanup:** the supplied PNG has a solid white background, so near-
  white pixels are converted to smooth transparency at runtime.
- **Lifecycle:** the 38-meter mark remains for 45 seconds, fades for 15 seconds,
  then destroys its GameObject, material and texture.

## 2026-10-11 — v0.12.4 retained as meteor trail Plan B

- **Result:** the three-layer particle trail rendered and did not crash or
  create overlap warnings. Impact damage and native perimeter fires remained
  functional. The flight trail was usable, but its particle cards appeared as
  visible orange squares/rectangles.
- **Decision:** preserve Batch 0.12A R5 / v0.12.4 as **Plan B**. If later VFX
  refinement is worse or unstable, return to that exact build.
- **Cause of rectangles:** `Particles/Standard Unlit` accepted the generated
  alpha texture but remained in its default opaque `_Mode`; HDRP transparency
  properties alone do not switch this shader into alpha blending.
- **Fix under test:** v0.12.5 explicitly selects particle Fade mode, enables
  alpha-blend keywords, disables alpha-test/premultiply keywords, assigns a
  transparent render tag, and increases smoke/fire emission density.

## 2026-10-11 — Meteor flight VFX restarted with soft isolated particles

- **Confirmed baseline:** v0.12.3 has a stable textured meteor on terrain and
  networks, working 100-meter impact damage, and visible native fires in the
  100–150 meter outer ring. It has no overlap warning or flight trail.
- **Problem with older custom trails:** mesh particles and untextured cards
  rendered as blocks, horizontal bars or flat red mist.
- **Fix under test:** v0.12.4 uses three disposable Unity particle layers:
  expanding charcoal smoke, an orange/red flame body, and a yellow/white hot
  core. Each layer uses a generated soft alpha texture, transparent blending,
  camera-facing billboards and world-space simulation.
- **Safety:** the trail creates no ECS entities, placed objects, native fire
  carriers, collision objects or quadtree entries. If no compatible shader is
  available, that layer destroys itself and the stable meteor continues.

## 2026-10-11 — Native bush creation caused overlap warnings and no flight VFX

- **Observed in:** v0.12.2.
- **Result:** `FlowerBushWild01` was created through the proper native object
  pipeline without crashing, but CS2 treated it as real placed landscaping.
  It produced an overlap/placement warning around networks and still displayed
  no visible airborne wildfire flames or smoke.
- **Fix:** v0.12.3 removes the native bush creation request completely. Meteor
  flight returns to the stable dedicated Unity rock with no simulated carrier.
- **Replacement behavior:** at impact, up to eight real initialized trees or
  buildings in the 100–150 meter outer ring receive normal native wildfire
  ignition. Direct targets inside 100 meters retain the established destruction
  behavior.
- **Rule going forward:** do not use trees, bushes, motorcycles, vehicles or
  other placeable simulation objects as airborne meteor VFX carriers. A
  high-quality flight trail requires a packaged custom VFX prefab/material.

## 2026-10-10 — Native bush carrier must use the creation pipeline

- **Observed after:** the stable v0.12.1 crash hotfix.
- **Decision:** a motorcycle carrier was rejected because its vehicle AI,
  lanes, occupants and despawn lifecycle add unnecessary failure modes. The
  much smaller `FlowerBushWild01` remains the preferred wildfire carrier.
- **Fix under test:** v0.12.2 creates a `CreationDefinition` plus
  `ObjectDefinition` request for `FlowerBushWild01`, waits for a fully
  initialized result, then queues the same native `Ignite` event used by the
  working Wildfire disaster. It never clones a live entity.
- **Safety:** if creation or initialization fails, the request times out and
  the stable rock-only meteor continues. The live-tree clone remains banned.

## 2026-10-10 — Cloning a live tree caused an immediate Burst CTD

- **Observed in:** v0.12.0.
- **Crash:** `System.Exception: Item not found (NativeQuadTree.Update)` followed
  by `Burst ... will now abort the Application` in `Player.log`.
- **Cause:** `EntityManager.Instantiate(sourceTree)` copied spatial/culling
  state from a live registered tree. Moving the clone made the native quadtree
  update an entity that had never been registered through the object creation
  pipeline.
- **Fix:** v0.12.1 permanently removes the live-tree clone. Meteor targeting,
  textured rock, network impacts and 100-meter damage remain active; flight
  fire is temporarily disabled instead of risking another CTD.
- **Rule going forward:** never clone and relocate a live world entity carrying
  spatial, culling, lane, ownership or quadtree state. Create moving carriers
  through the complete native creation/initialization pipeline or use an
  isolated Unity visual with no ECS spatial registration.

## 2026-10-10 — Batch refresh still showed only meteor fire icons

- **Observed in:** v0.11.9 and its runtime log.
- **Result:** all four carriers were created with `BatchesUpdated=True`, but
  still produced no visible fire or smoke. The log also revealed that the
  prefab scorer selected `NA_SmiliesCommercialLowLeft002_L1_2x4`, proving the
  carrier was a synthetic commercial-building entity rather than a genuine
  initialized tree.
- **Fix under test:** v0.12.0 clones one real, live, unburned tree entity so it
  retains the native render/culling/effect component set. The clone is buried
  3.5 meters inside the meteor and ignited through the same `Ignite` event used
  by the working Wildfire disaster.
- **Rule going forward:** manually combining `PrefabRef`, `Transform`, and
  `OnFire` does not reproduce a renderable object. When native effects depend
  on the object's initialized archetype, use a real initialized instance or
  the complete native object-creation pipeline.

## 2026-10-10 — Four offset OnFire carriers still produced icons only

- **Observed in:** v0.11.8.
- **Result:** moving four carriers outside and above the meteor produced the
  same fire status icon but no visible fire or smoke. This disproved the
  buried-effect and undersized-effect hypotheses.
- **Native contract found:** CS2's own `AnimationControlsPanel.ToggleFire`
  adds `OnFire`, creates the `EnabledEffect` buffer, and then adds
  `Game.Common.BatchesUpdated` to force the render batch/effect graph to
  refresh. Our carrier omitted that final render notification.
- **Fix under test:** v0.11.9 adds `BatchesUpdated` when each native fire
  carrier is created. The four offset positions remain in place.
- **Rule going forward:** do not treat an `OnFire` icon as proof that the
  effect graph was refreshed. Native visual-state changes must reproduce the
  game's batch update notification as well.

## 2026-10-10 — Native meteor fire carrier showed an icon but no visible VFX

- **Observed in:** v0.11.7.
- **Result:** the fire status icon appeared on the meteor, proving that the
  `OnFire` carrier and event source were active, but no fire or smoke was
  visible. The single carrier was positioned at the center of the roughly
  11-meter rock, so a small native effect could be fully obscured.
- **Fix under test:** v0.11.8 replaces the centered carrier with four native
  carriers positioned around and above the meteor's upper surface. All four
  follow the meteor and are cleaned up after impact.
- **Rule going forward:** an active fire icon proves simulation state, not
  visible rendering. If the offset cluster is still invisible, stop adding
  carriers—the missing piece is then a native effect activation/rendering
  contract rather than effect size or placement.

## 2026-10-10 — FireData prefabs contain no standalone visual effects

- **Observed in:** v0.11.6 runtime log and native pipeline report 0.11C.
- **Result:** `Forest Fire`, `Building Fire`, and `Lightning Strike` provide
  simulation data only; none carries a `Game.Prefabs.Effect` buffer, so using
  one directly as an effect owner produces no fire or smoke.
- **Native contract found:** `FireSimulationSystem` queries `OnFire` entities
  and reads their `PrefabRef` and `Transform`. The visible smoke/fire reference
  belongs to the burning object's prefab, while `OnFire.m_Event` points to the
  separate FireData event source.
- **Fix:** v0.11.7 searches object prefabs for an effect buffer referencing
  `SmokeFireVFX`, then creates a moving carrier with that object `PrefabRef`,
  `Transform`, `OnFire`, and `EnabledEffect`. The custom low-quality particle
  trail remains disabled.
- **Rule going forward:** FireData controls ignition behavior and intensity;
  the target object's prefab supplies the visual effect graph. Both halves are
  required for native CS2 fire rendering.

## 2026-10-10 — Rounded mesh particles still rendered as horizontal bars

- **Observed in:** v0.11.5.
- **Result:** the layered colors improved, but fast downward movement exposed
  each mesh particle as a horizontal orange/red bar. The effect remained far
  below the quality of CS2's vehicle and vegetation fires.
- **Fix:** v0.11.6 removes the custom meteor particle emitters from the active
  visual path. The meteor now creates a moving native effect owner backed by
  CS2's own `Forest Fire`, `Building Fire`, or `Lightning Strike` FireData
  prefab, in that preference order. This reuses the game's fire/smoke effect
  buffer while keeping the network-safe Unity rock and ECS impact damage.
- **Fallback:** if no FireData prefab exposes an Effect buffer, the meteor
  continues with the stable rock only and writes the exact rejection to the
  mod log. It does not restore the low-quality particle bars.
- **Rule going forward:** do not synthesize flight fire using generic mesh
  particles when a native effect graph exists; use the game's effect owner
  contract and keep visual failure isolated from targeting and damage.

## 2026-10-10 — Faceted fire became red torn-looking streaks

- **Observed in:** v0.11.4.
- **Result:** the non-billboard geometry removed square cards, but fast motion
  exposed the octahedron facets as red strips. A fixed red HDRP material also
  overrode the intended yellow/orange particle gradient.
- **Fix:** v0.11.5 replaces octahedrons with rounded, mildly warped sphere
  particles and separates the effect into charcoal smoke, orange outer flame,
  and a compact yellow-hot core. Random 3D rotation and size-over-lifetime
  reduce repetition and hard particle popping.
- **Rule going forward:** never rely exclusively on particle vertex tint with
  an uncertain HDRP shader. Give visually distinct layers distinct fallback
  material colors, and avoid sharply faceted meshes for fast-moving trails.

## 2026-10-10 — Soft-textured HDRP particles still exposed square cards

- **Observed in:** v0.11.3.
- **Result:** smoke and fire colors became visible, but HDRP still rendered the
  rectangular billboard geometry clearly at city-scale viewing distances.
- **Fix:** v0.11.4 abandons billboard rendering. Smoke and fire now use small,
  generated, irregular faceted meshes with separate rough smoke and emissive
  fire materials. Generated particle meshes are explicitly cleaned up.
- **Rule going forward:** when the active render pipeline does not honor the
  expected particle alpha path, stop tuning billboard materials and change the
  particle geometry so rectangular cards cannot appear.

## 2026-10-10 — Particle trails rendered as white billboard squares

- **Observed in:** v0.11.2 meteor fire/smoke test.
- **Result:** particle emission and world-space trails worked, but the fallback
  HDRP material had no soft alpha texture; smoke appeared as large white
  squares and fire was not visibly distinct.
- **Fix:** v0.11.3 generates radial alpha textures for smoke and fire, assigns
  all supported HDRP/built-in texture properties, explicitly enables
  transparent blending, uses additive blending/emission for fire, and cleans
  up the generated textures with each meteor.
- **Rule going forward:** a billboard particle system needs both a compatible
  transparent shader configuration and a soft alpha texture. Particle colors
  alone still render the billboard's full rectangular quad.

## 2026-10-10 — Dedicated meteor VFX dependency boundary

- **Added in:** v0.11.2.
- **Change:** fire and smoke use child Unity particle systems with world-space
  particles, so the trail remains behind while the meteor moves.
- **Dependency:** `UnityEngine.ParticleSystemModule.dll` is referenced through
  the user's confirmed CS2 Managed directory.
- **Cleanup:** particle materials are owned by the meteor and explicitly
  destroyed with the visual.
- **Rule going forward:** particle APIs require their own Unity module; do not
  assume `UnityEngine.CoreModule` supplies them.

## 2026-10-10 — Dedicated meteor rendered too shiny and elongated

- **Observed in:** v0.11.0 dedicated meteor visual.
- **Result:** the network-safe meteor worked, but its brown glossy material and
  stretched scale made it resemble a giant shiny turd rather than rock.
- **Fix:** v0.11.1 uses charcoal gray, zero metallic, very low smoothness, a
  generated two-frequency stone texture, stronger multi-scale mesh
  irregularity, and a rounder scale. It also explicitly cleans up each
  temporary mesh, material and texture after impact.
- **Rule going forward:** temporary runtime visuals must define both surface
  roughness and resource ownership; color alone is not adequate material setup.

## 2026-10-10 — Native `Boulder07` cannot serve as a network-safe meteor

- **Observed through:** v0.10.27 after raycast, deletion-guard and direct
  `CullingInfo` experiments.
- **Result:** network clicks and 100 m impact damage were correct, but the
  native landscaping boulder remained invisible over roads and sidewalks.
- **Cause:** `Boulder07` is governed by CS2's static landscaping placement and
  spatial-rendering lifecycle. Moving its ECS transform and exposed culling
  bounds does not convert it into a network-safe moving renderer.
- **Fix:** v0.11.0 removes `Boulder07` from the flight visual path and uses a
  temporary dedicated Unity-rendered procedural rock. Damage and targeting
  remain ECS-based and unchanged.
- **Rule going forward:** do not use native static landscaping objects as
  freely moving cross-network visuals. Use a real moving-object contract or an
  independent temporary renderer.

## 2026-10-10 — CS0315: `QuadTreeBoundsXZ` is not ECS component data

- **Introduced in:** direct bounds patch v0.10.24; exposed after the assembly
  reference was correctly resolved in v0.10.26.
- **Error:** `QuadTreeBoundsXZ` cannot be used with
  `EntityManager.GetComponentData<T>` or `SetComponentData<T>` because it does
  not implement `Unity.Entities.IComponentData`.
- **Cause:** `Game.Common.QuadTreeBoundsXZ` is a bounds/helper value type used
  inside other game data, not a standalone ECS component on the meteor entity.
- **Fix:** v0.10.27 removes the invalid direct quadtree access. The patch keeps
  direct updates to `Game.Rendering.CullingInfo`, which is valid ECS component
  data and is the component relevant to visual renderer culling. Existing
  `Updated` and `BatchesUpdated` markers remain responsible for native follow-up
  processing.
- **Rule going forward:** proving a type exists and is unmanaged is not enough
  to use it with `EntityManager.GetComponentData<T>`; verify that it implements
  `IComponentData` (or use the correct buffer/shared-component API).

## 2026-10-10 — Named reference did not resolve `Colossal.Mathematics.dll`

- **Introduced in:** v0.10.25 attempted dependency fix.
- **Error:** the `Bounds3`/ECS generic errors remained unchanged even though
  `<Reference Include="Colossal.Mathematics">` was present.
- **Cause:** the CS2 mod build tooling did not resolve that assembly name from
  the game's Managed directory automatically.
- **Fix:** v0.10.26 adds an explicit `HintPath` to the user's confirmed game
  installation:
  `F:\Steam\steamapps\common\Cities Skylines II\Cities2_Data\Managed\Colossal.Mathematics.dll`.
- **Rule going forward:** after adding a nonstandard game assembly reference,
  verify that it resolves through the mod toolchain. If it does not, use a
  confirmed `HintPath`; merely adding `Reference Include` is insufficient.

## 2026-10-10 — `Bounds3` assembly reference and cascading ECS generic errors

- **Introduced in:** meteor direct-culling-bounds patch v0.10.24.
- **Primary error:** `Bounds3` is defined in unreferenced assembly
  `Colossal.Mathematics`.
- **Cascading errors:** `CullingInfo` and `QuadTreeBoundsXZ` were reported as
  invalid generic arguments for `GetComponentData<T>` / `SetComponentData<T>`.
- **Cause:** both valid ECS structs contain `Colossal.Mathematics.Bounds3`.
  Without the defining assembly referenced, the compiler cannot prove the
  complete struct is unmanaged/non-nullable and emits misleading generic
  constraint errors too.
- **Fix:** added `<Reference Include="Colossal.Mathematics">` with
  `<Private>false</Private>` to `GODsDisasterControl.csproj` in v0.10.25.
- **Rule going forward:** when using a public CS2 component, inspect the
  assemblies of its nested field types as well. A reference to `Game.dll` does
  not automatically make those compile-time dependencies available.

## 2026-10-10 — CS0246: `TransformUpdated` / `BoundsUpdated` not found

- **Introduced in:** experimental meteor network-bounds patch v0.10.23.
- **Error:** CS2's referenced `Game.dll` exposes neither `TransformUpdated` nor
  `BoundsUpdated` as usable component types.
- **Cause:** names found in an inspection script's search list were treated as
  verified API contracts. The actual IL report did not prove those types exist.
- **Initial fix:** removed both invalid tags. Version 0.10.24 attempted direct
  bounds movement. Later compiler testing proved only
  `Game.Rendering.CullingInfo` is valid standalone ECS component data;
  `QuadTreeBoundsXZ` access was removed in v0.10.27.
- **Rule going forward:** a name appearing in an inspector pattern list is not
  API proof. Before referencing a new CS2 type, verify it in generated IL,
  decompiled source, an existing successfully compiled project, or a user-side
  compile probe.

## Earlier confirmed compiler fixes

- **CS0221 (`-1` to `Game.Net.Layer`):** use
  `unchecked((Game.Net.Layer)(-1))` for the all-network-layer mask.
- **CS0104 (`Resident` ambiguous):** alias or fully qualify
  `Game.Creatures.Resident`; do not rely on the short name while
  `Game.Prefabs` is imported.
- **CS0136 (duplicate `fireState` local):** use distinct local names across
  nested/enclosing scopes.
- **CS1620 (missing `out`):** pass the first argument using `out` where required
  by the resolved CS2 API signature.
# Nuclear ICBM 0.14.0

- **Missile completed:** the compact Missile Strike is now accepted as DONE at v0.13.1.
- **Nuclear scale:** the screenshot reference is represented by a guaranteed 275m destruction
  core, deterministic partial destruction to 450m, and dense non-random ignition from 450–520m.
- **ICBM presentation:** launch altitude is 950m; the finless body is 50% larger than the compact
  missile, with a white body, black nose, and six separate propulsion emitters.
- **Initial flash:** an emissive translucent white sphere expands to a 550m diameter over 2.25s;
  its lower half is naturally hidden below the target surface, producing a visible dome.
- **Pollution safety:** air, ground, and water simulation maps are resolved at runtime and written
  only after completing each map dependency. Unsupported/renamed maps are logged and skipped so
  an API mismatch cannot abort the blast or crash the game.

## Nuclear ICBM 0.14.1 size and erasure revision

- **Confirmed in game:** air pollution, ground pollution, the nuclear explosion, and the white
  blast dome all worked in v0.14.0.
- **Missile size:** changed from 150% to 175% of the compact Missile Strike model. The engine
  ring and rear attachment height were enlarged with the body.
- **Dome size:** radius increased 30%, from 275m to 357.5m (715m diameter).
- **Initial erasure:** every ordinary rendered world object inside 357.5m receives CS2's native
  `Deleted` cleanup marker instead of damage/collapse. This covers buildings, props, trees and
  vehicles; citizens still use the native death path.
- **Network safety:** raw road, rail, sidewalk node/edge/lane entities are deliberately excluded.
  Directly deleting those ECS entities can leave broken topology and corrupt pathfinding or the
  save; network removal must use CS2's native network-edit pipeline in a separate revision.

## Nuclear ICBM 0.14.2 impact overlay and terrain-crater inspection

- **Impact artwork:** embedded `Images/Nuclear Impact Crater.png`, preserving the original
  1254x1254 canvas. The fake gray checkerboard was replaced with genuine transparent alpha.
- **In-game footprint:** the nuclear overlay renders across the full 715m erasure diameter and
  uses the existing timed hold/fade cleanup, independently of the missile and meteor marks.
- **Terrain depth request:** target depth is -116m at the center with a smooth circular falloff.
  Raw height-buffer writes were rejected because they bypass native terrain dependencies and
  could corrupt terrain or save state.
- **Inspector added:** `Scripts/GODs-Nuclear-Terrain-Crater-API-Inspector-0.14C.ps1` extracts the
  installed game's Brush, BrushDefinition, TerrainToolSystem, ApplyBrushesSystem, TerrainSystem,
  height-writer, renderer, water, and network follow-up contracts before implementation.

## Nuclear ICBM 0.14.3 incomplete impact image fix

- **Observed failure:** the transparent nuclear PNG was complete, but a large irregular section
  vanished in game. A 715m flat quad placed only 0.12m above the target intersects uneven terrain
  and retained road/network surfaces, causing those surfaces to win the depth test.
- **Fix:** the nuclear-only overlay is raised to 0.75m, uses a late transparent render queue, and
  requests `ZTest Always` when the selected shader exposes `_ZTest`. Meteor and missile impact
  marks retain their prior placement and depth behavior.
- **Inspector timing:** the 0.14C report may be generated with CS2 open because it only reads the
  installed `Game.dll`; running it with CS2 closed remains cleaner if the installation is being
  updated or another process locks that assembly.
- **Terrain report result:** CS2 applies height through `ApplyBrushesSystem` into
  `TerrainSystem.ApplyBrush`, using a valid `BrushPrefab`, `Brush`, `PrefabRef`,
  `TerraformingData`, and the tool's applied/deleted lifecycle. A raw height-buffer write or an
  incomplete synthetic brush request remains disabled until that complete lifecycle is reproduced.

## Nuclear ICBM 0.14.4 invisible impact image correction

- **Observed regression:** the 0.14.3 nuclear impact image did not render at all. CS2's selected
  transparent shader/render pipeline did not tolerate the nuclear-only `_ZTest Always` plus late
  render queue combination reliably.
- **Fix:** removed the depth override and restored the proven transparent render queue `3001`.
  The nuclear quad now clears terrain and retained road surfaces through a 3.0m vertical offset;
  meteor and missile overlays remain at 0.12m.
- **Lesson retained:** do not force `_ZTest` or a queue of `3600` on these runtime impact-mark
  materials. Prefer ordinary transparent depth behavior and physical surface clearance.

## Nuclear ICBM 0.14.5 native terrain crater

- **Implementation:** queues one native `Brush` work entity through CS2's existing
  `ApplyBrushesSystem`. The request uses the installed signed height-shift `TerraformingData`
  prefab and an installed circular `BrushData` prefab instead of writing the terrain buffer.
- **Shape:** 715m diameter, smooth brush-texture falloff, and -116m signed center strength.
- **Lifecycle:** the work entity is created from the selected brush prefab's own archetype and
  carries `Brush`, `PrefabRef`, and applied `Temp` data. CS2 applies it and adds its normal
  `Applied`/`Deleted` cleanup markers.
- **Failure guard:** missing/incompatible prefabs or any runtime exception skips only terrain
  deformation. The nuclear destruction, fires, pollution, dome, and impact image continue.
- **Networks:** this deforms terrain beneath roads and other networks; it does not delete or
  rebuild network topology, so retained roads can remain suspended across the crater.

## Nuclear ICBM 0.14.6 direct native crater and magenta shader fix

- **Observed terrain failure:** v0.14.5 logged the correct `Terrain Shift Tool` and `Default Brush`
  and created a temporary brush request, but no terrain changed. The request was not consumed by
  `ApplyBrushesSystem` through the expected tool-output lifecycle.
- **Terrain fix:** after resolving those same native prefabs, invoke the inspected private
  `ApplyBrushesSystem.ApplyHeight(Brush, Entity, TerraformingType)` method directly. This still
  ends in `TerrainSystem.ApplyBrush`; it only removes the failed transient-entity handoff.
- **Observed visual failure:** `Sprites/Default` displayed the nuclear texture in bright
  magenta/purple, Unity's unsupported-shader output under CS2's HDRP renderer.
- **Visual fix:** nuclear impact marks now prefer `HDRP/Unlit`, already proven by the working
  nuclear blast dome. Meteor and missile marks retain their established sprite-first path.

## Nuclear ICBM 0.14.7 crater-floor impact placement

- **Observed failure:** after the native -116m crater succeeded, the nuclear artwork remained at
  the pre-impact surface elevation and visibly floated across the open crater.
- **Fix:** nuclear-only impact artwork is positioned at `originalY - 116m + 0.75m`, placing it
  just above the new crater floor while preserving a small anti-flicker clearance. Meteor and
  missile impact placement is unchanged.
- **Expected occlusion:** because the artwork remains a flat quad, the crater's rising sidewalls
  naturally hide its outer portions. This is intentional and preferable to a floating 715m sheet.

## Nuclear ICBM 0.14.8 optional terrain crater

- **Option:** added a persistent `Terrain Crater` ON/OFF control under Nuclear Strike settings;
  it defaults to ON and is restored from Cohtml local storage at UI startup.
- **ON:** applies the native -116m terrain crater and places the impact artwork at its floor.
- **OFF:** skips only terrain deformation and places the artwork at the original surface. The
  ICBM, blast, erasure, fires, pollution, dome, and other damage remain enabled.
- **Diagnostics:** every option change and every user-disabled crater skip is written to the mod log.

## Nuclear ICBM 0.14.9 crater depth adjustment

- **Adjustment:** reduced nuclear terrain deformation from -116m to -58m after in-game testing
  showed the original crater was excessively deep.
- **Impact artwork:** continues to derive its floor position from `NuclearCraterDepth`, so it now
  follows the new -58m floor automatically without a separate hard-coded offset.
- **UI:** updated the Terrain Crater description to display the correct permanent -58m depth.

## Nuclear ICBM 0.14.10 nuclear image black-background fix

- **Observed failure:** with Terrain Crater OFF, the HDRP nuclear image rendered its transparent
  PNG pixels as a large opaque black square at surface level.
- **Fix:** enable HDRP alpha clipping for the nuclear impact material at a low 0.04 cutoff. Fully
  transparent background pixels are discarded while the visible crater artwork remains.
- **Scope:** the shader correction applies whether Terrain Crater is ON or OFF. Crater depth,
  damage, fires, pollution, and the toggle behavior are unchanged.

## Nuclear ICBM 0.14.11 alpha-shaped nuclear impact mesh

- **Observed regression:** HDRP alpha clipping removed some of the black border but still rendered
  large opaque black regions because the runtime shader continued to ignore portions of PNG alpha.
- **Fix:** nuclear artwork no longer uses a solid rectangular quad. A 96x96 runtime grid samples
  the PNG alpha channel and creates geometry only for visible cells; transparent cells have no
  triangles and therefore cannot render black regardless of shader alpha handling.
- **Cleanup:** the generated mesh is tracked with the timed impact mark and explicitly destroyed
  alongside its GameObject, material, and texture to prevent native Unity resource leaks.

