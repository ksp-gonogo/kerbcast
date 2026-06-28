// Headless preview renderer for the kerbcast FX shaders.
//
// Invoked from CI by:
//   "$UNITY_EDITOR_PATH" -batchmode -quit \
//      -projectPath . \
//      -executeMethod KerbcastCI.RenderFxPreviews.RenderAll \
//      -buildTarget Linux64 -logFile -
//
// For each *.json under ci/kerbcast-shaders/Fixtures/, and for each of the
// four FX shaders (Plasma/Core, Bowshock, Trail, Ember), and for each of
// that shader's camera viewpoints (see ViewsFor), render the shader on a
// proxy vessel and save a PNG keyed {fixture}_{shader}_{view}.png. Plus a
// ramp_bowshock_q* fade strip sweeping the shared q ramp at mach 8.
//
// Silhouette measurement, mesh placement, intensity ramps, and the
// procedural meshes all come from the SHARED FX core (Assets/Editor/Shared
// is a symlink to Plugin/Kerbcast/Fx/Core) — the same code the plugin runs
// in-game, so these renders test it rather than a hand-mirrored copy.
// Per-shader scene setup mirrors the runtime draw path:
// CommandBuffer.DrawRenderer on proxy renderers for plasma/ember,
// procedural dome ahead of the vessel for bowshock, procedural tapered
// tube astern for trail.

using System.Collections.Generic;
using System.IO;
using Kerbcast;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace KerbcastCI
{
    public static class RenderFxPreviews
    {
        private const int _outWidth = 1024;
        private const int _outHeight = 576;
        private const string _fixturesDir = "Fixtures";
        private const string _outputDir = "Previews";
        private const float _previewTubeRadius = 0.6f; // m, preview trail mesh natural radius

        private static readonly string[] _shaderIds = { "plasma", "bowshock", "trail", "ember" };
        // Per-shader viewpoints. The previous shared external/nose_up/body_out
        // (and later side_profile/aft/forward) sets framed the vessel, not the
        // FX — the bowshock dome sits past the nose along the wind axis and
        // the wake extends tens of metres astern, so both fell partly or
        // wholly outside the frustum. Each shader now gets views aimed at the
        // FX anchor computed from the wind direction + windward profile, so
        // the sideways/diagonal wind fixtures frame correctly too.
        //
        //   side_profile       — vessel-centred side view (LLVMpipe-proven
        //                        3.5 m; do not pull back, see 13a5697)
        //   aft/forward_hullcam— body-mounted hullcam views (unchanged)
        //   dome_side/3q       — aimed at the bowshock dome centre
        //   wake_side/3q       — aimed a few metres down the trail tube
        //   ember_three_quarter— leeward three-quarter on the ember shed
        private static string[] ViewsFor(string shaderId)
        {
            switch (shaderId)
            {
                case "bowshock": return new[] { "dome_side", "dome_three_quarter", "forward_hullcam" };
                case "trail":    return new[] { "wake_side", "wake_three_quarter", "aft_hullcam" };
                case "ember":    return new[] { "side_profile", "ember_three_quarter", "aft_hullcam" };
                default:         return new[] { "side_profile", "forward_hullcam", "aft_hullcam" };
            }
        }

        public static void RenderAll()
        {
            if (!Directory.Exists(_outputDir)) Directory.CreateDirectory(_outputDir);

            var fixtureFiles = Directory.GetFiles(_fixturesDir, "*.json");
            System.Array.Sort(fixtureFiles);
            Debug.Log($"[Kerbcast-CI] RenderFxPreviews: {fixtureFiles.Length} fixture(s), {_shaderIds.Length} shader(s), 3 views each");

            var plasmaShader = Shader.Find("Kerbcast/Plasma");
            var bowshockShader = Shader.Find("Kerbcast/Bowshock");
            var trailShader = Shader.Find("Kerbcast/Trail");
            var emberShader = Shader.Find("Kerbcast/Ember");
            if (plasmaShader == null || bowshockShader == null || trailShader == null || emberShader == null)
            {
                Debug.LogError("[Kerbcast-CI] one or more Kerbcast shaders not found — did the bundle compile?");
                EditorApplication.Exit(2);
                return;
            }

            foreach (var path in fixtureFiles)
            {
                var json = File.ReadAllText(path);
                var fx = JsonUtility.FromJson<FxFixture>(json);
                if (fx == null || string.IsNullOrEmpty(fx.name))
                {
                    Debug.LogWarning($"[Kerbcast-CI] skipping unparseable fixture: {path}");
                    continue;
                }
                var fixtureDir = Path.GetDirectoryName(path);
                foreach (var shaderId in _shaderIds)
                {
                    Shader sh =
                        shaderId == "plasma"   ? plasmaShader :
                        shaderId == "bowshock" ? bowshockShader :
                        shaderId == "trail"    ? trailShader :
                        emberShader;
                    foreach (var viewId in ViewsFor(shaderId))
                    {
                        /* Body-mounted plasma hullcams with non-axial wind
                           put full-length strips THROUGH the camera plane —
                           giant clipped triangles hang LLVMpipe outright,
                           at any resolution (runs 27264102844, 27265194905).
                           Broadside plasma shape is judged from
                           side_profile, which renders fine at half res. */
                        bool nonAxialWind = Mathf.Abs(Vector3.Dot(
                            WindDirFromInputs(fx.inputs), Vector3.up)) < 0.99f;
                        if (shaderId == "plasma" && nonAxialWind && viewId.EndsWith("_hullcam"))
                        {
                            Debug.Log($"[Kerbcast-CI]   SKIP  {fx.name}/{shaderId}/{viewId} (LLVMpipe hang: strips cross hullcam plane in non-axial wind)");
                            continue;
                        }
                        Debug.Log($"[Kerbcast-CI]   begin {fx.name}/{shaderId}/{viewId}");
                        try
                        {
                            RenderOne(fx, shaderId, sh, viewId, fixtureDir);
                            Debug.Log($"[Kerbcast-CI]   end   {fx.name}/{shaderId}/{viewId} OK");
                        }
                        catch (System.Exception e)
                        {
                            Debug.LogError($"[Kerbcast-CI]   end   {fx.name}/{shaderId}/{viewId} FAILED: {e.GetType().Name}: {e.Message}");
                        }
                    }
                }
            }
            // q-sweep fade strip: renders the bowshock at mach 8 (mach ramp
            // saturated, like real reentry) across the q ramp, so the
            // fade-in behaviour is VIEWABLE — each frame's intensity comes
            // from the shared FxRamps.Bowshock, the exact curve the plugin
            // runs in-game. A binary q gate shows up here as one black
            // frame followed by a full-brightness one (the reentry pop-in).
            foreach (float q in new[] { 0.1f, 0.35f, 0.6f, 0.85f, 1.1f, 1.5f })
            {
                float intensity = FxRamps.Bowshock(8f, q);
                var rampFx = new FxFixture
                {
                    name = $"ramp_bowshock_q{q:0.00}",
                    inputs = new FxFixture.Inputs
                    {
                        intensity = intensity,
                        fxState = 1f,
                        windDirWorld = new[] { 0f, 1f, 0f, 0f },
                    },
                };
                Debug.Log($"[Kerbcast-CI]   begin {rampFx.name} (intensity={intensity:F2})");
                try
                {
                    RenderOne(rampFx, "bowshock", bowshockShader, "dome_side", _fixturesDir);
                    Debug.Log($"[Kerbcast-CI]   end   {rampFx.name} OK");
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[Kerbcast-CI]   end   {rampFx.name} FAILED: {e.GetType().Name}: {e.Message}");
                }
            }

            Debug.Log("[Kerbcast-CI] RenderFxPreviews: done");
        }

        // Single (fixture, shader, view) render. New scene root per render so
        // state can't leak between passes.
        private static void RenderOne(FxFixture fx, string shaderId, Shader shader, string viewId, string fixtureDir)
        {
            var sceneRoot = new GameObject($"__fx_preview_{fx.name}_{shaderId}_{viewId}");
            try
            {
                BuildProxyVessel(sceneRoot.transform);
                AddDirectionalLight(sceneRoot.transform);

                // Wind direction + silhouette drive both the FX mesh
                // placement and the FX-anchored camera views. Computed BEFORE
                // any FX renderer is parented under the root so the silhouette
                // only measures the proxy vessel.
                Vector3 windDir = WindDirFromInputs(fx.inputs);
                var profile = ComputeSilhouette(sceneRoot.transform, windDir);

                var camGo = new GameObject("__fx_preview_camera");
                camGo.transform.SetParent(sceneRoot.transform, false);
                ApplyCameraPose(camGo.transform, fx.camera, viewId, windDir, profile);
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.05f, 0.07f, 0.12f, 1f);
                cam.fieldOfView = fx.camera != null && fx.camera.fov > 0f ? fx.camera.fov : 60f;
                cam.nearClipPlane = fx.camera != null && fx.camera.near > 0f ? fx.camera.near : 0.3f;
                cam.farClipPlane = fx.camera != null && fx.camera.far > 0f ? fx.camera.far : 200f;
                cam.allowMSAA = false;
                cam.allowHDR = true;

                ApplyGlobals(fx.globals, fixtureDir, fx.textures);

                // Windward depth prepass — AFTER ApplyGlobals so it overrides
                // the fixture's placeholder depth map + matrices with a real
                // render of the proxy vessel from upwind (what KSP's FXCamera
                // publishes in-game). Without it the plasma wrap term — the
                // white wind sheath hugging the windward surfaces — is zero
                // in every preview. Only runs when no fixture-provided depth
                // texture exists. Must render BEFORE FX objects are added to
                // the scene so it measures the proxy alone.
                RenderTexture depthRt = null;
                if (fx.textures == null || string.IsNullOrEmpty(fx.textures.fxDepthMap))
                {
                    depthRt = RenderWindwardDepth(sceneRoot.transform, windDir, profile);
                }

                Material mat = new Material(shader);
                ApplyMaterialInputs(mat, fx.inputs, shaderId);

                // Shader-specific scene attachments.
                switch (shaderId)
                {
                    case "plasma": SetupPlasma(sceneRoot.transform, cam, mat); break;
                    case "bowshock": SetupBowshock(sceneRoot.transform, mat, windDir, profile); break;
                    case "trail": SetupTrail(sceneRoot.transform, mat, windDir, profile, viewId); break;
                    case "ember": SetupEmber(sceneRoot.transform, cam, mat, fx.inputs); break;
                }

                /* Non-axial-wind plasma renders fill the whole frame with
                   full-length strips from EVERY view (the wind crosses the
                   camera axis, so strips sweep the frame edge-to-edge) —
                   LLVMpipe's pathological case, and at real-part proxy
                   tessellation it hangs the runner outright (one view
                   confirmed hung per run: first side_profile, then
                   forward_hullcam). Half resolution quarters the fill; these
                   views judge shape (drag direction, comb, envelope), not
                   pixel detail. */
                bool lowRes = shaderId == "plasma"
                              && Mathf.Abs(Vector3.Dot(windDir, Vector3.up)) < 0.99f;
                int w = lowRes ? _outWidth / 2 : _outWidth;
                int h = lowRes ? _outHeight / 2 : _outHeight;

                // Render to RT
                var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32)
                {
                    name = $"FxPreviewRT_{fx.name}_{shaderId}_{viewId}",
                    antiAliasing = 1
                };
                cam.targetTexture = rt;
                cam.Render();

                // Read back + encode
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;

                var outName = $"{fx.name}_{shaderId}_{viewId}.png";
                var outPath = Path.Combine(_outputDir, outName);
                File.WriteAllBytes(outPath, tex.EncodeToPNG());

                cam.targetTexture = null;
                Object.DestroyImmediate(tex);
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(mat);
                if (depthRt != null)
                {
                    Shader.SetGlobalTexture("_FXDepthMap", null);
                    Object.DestroyImmediate(depthRt);
                }
            }
            finally
            {
                Object.DestroyImmediate(sceneRoot);
            }
        }

        // ------------------------------------------------------------------
        // Shader-specific scene setups
        // ------------------------------------------------------------------

        // Plasma: apply material via CommandBuffer.DrawRenderer on the proxy
        // vessel's renderers, attached at AfterForwardAlpha — the runtime path.
        private static void SetupPlasma(Transform root, Camera cam, Material mat)
        {
            var cb = new CommandBuffer { name = "Kerbcast Preview FX Plasma" };
            foreach (var rend in root.GetComponentsInChildren<Renderer>())
            {
                if (rend == null || rend is ParticleSystemRenderer) continue;
                int subMeshes = rend.sharedMaterials != null ? rend.sharedMaterials.Length : 1;
                if (subMeshes < 1) subMeshes = 1;
                for (int s = 0; s < subMeshes; s++) cb.DrawRenderer(rend, mat, s);
            }
            cam.AddCommandBuffer(CameraEvent.AfterForwardAlpha, cb);
        }

        // Bowshock: a procedural oblate dome (flattened hemisphere) ahead
        // of the vessel along the wind axis. The DOME mesh better matches
        // real bowshock physics (blunt-body detached shock = flat dome)
        // than the previous cone shape. Size and position are computed
        // from the proxy vessel's windward profile so the shock adapts to
        // vessel orientation: when wind blows broadside, the dome becomes
        // wider and closer to the vessel; when wind is end-on, the dome
        // is narrower and further forward. Same logic mirrored on the
        // runtime in BowshockEffect.
        private static void SetupBowshock(Transform root, Material mat, Vector3 windDir, FxSilhouette profile)
        {
            // Placement + mesh come from the SHARED FX core — the same code
            // BowshockEffect runs in-game, so this render tests it.
            var pose = FxPlacement.Bowshock(profile, windDir, root.position);

            var go = new GameObject("bowshock_dome");
            go.transform.SetParent(root, false);
            go.transform.position = pose.Position;
            go.transform.rotation = pose.Rotation;
            go.transform.localScale = pose.Scale;
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mf.sharedMesh = FxMeshes.BuildDome();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        // Trail: procedural tapered tube behind the vessel along airflow.
        // axis +Z = downstream. The mesh's natural size is 0.6 m × 40 m;
        // we apply a NON-UNIFORM scale: the tube's start radius is scaled
        // to match the vessel's windward radius (so the wake's head
        // matches the vessel's profile), then a small overlap pulls the
        // head INSIDE the vessel so the cylinder occludes the top edge.
        // Mirrored on runtime in TrailEffect.
        private static void SetupTrail(Transform root, Material mat, Vector3 windDir, FxSilhouette profile, string viewId)
        {
            // Placement comes from the SHARED FX core (same code as
            // TrailEffect in-game). The preview tube is narrower (0.6 m)
            // and longer (40 m) than the runtime's 4 m × 20 m so emergence
            // is visible, but FxPlacement.Trail normalises by the natural
            // radius — world-space wake size is identical either way.
            var pose = FxPlacement.Trail(profile, windDir, root.position, _previewTubeRadius);

            var go = new GameObject("trail_tube");
            go.transform.SetParent(root, false);
            go.transform.position = pose.Position;
            go.transform.rotation = pose.Rotation;
            // PREVIEW-ONLY cap on top of the shared placement: the close
            // aft_hullcam camera sits about 0.9 m from the trail head, so a
            // tube wider than ~0.6 m fills the near plane and LLVMpipe
            // hangs on the near-fullscreen additive triangles. Pulled-back
            // wake views render the uncapped shared scale.
            Vector3 scale = pose.Scale;
            if (viewId == "aft_hullcam")
            {
                scale.x = Mathf.Min(scale.x, 1f);
                scale.y = Mathf.Min(scale.y, 1f);
            }
            go.transform.localScale = scale;
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mf.sharedMesh = FxMeshes.BuildTaperedTube(_previewTubeRadius, 40f, 32, 24);
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        // Ember: same CB.DrawRenderer pattern as plasma — the new ember
        // shader has its own geom stage that filters windward triangles
        // and emits camera-aligned spark quads along the airflow
        // extrusion. Sparks shed FROM the vessel's heated surfaces (where
        // ablation physically happens) rather than spawning from an
        // abstract perpendicular disc. Replaces the old quad-mesh
        // pre-baking and ParticleSystem approaches.
        private static void SetupEmber(Transform root, Camera cam, Material mat, FxFixture.Inputs inputs)
        {
            var cb = new CommandBuffer { name = "Kerbcast Preview FX Ember" };
            foreach (var rend in root.GetComponentsInChildren<Renderer>())
            {
                if (rend == null || rend is ParticleSystemRenderer) continue;
                int subMeshes = rend.sharedMaterials != null ? rend.sharedMaterials.Length : 1;
                if (subMeshes < 1) subMeshes = 1;
                for (int s = 0; s < subMeshes; s++) cb.DrawRenderer(rend, mat, s);
            }
            cam.AddCommandBuffer(CameraEvent.AfterForwardAlpha, cb);
        }

        // ------------------------------------------------------------------
        // Proxy vessel + camera viewpoints
        // ------------------------------------------------------------------

        private static void BuildProxyVessel(Transform root)
        {
            var body = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            body.transform.SetParent(root, false);
            body.transform.localPosition = Vector3.zero;
            body.transform.localScale = new Vector3(0.6f, 1.2f, 0.6f);
            var nose = MakeCone();
            nose.transform.SetParent(root, false);
            nose.transform.localPosition = new Vector3(0f, 1.5f, 0f);
            nose.transform.localScale = new Vector3(0.6f, 0.6f, 0.6f);
        }

        private static void AddDirectionalLight(Transform root)
        {
            var lightGo = new GameObject("__fx_preview_light");
            lightGo.transform.SetParent(root, false);
            lightGo.transform.rotation = Quaternion.Euler(45f, -30f, 0f);
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            light.color = new Color(1f, 0.96f, 0.9f, 1f);
        }

        // Camera presets. The body-mounted hullcam views and the 3.5 m
        // side_profile are unchanged (LLVMpipe-proven — pulling side_profile
        // back to even 5 m makes the plasma extrusion take minutes/render).
        // The dome_/wake_/ember_ views are FX-anchored: they aim at where
        // the effect actually is, computed from the same windward profile
        // the mesh placement uses, so they stay framed across the sideways
        // and diagonal wind fixtures.
        private static void ApplyCameraPose(Transform t, FxFixture.CameraPose pose, string viewId,
            Vector3 windDir, FxSilhouette profile)
        {
            // A stable direction perpendicular to the wind axis — the "side"
            // the side/three-quarter views shoot from.
            Vector3 perpHelper = Mathf.Abs(windDir.y) < 0.99f ? Vector3.up : Vector3.forward;
            Vector3 perp = Vector3.Cross(windDir, perpHelper).normalized;

            // Camera anchors from the SHARED placement (root at origin), so
            // the views stay aimed even if the placement maths changes.
            var domePose = FxPlacement.Bowshock(profile, windDir, Vector3.zero);
            var wakePose = FxPlacement.Trail(profile, windDir, Vector3.zero, _previewTubeRadius);
            Vector3 domeCentre = domePose.Position + windDir * (domePose.Scale.z * 0.5f);
            Vector3 wakeHead = wakePose.Position;
            float domeDist = Mathf.Max(4.5f, domePose.Scale.x * 3.5f);

            switch (viewId)
            {
                case "side_profile":
                    // Perpendicular to the WIND, not a fixed +X — with a
                    // sideways/diagonal wind the fixed position looked
                    // straight down the wind axis and the plasma drag shape
                    // was invisible. Distance stays at the LLVMpipe-proven
                    // 3.5 m. For the vertical-wind fixtures perp = +X, i.e.
                    // exactly the old camera.
                    PlaceLookAt(t, Vector3.zero, perp * 3.5f);
                    return;
                case "aft_hullcam":
                    t.localPosition = new Vector3(0.85f, -0.5f, 0.3f);
                    // LookRotation up CANNOT be world-up here because the
                    // direction is mostly along -Y (degenerate). Use +Z as
                    // the helper up so the image "up" is the vessel's +Z.
                    t.localRotation = Quaternion.LookRotation(
                        new Vector3(-0.5f, -1.6f, 0f).normalized, Vector3.forward);
                    return;
                case "forward_hullcam":
                    t.localPosition = new Vector3(0.85f, 0.8f, 0.3f);
                    t.localRotation = Quaternion.LookRotation(
                        new Vector3(-0.5f, 1.6f, 0f).normalized, Vector3.forward);
                    return;
                case "dome_side":
                    // Square-on to the dome's silhouette — shows the rim arc
                    // and how the base sits against the nose.
                    PlaceLookAt(t, domeCentre, perp * domeDist);
                    return;
                case "dome_three_quarter":
                    // From the windward side at 45° — shows the curved face
                    // and the dome/vessel standoff in depth.
                    PlaceLookAt(t, domeCentre, (perp + windDir).normalized * domeDist);
                    return;
                case "wake_side":
                    // Square-on a few metres down the tube — head emergence
                    // from the hull plus the early taper in one frame.
                    PlaceLookAt(t, wakeHead - windDir * 4f, perp * 9f);
                    return;
                case "wake_three_quarter":
                    // From astern-and-side looking back up the tube at the
                    // vessel — the view a chase cam gets of the wake.
                    PlaceLookAt(t, wakeHead - windDir * 2f, (perp * 0.7f - windDir * 0.7f).normalized * 9f);
                    return;
                case "ember_three_quarter":
                    // Leeward three-quarter — embers shed downwind off the
                    // heated surfaces, so frame the vessel plus its lee side.
                    PlaceLookAt(t, -windDir * 1f, (perp - windDir * 0.8f).normalized * 4.5f);
                    return;
                default:
                    if (pose == null) { t.localPosition = new Vector3(3.5f, 0f, 0f); t.LookAt(Vector3.zero, Vector3.up); return; }
                    t.localPosition = ToVec3(pose.position, new Vector3(3.5f, 0f, 0f));
                    if (pose.rotation != null && pose.rotation.Length >= 4)
                        t.localRotation = new Quaternion(pose.rotation[0], pose.rotation[1], pose.rotation[2], pose.rotation[3]);
                    else t.LookAt(Vector3.zero, Vector3.up);
                    return;
            }
        }

        private static void PlaceLookAt(Transform t, Vector3 target, Vector3 offset)
        {
            t.localPosition = target + offset;
            Vector3 dir = (target - t.localPosition).normalized;
            Vector3 up = Mathf.Abs(Vector3.Dot(dir, Vector3.up)) > 0.95f ? Vector3.forward : Vector3.up;
            t.localRotation = Quaternion.LookRotation(dir, up);
        }

        // ------------------------------------------------------------------
        // Material uniforms + global state
        // ------------------------------------------------------------------

        private static void ApplyMaterialInputs(Material mat, FxFixture.Inputs inputs, string shaderId)
        {
            if (inputs == null) return;
            mat.SetFloat("_Intensity", inputs.intensity);
            // Plasma + ember both consume _FxState for the
            // Condensation→Reentry colour blend. Plasma additionally has
            // _FxRadiusMul for lateral spread.
            if (shaderId == "plasma" || shaderId == "ember")
            {
                mat.SetFloat("_FxState", inputs.fxState);
            }
            if (shaderId == "plasma")
            {
                mat.SetFloat("_FxRadiusMul", inputs.fxRadiusMul > 0f ? inputs.fxRadiusMul : 1.6f);
            }
            mat.SetVector("_WindDirWorld", ToVec4(inputs.windDirWorld, new Vector4(0f, 1f, 0f, 0f)));
        }

        private static void ApplyGlobals(FxFixture.Globals g, string fixtureDir, FxFixture.Textures textures)
        {
            if (g != null)
            {
                Shader.SetGlobalVector("_LightDirection0", ToVec4(g.lightDirection0, new Vector4(0f, -1f, 0f, 0f)));
                Shader.SetGlobalVector("_FXColor", ToVec4(g.fxColor, new Vector4(1f, 0.5f, 0.2f, 1f)));
                Shader.SetGlobalFloat("_FxLength", g.fxLength);
                Shader.SetGlobalFloat("_FXWobble", g.fxWobble);
                Shader.SetGlobalFloat("_FXFalloff", g.fxFalloff);
                Shader.SetGlobalMatrix("_FXDepthCamMatrix", ToMat4(g.fxDepthCamMatrix, Matrix4x4.identity));
                Shader.SetGlobalMatrix("_FXDepthProjMatrix", ToMat4(g.fxDepthProjMatrix, Matrix4x4.identity));
                Shader.SetGlobalFloat("_FXProjectionNear", g.fxProjectionNear > 0f ? g.fxProjectionNear : 0.5f);
                Shader.SetGlobalFloat("_FXProjectionFar", g.fxProjectionFar > 0f ? g.fxProjectionFar : 80f);
            }
            Shader.SetGlobalTexture("_FXMainTex",
                LoadOrPlaceholder(textures != null ? textures.fxMainTex : null, fixtureDir, MakeNoiseTexture));
            Shader.SetGlobalTexture("_FXDepthMap",
                LoadOrPlaceholder(textures != null ? textures.fxDepthMap : null, fixtureDir, MakeFlatDepthTexture));
        }

        private static Texture2D LoadOrPlaceholder(string relPath, string fixtureDir, System.Func<Texture2D> fallback)
        {
            if (!string.IsNullOrEmpty(relPath))
            {
                var abs = Path.Combine(fixtureDir, relPath);
                if (File.Exists(abs))
                {
                    var bytes = File.ReadAllBytes(abs);
                    var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (t.LoadImage(bytes)) return t;
                    Debug.LogWarning($"[Kerbcast-CI] failed to load image: {abs}");
                }
                else Debug.LogWarning($"[Kerbcast-CI] missing texture file: {abs}");
            }
            return fallback();
        }

        private static Texture2D MakeNoiseTexture()
        {
            const int size = 256;
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "PlaceholderFXMainTex" };
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float n = Mathf.PerlinNoise(x * 0.04f, y * 0.04f);
                float streak = Mathf.PerlinNoise(x * 0.005f, y * 0.18f);
                float v = Mathf.Clamp01(0.4f * n + 0.7f * streak);
                px[y * size + x] = new Color(v, v, v, 1f);
            }
            t.SetPixels(px);
            t.Apply();
            return t;
        }

        private static Texture2D MakeFlatDepthTexture()
        {
            var t = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = "PlaceholderFXDepthMap" };
            var px = new Color[16];
            for (int i = 0; i < 16; i++) px[i] = Color.white;
            t.SetPixels(px);
            t.Apply();
            return t;
        }

        // ------------------------------------------------------------------
        // Mesh builders (proxies)
        // ------------------------------------------------------------------

        /* Ringed cone at real-part tessellation. The old 12-segment cone
           shared one apex vertex across 12 giant triangles, so every
           forward view showed 12 cartoon wedges radiating from the tip —
           an artifact real KSP parts (~32-48 radial segments, ringed)
           never produce, which made along-axis strip artifacts impossible
           to evaluate in CI. Triangle count is the LLVMpipe fill-cost
           driver (strip area is per-triangle, not per-edge-length), so
           keep seg*rings modest. */
        private static GameObject MakeCone()
        {
            var go = new GameObject("nose_cone");
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            var std = Shader.Find("Standard");
            if (std != null) mr.sharedMaterial = new Material(std);
            const int seg = 24;
            const int rings = 2;
            const float r = 1f;
            const float h = 1.5f;
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            // Side rings: radius tapers linearly to a small tip ring, then a
            // single apex vertex closes it. seg+1 verts per ring so the UV
            // seam wraps cleanly.
            for (int j = 0; j <= rings; j++)
            {
                float v = j / (float)rings;
                float rr = Mathf.Lerp(r, 0.06f, v);
                float y = v * h;
                for (int i = 0; i <= seg; i++)
                {
                    float u = i / (float)seg;
                    float a = u * Mathf.PI * 2f;
                    verts.Add(new Vector3(Mathf.Cos(a) * rr, y, Mathf.Sin(a) * rr));
                    uvs.Add(new Vector2(u, v));
                }
            }
            for (int j = 0; j < rings; j++)
            {
                int row0 = j * (seg + 1), row1 = (j + 1) * (seg + 1);
                for (int i = 0; i < seg; i++)
                {
                    tris.Add(row0 + i); tris.Add(row1 + i); tris.Add(row1 + i + 1);
                    tris.Add(row0 + i); tris.Add(row1 + i + 1); tris.Add(row0 + i + 1);
                }
            }
            int apex = verts.Count;
            verts.Add(new Vector3(0f, h, 0f));
            uvs.Add(new Vector2(0.5f, 1f));
            int tipRow = rings * (seg + 1);
            for (int i = 0; i < seg; i++)
            {
                tris.Add(tipRow + i); tris.Add(apex); tris.Add(tipRow + i + 1);
            }
            int baseCentre = verts.Count;
            verts.Add(Vector3.zero);
            uvs.Add(new Vector2(0.5f, 0f));
            for (int i = 0; i < seg; i++)
            {
                tris.Add(baseCentre); tris.Add(i + 1); tris.Add(i);
            }
            var mesh = new Mesh { name = "nose_cone_mesh" };
            mesh.vertices = verts.ToArray();
            mesh.uv = uvs.ToArray();
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            mf.sharedMesh = mesh;
            return go;
        }

        // Silhouette of the proxy vessel: collect renderer-AABB corners
        // relative to the root and hand them to the SHARED FxSilhouette
        // (Assets/Editor/Shared → Plugin/Kerbcast/Fx/Core, symlinked) — the
        // exact code the plugin runs in-game, so these renders test it.
        private static FxSilhouette ComputeSilhouette(Transform root, Vector3 windDir)
        {
            var corners = new List<Vector3>();
            Vector3 origin = root.position;
            foreach (var rend in root.GetComponentsInChildren<Renderer>())
            {
                if (rend == null || rend is ParticleSystemRenderer) continue;
                var b = rend.bounds;
                Vector3 c = b.center, e = b.extents;
                for (int i = 0; i < 8; i++)
                {
                    corners.Add(c - origin + new Vector3(
                        (i & 1) == 0 ? -e.x : e.x,
                        (i & 2) == 0 ? -e.y : e.y,
                        (i & 4) == 0 ? -e.z : e.z));
                }
            }
            return FxSilhouette.FromCorners(corners, windDir);
        }

        // Render the proxy vessel's depth as seen from upwind into an RT and
        // publish it (plus the matching matrices/planes) as the _FXDepthMap
        // globals — the same contract KSP's FXCamera fulfils in-game. The
        // plasma shader's wrapFromDepthMap converts (fragmentDepth - sampled)
        // × (far - near) into metres downstream of the windward surface, so
        // both this depth encoding and _FXProjectionNear/Far must be linear01
        // over the same range (the CIDepth replacement shader guarantees it).
        // Orthographic — only relative depth matters, and ortho keeps the
        // linear01 encoding exact across the frame.
        private static RenderTexture RenderWindwardDepth(Transform root, Vector3 windDir, FxSilhouette profile)
        {
            var depthShader = Shader.Find("Kerbcast/CIDepth");
            if (depthShader == null)
            {
                Debug.LogWarning("[Kerbcast-CI] Kerbcast/CIDepth shader missing — wind wrap layer will be absent");
                return null;
            }

            const float near = 0.5f;
            const float far = 80f;
            var rt = new RenderTexture(256, 256, 24, RenderTextureFormat.ARGBFloat)
            {
                name = "FxPreviewDepthRT",
                antiAliasing = 1,
                filterMode = FilterMode.Bilinear,
            };

            var camGo = new GameObject("__fx_depth_camera");
            camGo.transform.SetParent(root, false);
            try
            {
                float upwind = profile.ForwardStandoff + 5f;
                camGo.transform.position = root.position + windDir * upwind;
                Vector3 helperUp = Mathf.Abs(windDir.y) < 0.99f ? Vector3.up : Vector3.forward;
                camGo.transform.rotation = Quaternion.LookRotation(-windDir, helperUp);

                var cam = camGo.AddComponent<Camera>();
                cam.enabled = false;
                cam.orthographic = true;
                cam.orthographicSize = Mathf.Max(profile.RadiusMajor * 1.6f,
                    (profile.ForwardStandoff + profile.AftStandoff) * 0.75f);
                cam.nearClipPlane = near;
                cam.farClipPlane = far;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.white; // far plane = "nothing upwind here"
                cam.targetTexture = rt;
                cam.RenderWithShader(depthShader, "");
                cam.targetTexture = null;

                Shader.SetGlobalTexture("_FXDepthMap", rt);
                Shader.SetGlobalMatrix("_FXDepthCamMatrix", cam.worldToCameraMatrix);
                Shader.SetGlobalMatrix("_FXDepthProjMatrix", cam.projectionMatrix);
                Shader.SetGlobalFloat("_FXProjectionNear", near);
                Shader.SetGlobalFloat("_FXProjectionFar", far);
                return rt;
            }
            finally
            {
                Object.DestroyImmediate(camGo);
            }
        }

        private static Vector3 WindDirFromInputs(FxFixture.Inputs inputs)
        {
            Vector3 windDir = inputs != null && inputs.windDirWorld != null && inputs.windDirWorld.Length >= 3
                ? new Vector3(inputs.windDirWorld[0], inputs.windDirWorld[1], inputs.windDirWorld[2])
                : Vector3.up;
            if (windDir.sqrMagnitude < 1e-3f) windDir = Vector3.up;
            return windDir.normalized;
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static Vector3 ToVec3(float[] a, Vector3 fallback)
        {
            if (a == null || a.Length < 3) return fallback;
            return new Vector3(a[0], a[1], a[2]);
        }

        private static Vector4 ToVec4(float[] a, Vector4 fallback)
        {
            if (a == null || a.Length < 4) return fallback;
            return new Vector4(a[0], a[1], a[2], a[3]);
        }

        private static Matrix4x4 ToMat4(float[] a, Matrix4x4 fallback)
        {
            if (a == null || a.Length < 16) return fallback;
            var m = new Matrix4x4();
            for (int i = 0; i < 16; i++) m[i / 4, i % 4] = a[i];
            return m;
        }
    }
}
