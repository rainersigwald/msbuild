// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.


using System;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;

#nullable enable
namespace Microsoft.Build.Shared
{
    /// <summary>
    /// This class is used to isolate the types used by an MSBuild plugin
    /// (SDK resolver, logger, or task).
    /// </summary>
    internal class MSBuildLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        private static readonly ImmutableHashSet<string> _wellKnownAssemblyNames =
            new[]
            {
                "MSBuild",
                "Microsoft.Build",
                "Microsoft.Build.Framework",
                "Microsoft.Build.Tasks.Core",
                "Microsoft.Build.Utilities.Core",
            }.ToImmutableHashSet();


        public MSBuildLoadContext(string assemblyPath) :
            base(name: assemblyPath, isCollectible: false) // TODO: make this collectible?
        {
            _resolver = new AssemblyDependencyResolver(assemblyPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (_wellKnownAssemblyNames.Contains(assemblyName.Name!))
            {
                // MSBuild assemblies should be loaded from the default context
                return null;
            }

            string? assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
            if (assemblyPath != null)
            {
                return LoadFromAssemblyPath(assemblyPath);
            }

            return null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string? libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (libraryPath != null)
            {
                return LoadUnmanagedDllFromPath(libraryPath);
            }

            return IntPtr.Zero;
        }

    }
}
