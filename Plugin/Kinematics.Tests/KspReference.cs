using System;
using Kerbcast;

/*
 * KSP 1.12's own maths for where a conic or a surface point is, transcribed
 * from Assembly-CSharp (Orbit, Planetarium.CelestialFrame, CelestialBody)
 * with ilspycmd, keeping KSP's solvers and tolerances. The golden fixtures
 * compare Kerbcast's kinematics against these, so a frame or sign slip shows
 * as a failure here rather than as a camera pointing the wrong way.
 */
struct KspFrame
{
    public DVec3 X, Y, Z;

    public DVec3 WorldToLocal(DVec3 r) => new DVec3(DVec3.Dot(r, X), DVec3.Dot(r, Y), DVec3.Dot(r, Z));
    public DVec3 LocalToWorld(DVec3 r) => X * r.X + Y * r.Y + Z * r.Z;

    public static KspFrame Set(double a, double b, double c)
    {
        double num = Math.Cos(a), num2 = Math.Sin(a), num3 = Math.Cos(b), num4 = Math.Sin(b), num5 = Math.Cos(c), num6 = Math.Sin(c);
        return new KspFrame
        {
            X = new DVec3(num * num5 - num2 * num3 * num6, num2 * num5 + num * num3 * num6, num4 * num6),
            Y = new DVec3(-num * num6 - num2 * num3 * num5, -num2 * num6 + num * num3 * num5, num4 * num5),
            Z = new DVec3(num2 * num4, -num * num4, num3),
        };
    }

    public static KspFrame Orbital(double lan, double inc, double argPe) =>
        Set(lan * Math.PI / 180.0, inc * Math.PI / 180.0, argPe * Math.PI / 180.0);

    public static KspFrame Planetary(double ra, double dec, double rot) =>
        Set((ra - 90.0) * Math.PI / 180.0, (dec - 90.0) * Math.PI / 180.0, (rot + 90.0) * Math.PI / 180.0);
}

/* The planetarium's state: Zup = PlanetaryFrame(0, 90, InverseRotAngle). */
sealed class KspPlanetarium
{
    public double InverseRotAngle;
    public KspFrame Zup => KspFrame.Planetary(0.0, 90.0, InverseRotAngle);

    public static DVec3 Xzy(DVec3 v) => new DVec3(v.X, v.Z, v.Y);
}

sealed class KspBody
{
    public double Mu;
    public double Radius;
    public double InitialRotation;
    public double RotationPeriod;
    public bool InverseRotation;
    public double DirectRotAngle;
    public double RotationAngle;
    public KspFrame BodyFrame;
    public DVec3 Position;

    /* CelestialBody.CBUpdate's rotation step at `ut`. */
    public void Update(KspPlanetarium p, double ut)
    {
        double rotPeriodRecip = 1.0 / RotationPeriod;
        RotationAngle = (InitialRotation + 360.0 * rotPeriodRecip * ut) % 360.0;
        if (!InverseRotation)
        {
            DirectRotAngle = (RotationAngle - p.InverseRotAngle) % 360.0;
            BodyFrame = KspFrame.Planetary(0.0, 90.0, DirectRotAngle);
        }
        else
        {
            p.InverseRotAngle = (RotationAngle - DirectRotAngle) % 360.0;
            BodyFrame = KspFrame.Planetary(0.0, 90.0, DirectRotAngle);
        }
    }

    public DVec3 GetWorldSurfacePosition(double lat, double lon, double alt)
    {
        lat *= Math.PI / 180.0;
        lon *= Math.PI / 180.0;
        double num = Math.Cos(lat);
        var spherical = new DVec3(num * Math.Cos(lon), num * Math.Sin(lon), Math.Sin(lat));
        var nvector = KspPlanetarium.Xzy(spherical);
        var rel = nvector * (Radius + alt);
        return KspPlanetarium.Xzy(BodyFrame.LocalToWorld(KspPlanetarium.Xzy(rel))) + Position;
    }
}

sealed class KspOrbit
{
    public double inclination, eccentricity, semiMajorAxis, LAN, argumentOfPeriapsis, meanAnomalyAtEpoch, epoch;
    public KspBody referenceBody;
    public KspFrame OrbitFrame;
    public double meanMotion, ObTAtEpoch, period, hSqr;
    readonly KspPlanetarium planetarium;

    public KspOrbit(KspPlanetarium p) { planetarium = p; }

    public static KspOrbit FromElements(KspPlanetarium p, double inc, double e, double sma, double lan, double argPe,
        double mEp, double t, KspBody body)
    {
        var o = new KspOrbit(p)
        {
            inclination = inc, eccentricity = e, semiMajorAxis = sma, LAN = lan, argumentOfPeriapsis = argPe,
            meanAnomalyAtEpoch = mEp, epoch = t, referenceBody = body,
        };
        o.Init();
        return o;
    }

    void Init()
    {
        OrbitFrame = KspFrame.Orbital(LAN, inclination, argumentOfPeriapsis);
        hSqr = referenceBody.Mu * semiMajorAxis * (1.0 - eccentricity * eccentricity);
        meanMotion = GetMeanMotion(semiMajorAxis);
        ObTAtEpoch = meanAnomalyAtEpoch / meanMotion;
        period = eccentricity < 1.0 ? Math.PI * 2.0 / meanMotion : double.PositiveInfinity;
    }

    double GetMeanMotion(double sma)
    {
        double num = Math.Abs(sma);
        return Math.Sqrt(referenceBody.Mu / (num * num * num));
    }

    double semiLatusRectum => hSqr / referenceBody.Mu;

    /* Orbit.UpdateFromStateVectors: pos and vel in Zup's frame, as Orbit.pos and getRelativePositionAtUT give them. */
    public static KspOrbit FromStateVectors(KspPlanetarium p, DVec3 pos, DVec3 vel, KspBody body, double ut)
    {
        var zup = p.Zup;
        return FromFixedVectors(p, zup.LocalToWorld(pos), zup.LocalToWorld(vel), body, ut);
    }

    /* Orbit.UpdateFromFixedVectors, for a craft with angular momentum. */
    public static KspOrbit FromFixedVectors(KspPlanetarium p, DVec3 pos, DVec3 vel, KspBody body, double ut)
    {
        var o = new KspOrbit(p) { referenceBody = body };
        double mu = body.Mu;
        var h = DVec3.Cross(pos, vel);
        var forward = new DVec3(0, 0, 1);
        var an = DVec3.Cross(forward, h);
        o.OrbitFrame.Z = h * (1.0 / h.Magnitude);
        o.inclination = AngleBetween(o.OrbitFrame.Z, forward) * (180.0 / Math.PI);
        if (an.Magnitude == 0) an = new DVec3(1, 0, 0);
        o.LAN = Math.Atan2(an.Y, an.X) * (180.0 / Math.PI);
        o.LAN = (o.LAN + 360.0) % 360.0;
        var eccVec = pos * (DVec3.Dot(vel, vel) / mu - 1.0 / pos.Magnitude) - vel * (DVec3.Dot(pos, vel) / mu);
        o.eccentricity = eccVec.Magnitude;
        double energy = DVec3.Dot(vel, vel) / 2.0 - mu / pos.Magnitude;
        o.hSqr = DVec3.Dot(h, h);
        o.semiMajorAxis = !(o.eccentricity < 1.0)
            ? -o.semiLatusRectum / (DVec3.Dot(eccVec, eccVec) - 1.0)
            : -mu / (2.0 * energy);
        o.OrbitFrame.X = eccVec.Normalized;
        o.argumentOfPeriapsis = AngleBetween(an, o.OrbitFrame.X);
        if (DVec3.Dot(DVec3.Cross(an, o.OrbitFrame.X), h) < 0.0) o.argumentOfPeriapsis = Math.PI * 2.0 - o.argumentOfPeriapsis;
        o.OrbitFrame.Y = DVec3.Cross(o.OrbitFrame.Z, o.OrbitFrame.X);
        o.argumentOfPeriapsis *= 180.0 / Math.PI;
        o.meanMotion = o.GetMeanMotion(o.semiMajorAxis);
        double x = DVec3.Dot(pos, o.OrbitFrame.X), y = DVec3.Dot(pos, o.OrbitFrame.Y);
        double trueAnomaly = Math.Atan2(y, x);
        double ecc = o.GetEccentricAnomaly(trueAnomaly);
        o.meanAnomalyAtEpoch = o.GetMeanAnomaly(ecc);
        o.ObTAtEpoch = o.meanAnomalyAtEpoch / o.meanMotion;
        o.epoch = ut;
        o.period = o.eccentricity < 1.0 ? Math.PI * 2.0 / o.meanMotion : double.PositiveInfinity;
        return o;
    }

    /* UtilMath.AngleBetween. */
    static double AngleBetween(DVec3 a, DVec3 b)
    {
        double d = DVec3.Dot(a.Normalized, b.Normalized);
        return Math.Acos(Math.Max(-1.0, Math.Min(1.0, d)));
    }

    public DVec3 getPositionAtUT(double UT) => referenceBody.Position + KspPlanetarium.Xzy(getRelativePositionAtUT(UT));

    public DVec3 getRelativePositionAtUT(double UT) => getRelativePositionAtT(getObtAtUT(UT));

    public DVec3 getOrbitalVelocityAtUT(double UT) => getOrbitalVelocityAtTrueAnomaly(TrueAnomalyAtT(getObtAtUT(UT)));

    double getObtAtUT(double UT)
    {
        double num;
        if (eccentricity < 1.0)
        {
            num = (UT - epoch + ObTAtEpoch) % period;
            if (num > period / 2.0) num -= period;
        }
        else num = ObTAtEpoch + (UT - epoch);
        return num;
    }

    double TrueAnomalyAtT(double T) => GetTrueAnomaly(solveEccentricAnomaly(T * meanMotion, eccentricity));

    DVec3 getRelativePositionAtT(double T)
    {
        double m = T * meanMotion;
        double e = solveEccentricAnomaly(m, eccentricity);
        double tA = GetTrueAnomaly(e);
        double num = Math.Cos(tA), num2 = Math.Sin(tA);
        var v = (OrbitFrame.X * num + OrbitFrame.Y * num2) * (semiLatusRectum / (1.0 + eccentricity * num));
        return planetarium.Zup.WorldToLocal(v);
    }

    DVec3 getOrbitalVelocityAtTrueAnomaly(double tA)
    {
        double num = Math.Cos(tA), num2 = Math.Sin(tA);
        double num3 = Math.Sqrt(referenceBody.Mu / (semiMajorAxis * (1.0 - eccentricity * eccentricity)));
        var v = OrbitFrame.X * (-num2 * num3) + OrbitFrame.Y * ((num + eccentricity) * num3);
        return planetarium.Zup.WorldToLocal(v);
    }

    double GetEccentricAnomaly(double tA)
    {
        double num = Math.Cos(tA / 2.0), num2 = Math.Sin(tA / 2.0);
        if (eccentricity < 1.0)
            return 2.0 * Math.Atan2(Math.Sqrt(1.0 - eccentricity) * num2, Math.Sqrt(1.0 + eccentricity) * num);
        double num3 = Math.Sqrt((eccentricity - 1.0) / (eccentricity + 1.0)) * num2 / num;
        if (num3 >= 1.0) return double.PositiveInfinity;
        if (num3 <= -1.0) return double.NegativeInfinity;
        return Math.Log((1.0 + num3) / (1.0 - num3));
    }

    double GetMeanAnomaly(double E)
    {
        if (eccentricity < 1.0)
        {
            double m = E - eccentricity * Math.Sin(E);
            /* UtilMath.ClampRadiansTwoPI */
            m %= Math.PI * 2.0;
            return m < 0 ? m + Math.PI * 2.0 : m;
        }
        if (double.IsInfinity(E)) return E;
        return eccentricity * Math.Sinh(E) - E;
    }

    double GetTrueAnomaly(double E)
    {
        if (eccentricity < 1.0)
        {
            double num = Math.Cos(E / 2.0), num2 = Math.Sin(E / 2.0);
            return 2.0 * Math.Atan2(Math.Sqrt(1.0 + eccentricity) * num2, Math.Sqrt(1.0 - eccentricity) * num);
        }
        if (double.IsPositiveInfinity(E)) return Math.Acos(-1.0 / eccentricity);
        if (double.IsNegativeInfinity(E)) return -Math.Acos(-1.0 / eccentricity);
        double num3 = Math.Sinh(E / 2.0), num4 = Math.Cosh(E / 2.0);
        return 2.0 * Math.Atan2(Math.Sqrt(eccentricity + 1.0) * num3, Math.Sqrt(eccentricity - 1.0) * num4);
    }

    double solveEccentricAnomaly(double M, double ecc, double maxError = 1E-07, int maxIterations = 8)
    {
        if (!(eccentricity < 1.0)) return solveEccentricAnomalyHyp(M, eccentricity, maxError);
        if (!(eccentricity < 0.8)) return solveEccentricAnomalyExtremeEcc(M, eccentricity, maxIterations);
        return solveEccentricAnomalyStd(M, eccentricity, maxError);
    }

    static double solveEccentricAnomalyStd(double M, double ecc, double maxError)
    {
        double num = 1.0;
        double num2 = M + ecc * Math.Sin(M) + 0.5 * ecc * ecc * Math.Sin(2.0 * M);
        while (Math.Abs(num) > maxError)
        {
            double num3 = num2 - ecc * Math.Sin(num2);
            num = (M - num3) / (1.0 - ecc * Math.Cos(num2));
            num2 += num;
        }
        return num2;
    }

    double solveEccentricAnomalyExtremeEcc(double M, double ecc, int iterations)
    {
        double num = M + 0.85 * eccentricity * Math.Sign(Math.Sin(M));
        for (int i = 0; i < iterations; i++)
        {
            double num2 = ecc * Math.Sin(num), num3 = ecc * Math.Cos(num);
            double num4 = num - num2 - M, num5 = 1.0 - num3, num6 = num2;
            num += -5.0 * num4 / (num5 + Math.Sign(num5) * Math.Sqrt(Math.Abs(16.0 * num5 * num5 - 20.0 * num4 * num6)));
        }
        return num;
    }

    double solveEccentricAnomalyHyp(double M, double ecc, double maxError)
    {
        if (double.IsInfinity(M)) return M;
        double num = 1.0;
        double num2 = 2.0 * M / ecc;
        double num3 = Math.Log(Math.Sqrt(num2 * num2 + 1.0) + num2);
        while (Math.Abs(num) > maxError)
        {
            num = (eccentricity * Math.Sinh(num3) - num3 - M) / (eccentricity * Math.Cosh(num3) - 1.0);
            num3 -= num;
        }
        return num3;
    }
}
