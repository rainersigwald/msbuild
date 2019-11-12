// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;

#nullable enable
namespace Microsoft.Build.Shared
{
    /// <summary>
    /// CoreCLR-compatible wrapper for loading task assemblies.
    /// </summary>
    internal sealed class CoreClrAssemblyLoader
    {
        private readonly Dictionary<string, Assembly> _pathsToAssemblies = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        private readonly object _guard = new object();

        public Assembly LoadFromPath(string fullPath)
        {
            Debug.Assert(Path.IsPathRooted(fullPath));

            lock (_guard)
            {
                if (_pathsToAssemblies.TryGetValue(fullPath, out Assembly? assembly))
                {
                    return assembly;
                }

                var contextForAssemblyPath = new MSBuildLoadContext(fullPath);

                assembly = contextForAssemblyPath.LoadFromAssemblyPath(fullPath);

                _pathsToAssemblies[fullPath] = assembly;

                return assembly;
            }
        }
    }
}
