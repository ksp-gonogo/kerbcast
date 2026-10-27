using System.Reflection;
using System.Runtime.CompilerServices;

/* KSPAssembly and the file version come from Versions.props. AssemblyVersion
   stays 1.0.0.0 for the whole 1.x line so a mod built against any 1.x binds
   to any other. */
[assembly: AssemblyTitle("Kerbcast.API")]
[assembly: AssemblyVersion("1.0.0.0")]

/* Kerbcast.dll registers itself as the implementation and builds the views;
   the test assembly installs its recorder. Neither is something a mod can do. */
[assembly: InternalsVisibleTo("Kerbcast")]
[assembly: InternalsVisibleTo("Kerbcast.API.Testing")]

namespace Kerbcast.Api
{
    /* KSP's AssemblyLoader reads this attribute through Cecil by its type name
       alone, so declaring it here lets mods order themselves after Kerbcast.API
       ([KSPAssemblyDependency("KerbcastAPI", 1, 0)]) without this assembly
       referencing Assembly-CSharp, where KSP's own copy lives. */
    [System.AttributeUsage(System.AttributeTargets.Assembly)]
    internal sealed class KSPAssembly : System.Attribute
    {
        public KSPAssembly(string name, int major, int minor) { }
    }
}
