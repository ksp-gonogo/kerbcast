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
                    case "bowshock": SetupBowshock(sceneRoot.transform, mat, fx.inputs); break;
                    case "trail": SetupTrail(sceneRoot.transform, mat, fx.inputs); break;
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
        private static void SetupBowshock(Transform root, Material mat, FxFixture.Inputs inputs)
        {
            Vector3 windDir = WindDirFromInputs(inputs);
            var profile = ComputeWindwardProfile(root, windDir);

            // Dome width = 1.5× vessel windward radius (shock is wider than
            // body); depth = 0.55× width (flat oblate).
            float domeRadius = Mathf.Max(profile.WindwardRadius * 1.5f, 0.5f);
            float domeDepth = domeRadius * 0.55f;

            // Dome base sits right at the vessel's windward extreme; the
            // dome's curved surface bulges forward into the airflow.
            Vector3 basePos = root.position + windDir * profile.ForwardStandoff;

            var go = new GameObject("bowshock_dome");
            go.transform.SetParent(root, false);
            go.transform.position = basePos;
            // Local +Z = wind direction (curved surface facing into wind).
            // helperUp avoids degenerate LookRotation when wind ≈ ±Y.
            Vector3 helperUp = Mathf.Abs(windDir.y) < 0.99f ? Vector3.up : Vector3.right;
            go.transform.rotation = Quaternion.LookRotation(windDir, helperUp);
            go.transform.localScale = new Vector3(domeRadius, domeRadius, domeDepth);
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
        private static void SetupTrail(Transform root, Material mat, FxFixture.Inputs inputs)
        {
            Vector3 windDir = WindDirFromInputs(inputs);
            var profile = ComputeWindwardProfile(root, windDir);

            var go = new GameObject("trail_tube");
            go.transform.SetParent(root, false);
            // Position: at the vessel's aft windward edge, pulled IN by
            // 0.5 m so the head buries inside the vessel and the cylinder
            // occludes the ring of vertices at the tube's start.
            Vector3 trailHeadPos = root.position - windDir * (profile.AftStandoff - 0.5f);
            go.transform.position = trailHeadPos;
            Vector3 helperUp = Mathf.Abs(windDir.y) < 0.99f ? Vector3.up : Vector3.right;
            go.transform.rotation = Quaternion.LookRotation(-windDir, helperUp);
            // Scale the tube's start radius to vessel windward radius so the
            // wake is wide enough to match the vessel's profile. Length stays
            // at the mesh's natural 40 m (already long enough to fade).
            float radiusScale = Mathf.Max(profile.WindwardRadius / 0.6f, 0.5f);
            go.transform.localScale = new Vector3(radiusScale, radiusScale, 1f);
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
        //
        // Spawn area + drift direction are derived from the windward
        // profile so the embers shed off the vessel's actual windward
        // edge and trail along the airflow regardless of orientation.
        private static void SetupEmber(Transform root, Camera cam, Material mat, FxFixture.Inputs inputs)
        {
            Vector3 windDir = WindDirFromInputs(inputs);
            var profile = ComputeWindwardProfile(root, windDir);
            var go = new GameObject("ember_quads");
            go.transform.SetParent(root, false);
            go.transform.localPosition = Vector3.zero;
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mf.sharedMesh = BuildEmberQuadMesh(cam, windDir, profile);
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        // Build N camera-facing quads spread along the wake. The emitter
        // region spans the vessel's windward edge (perpendicular to wind)
        // and the wake extends along -windDir for ~6m. Each particle's
        // colour is sampled from the ember gradient based on its
        // along-wake position so the spatial distribution reads
        // hot→cool from front to tail.
        private static Mesh BuildEmberQuadMesh(Camera cam, Vector3 windDir, WindwardProfile profile)
        {
            const int count = 80;
            const float wakeLen = 4.5f;
            var gradient = MakeEmberGradient();
            Vector3 camRight = cam.transform.right;
            Vector3 camUp = cam.transform.up;

            // Build an orthonormal basis perpendicular to windDir so we
            // can scatter the emit origins across the windward face.
            Vector3 perpUp = Mathf.Abs(windDir.y) < 0.99f ? Vector3.up : Vector3.right;
            Vector3 perpA = Vector3.Cross(windDir, perpUp).normalized;
            Vector3 perpB = Vector3.Cross(windDir, perpA).normalized;
            float emitRadius = Mathf.Max(profile.WindwardRadius * 0.7f, 0.2f);
            // Origin of the wake: at the vessel's aft windward edge.
            Vector3 wakeOrigin = -windDir * (profile.AftStandoff - 0.2f);

            var verts = new Vector3[count * 4];
            var uvs = new Vector2[count * 4];
            var cols = new Color[count * 4];
            var tris = new int[count * 6];
            for (int i = 0; i < count; i++)
            {
                float along01 = i / (float)(count - 1);
                float along = -along01 * wakeLen + Random.Range(-0.2f, 0.2f);
                float spread = Mathf.Lerp(emitRadius * 0.2f, emitRadius * 1.4f, along01)
                               * Random.Range(0.2f, 1.4f);
                float theta = Random.Range(0f, Mathf.PI * 2f);
                Vector3 centre = wakeOrigin
                    + windDir * along
                    + (perpA * Mathf.Cos(theta) + perpB * Mathf.Sin(theta)) * spread;
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
                    // Original close (3.5 m) position — pulling back even to
                    // 5 m makes LLVMpipe spend minutes per render on the
                    // plasma extrusion. Look-at centred at origin so both
                    // bowshock (above vessel) and trail (below) get partial
                    // visibility — neither is fully framed but both register.
                    t.localPosition = new Vector3(3.5f, 0f, 0f);
                    t.localRotation = Quaternion.LookRotation(
                        (new Vector3(0f, 0f, 0f) - t.localPosition).normalized, Vector3.up);
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
        }

        private static WindwardProfile ComputeWindwardProfile(Transform root, Vector3 windDir)
        {
            float fwd = 0f, aft = 0f, perpMax = 0f;
            Vector3 origin = root.position;
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
                    float perpDist = perp.magnitude;
                    if (perpDist > perpMax) perpMax = perpDist;
                }
            }
            return new WindwardProfile { WindwardRadius = perpMax, ForwardStandoff = fwd, AftStandoff = aft };
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
            const int latSeg = 10;
            const int lonSeg = 32;
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
