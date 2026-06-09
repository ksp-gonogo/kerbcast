// Headless preview renderer for the kerbcam FX shaders.
//
// Invoked from CI by:
//   "$UNITY_EDITOR_PATH" -batchmode -quit \
//      -projectPath . \
//      -executeMethod KerbcamCI.RenderFxPreviews.RenderAll \
//      -buildTarget Linux64 -logFile -
//
// For each *.json under ci/kerbcam-shaders/Fixtures/, and for each of the
// four FX shaders (Plasma/Core, Bowshock, Trail, Ember), and for each of
// the three camera viewpoints (external, nose_up, body_out), render the
// shader on a proxy vessel and save a PNG keyed
// {fixture}_{shader}_{view}.png. Per-shader scene setup mirrors what the
// plugin does at runtime: CommandBuffer.DrawRenderer on proxy renderers
// for plasma, procedural cone ahead of vessel for bowshock, procedural
// tapered tube behind vessel for trail, ParticleSystem for embers.

using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace KerbcamCI
{
    public static class RenderFxPreviews
    {
        private const int _outWidth = 1024;
        private const int _outHeight = 576;
        private const string _fixturesDir = "Fixtures";
        private const string _outputDir = "Previews";

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
            Debug.Log($"[Kerbcam-CI] RenderFxPreviews: {fixtureFiles.Length} fixture(s), {_shaderIds.Length} shader(s), 3 views each");

            var plasmaShader = Shader.Find("Kerbcam/Plasma");
            var bowshockShader = Shader.Find("Kerbcam/Bowshock");
            var trailShader = Shader.Find("Kerbcam/Trail");
            var emberShader = Shader.Find("Kerbcam/Ember");
            if (plasmaShader == null || bowshockShader == null || trailShader == null || emberShader == null)
            {
                Debug.LogError("[Kerbcam-CI] one or more Kerbcam shaders not found — did the bundle compile?");
                EditorApplication.Exit(2);
                return;
            }

            foreach (var path in fixtureFiles)
            {
                var json = File.ReadAllText(path);
                var fx = JsonUtility.FromJson<FxFixture>(json);
                if (fx == null || string.IsNullOrEmpty(fx.name))
                {
                    Debug.LogWarning($"[Kerbcam-CI] skipping unparseable fixture: {path}");
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
                        Debug.Log($"[Kerbcam-CI]   begin {fx.name}/{shaderId}/{viewId}");
                        try
                        {
                            RenderOne(fx, shaderId, sh, viewId, fixtureDir);
                            Debug.Log($"[Kerbcam-CI]   end   {fx.name}/{shaderId}/{viewId} OK");
                        }
                        catch (System.Exception e)
                        {
                            Debug.LogError($"[Kerbcam-CI]   end   {fx.name}/{shaderId}/{viewId} FAILED: {e.GetType().Name}: {e.Message}");
                        }
                    }
                }
            }
            Debug.Log("[Kerbcam-CI] RenderFxPreviews: done");
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

                // Wind direction + windward profile drive both the FX mesh
                // placement and the FX-anchored camera views. Computed BEFORE
                // any FX renderer is parented under the root so the profile
                // only measures the proxy vessel.
                Vector3 windDir = WindDirFromInputs(fx.inputs);
                var profile = ComputeWindwardProfile(sceneRoot.transform, windDir);

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

                // Render to RT
                var rt = new RenderTexture(_outWidth, _outHeight, 24, RenderTextureFormat.ARGB32)
                {
                    name = $"FxPreviewRT_{fx.name}_{shaderId}_{viewId}",
                    antiAliasing = 1
                };
                cam.targetTexture = rt;
                cam.Render();

                // Read back + encode
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(_outWidth, _outHeight, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, _outWidth, _outHeight), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;

                var outName = $"{fx.name}_{shaderId}_{viewId}.png";
                var outPath = Path.Combine(_outputDir, outName);
                File.WriteAllBytes(outPath, tex.EncodeToPNG());

                cam.targetTexture = null;
                Object.DestroyImmediate(tex);
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(mat);
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
            var cb = new CommandBuffer { name = "Kerbcam Preview FX Plasma" };
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
        private static void SetupBowshock(Transform root, Material mat, Vector3 windDir, WindwardProfile profile)
        {
            // Dome = 1.5× the vessel's elliptical windward silhouette (shock
            // wider than body): local X scaled by the major radius, local Y
            // by the minor — broadside gives a long "canoe" shock along the
            // vessel's length. Depth from the geometric mean (flat oblate).
            // Mirrored on the runtime in BowshockEffect.
            float radMajor = Mathf.Max(profile.RadiusMajor * 1.5f, 0.5f);
            float radMinor = Mathf.Max(profile.RadiusMinor * 1.5f, 0.4f);
            float domeDepth = Mathf.Sqrt(radMajor * radMinor) * 0.55f;

            // Dome base sits right at the vessel's windward extreme; the
            // dome's curved surface bulges forward into the airflow.
            Vector3 basePos = root.position + windDir * profile.ForwardStandoff;

            var go = new GameObject("bowshock_dome");
            go.transform.SetParent(root, false);
            go.transform.position = basePos;
            // Local +Z = wind direction; up = minor axis (always ⊥ wind, so
            // it also guards the degenerate LookRotation case).
            go.transform.rotation = Quaternion.LookRotation(windDir, profile.MinorAxis(windDir));
            go.transform.localScale = new Vector3(radMajor, radMinor, domeDepth);
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mf.sharedMesh = BuildDomeMesh();
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
        private static void SetupTrail(Transform root, Material mat, Vector3 windDir, WindwardProfile profile, string viewId)
        {
            var go = new GameObject("trail_tube");
            go.transform.SetParent(root, false);
            // Position: at the vessel's aft windward edge, pulled IN by
            // 0.5 m so the head buries inside the vessel and the cylinder
            // occludes the ring of vertices at the tube's start.
            Vector3 trailHeadPos = root.position - windDir * (profile.AftStandoff - 0.5f);
            go.transform.position = trailHeadPos;
            // up = minor axis: local X (major) follows the vessel's long
            // silhouette axis so a broadside wake is a wide flat ribbon,
            // not a circular tube. Mirrored on the runtime in TrailEffect.
            go.transform.rotation = Quaternion.LookRotation(-windDir, profile.MinorAxis(windDir));
            // Scale each perp axis of the tube to the vessel's elliptical
            // silhouette. The major cap of 1.0× applies ONLY to the close
            // aft_hullcam view — its camera sits about 0.9 m from the trail
            // head, so a wide tube fills the near plane and LLVMpipe hangs
            // on the near-fullscreen additive triangles. The pulled-back
            // wake views get the uncrushed broadside ribbon (the runtime
            // tube is 4 m so its 1.0× cap never crushes a real silhouette).
            float majorCap = viewId == "aft_hullcam" ? 1.0f : 4.5f;
            float scaleMajor = Mathf.Clamp(profile.RadiusMajor / 0.6f, 0.5f, majorCap);
            float scaleMinor = Mathf.Clamp(profile.RadiusMinor / 0.6f, 0.3f, 1.0f);
            go.transform.localScale = new Vector3(scaleMajor, scaleMinor, 1f);
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mf.sharedMesh = BuildTaperedTubeMesh();
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
            var cb = new CommandBuffer { name = "Kerbcam Preview FX Ember" };
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
            Vector3 windDir, WindwardProfile profile)
        {
            // A stable direction perpendicular to the wind axis — the "side"
            // the side/three-quarter views shoot from.
            Vector3 perpHelper = Mathf.Abs(windDir.y) < 0.99f ? Vector3.up : Vector3.forward;
            Vector3 perp = Vector3.Cross(windDir, perpHelper).normalized;

            float radMajor = Mathf.Max(profile.RadiusMajor * 1.5f, 0.5f);
            float domeDepth = Mathf.Sqrt(radMajor * Mathf.Max(profile.RadiusMinor * 1.5f, 0.4f)) * 0.55f;
            Vector3 domeCentre = windDir * (profile.ForwardStandoff + domeDepth * 0.5f);
            Vector3 wakeHead = -windDir * Mathf.Max(profile.AftStandoff - 0.5f, 0f);
            float domeDist = Mathf.Max(4.5f, radMajor * 3.5f);

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
                    Debug.LogWarning($"[Kerbcam-CI] failed to load image: {abs}");
                }
                else Debug.LogWarning($"[Kerbcam-CI] missing texture file: {abs}");
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

        private static GameObject MakeCone()
        {
            var go = new GameObject("nose_cone");
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            var std = Shader.Find("Standard");
            if (std != null) mr.sharedMaterial = new Material(std);
            const int seg = 12;
            const float r = 1f;
            const float h = 1.5f;
            var verts = new Vector3[seg + 2];
            var uvs = new Vector2[seg + 2];
            verts[0] = new Vector3(0f, h, 0f);
            uvs[0] = new Vector2(0.5f, 1f);
            for (int i = 0; i < seg; i++)
            {
                float u = i / (float)seg;
                float a = u * Mathf.PI * 2f;
                verts[1 + i] = new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                uvs[1 + i] = new Vector2(u, 0f);
            }
            verts[seg + 1] = Vector3.zero;
            uvs[seg + 1] = new Vector2(0.5f, 0f);
            var tris = new List<int>();
            for (int i = 0; i < seg; i++) { tris.Add(0); tris.Add(1 + i); tris.Add(1 + ((i + 1) % seg)); }
            for (int i = 0; i < seg; i++) { tris.Add(seg + 1); tris.Add(1 + ((i + 1) % seg)); tris.Add(1 + i); }
            var mesh = new Mesh { name = "nose_cone_mesh" };
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            mf.sharedMesh = mesh;
            return go;
        }

        // Windward profile of the proxy vessel relative to the wind axis.
        // WindwardRadius is the max perpendicular-to-wind distance from
        // the root origin to any renderer's AABB corner — sizes the
        // bowshock dome and trail tube. ForwardStandoff is the max along
        // +windDir (vessel-to-shock standoff). AftStandoff is the max
        // along -windDir (where the trail/embers attach).
        private struct WindwardProfile
        {
            public float WindwardRadius;
            public float ForwardStandoff;
            public float AftStandoff;
            // Elliptical perp cross-section — see the runtime WindwardProfile:
            // a broadside vessel presents a long flat silhouette, and FX
            // sized as circles read as if it were flying nose-first.
            public Vector3 MajorAxis;
            public float RadiusMajor;
            public float RadiusMinor;

            public Vector3 MinorAxis(Vector3 windDir)
            {
                Vector3 minor = Vector3.Cross(windDir, MajorAxis);
                return minor.sqrMagnitude > 1e-6f ? minor.normalized : Vector3.up;
            }
        }

        private static WindwardProfile ComputeWindwardProfile(Transform root, Vector3 windDir)
        {
            float fwd = 0f, aft = 0f, perpMax = 0f;
            Vector3 origin = root.position;
            Vector3 majorDir = Vector3.right;
            var perps = new List<Vector3>();
            foreach (var rend in root.GetComponentsInChildren<Renderer>())
            {
                if (rend == null || rend is ParticleSystemRenderer) continue;
                var b = rend.bounds;
                Vector3 c = b.center, e = b.extents;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = c + new Vector3(
                        (i & 1) == 0 ? -e.x : e.x,
                        (i & 2) == 0 ? -e.y : e.y,
                        (i & 4) == 0 ? -e.z : e.z);
                    Vector3 rel = corner - origin;
                    float along = Vector3.Dot(rel, windDir);
                    if (along > fwd) fwd = along;
                    if (-along > aft) aft = -along;
                    Vector3 perp = rel - along * windDir;
                    perps.Add(perp);
                    float perpDist = perp.magnitude;
                    if (perpDist > perpMax)
                    {
                        perpMax = perpDist;
                        majorDir = perp;
                    }
                }
            }
            float minorMax = perpMax;
            if (majorDir.sqrMagnitude > 1e-6f)
            {
                Vector3 majorAxis = majorDir.normalized;
                Vector3 minorAxis = Vector3.Cross(windDir, majorAxis);
                if (minorAxis.sqrMagnitude > 1e-6f)
                {
                    minorAxis.Normalize();
                    minorMax = 0f;
                    foreach (var perp in perps)
                    {
                        float d = Mathf.Abs(Vector3.Dot(perp, minorAxis));
                        if (d > minorMax) minorMax = d;
                    }
                }
                majorDir = majorAxis;
            }
            return new WindwardProfile
            {
                WindwardRadius = perpMax, ForwardStandoff = fwd, AftStandoff = aft,
                MajorAxis = majorDir, RadiusMajor = perpMax, RadiusMinor = minorMax,
            };
        }

        private static Vector3 WindDirFromInputs(FxFixture.Inputs inputs)
        {
            Vector3 windDir = inputs != null && inputs.windDirWorld != null && inputs.windDirWorld.Length >= 3
                ? new Vector3(inputs.windDirWorld[0], inputs.windDirWorld[1], inputs.windDirWorld[2])
                : Vector3.up;
            if (windDir.sqrMagnitude < 1e-3f) windDir = Vector3.up;
            return windDir.normalized;
        }

        // Oblate dome (flattened hemisphere) for the bowshock. Local frame:
        // open base at z=0 (faces the vessel), curved surface extends to
        // z=+1 (faces the airflow). xy in [-1, +1] at the base. The
        // GameObject's localScale stretches this unit dome to (radius,
        // radius, depth) for the actual flat shape.
        private static Mesh BuildDomeMesh()
        {
            // 32×64 (was 10×32): the shader's spherical normal comes from the
            // INTERPOLATED localPos, which kinks at every latitude ring on a
            // coarse mesh — near-axis views showed the fresnel quantised into
            // ~10 concentric rings. Tessellation is the fix; the normal math
            // itself is fine.
            const int latSeg = 32;
            const int lonSeg = 64;
            int ringVerts = lonSeg + 1;
            int totalVerts = latSeg * ringVerts + 1;
            var verts = new Vector3[totalVerts];
            var uvs = new Vector2[totalVerts];
            verts[0] = new Vector3(0f, 0f, 1f);
            uvs[0] = new Vector2(0.5f, 1f);
            for (int lat = 1; lat <= latSeg; lat++)
            {
                float phi = (lat / (float)latSeg) * Mathf.PI * 0.5f;
                float sp = Mathf.Sin(phi);
                float cp = Mathf.Cos(phi);
                for (int lon = 0; lon < ringVerts; lon++)
                {
                    float th = (lon / (float)lonSeg) * Mathf.PI * 2f;
                    int idx = 1 + (lat - 1) * ringVerts + lon;
                    verts[idx] = new Vector3(sp * Mathf.Cos(th), sp * Mathf.Sin(th), cp);
                    uvs[idx] = new Vector2(lon / (float)lonSeg, 1f - lat / (float)latSeg);
                }
            }
            var tris = new List<int>();
            for (int lon = 0; lon < lonSeg; lon++)
            {
                tris.Add(0); tris.Add(1 + lon); tris.Add(1 + lon + 1);
            }
            for (int lat = 1; lat < latSeg; lat++)
            {
                int rowA = 1 + (lat - 1) * ringVerts;
                int rowB = 1 + lat * ringVerts;
                for (int lon = 0; lon < lonSeg; lon++)
                {
                    tris.Add(rowA + lon);     tris.Add(rowB + lon);     tris.Add(rowA + lon + 1);
                    tris.Add(rowA + lon + 1); tris.Add(rowB + lon);     tris.Add(rowB + lon + 1);
                }
            }
            var mesh = new Mesh { name = "Kerbcam Bowshock Dome" };
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // Higher-segment cone (32 radial faces) for the preview so the
        // polygonal silhouette outline disappears against the camera frame.
        // Runtime BowshockEffect uses 16 — same generator, more faces.
        private static Mesh BuildConeMesh()
        {
            const int faces = 32;
            const float baseRadius = 3f;
            const float length = 6f;
            var verts = new Vector3[faces * 2];
            var normals = new Vector3[faces * 2];
            var tris = new int[faces * 3];
            float apexZ = length * 0.5f;
            float baseZ = -length * 0.5f;
            Vector3 apex = new Vector3(0f, 0f, apexZ);
            for (int f = 0; f < faces; f++)
            {
                float angMid = (f + 0.5f) / faces * Mathf.PI * 2f;
                float angA = (float)f / faces * Mathf.PI * 2f;
                float angB = (float)(f + 1) / faces * Mathf.PI * 2f;
                Vector3 baseA = new Vector3(Mathf.Cos(angA) * baseRadius, Mathf.Sin(angA) * baseRadius, baseZ);
                Vector3 baseB = new Vector3(Mathf.Cos(angB) * baseRadius, Mathf.Sin(angB) * baseRadius, baseZ);
                Vector3 e1 = baseA - apex;
                Vector3 e2 = baseB - apex;
                Vector3 n = Vector3.Cross(e1, e2).normalized;
                Vector3 outwardXY = new Vector3(Mathf.Cos(angMid), Mathf.Sin(angMid), 0f);
                if (Vector3.Dot(n, outwardXY) < 0f) n = -n;
                int vBase = f * 2;
                verts[vBase + 0] = apex;
                verts[vBase + 1] = baseA;
                normals[vBase + 0] = n;
                normals[vBase + 1] = n;
                int next = (f + 1) % faces;
                int tBase = f * 3;
                tris[tBase + 0] = vBase + 0;
                tris[tBase + 1] = vBase + 1;
                tris[tBase + 2] = next * 2 + 1;
            }
            var mesh = new Mesh { name = "Kerbcam Bowshock Cone" };
            mesh.vertices = verts;
            mesh.normals = normals;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            return mesh;
        }

        // Preview trail tube: narrower at the vessel end (0.6 m) and much
        // longer (40 m) than the runtime fixed 4 m × 20 m mesh, so it can
        // visually emerge FROM the vessel rather than hanging off-axis
        // below it. 24 radial × 32 length so the silhouette isn't
        // visibly polygonal.
        private static Mesh BuildTaperedTubeMesh()
        {
            const int lengthSeg = 32;
            const int radialSeg = 24;
            const float startR = 0.6f;
            const float length = 40f;
            int ringVerts = radialSeg + 1;
            int totalVerts = (lengthSeg + 1) * ringVerts;
            var verts = new Vector3[totalVerts];
            var uvs = new Vector2[totalVerts];
            for (int yi = 0; yi <= lengthSeg; yi++)
            {
                float v = yi / (float)lengthSeg;
                float r = startR * (1f - v);
                float z = v * length;
                for (int xi = 0; xi <= radialSeg; xi++)
                {
                    float u = xi / (float)radialSeg;
                    float a = u * Mathf.PI * 2f;
                    int idx = yi * ringVerts + xi;
                    verts[idx] = new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, z);
                    uvs[idx] = new Vector2(u, v);
                }
            }
            var tris = new List<int>();
            for (int yi = 0; yi < lengthSeg; yi++)
            {
                for (int xi = 0; xi < radialSeg; xi++)
                {
                    int a = yi * ringVerts + xi;
                    int b = a + 1;
                    int c = (yi + 1) * ringVerts + xi;
                    int d = c + 1;
                    tris.Add(a); tris.Add(c); tris.Add(b);
                    tris.Add(b); tris.Add(c); tris.Add(d);
                }
            }
            var mesh = new Mesh { name = "Kerbcam Trail Tube" };
            if (totalVerts > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
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
