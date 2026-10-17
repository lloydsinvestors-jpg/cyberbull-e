# Editing the Schedule I map from a mod

What was learned, by building and measuring, while making live map changes (an earthquake that splits the town) for a
Schedule I mod. Every point here was either measured in the game or found to fail first. The failures are listed too, so
nobody has to repeat them.

Stack: Schedule I = Unity 2022.3.62f2, URP 14, IL2CPP; mods on MelonLoader 0.7 + Il2CppInterop + Harmony.

## 1. Measure the pipeline before any render trick

Log it at world load before designing anything that depends on draw order:

- `GraphicsSettings.currentRenderPipeline` → `UniversalRenderPipelineAsset`. Log `supportsCameraDepthTexture`, `supportsCameraOpaqueTexture` and `m_RendererDataList`.
- Each `UniversalRendererData`: `renderingMode`, `depthPrimingMode`, `copyDepthMode`, `rendererFeatures`.
- The live renderer: `asset.scriptableRenderer` → `UniversalRenderer.renderingModeActual`.

Measured:

- **Rendering:** `Deferred Renderer`, depth priming Disabled, copy depth AfterOpaques.
- **Renderer features:** ScheduleOneFog, Beautify, Decal, SSAO, GodRays, Outline, LiquidVolume depth pre-pass, StylizedWater2, GrabScreen, psychedelic.

What deferred means here:

- **Lit and Shader Graph materials** draw in the **G-buffer pass**. `SRPDefaultUnlit` and `UniversalForwardOnly` passes draw in the forward-only pass **after lighting**.
- **The G-buffer pass overrides every draw's stencil** to write its material bits. A stencil trick inside it is silently ignored.
- **Stencil bits 0x10-0x60 belong to deferred lighting and material types.** Only 0x01-0x0F are free.

## 2. What the game lets a mod read

- **Static meshes can't be read:** they are static-batched, GPU-only combined meshes (`isReadable` false), and GPU readback (`GraphicsBuffer.GetData`) is stripped. To draw part of one: `Graphics.DrawMesh(combinedMesh, matrix, material, layer, null, renderer.subMeshStartIndex + i)`. The combined mesh is in world space.
- **To read a surface anyway, capture it.** Draw the renderer top-down into float render targets with a probe shader: `CommandBuffer.SetViewProjectionMatrices` + `DrawRenderer(renderer, probe, submesh)` + `ReadPixels`.
  - Pass 0: `(uv0, worldY, 1)`. Pass 1: `(worldX, worldZ, uv1)`.
  - Check which way up the target came back by comparing a pixel's recorded world z.
  - Capture at 2 cm. At 4 cm, outlines rebuilt from the capture stair-step visibly.
- **TerrainData's 2-D array API is stripped:** GetHeights indexing, SetHeights, Get/SetAlphamaps, Get/SetDetailLayer, and the `alphamapTextures` getter. Use the GPU route, as Unity's own terrain brushes do:
  1. Blit `heightmapTexture` to an RFloat RT, ReadPixels, edit.
  2. Blit back to an RT of the heightmap's format.
  3. Call `CopyActiveRenderTextureToHeightmap(rect, dest, HeightAndLod)`. That also updates the collider.
  - Height encoding: **measure it**. Compare a texel with `GetHeight / size.y`. It was 0.5.
  - Paint: `GetAlphamapTexture(i)` (GetPixels/SetPixels region, `SetBaseMapDirty`).
  - Grass details can't be edited.
- **Main terrain measured:** 512 x 512 m, heightmap 2049 (0.25 m per sample), splat 2048 x 4 layers (Grass, Seabed, Dirt, Gravel).
- **Shader Graphs on the map:**
  - Roads use `Shader Graphs/GroundWet`: `_BaseMap` on mesh UVs, `_BaseColor`, rain inputs mostly 0.
  - Concrete, paths, tiles and parking use `Shader Graphs/WorldspaceUV_New`: `_DiffuseTexture` projected from world position × `_Tiling`, `_Rotation`, `_Color`, `_NormalTexture` × `_NormalStrength`, `_Smoothness`, `_Metallic`.
  - Log a material's inputs with `shader.GetPropertyCount / GetPropertyName / GetPropertyType`.
- **Player properties:** `Property.Properties`. Each has `propertyBoundsColliders` and `DoBoundsContainPoint(p)`.

## 3. Map changes are map STATES

This is how live-service games change maps mid-game (Warzone's Verdansk '84, Battlefield's Levolution, Geo-Mod's pre-broken meshes): build the changed version ahead, swap it in at a story beat, swap back to restore.

- **Terrain:** `Object.Instantiate(terrainData)` is the copy. Edit the copy, then at the swap the Terrain **and** its TerrainCollider take it. Swapping back returns the untouched original.
- **Surfaces a crack crosses:** hide the original renderer and show a rebuilt copy (section 4). The originals are never destroyed.
- **Bake the result** (the No Man's Sky approach: edits stored as data). The crack network and every rebuilt surface go to disk on the first build and are read back after that. Before baking, a 30-130 s build ran every load, and the network came out different between loads because the walk's ground rays hit parked cars.

## 4. Rebuilding a surface with a hole in it

What failed, in order (don't repeat these):

1. **Flat strips laid on the road:** read as blocky tape.
2. **Stencil-hole overlay:** read as an overlay.
3. **Copies of the game's Lit shader with a clip:** fine for Lit, but roads and concrete are Shader Graphs. The rebuilt graph copies drew nothing, so whole tiles vanished.
4. **Whole-tile copies:** worked, with jagged outlines at 4 cm capture and mismatched cell sizes.
5. **Screen-space hole (mark visible pixels, reset depth, redraw a band):** sky showed in the hole, then the hole showed through walls. That was deferred G-buffer ordering plus stencil override (section 1).

What works:

- **Replace the affected tile with a solid copy** drawn with the tile's **own material** (any shader), layer, shadows and lightmap. It goes through the normal pipeline, so there is nothing to see through.
- **Adaptive quadtree, conforming:**
  - big cells (≤ 1 m) where nothing changes
  - one uniform small size (5 cm) everywhere near the crack. Mixed sizes leave T-junction gaps on any lowered area.
  - weld shared corners
- **Pin corners to real edges:**
  - Corners that fall inside the crack **snap onto the crack's edge line**. Dropping whole cells leaves stair-steps.
  - Corners that fall off the surface are bisected back onto its real outline.
  - Only snap to a *real outside* edge: at bends, one stretch's edge runs inside its neighbour.
- **Rolled lip:** within 10 cm of the edge the surface drops on a quarter circle, normals bent to match, so light rolls over the edge.
- **Replace the walkable collider too.** Rebuild it as cells read off the original collider (`collider.Raycast` per cell) minus the opening. Switching a whole tile's collider off left walk-through holes.

## 5. Cracks: networks, junctions, trenches, fill

- **Real cracks don't cross.**
  - A newer crack that reaches an older one stops there at about a right angle: a T-junction.
  - Cracks that start together meet at about 120°: a Y-junction.
  - Physics Today on mud cracks; Goehring, arXiv 1211.6762.
  - So the walk marks cells with its crack id and ends where it meets another.
- **Converging channels are ONE field, not separate tubes.** Model the trench as a single depth field, where each point takes the deepest crack covering it (a union, as in SDF and heightfield work). Converging cracks then merge into one pit with no walls between them. Separate tubes left walls running across the ruts.
  - The rim sits on the same edge line as the rolled lip, at the lip's depth, so surface and trench meet with no seam.
  - Faces up only, so nothing is visible from below or outside.
- **Liquid in a crack is a level:** glowing wherever the floor is below it, so it's continuous and pools at junctions. Per-segment strips left gaps.
- **Each ground breaks its own way:**
  - Asphalt: ragged edge, en echelon side cracks, exposed courses: 40 mm surface + 60 mm binder + 120 mm base, then crushed stone, then soil.
  - Concrete: angular, with slabs that heave.
  - Grass: steep root-bound walls, torn turf lips.
  - Dirt: a slumped V.
- **Protect player bases:** treat every property's bounds (+2 m) as a wall, so the crack walk turns away.

## 6. Performance traps hit

- **Distance queries in mesh builders:** checking every outline piece for each of ~640k lip vertices froze the load for minutes. Index samples in 1 m cells and measure only the neighbours.
- **The "real outside edge" test:** testing every candidate against every quad took 117 s. Sort candidates nearest first and stop at the first valid one.
- **Edge-search scans:** a `FindObjectsOfType` scan on a timer costs ~60 ms a hit. Find once per load.

## 7. Process rules that saved rounds

- Log a survey before designing: the pipeline, a material's inputs, terrain resolution, what's readable.
- After a guess fails once, build the diagnostic instead of guessing twice.
- Gate log waits on the log file being newer than the launch.
- Every geometry-format change bumps the bake version.
