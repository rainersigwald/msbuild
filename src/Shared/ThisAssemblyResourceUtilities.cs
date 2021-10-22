// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Resources;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.ComponentModel;

using static Microsoft.Build.Shared.ErrorUtilities;

#if BUILDINGAPPXTASKS
namespace Microsoft.Build.AppxPackage.Shared
#else
namespace Microsoft.Build.Shared
#endif
{
    /// <summary>
    /// This class contains utility methods for dealing with resources.
    /// </summary>
    internal static class ThisAssemblyResourceUtilities
    {
#if !BUILDINGAPPXTASKS
        /// <summary>
        /// Retrieves the contents of the named resource string.
        /// </summary>
        /// <param name="resourceName">Resource string name.</param>
        /// <returns>Resource string contents.</returns>
        internal static string GetResourceString(string resourceName)
        {
            string result = AssemblyResources.GetString(resourceName);
            return result;
        }

        /// <summary>
        /// Loads the specified string resource and formats it with the arguments passed in. If the string resource has an MSBuild
        /// message code and help keyword associated with it, they too are returned.
        ///
        /// PERF WARNING: calling a method that takes a variable number of arguments is expensive, because memory is allocated for
        /// the array of arguments -- do not call this method repeatedly in performance-critical scenarios
        /// </summary>
        /// <remarks>This method is thread-safe.</remarks>
        /// <param name="code">[out] The MSBuild message code, or null.</param>
        /// <param name="helpKeyword">[out] The MSBuild F1-help keyword for the host IDE, or null.</param>
        /// <param name="resourceName">Resource string to load.</param>
        /// <param name="args">Optional arguments for formatting the resource string.</param>
        /// <returns>The formatted resource string.</returns>
        internal static string FormatResourceStringStripCodeAndKeyword(out string code, out string helpKeyword, string resourceName, params object[] args)
        {
            helpKeyword = GetHelpKeyword(resourceName);

            // NOTE: the AssemblyResources.GetString() method is thread-safe
            return ExtractMessageCode(true /* msbuildCodeOnly */, FormatString(GetResourceString(resourceName), args), out code);
        }

        [Obsolete("Use GetResourceString instead.", true)]
        [EditorBrowsable(EditorBrowsableState.Never)]
        internal static string FormatResourceString(string resourceName)
        {   // Avoids an accidental dependency on FormatResourceString(string, params object[])
            return null;
        }

        /// <summary>
        /// Looks up a string in the resources, and formats it with the arguments passed in. If the string resource has an MSBuild
        /// message code and help keyword associated with it, they are discarded.
        ///
        /// PERF WARNING: calling a method that takes a variable number of arguments is expensive, because memory is allocated for
        /// the array of arguments -- do not call this method repeatedly in performance-critical scenarios
        /// </summary>
        /// <remarks>This method is thread-safe.</remarks>
        /// <param name="resourceName">Resource string to load.</param>
        /// <param name="args">Optional arguments for formatting the resource string.</param>
        /// <returns>The formatted resource string.</returns>
        internal static string FormatResourceStringStripCodeAndKeyword(string resourceName, params object[] args)
        {
            string code;
            string helpKeyword;

            return FormatResourceStringStripCodeAndKeyword(out code, out helpKeyword, resourceName, args);
        }

        /// <summary>
        /// Formats the resource string with the given arguments.
        /// Ignores error codes and keywords
        /// </summary>
        /// <param name="resourceName"></param>
        /// <param name="args"></param>
        /// <returns></returns>
        internal static string FormatResourceStringIgnoreCodeAndKeyword(string resourceName, params object[] args)
        {
            // NOTE: the AssemblyResources.GetString() method is thread-safe
            return FormatString(GetResourceString(resourceName), args);
        }

        /// <summary>
        /// Verifies that a particular resource string actually exists in the string table. This will only be called in debug
        /// builds. It helps catch situations where a dev calls VerifyThrowXXX with a new resource string, but forgets to add the
        /// resource string to the string table, or misspells it!
        /// </summary>
        /// <remarks>This method is thread-safe.</remarks>
        /// <param name="resourceName">Resource string to check.</param>
        internal static void VerifyResourceStringExists(string resourceName)
        {
#if DEBUG
            try
            {
                // Look up the resource string in the engine's string table.
                // NOTE: the AssemblyResources.GetString() method is thread-safe
                string unformattedMessage = AssemblyResources.GetString(resourceName);

                if (unformattedMessage == null)
                {
                    ThrowInternalError("The resource string \"" + resourceName + "\" was not found.");
                }
            }
            catch (ArgumentException e)
            {
#if FEATURE_DEBUG_LAUNCH
                Debug.Fail("The resource string \"" + resourceName + "\" was not found.");
#endif
                ThrowInternalError(e.Message);
            }
            catch (InvalidOperationException e)
            {
#if FEATURE_DEBUG_LAUNCH
                Debug.Fail("The resource string \"" + resourceName + "\" was not found.");
#endif
                ThrowInternalError(e.Message);
            }
            catch (MissingManifestResourceException e)
            {
#if FEATURE_DEBUG_LAUNCH
                Debug.Fail("The resource string \"" + resourceName + "\" was not found.");
#endif
                ThrowInternalError(e.Message);
            }
#endif
        }
#endif
    }
}
