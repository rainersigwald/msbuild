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
    internal static class ResourceUtilities
    {
        /// <summary>
        /// Extracts the message code (if any) prefixed to the given string.
        /// <![CDATA[
        /// MSBuild codes match "^\s*(?<CODE>MSB\d\d\d\d):\s*(?<MESSAGE>.*)$"
        /// Arbitrary codes match "^\s*(?<CODE>[A-Za-z]+\d+):\s*(?<MESSAGE>.*)$"
        /// ]]>
        /// Thread safe.
        /// </summary>
        /// <param name="msbuildCodeOnly">Whether to match only MSBuild error codes, or any error code.</param>
        /// <param name="message">The string to parse.</param>
        /// <param name="code">[out] The message code, or null if there was no code.</param>
        /// <returns>The string without its message code prefix, if any.</returns>
        [SuppressMessage("Microsoft.Maintainability", "CA1502:AvoidExcessiveComplexity", Scope = "member", Target = "Microsoft.Build.Shared.#ExtractMessageCode(System.Boolean,System.String,System.String&)", Justification = "Unavoidable complexity")]
        internal static string ExtractMessageCode(bool msbuildCodeOnly, string message, out string code)
        {
#if !BUILDINGAPPXTASKS
            VerifyThrowInternalNull(message, nameof(message));
#endif

            code = null;
            int i = 0;

            while (i < message.Length && Char.IsWhiteSpace(message[i]))
            {
                i++;
            }

#if !BUILDINGAPPXTASKS
            if (msbuildCodeOnly)
            {
                if (
                    message.Length < i + 8 ||
                    message[i] != 'M' ||
                    message[i + 1] != 'S' ||
                    message[i + 2] != 'B' ||
                    message[i + 3] < '0' || message[i + 3] > '9' ||
                    message[i + 4] < '0' || message[i + 4] > '9' ||
                    message[i + 5] < '0' || message[i + 5] > '9' ||
                    message[i + 6] < '0' || message[i + 6] > '9' ||
                    message[i + 7] != ':'
                    )
                {
                    return message;
                }

                code = message.Substring(i, 7);

                i += 8;
            }
            else
#endif
            {
                int j = i;
                for (; j < message.Length; j++)
                {
                    char c = message[j];
                    if (((c < 'a') || (c > 'z')) && ((c < 'A') || (c > 'Z')))
                    {
                        break;
                    }
                }

                if (j == i)
                {
                    return message; // Should have been at least one letter
                }

                int k = j;

                for (; k < message.Length; k++)
                {
                    char c = message[k];
                    if (c < '0' || c > '9')
                    {
                        break;
                    }
                }

                if (k == j)
                {
                    return message; // Should have been at least one digit
                }

                if (k == message.Length || message[k] != ':')
                {
                    return message;
                }

                code = message.Substring(i, k - i);

                i = k + 1;
            }

            while (i < message.Length && Char.IsWhiteSpace(message[i]))
            {
                i++;
            }

            if (i < message.Length)
            {
                message = message.Substring(i, message.Length - i);
            }

            return message;
        }

        /// <summary>
        /// Retrieves the MSBuild F1-help keyword for the given resource string. Help keywords are used to index help topics in
        /// host IDEs.
        /// </summary>
        /// <param name="resourceName">Resource string to get the MSBuild F1-keyword for.</param>
        /// <returns>The MSBuild F1-help keyword string.</returns>
        private static string GetHelpKeyword(string resourceName)
        {
            return "MSBuild." + resourceName;
        }

#if !BUILDINGAPPXTASKS
        /// <summary>
        /// Formats the given string using the variable arguments passed in.
        ///
        /// PERF WARNING: calling a method that takes a variable number of arguments is expensive, because memory is allocated for
        /// the array of arguments -- do not call this method repeatedly in performance-critical scenarios
        ///
        /// Thread safe.
        /// </summary>
        /// <param name="unformatted">The string to format.</param>
        /// <param name="args">Optional arguments for formatting the given string.</param>
        /// <returns>The formatted string.</returns>
        internal static string FormatString(string unformatted, params object[] args)
        {
            string formatted = unformatted;

            // NOTE: String.Format() does not allow a null arguments array
            if ((args?.Length > 0))
            {
#if DEBUG
                // If you accidentally pass some random type in that can't be converted to a string,
                // FormatResourceString calls ToString() which returns the full name of the type!
                foreach (object param in args)
                {
                    // Check it has a real implementation of ToString() and the type is not actually System.String
                    if (param != null)
                    {
                        if (string.Equals(param.GetType().ToString(), param.ToString(), StringComparison.Ordinal) &&
                            param.GetType() != typeof(string))
                        {
                            ThrowInternalError("Invalid resource parameter type, was {0}",
                                param.GetType().FullName);
                        }
                    }
                }
#endif
                // Format the string, using the variable arguments passed in.
                // NOTE: all String methods are thread-safe
                formatted = String.Format(CultureInfo.CurrentCulture, unformatted, args);
            }

            return formatted;
        }
#endif
    }
}
