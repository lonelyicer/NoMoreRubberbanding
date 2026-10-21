// HarmonyX transitively references MonoMod.Utils, which ships its own type with the same name.
#pragma warning disable CS0436
[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("Barotrauma")]
[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("BarotraumaCore")]
[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("DedicatedServer")]
#pragma warning restore CS0436

namespace System.Runtime.CompilerServices;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
internal sealed class IgnoresAccessChecksToAttribute(string assemblyName) : Attribute
{
    public string AssemblyName { get; } = assemblyName;
}
