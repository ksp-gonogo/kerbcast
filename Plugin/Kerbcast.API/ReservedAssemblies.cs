using System;

namespace Kerbcast.Api
{
    /* Assemblies a reservation may not name: they are always loaded, so a
       reservation naming one would never lapse when its owner is gone. */
    internal static class ReservedAssemblies
    {
        static readonly string[] Exact =
        {
            "Kerbcast", "Kerbcast.API", "KerbcastAPI", "Kerbcast.Kos", "Assembly-CSharp", "Assembly-CSharp-firstpass",
            "Assembly-UnityScript", "Assembly-UnityScript-firstpass", "mscorlib", "netstandard", "0Harmony", "KSPAssets",
            "KSPTrackIR", "TDx.TDxInput", "Ionic.Zip", "Mono.Cecil", "System", "UnityEngine",
        };

        /* Each ends in a dot, so a mod whose name merely starts the same (SystemHeat) is not caught. */
        static readonly string[] Prefixes = { "UnityEngine.", "Unity.", "System.", "Mono.", "Microsoft." };

        /* The name without a ".dll" ending. */
        public static string Bare(string assembly) =>
            assembly != null && assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? assembly.Substring(0, assembly.Length - 4) : assembly;

        public static bool IsReserved(string assembly)
        {
            string bare = Bare(assembly);
            if (string.IsNullOrEmpty(bare)) return false;
            foreach (var e in Exact)
                if (string.Equals(bare, e, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var p in Prefixes)
                if (bare.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /* Whether `value` survives a save as one ConfigNode value: no line break, no brace, no comment
           marker, and no edge whitespace, which ConfigNode trims on reading it back. */
        public static bool SafeValue(string value) =>
            value == null || (value.IndexOfAny(new[] { '\n', '\r', '{', '}' }) < 0 && value.IndexOf("//", StringComparison.Ordinal) < 0 && value.Trim() == value);

        /* Owners and silence texts are saved in Kerbcast's settings, so they must not break out of their node. */
        public static void CheckText(string value, string parameter)
        {
            if (!SafeValue(value)) throw new ArgumentException("must not contain a line break, '{', '}' or '//', or start or end with whitespace", parameter);
        }

        /* The checks every Reserve makes, Kerbcast's and the recorder's alike. */
        public static void Check(string owner, string ownerAssembly, string heldBy)
        {
            if (string.IsNullOrEmpty(owner)) throw new ArgumentException("an owner is required", nameof(owner));
            CheckText(owner, nameof(owner));
            CheckText(ownerAssembly, nameof(ownerAssembly));
            if (string.IsNullOrEmpty(ownerAssembly)) throw new ArgumentException("the owner's assembly is required, so the reservation lapses when it is gone", nameof(ownerAssembly));
            if (IsReserved(ownerAssembly)) throw new ArgumentException($"'{ownerAssembly}' is always loaded, so a reservation naming it could never lapse", nameof(ownerAssembly));
            if (heldBy != null && !string.Equals(heldBy, owner, StringComparison.Ordinal))
                throw new InvalidOperationException($"{heldBy} holds the reservation; only it can clear it");
        }
    }
}
