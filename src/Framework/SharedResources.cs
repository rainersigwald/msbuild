﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Resources;
using System.Reflection;
using System.Globalization;

namespace Microsoft.Build.Shared
{
    /// <summary>
    /// This class provides access to the assembly's resources.
    /// </summary>
    static class SharedResources
    {
        /// <summary>
        /// Loads the specified resource string, either from the assembly's primary resources, or its shared resources.
        /// </summary>
        /// <remarks>This method is thread-safe.</remarks>
        /// <param name="name"></param>
        /// <returns>The resource string, or null if not found.</returns>
        internal static string GetString(string name)
        {
            // NOTE: the ResourceManager.GetString() method is thread-safe
            string resource = s_sharedResources.GetString(name, CultureInfo.CurrentUICulture);

            VerifyThrow(resource != null, "Missing resource '{0}'", name);

            return resource;
        }

        internal static ResourceManager ResourceManager
        {
            get { return s_sharedResources; }
        }


        // shared resources
        private static readonly ResourceManager s_sharedResources = new ResourceManager("MSBuild.Strings.shared", typeof(SharedResources).GetTypeInfo().Assembly);
    }
}
