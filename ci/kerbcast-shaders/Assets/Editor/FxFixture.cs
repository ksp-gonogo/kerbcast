// FX fixture: one captured (or hand-crafted) snapshot of the state needed
// to render the kerbcast plasma shader off-line in the CI Unity job.
//
// Loaded from JSON files in ci/kerbcast-shaders/Fixtures/*.json by
// RenderFxPreviews.cs. Same format will be emitted by the in-game FxCapture
// hotkey once that lands, so the loop is: capture in flight → drop JSON +
// texture PNGs into the Fixtures dir → CI render → inspect artifact.
//
// All vector fields are float arrays so JsonUtility round-trips cleanly:
// [x, y, z] for Vector3, [x, y, z, w] for Vector4/Quaternion, [16 floats]
// row-major for Matrix4x4. Empty / null arrays fall back to identity.

using System;

namespace KerbcastCI
{
    [Serializable]
    public class FxFixture
    {
        public string name;          // used as the output PNG filename
        public string note;          // freeform description, ignored by the renderer
        public Scalars scalars;      // physics state at the captured frame
        public Globals globals;      // KSP-published _FX* shader globals
        public Inputs inputs;        // C#-driven material uniforms
        public CameraPose camera;    // preview camera pose + intrinsics
        public Textures textures;    // file paths (relative to fixture JSON) for global textures

        [Serializable]
        public class Scalars
        {
            public float mach;
            public float dynamicPressureKPa;
            public float atmDensity;
            public float altitude;             // metres ASL
            public float srfSpeed;             // m/s
            public float[] srfVelocity;        // [x,y,z] world
        }

        [Serializable]
        public class Globals
        {
            public float[] lightDirection0;    // [x,y,z,w] — airflow direction (= -velocity)
            public float[] fxColor;            // [r,g,b,a] — published heat colour
            public float fxLength;             // mach-blended trail length multiplier
            public float fxWobble;             // mach-blended vertex perturbation
            public float fxFalloff;            // mach-blended wrap falloff
            public float[] fxDepthCamMatrix;   // 16 floats, row-major (world → velocityCam view)
            public float[] fxDepthProjMatrix;  // 16 floats, row-major (velocityCam view → clip)
            public float fxProjectionNear;
            public float fxProjectionFar;
        }

        [Serializable]
        public class Inputs
        {
            public float intensity;
            public float fxState;
            public float fxRadiusMul;
            public float[] windDirWorld;       // [x,y,z,w] vessel-velocity direction fallback
        }

        [Serializable]
        public class CameraPose
        {
            public float fov;
            public float near;
            public float far;
            public float[] position;           // [x,y,z]
            public float[] rotation;           // [x,y,z,w]
        }

        [Serializable]
        public class Textures
        {
            public string fxMainTex;           // path relative to fixture JSON; "" → procedural noise
            public string fxDepthMap;          // path relative to fixture JSON; "" → flat depth=1
        }
    }
}
