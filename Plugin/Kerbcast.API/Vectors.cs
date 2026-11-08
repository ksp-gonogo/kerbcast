using System;

namespace Kerbcast.Api
{
    /** <summary>A double-precision vector in whatever frame the member using it names.</summary> */
    public readonly struct Vec3d : IEquatable<Vec3d>
    {
        public readonly double X;
        public readonly double Y;
        public readonly double Z;

        public Vec3d(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static readonly Vec3d Zero = new Vec3d(0, 0, 0);

        /** <summary>No component is NaN or infinite.</summary> */
        public bool IsFinite => Finite(X) && Finite(Y) && Finite(Z);

        public double Magnitude => Math.Sqrt(X * X + Y * Y + Z * Z);

        public bool Equals(Vec3d other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);
        public override bool Equals(object obj) => obj is Vec3d v && Equals(v);
        public override int GetHashCode() => (X.GetHashCode() * 397 ^ Y.GetHashCode()) * 397 ^ Z.GetHashCode();
        public static bool operator ==(Vec3d a, Vec3d b) => a.Equals(b);
        public static bool operator !=(Vec3d a, Vec3d b) => !a.Equals(b);
        public override string ToString() => $"({X}, {Y}, {Z})";

        internal static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    }

    /** <summary>A double-precision rotation quaternion, W the scalar part.</summary> */
    public readonly struct Quatd : IEquatable<Quatd>
    {
        public readonly double X;
        public readonly double Y;
        public readonly double Z;
        public readonly double W;

        public Quatd(double x, double y, double z, double w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        public static readonly Quatd Identity = new Quatd(0, 0, 0, 1);

        /** <summary>No component is NaN or infinite.</summary> */
        public bool IsFinite => Vec3d.Finite(X) && Vec3d.Finite(Y) && Vec3d.Finite(Z) && Vec3d.Finite(W);

        public bool Equals(Quatd other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z) && W.Equals(other.W);
        public override bool Equals(object obj) => obj is Quatd q && Equals(q);
        public override int GetHashCode() => ((X.GetHashCode() * 397 ^ Y.GetHashCode()) * 397 ^ Z.GetHashCode()) * 397 ^ W.GetHashCode();
        public static bool operator ==(Quatd a, Quatd b) => a.Equals(b);
        public static bool operator !=(Quatd a, Quatd b) => !a.Equals(b);
        public override string ToString() => $"({X}, {Y}, {Z}, {W})";
    }
}
