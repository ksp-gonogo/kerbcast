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
        // Three viewpoints chosen to actually frame the FX they're meant to
        // showcase:
        //   side_profile     — pulled back, full vessel + wake + bowshock in frame
        //   aft_hullcam      — body-mounted, looking back/down — sees trail + embers
        //   forward_hullcam  — body-mounted, looking forward/up — sees bowshock
        private static readonly string[] _viewIds = { "side_profile", "aft_hullcam", "forward_hullcam" };

        public static void RenderAll()
        {
            if (!Directory.Exists(_outputDir)) Directory.CreateDirectory(_outputDir);

            var fixtureFiles = Directory.GetFiles(_fixturesDir, "*.json");
            System.Array.Sort(fixtureFiles);
            Debug.Log($"[Kerbcam-CI] RenderFxPreviews: {fixtureFiles.Length} fixture(s), {_shaderIds.Length} shader(s), {_viewIds.Length} view(s)");

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
                    foreach (var viewId in _viewIds)
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

                var camGo = new GameObject("__fx_preview_camera");
                camGo.transform.SetParent(sceneRoot.transform, false);
                ApplyCameraPose(camGo.transform, fx.camera, viewId);
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
                    case "bowshock": SetupBowshock(sceneRoot.transform, mat); break;
                    case "trail": SetupTrail(sceneRoot.transform, mat, fx.inputs); break;
                    case "ember": SetupEmber(sceneRoot.transform, cam, mat); break;
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

        // Bowshock: place a procedural hollow cone above the vessel (apex
        // points +Y = along velocity). Scaled down to vessel-relative size
        // — the runtime BowshockEffect picks a scale of vesselExtent*0.35,
        // which for our 4 m proxy works out to ≈0.3× of the default mesh.
        private static void SetupBowshock(Transform root, Material mat)
        {
            var go = new GameObject("bowshock_cone");
            go.transform.SetParent(root, false);
            go.transform.localPosition = new Vector3(0f, 3.2f, 0f);
            go.transform.localRotation = Quaternion.LookRotation(Vector3.up, Vector3.forward);
            go.transform.localScale = new Vector3(0.3f, 0.3f, 0.3f);
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mf.sharedMesh = BuildConeMesh();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        // Trail: procedural tapered tube behind the vessel along airflow.
        // axis +Z = downstream. The preview mesh is sized to start AT the
        // vessel's local radius (0.6 m) and extend 40 m downstream so the
        // wake reads as continuous with the vessel rather than a detached
        // shape; no extra scale applied. Position is exactly at the cylinder
        // base so the head of the tube touches the vessel.
        private static void SetupTrail(Transform root, Material mat, FxFixture.Inputs inputs)
        {
            var go = new GameObject("trail_tube");
            go.transform.SetParent(root, false);
            go.transform.localPosition = new Vector3(0f, -1.2f, 0f); // exactly at cylinder base
            Vector3 windDir = inputs != null && inputs.windDirWorld != null && inputs.windDirWorld.Length >= 3
                ? new Vector3(inputs.windDirWorld[0], inputs.windDirWorld[1], inputs.windDirWorld[2]).normalized
                : Vector3.up;
            if (windDir.sqrMagnitude < 1e-3f) windDir = Vector3.up;
            Vector3 helperUp = Mathf.Abs(windDir.y) < 0.99f ? Vector3.up : Vector3.right;
            go.transform.localRotation = Quaternion.LookRotation(-windDir, helperUp);
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mf.sharedMesh = BuildTaperedTubeMesh();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        // Ember: a pre-baked mesh of N billboarded quads with per-vertex
        // colour. We do NOT use ParticleSystem in the preview — in headless
        // batchmode the PS renderer doesn't tick (no frame advance), so
        // particles emitted via Emit() don't render. Plain MeshRenderer
        // bypasses that entirely. Each quad is manually oriented to face
        // the camera at mesh-build time (the ember shader vert stage does
        // a vanilla ObjectToClipPos and doesn't billboard internally), so
        // we rebuild per render to track the active camera.
        private static void SetupEmber(Transform root, Camera cam, Material mat)
        {
            var go = new GameObject("ember_quads");
            go.transform.SetParent(root, false);
            go.transform.localPosition = Vector3.zero;
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mf.sharedMesh = BuildEmberQuadMesh(cam);
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        // Build N camera-facing quads spread along the wake (-Y from vessel
        // base), with per-vertex colour sampled from the ember gradient so
        // the spatial distribution reads hot→cool from front to tail.
        private static Mesh BuildEmberQuadMesh(Camera cam)
        {
            const int count = 80;
            const float wakeLen = 4.5f;
            var gradient = MakeEmberGradient();
            // Camera-aligned right/up so each quad is screen-facing for the
            // current view. Computed once in world space then converted to
            // mesh-local (mesh sits at root origin so they're equivalent).
            Vector3 camRight = cam.transform.right;
            Vector3 camUp = cam.transform.up;

            var verts = new Vector3[count * 4];
            var uvs = new Vector2[count * 4];
            var cols = new Color[count * 4];
            var tris = new int[count * 6];
            for (int i = 0; i < count; i++)
            {
                float along01 = i / (float)(count - 1);
                float along = -0.5f - along01 * wakeLen + Random.Range(-0.2f, 0.2f);
                float spread = Mathf.Lerp(0.05f, 0.7f, along01) * Random.Range(0.2f, 1.4f);
                float theta = Random.Range(0f, Mathf.PI * 2f);
                Vector3 centre = new Vector3(
                    Mathf.Cos(theta) * spread,
                    along,
                    Mathf.Sin(theta) * spread);
                float size = Mathf.Lerp(0.10f, 0.04f, along01);
                Color col = gradient.Evaluate(along01);
                int v = i * 4;
                verts[v + 0] = centre + (-camRight - camUp) * size;
                verts[v + 1] = centre + ( camRight - camUp) * size;
                verts[v + 2] = centre + (-camRight + camUp) * size;
                verts[v + 3] = centre + ( camRight + camUp) * size;
                uvs[v + 0] = new Vector2(0, 0);
                uvs[v + 1] = new Vector2(1, 0);
                uvs[v + 2] = new Vector2(0, 1);
                uvs[v + 3] = new Vector2(1, 1);
                cols[v + 0] = cols[v + 1] = cols[v + 2] = cols[v + 3] = col;
                int t = i * 6;
                tris[t + 0] = v + 0; tris[t + 1] = v + 2; tris[t + 2] = v + 1;
                tris[t + 3] = v + 1; tris[t + 4] = v + 2; tris[t + 5] = v + 3;
            }
            var mesh = new Mesh { name = "ember_quads_mesh" };
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.colors = cols;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void ConfigureParticleSystem(ParticleSystem ps)
        {
            ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
            {
                var main = ps.main;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.5f, 1.2f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.03f, 0.12f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(5f, 15f);
                main.gravityModifier = 0f;
                main.maxParticles = 256;
                main.loop = true;
                main.playOnAwake = false;
                main.startColor = new ParticleSystem.MinMaxGradient(Color.white);
            }
            { var em = ps.emission; em.enabled = true; em.rateOverTime = 60f; }
            {
                var shape = ps.shape;
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Cone;
                shape.angle = 25f;
                shape.radius = 1.0f;
            }
            {
                var vel = ps.velocityOverLifetime;
                vel.enabled = true;
                vel.space = ParticleSystemSimulationSpace.World;
                // Downstream drift along -Y (airflow) with mild jitter.
                vel.x = new ParticleSystem.MinMaxCurve(-1.5f, 1.5f);
                vel.y = new ParticleSystem.MinMaxCurve(-10f, -6f);
                vel.z = new ParticleSystem.MinMaxCurve(-1.5f, 1.5f);
            }
            {
                var col = ps.colorOverLifetime;
                col.enabled = true;
                var g = new Gradient();
                g.SetKeys(
                    new[]
                    {
                        new GradientColorKey(new Color(1.0f, 0.95f, 0.75f), 0.0f),
                        new GradientColorKey(new Color(1.0f, 0.55f, 0.15f), 0.35f),
                        new GradientColorKey(new Color(0.8f, 0.15f, 0.05f), 0.75f),
                        new GradientColorKey(new Color(0.1f, 0.02f, 0.0f), 1.0f),
                    },
                    new[]
                    {
                        new GradientAlphaKey(1.0f, 0.0f),
                        new GradientAlphaKey(0.8f, 0.4f),
                        new GradientAlphaKey(0.3f, 0.8f),
                        new GradientAlphaKey(0.0f, 1.0f),
                    });
                col.color = new ParticleSystem.MinMaxGradient(g);
            }
            {
                var sz = ps.sizeOverLifetime;
                sz.enabled = true;
                var c = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(0.6f, 0.7f), new Keyframe(1f, 0.3f));
                sz.size = new ParticleSystem.MinMaxCurve(1f, c);
            }
        }

        private static Gradient MakeEmberGradient()
        {
            var g = new Gradient();
            g.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1.0f, 0.95f, 0.75f), 0.0f),
                    new GradientColorKey(new Color(1.0f, 0.55f, 0.15f), 0.35f),
                    new GradientColorKey(new Color(0.8f, 0.15f, 0.05f), 0.75f),
                    new GradientColorKey(new Color(0.1f, 0.02f, 0.0f), 1.0f),
                },
                new[]
                {
                    new GradientAlphaKey(1.0f, 0.0f),
                    new GradientAlphaKey(0.8f, 0.4f),
                    new GradientAlphaKey(0.3f, 0.8f),
                    new GradientAlphaKey(0.0f, 1.0f),
                });
            return g;
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

        // Camera presets that actually frame the FX in question — replaces
        // the first iteration's `external/nose_up/body_out` set which left
        // most FX outside the visible frustum.
        //
        //   side_profile:    pulled-back side view at (9, 1, 0) looking at
        //                    (0, -3, 0). Frames the whole vessel + trail
        //                    region + bowshock cone in one shot.
        //   aft_hullcam:     body-mounted (0.85, -0.5, 0) angled aft-down
        //                    so the trail tube and embers fill the frame —
        //                    the kerbcam-on-booster looking back use case.
        //   forward_hullcam: body-mounted (0.85, 0.8, 0) angled forward
        //                    past the nose so the bowshock + nose tip are
        //                    in frame — the kerbcam-on-body looking up.
        private static void ApplyCameraPose(Transform t, FxFixture.CameraPose pose, string viewId)
        {
            switch (viewId)
            {
                case "side_profile":
                    t.localPosition = new Vector3(9f, 1f, 0f);
                    t.localRotation = Quaternion.LookRotation(
                        (new Vector3(0f, -3f, 0f) - t.localPosition).normalized, Vector3.up);
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
                default:
                    if (pose == null) { t.localPosition = new Vector3(3.5f, 0f, 0f); t.LookAt(Vector3.zero, Vector3.up); return; }
                    t.localPosition = ToVec3(pose.position, new Vector3(3.5f, 0f, 0f));
                    if (pose.rotation != null && pose.rotation.Length >= 4)
                        t.localRotation = new Quaternion(pose.rotation[0], pose.rotation[1], pose.rotation[2], pose.rotation[3]);
                    else t.LookAt(Vector3.zero, Vector3.up);
                    return;
            }
        }

        // ------------------------------------------------------------------
        // Material uniforms + global state
        // ------------------------------------------------------------------

        private static void ApplyMaterialInputs(Material mat, FxFixture.Inputs inputs, string shaderId)
        {
            if (inputs == null) return;
            mat.SetFloat("_Intensity", inputs.intensity);
            // Shader-specific: only Plasma has _FxState and _FxRadiusMul.
            if (shaderId == "plasma")
            {
                mat.SetFloat("_FxState", inputs.fxState);
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
