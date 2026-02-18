using System.Runtime.CompilerServices;

// Make internals visible to the FFI layer
[assembly: InternalsVisibleTo("HPD-Agent.FFI")]

// Make internals visible to the Hosting layer (needed to create Session/Branch without agent)
[assembly: InternalsVisibleTo("HPD-Agent.Hosting")]
