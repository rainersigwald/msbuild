// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO;
using System.Globalization;
using System.Text;
using System.Diagnostics;
using System.Reflection;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;

using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Utilities;

#if (!STANDALONEBUILD)
using Microsoft.Internal.Performance;
#endif
using FrameworkNameVersioning = System.Runtime.Versioning.FrameworkName;
using SystemProcessorArchitecture = System.Reflection.ProcessorArchitecture;
using System.Xml.Linq;
using Microsoft.Build.Tasks.AssemblyDependency;

namespace Microsoft.Build.Tasks
{
    /// <summary>
    /// Given a list of assemblyFiles, determine the closure of all assemblyFiles that
    /// depend on those assemblyFiles including second and nth-order dependencies too.
    /// </summary>
    internal class ResolveAssemblyReferenceImpl : TaskExtension
    {
        public ResolveAssemblyReferenceRequest _request { get; set; }

        public ResolveAssemblyReferenceResponse _response { get; } = new ResolveAssemblyReferenceResponse();

        /// <summary>
        /// key assembly used to trigger inclusion of facade references. 
        /// </summary>
        private const string SystemRuntimeAssemblyName = "System.Runtime";

        /// <summary>
        /// additional key assembly used to trigger inclusion of facade references. 
        /// </summary>
        private const string NETStandardAssemblyName = "netstandard";

        /// <summary>
        /// Delegate to a method that takes a targetFrameworkDirectory and returns an array of redist or subset list paths
        /// </summary>
        /// <param name="targetFrameworkDirectory">TargetFramework directory to search for redist or subset list</param>
        /// <returns>String array of redist or subset lists</returns>
        private delegate string[] GetListPath(string targetFrameworkDirectory);

        /// <summary>
        /// Cache of system state information, used to optimize performance.
        /// </summary>
        private SystemState _cache = null;

        /// <summary>
        /// Construct
        /// </summary>
        public ResolveAssemblyReferenceImpl()
        {
        }

        /// <summary>
        /// Construct
        /// </summary>
        public ResolveAssemblyReferenceImpl(ResolveAssemblyReferenceRequest request)
        {
            _request = request;
        }

        #region Properties
        private ITaskItem[] _resolvedDependencyFiles = Array.Empty<TaskItem>();
        private ITaskItem[] _relatedFiles = Array.Empty<TaskItem>();
        private ITaskItem[] _satelliteFiles = Array.Empty<TaskItem>();
        private ITaskItem[] _serializationAssemblyFiles = Array.Empty<TaskItem>();
        private ITaskItem[] _scatterFiles = Array.Empty<TaskItem>();
        private ITaskItem[] _copyLocalFiles = Array.Empty<TaskItem>();
        private Version _projectTargetFramework;
        private string _profileName = String.Empty;
        private Dictionary<string, MessageImportance> _showAssemblyFoldersExLocations = new Dictionary<string, MessageImportance>(StringComparer.OrdinalIgnoreCase);
        private bool _logVerboseSearchResults = false;

        /// <summary>
        /// A list of all n-th order paths-to-dependencies with the following attributes:
        ///     bool CopyLocal - whether the given reference should be copied to the output directory.
        ///     string FusionName - the fusion name for this dependency.
        ///     string ResolvedFrom - the literal search path that this file was resolved from.
        ///     bool IsRedistRoot - Whether or not this assembly is the representative for an entire redist.
        ///         'true' means the assembly is representative of an entire redist and should be indicated as 
        ///         an application dependency in an application manifest.
        ///         'false' means the assembly is internal to a redist and should not be part of the 
        ///         application manifest.
        ///     string Redist - The name (if any) of the redist that contains this assembly.
        /// Does not include first order primary references--this list is in ResolvedFiles.
        /// </summary>
        [Output]
        public ITaskItem[] ResolvedDependencyFiles
        {
            get { return _resolvedDependencyFiles; }
        }

        /// <summary>
        /// Related files are files like intellidoc (.XML) and symbols (.PDB) that have the same base
        /// name as a reference.
        ///     bool Primary [always false] - true if this assembly was passed in with _request.Assemblies.
        ///     bool CopyLocal - whether the given reference should be copied to the output directory.
        /// </summary>
        [Output]
        public ITaskItem[] RelatedFiles
        {
            get { return _relatedFiles; }
        }

        /// <summary>
        /// Any satellite files found. These will be CopyLocal=true iff the reference or dependency 
        /// that caused this item to exist is CopyLocal=true.
        ///     bool CopyLocal - whether the given reference should be copied to the output directory.
        ///     string DestinationSubDirectory - the relative destination directory that this file 
        ///       should be copied to. This is mainly for satellites.
        /// </summary>
        [Output]
        public ITaskItem[] SatelliteFiles
        {
            get { return _satelliteFiles; }
        }

        /// <summary>
        /// Any XML serialization assemblies found. These will be CopyLocal=true iff the reference or dependency 
        /// that caused this item to exist is CopyLocal=true.
        ///     bool CopyLocal - whether the given reference should be copied to the output directory.
        /// </summary>
        [Output]
        public ITaskItem[] SerializationAssemblyFiles
        {
            get { return _serializationAssemblyFiles; }
        }

        /// <summary>
        /// Scatter files associated with one of the given assemblies.
        ///     bool CopyLocal - whether the given reference should be copied to the output directory.
        /// </summary>
        [Output]
        public ITaskItem[] ScatterFiles
        {
            get { return _scatterFiles; }
        }

        /// <summary>
        /// Returns every file in ResolvedFiles+ResolvedDependencyFiles+RelatedFiles+SatelliteFiles+ScatterFiles+SatelliteAssemblyFiles
        /// that have CopyLocal flags set to 'true'.
        /// </summary>
        /// <value></value>
        [Output]
        public ITaskItem[] CopyLocalFiles
        {
            get { return _copyLocalFiles; }
        }

        /// <summary>
        /// Regardless of the value of AutoUnify, returns one item for every distinct conflicting assembly 
        /// identity--including culture and PKT--that was found that did not have a suitable bindingRedirect 
        /// entry in the ApplicationConfigurationFile. 
        ///
        /// Each returned ITaskItem will have the following values:
        ///  ItemSpec - the full fusion name of the assembly family with empty version=0.0.0.0
        ///  MaxVersion - the maximum version number.
        /// </summary>
        [Output]
        public ITaskItem[] SuggestedRedirects { get; private set; } = Array.Empty<TaskItem>();

        /// <summary>
        /// Storage for names of all files writen to disk.
        /// </summary>
        private ArrayList _filesWritten = new ArrayList();

        /// <summary>
        /// The names of all files written to disk.
        /// </summary>
        [Output]
        public ITaskItem[] FilesWritten
        {
            set { /*Do Nothing, Inputs not Allowed*/ }
            get { return (ITaskItem[])_filesWritten.ToArray(typeof(ITaskItem)); }
        }

        /// <summary>
        /// Whether the assembly or any of its primary references depends on system.runtime. (Aka needs Facade references to resolve duplicate types)
        /// </summary>
        [Output]
        public String DependsOnSystemRuntime
        {
            get;
            private set;
        }

        /// <summary>
        /// Whether the assembly or any of its primary references depends on netstandard
        /// </summary>
        [Output]
        public String DependsOnNETStandard
        {
            get;
            private set;
        }


        #endregion
        #region Logging

        /// <summary>
        /// Log the results.
        /// </summary>
        /// <param name="dependencyTable">Reference table.</param>
        /// <param name="idealAssemblyRemappings">Array of ideal assembly remappings.</param>
        /// <param name="idealAssemblyRemappingsIdentities">Array of identities of ideal assembly remappings.</param>
        /// <param name="generalResolutionExceptions">List of exceptions that were not attributable to a particular fusion name.</param>
        /// <returns></returns>
        private bool LogResults
        (
            ReferenceTable dependencyTable,
            DependentAssembly[] idealAssemblyRemappings,
            AssemblyNameReference[] idealAssemblyRemappingsIdentities,
            ArrayList generalResolutionExceptions
        )
        {
            bool success = true;
#if (!STANDALONEBUILD)
            using (new CodeMarkerStartEnd(CodeMarkerEvent.perfMSBuildRARLogResultsBegin, CodeMarkerEvent.perfMSBuildRARLogResultsEnd))
#endif
            {
                /*
                PERF NOTE: The _request.Silent flag turns off logging completely from the task side. This means
                we avoid the String.Formats that would normally occur even if the verbosity was set to 
                quiet at the engine level.
                */
                if (!_request.Silent)
                {
                    // First, loop over primaries and display information.
                    foreach (AssemblyNameExtension assemblyName in dependencyTable.References.Keys)
                    {
                        string fusionName = assemblyName.FullName;
                        Reference primaryCandidate = dependencyTable.GetReference(assemblyName);

                        if (primaryCandidate.IsPrimary && !(primaryCandidate.IsConflictVictim && primaryCandidate.IsCopyLocal))
                        {
                            LogReference(primaryCandidate, fusionName);
                        }
                    }

                    // Second, loop over dependencies and display information.
                    foreach (AssemblyNameExtension assemblyName in dependencyTable.References.Keys)
                    {
                        string fusionName = assemblyName.FullName;
                        Reference dependencyCandidate = dependencyTable.GetReference(assemblyName);

                        if (!dependencyCandidate.IsPrimary && !(dependencyCandidate.IsConflictVictim && dependencyCandidate.IsCopyLocal))
                        {
                            LogReference(dependencyCandidate, fusionName);
                        }
                    }

                    // Third, show conflicts and their resolution.
                    foreach (AssemblyNameExtension assemblyName in dependencyTable.References.Keys)
                    {
                        string fusionName = assemblyName.FullName;
                        Reference conflictCandidate = dependencyTable.GetReference(assemblyName);

                        if (conflictCandidate.IsConflictVictim)
                        {
                            LogConflict(conflictCandidate, fusionName);

                            // Log the assemblies and primary source items which are related to the conflict which was just logged.
                            Reference victor = dependencyTable.GetReference(conflictCandidate.ConflictVictorName);

                            // Log the winner of the conflict resolution, the source items and dependencies which caused it
                            LogReferenceDependenciesAndSourceItems(conflictCandidate.ConflictVictorName.FullName, victor);

                            // Log the reference which lost the conflict and the dependencies and source items which caused it.
                            LogReferenceDependenciesAndSourceItems(fusionName, conflictCandidate);
                        }
                    }

                    // Fourth, if there were any suggested redirects. Show one message per redirect and a single warning.
                    if (idealAssemblyRemappings != null)
                    {
                        bool foundAtLeastOneValidBindingRedirect = false;

                        var buffer = new StringBuilder();
                        var ns = XNamespace.Get("urn:schemas-microsoft-com:asm.v1");

                        // A high-priority message for each individual redirect.
                        for (int i = 0; i < idealAssemblyRemappings.Length; i++)
                        {
                            DependentAssembly idealRemapping = idealAssemblyRemappings[i];
                            AssemblyName idealRemappingPartialAssemblyName = idealRemapping.PartialAssemblyName;
                            Reference reference = idealAssemblyRemappingsIdentities[i].reference;

                            AssemblyNameExtension[] conflictVictims = reference.GetConflictVictims();

                            for (int j = 0; j < idealRemapping.BindingRedirects.Length; j++)
                            {
                                foreach (AssemblyNameExtension conflictVictim in conflictVictims)
                                {
                                    // Make note we only output a conflict suggestion if the reference has at 
                                    // least one conflict victim - that way we don't suggest redirects to 
                                    // assemblies that don't exist at runtime. For example, this avoids us suggesting
                                    // a redirect from Foo 1.0.0.0 -> 2.0.0.0 in the following:
                                    //
                                    //      Project -> Foo, 1.0.0.0
                                    //      Project -> Bar -> Foo, 2.0.0.0
                                    //
                                    // Above, Foo, 1.0.0.0 wins out and is copied to the output directory because 
                                    // it is a primary reference.
                                    foundAtLeastOneValidBindingRedirect = true;

                                    Reference victimReference = dependencyTable.GetReference(conflictVictim);
                                    var newVerStr = idealRemapping.BindingRedirects[j].NewVersion.ToString();
                                    Log.LogMessageFromResources
                                    (
                                        MessageImportance.High,
                                        "ResolveAssemblyReference.ConflictRedirectSuggestion",
                                        idealRemappingPartialAssemblyName,
                                        conflictVictim.Version,
                                        victimReference.FullPath,
                                        newVerStr,
                                        reference.FullPath
                                    );

                                    if (!_request.SupportsBindingRedirectGeneration && !_request.AutoUnify)
                                    {
                                        // When running against projects types (such as Web Projects) where we can't auto-generate
                                        // binding redirects during the build, populate a buffer (to be output below) with the
                                        // binding redirect syntax that users need to add manually to the App.Config.

                                        var assemblyIdentityAttributes = new List<XAttribute>(4);

                                        assemblyIdentityAttributes.Add(new XAttribute("name", idealRemappingPartialAssemblyName.Name));

                                        // We use "neutral" for "Invariant Language (Invariant Country)" in assembly names.
                                        var cultureString = idealRemappingPartialAssemblyName.CultureName;
                                        assemblyIdentityAttributes.Add(new XAttribute("culture", String.IsNullOrEmpty(idealRemappingPartialAssemblyName.CultureName) ? "neutral" : idealRemappingPartialAssemblyName.CultureName));

                                        var publicKeyToken = idealRemappingPartialAssemblyName.GetPublicKeyToken();
                                        assemblyIdentityAttributes.Add(new XAttribute("publicKeyToken", ResolveAssemblyReferenceImpl.ByteArrayToString(publicKeyToken)));

                                        var node = new XElement(
                                            ns + "assemblyBinding",
                                            new XElement(
                                                ns + "dependentAssembly",
                                                new XElement(
                                                    ns + "assemblyIdentity",
                                                    assemblyIdentityAttributes),
                                                new XElement(
                                                    ns + "bindingRedirect",
                                                    new XAttribute("oldVersion", "0.0.0.0-" + newVerStr),
                                                    new XAttribute("newVersion", newVerStr))));

                                        buffer.Append(node.ToString(SaveOptions.DisableFormatting));
                                    }
                                }
                            }

                            if (conflictVictims.Length == 0)
                            {
                                // This warning is logged regardless of AutoUnify since it means a conflict existed where the reference
                                // chosen was not the conflict victor in a version comparison, in other words it was older.
                                Log.LogWarningWithCodeFromResources("ResolveAssemblyReference.FoundConflicts", idealRemappingPartialAssemblyName.Name);
                            }
                        }

                        // Log the warning
                        if (idealAssemblyRemappings.Length > 0 && foundAtLeastOneValidBindingRedirect)
                        {
                            if (_request.SupportsBindingRedirectGeneration)
                            {
                                if (!_request.AutoUnify)
                                {
                                    Log.LogWarningWithCodeFromResources("ResolveAssemblyReference.TurnOnAutoGenerateBindingRedirects");
                                }
                                // else we'll generate bindingRedirects to address the remappings
                            }
                            else if (!_request.AutoUnify)
                            {
                                Log.LogWarningWithCodeFromResources("ResolveAssemblyReference.SuggestedRedirects", buffer.ToString());
                            }
                            // else AutoUnify is on and bindingRedirect generation is not supported
                            // we don't warn in this case since the binder will automatically unify these remappings
                        }
                    }

                    // Fifth, log general resolution problems.

                    // Log general resolution exceptions.
                    foreach (Exception error in generalResolutionExceptions)
                    {
                        if (error is InvalidReferenceAssemblyNameException)
                        {
                            InvalidReferenceAssemblyNameException e = (InvalidReferenceAssemblyNameException)error;
                            Log.LogWarningWithCodeFromResources("General.MalformedAssemblyName", e.SourceItemSpec);
                        }
                        else
                        {
                            // An unknown Exception type was returned. Just throw.
                            throw error;
                        }
                    }
                }
            }

#if FEATURE_WIN32_REGISTRY
            if (dependencyTable.Resolvers != null)
            {
                foreach (Resolver r in dependencyTable.Resolvers)
                {
                    if (r is AssemblyFoldersExResolver)
                    {
                        AssemblyFoldersEx assemblyFoldersEx = ((AssemblyFoldersExResolver)r).AssemblyFoldersExLocations;

                        MessageImportance messageImportance = MessageImportance.Low;
                        if (assemblyFoldersEx != null && _showAssemblyFoldersExLocations.TryGetValue(r.SearchPath, out messageImportance))
                        {
                            Log.LogMessageFromResources(messageImportance, "ResolveAssemblyReference.AssemblyFoldersExSearchLocations", r.SearchPath);
                            foreach (AssemblyFoldersExInfo info in assemblyFoldersEx)
                            {
                                Log.LogMessageFromResources(messageImportance, "ResolveAssemblyReference.EightSpaceIndent", info.DirectoryPath);
                            }
                        }
                    }
                }
            }
#endif

            return success;
        }

        /// <summary>
        /// Used to generate the string representation of a public key token.
        /// </summary>
        internal static string ByteArrayToString(byte[] a)
        {
            if (a == null)
            {
                return null;
            }

            var buffer = new StringBuilder(a.Length * 2);
            for (int i = 0; i < a.Length; ++i)
                buffer.Append(a[i].ToString("x2", CultureInfo.InvariantCulture));

            return buffer.ToString();
        }

        /// <summary>
        /// Log the source items and dependencies which lead to a given item.
        /// </summary>
        private void LogReferenceDependenciesAndSourceItems(string fusionName, Reference conflictCandidate)
        {
            ErrorUtilities.VerifyThrowInternalNull(conflictCandidate, "ConflictCandidate");
            Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.FourSpaceIndent", ResourceUtilities.FormatResourceString("ResolveAssemblyReference.ReferenceDependsOn", fusionName, conflictCandidate.FullPath));

            if (conflictCandidate.IsPrimary)
            {
                if (conflictCandidate.IsResolved)
                {
                    LogDependeeReference(conflictCandidate);
                }
                else
                {
                    Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.EightSpaceIndent", ResourceUtilities.FormatResourceString("ResolveAssemblyReference.UnResolvedPrimaryItemSpec", conflictCandidate.PrimarySourceItem));
                }
            }

            // Log the references for the conflict victim
            foreach (Reference dependeeReference in conflictCandidate.GetDependees())
            {
                LogDependeeReference(dependeeReference);
            }
        }

        /// <summary>
        /// Log the dependee and the item specs which caused the dependee reference to be resolved.
        /// </summary>
        /// <param name="dependeeReference"></param>
        private void LogDependeeReference(Reference dependeeReference)
        {
            Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.EightSpaceIndent", dependeeReference.FullPath);

            Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.TenSpaceIndent", ResourceUtilities.FormatResourceString("ResolveAssemblyReference.PrimarySourceItemsForReference", dependeeReference.FullPath));
            foreach (ITaskItem sourceItem in dependeeReference.GetSourceItems())
            {
                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.TwelveSpaceIndent", sourceItem.ItemSpec);
            }
        }

        /// <summary>
        /// Display the information about how a reference was resolved.
        /// </summary>
        /// <param name="reference">The reference information</param>
        /// <param name="fusionName">The fusion name of the reference.</param>
        private void LogReference(Reference reference, string fusionName)
        {
            // Set an importance level to be used for secondary messages.
            MessageImportance importance = ChooseReferenceLoggingImportance(reference);

            // Log the fusion name and whether this is a primary or a dependency.
            LogPrimaryOrDependency(reference, fusionName, importance);

            // Are there errors to report for this item?
            LogReferenceErrors(reference, importance);

            // Show the full name.
            LogFullName(reference, importance);

            // If there is a list of assemblyFiles that was considered but then rejected,
            // show information about them.
            LogAssembliesConsideredAndRejected(reference, fusionName, importance);

            if (!reference.IsBadImage)
            {
                // Show the files that made this dependency necessary.
                LogDependees(reference, importance);

                // If there were any related files (like pdbs and xmls) then show them here.
                LogRelatedFiles(reference, importance);

                // If there were any satellite files then show them here.
                LogSatellites(reference, importance);

                // If there were any scatter files then show them.
                LogScatterFiles(reference, importance);

                // Show the CopyLocal state
                LogCopyLocalState(reference, importance);

                // Show the CopyLocal state
                LogImageRuntime(reference, importance);
            }
        }

        /// <summary>
        /// Choose an importance level for reporting information about this reference.
        /// </summary>
        /// <param name="reference">The reference.</param>
        private MessageImportance ChooseReferenceLoggingImportance(Reference reference)
        {
            MessageImportance importance = MessageImportance.Low;

            bool hadProblems = reference.GetErrors().Count > 0;

            // No problems means low importance.
            if (hadProblems)
            {
                if (reference.IsPrimary || reference.IsCopyLocal)
                {
                    // The user cares more about Primary files and CopyLocal files.
                    // Accordingly, we show messages about these files only in the higher verbosity levels
                    // but only if there were errors during the resolution process.
                    importance = MessageImportance.Normal;
                }
            }

            return importance;
        }

        /// <summary>
        /// Log all task inputs.
        /// </summary>
        private void LogInputs()
        {
            if (!_request.Silent)
            {
                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.LogTaskPropertyFormat", "TargetFrameworkMoniker");
                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.FourSpaceIndent", _request.TargetFrameworkMoniker);

                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.LogTaskPropertyFormat", "TargetFrameworkMonikerDisplayName");
                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.FourSpaceIndent", _request.TargetFrameworkMonikerDisplayName);

                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.LogTaskPropertyFormat", "TargetedRuntimeVersion");
                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.FourSpaceIndent", _request.TargetedRuntimeVersion);

                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.LogTaskPropertyFormat", "Assemblies");
                foreach (ITaskItem item in _request.Assemblies)
                {
                    Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.FourSpaceIndent", item.ItemSpec);
                    LogAttribute(item, ItemMetadataNames.privateMetadata);
                    LogAttribute(item, ItemMetadataNames.hintPath);
                    LogAttribute(item, ItemMetadataNames.specificVersion);
                    LogAttribute(item, ItemMetadataNames.embedInteropTypes);
                    LogAttribute(item, ItemMetadataNames.executableExtension);
                    LogAttribute(item, ItemMetadataNames.subType);
                }

                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.LogTaskPropertyFormat", "AssemblyFiles");
                foreach (ITaskItem item in _request.AssemblyFiles)
                {
                    Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.FourSpaceIndent", item.ItemSpec);
                    LogAttribute(item, ItemMetadataNames.privateMetadata);
                    LogAttribute(item, ItemMetadataNames.fusionName);
                }

                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.LogTaskPropertyFormat", "CandidateAssemblyFiles");
                foreach (string file in _request.CandidateAssemblyFiles)
                {
                    try
                    {
                        if (FileUtilities.HasExtension(file, _request.AllowedAssemblyExtensions))
                        {
                            Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.FourSpaceIndent", file);
                        }
                    }
                    catch (Exception e) when (ExceptionHandling.IsIoRelatedException(e))
                    {
                        throw new InvalidParameterValueException("CandidateAssemblyFiles", file, e.Message);
                    }
                }

                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.LogTaskPropertyFormat", "TargetFrameworkDirectories");
                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.FourSpaceIndent", String.Join(",", _request.TargetFrameworkDirectories));

                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.LogTaskPropertyFormat", "InstalledAssemblyTables");
                foreach (ITaskItem installedAssemblyTable in _request.InstalledAssemblyTables)
                {
                    Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.FourSpaceIndent", installedAssemblyTable);
                    LogAttribute(installedAssemblyTable, ItemMetadataNames.frameworkDirectory);
                }

                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.LogTaskPropertyFormat", "IgnoreInstalledAssemblyTable");
                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.FourSpaceIndent", _request.IgnoreDefaultInstalledAssemblyTables);

                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.LogTaskPropertyFormat", "SearchPaths");
                foreach (string path in _request.SearchPaths)
                {
                    Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.FourSpaceIndent", path);
                }

                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.LogTaskPropertyFormat", "AllowedAssemblyExtensions");
                foreach (string allowedAssemblyExtension in _request.AllowedAssemblyExtensions)
                {
                    Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.FourSpaceIndent", allowedAssemblyExtension);
                }

                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.LogTaskPropertyFormat", "AllowedRelatedFileExtensions");
                foreach (string allowedRelatedFileExtension in _request.AllowedRelatedFileExtensions)
                {
                    Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.FourSpaceIndent", allowedRelatedFileExtension);
                }

                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.LogTaskPropertyFormat", "AppConfigFile");
                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.FourSpaceIndent", _request.AppConfigFile);

                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.LogTaskPropertyFormat", "AutoUnify");
                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.FourSpaceIndent", _request.AutoUnify.ToString());

                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.LogTaskPropertyFormat", "CopyLocalDependenciesWhenParentReferenceInGac");
                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.FourSpaceIndent", _request.CopyLocalDependenciesWhenParentReferenceInGac);

                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.LogTaskPropertyFormat", "FindDependencies");
                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.FourSpaceIndent", _request.FindDependencies);

                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.LogTaskPropertyFormat", "TargetProcessorArchitecture");
                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.FourSpaceIndent", _request.TargetProcessorArchitecture);

                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.LogTaskPropertyFormat", "StateFile");
                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.FourSpaceIndent", _request.StateFile);

                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.LogTaskPropertyFormat", "InstalledAssemblySubsetTables");
                foreach (ITaskItem installedAssemblySubsetTable in _request.InstalledAssemblySubsetTables)
                {
                    Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.FourSpaceIndent", installedAssemblySubsetTable);
                    LogAttribute(installedAssemblySubsetTable, ItemMetadataNames.frameworkDirectory);
                }

                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.LogTaskPropertyFormat", "IgnoreInstalledAssemblySubsetTable");
                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.FourSpaceIndent", _request.IgnoreDefaultInstalledAssemblySubsetTables);

                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.LogTaskPropertyFormat", "TargetFrameworkSubsets");
                foreach (string subset in _request.TargetFrameworkSubsets)
                {
                    Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.FourSpaceIndent", subset);
                }

                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.LogTaskPropertyFormat", "FullTargetFrameworkSubsetNames");
                foreach (string subset in _request.FullTargetFrameworkSubsetNames)
                {
                    Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.FourSpaceIndent", subset);
                }

                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.LogTaskPropertyFormat", "ProfileName");
                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.FourSpaceIndent", _request.ProfileName);

                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.LogTaskPropertyFormat", "FullFrameworkFolders");
                foreach (string fullFolder in _request.FullFrameworkFolders)
                {
                    Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.FourSpaceIndent", fullFolder);
                }

                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.LogTaskPropertyFormat", "LatestTargetFrameworkDirectories");
                foreach (string latestFolder in _request.LatestTargetFrameworkDirectories)
                {
                    Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.FourSpaceIndent", latestFolder);
                }


                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.LogTaskPropertyFormat", "ProfileTablesLocation");
                foreach (ITaskItem profileTable in _request.FullFrameworkAssemblyTables)
                {
                    Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.FourSpaceIndent", profileTable);
                    LogAttribute(profileTable, ItemMetadataNames.frameworkDirectory);
                }
            }
        }

        /// <summary>
        /// Log a specific item metadata.
        /// </summary>
        /// <param name="item"></param>
        /// <param name="attribute"></param>
        private void LogAttribute(ITaskItem item, string metadataName)
        {
            string metadataValue = item.GetMetadata(metadataName);
            if (metadataValue != null && metadataValue.Length > 0)
            {
                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.EightSpaceIndent", Log.FormatResourceString("ResolveAssemblyReference.LogAttributeFormat", metadataName, metadataValue));
            }
        }

        /// <summary>
        /// Describes whether this reference is primary or not
        /// </summary>
        /// <param name="reference">The reference.</param>
        /// <param name="fusionName">The fusion name for this reference.</param>
        /// <param name="importance">The importance of the message.</param>
        private void LogPrimaryOrDependency(Reference reference, string fusionName, MessageImportance importance)
        {
            if (reference.IsPrimary)
            {
                if (reference.IsUnified)
                {
                    Log.LogMessageFromResources(importance, "ResolveAssemblyReference.UnifiedPrimaryReference", fusionName);
                }
                else
                {
                    Log.LogMessageFromResources(importance, "ResolveAssemblyReference.PrimaryReference", fusionName);
                }
            }
            else
            {
                if (reference.IsUnified)
                {
                    Log.LogMessageFromResources(importance, "ResolveAssemblyReference.UnifiedDependency", fusionName);
                }
                else
                {
                    Log.LogMessageFromResources(importance, "ResolveAssemblyReference.Dependency", fusionName);
                }
            }

            foreach (UnificationVersion unificationVersion in reference.GetPreUnificationVersions())
            {
                switch (unificationVersion.reason)
                {
                    case UnificationReason.BecauseOfBindingRedirect:
                        if (_request.AutoUnify)
                        {
                            Log.LogMessageFromResources(importance, "ResolveAssemblyReference.FourSpaceIndent", Log.FormatResourceString("ResolveAssemblyReference.UnificationByAutoUnify", unificationVersion.version, unificationVersion.referenceFullPath));
                        }
                        else
                        {
                            Log.LogMessageFromResources(importance, "ResolveAssemblyReference.FourSpaceIndent", Log.FormatResourceString("ResolveAssemblyReference.UnificationByAppConfig", unificationVersion.version, _request.AppConfigFile, unificationVersion.referenceFullPath));
                        }
                        break;

                    case UnificationReason.FrameworkRetarget:
                        Log.LogMessageFromResources(importance, "ResolveAssemblyReference.FourSpaceIndent", Log.FormatResourceString("ResolveAssemblyReference.UnificationByFrameworkRetarget", unificationVersion.version, unificationVersion.referenceFullPath));
                        break;

                    case UnificationReason.DidntUnify:
                        break;

                    default:
                        Debug.Assert(false, "Should have handled this case.");
                        break;
                }
            }

            foreach (AssemblyRemapping remapping in reference.RemappedAssemblyNames())
            {
                Log.LogMessageFromResources(importance, "ResolveAssemblyReference.FourSpaceIndent", Log.FormatResourceString("ResolveAssemblyReference.RemappedReference", remapping.From.FullName, remapping.To.FullName));
            }
        }

        /// <summary>
        /// Log any errors for a reference.
        /// </summary>
        /// <param name="reference">The reference.</param>
        /// <param name="importance">The importance of the message.</param>
        private void LogReferenceErrors(Reference reference, MessageImportance importance)
        {
            ICollection itemErrors = reference.GetErrors();
            foreach (Exception itemError in itemErrors)
            {
                string message = String.Empty;
                string helpKeyword = null;
                bool dependencyProblem = false;

                if (itemError is ReferenceResolutionException)
                {
                    message = Log.FormatResourceString("ResolveAssemblyReference.FailedToResolveReference", itemError.Message);
                    helpKeyword = "MSBuild.ResolveAssemblyReference.FailedToResolveReference";
                    dependencyProblem = false;
                }
                else if (itemError is DependencyResolutionException)
                {
                    message = Log.FormatResourceString("ResolveAssemblyReference.FailedToFindDependentFiles", itemError.Message);
                    helpKeyword = "MSBuild.ResolveAssemblyReference.FailedToFindDependentFiles";
                    dependencyProblem = true;
                }
                else if (itemError is BadImageReferenceException)
                {
                    message = Log.FormatResourceString("ResolveAssemblyReference.FailedWithException", itemError.Message);
                    helpKeyword = "MSBuild.ResolveAssemblyReference.FailedWithException";
                    dependencyProblem = false;
                }
                else
                {
                    Debug.Assert(false, "Unexpected exception type.");
                }

                string messageOnly;
                string warningCode = Log.ExtractMessageCode(message, out messageOnly);

                // Treat as warning if this is primary and the problem wasn't with a dependency, otherwise, make it a comment.
                if (reference.IsPrimary && !dependencyProblem)
                {
                    // Treat it as a warning
                    Log.LogWarning(null, warningCode, helpKeyword, null, 0, 0, 0, 0, messageOnly);
                }
                else
                {
                    // Just show the the message as a comment.
                    Log.LogMessageFromResources(importance, "ResolveAssemblyReference.FourSpaceIndent", messageOnly);
                }
            }
        }

        /// <summary>
        /// Show the full name of a reference.
        /// </summary>
        /// <param name="reference">The reference.</param>
        /// <param name="importance">The importance of the message.</param>
        private void LogFullName(Reference reference, MessageImportance importance)
        {
            ErrorUtilities.VerifyThrowArgumentNull(reference, "reference");

            if (reference.IsResolved)
            {
                Log.LogMessageFromResources(importance, "ResolveAssemblyReference.FourSpaceIndent", Log.FormatResourceString("ResolveAssemblyReference.Resolved", reference.FullPath));
                Log.LogMessageFromResources(importance, "ResolveAssemblyReference.FourSpaceIndent", Log.FormatResourceString("ResolveAssemblyReference.ResolvedFrom", reference.ResolvedSearchPath));
            }
        }

        /// <summary>
        /// If there is a list of assemblyFiles that was considered but then rejected,
        /// show information about them.
        /// </summary>
        /// <param name="reference">The reference.</param>
        /// <param name="importance">The importance of the message.</param>
        private void LogAssembliesConsideredAndRejected(Reference reference, string fusionName, MessageImportance importance)
        {
            if (reference.AssembliesConsideredAndRejected != null)
            {
                string lastSearchPath = null;

                foreach (ResolutionSearchLocation location in reference.AssembliesConsideredAndRejected)
                {
                    // We need to keep track if whether or not we need to log the assemblyfoldersex folder structure at the end of RAR.
                    // We only need to do so if we logged a message indicating we looked in the assemblyfoldersex location
                    bool containsAssemblyFoldersExSentinel = String.Compare(location.SearchPath, 0, AssemblyResolutionConstants.assemblyFoldersExSentinel, 0, AssemblyResolutionConstants.assemblyFoldersExSentinel.Length, StringComparison.OrdinalIgnoreCase) == 0;
                    bool logAssemblyFoldersMinimal = containsAssemblyFoldersExSentinel && !_logVerboseSearchResults;
                    if (logAssemblyFoldersMinimal)
                    {
                        // We not only need to track if we logged a message but also what importance. We want the logging of the assemblyfoldersex folder structure to match the same importance.
                        MessageImportance messageImportance = MessageImportance.Low;
                        if (!_showAssemblyFoldersExLocations.TryGetValue(location.SearchPath, out messageImportance))
                        {
                            _showAssemblyFoldersExLocations.Add(location.SearchPath, importance);
                        }

                        if ((messageImportance == MessageImportance.Low && (importance == MessageImportance.Normal || importance == MessageImportance.High)) ||
                            (messageImportance == MessageImportance.Normal && importance == MessageImportance.High)
                           )
                        {
                            _showAssemblyFoldersExLocations[location.SearchPath] = importance;
                        }
                    }


                    // If this is a new search location, then show the message.
                    if (lastSearchPath != location.SearchPath)
                    {
                        lastSearchPath = location.SearchPath;
                        Log.LogMessageFromResources(importance, "ResolveAssemblyReference.EightSpaceIndent", Log.FormatResourceString("ResolveAssemblyReference.SearchPath", lastSearchPath));
                        if (logAssemblyFoldersMinimal)
                        {
                            Log.LogMessageFromResources(importance, "ResolveAssemblyReference.EightSpaceIndent", Log.FormatResourceString("ResolveAssemblyReference.SearchedAssemblyFoldersEx"));
                        }
                    }

                    // Show a message based on the reason.
                    switch (location.Reason)
                    {
                        case NoMatchReason.FileNotFound:
                            {
                                if (!logAssemblyFoldersMinimal)
                                {
                                    Log.LogMessageFromResources(importance, "ResolveAssemblyReference.EightSpaceIndent", Log.FormatResourceString("ResolveAssemblyReference.ConsideredAndRejectedBecauseNoFile", location.FileNameAttempted));
                                }
                                break;
                            }
                        case NoMatchReason.FusionNamesDidNotMatch:
                            Log.LogMessageFromResources(importance, "ResolveAssemblyReference.EightSpaceIndent", Log.FormatResourceString("ResolveAssemblyReference.ConsideredAndRejectedBecauseFusionNamesDidntMatch", location.FileNameAttempted, location.AssemblyName.FullName, fusionName));
                            break;

                        case NoMatchReason.TargetHadNoFusionName:
                            Log.LogMessageFromResources(importance, "ResolveAssemblyReference.EightSpaceIndent", Log.FormatResourceString("ResolveAssemblyReference.ConsideredAndRejectedBecauseTargetDidntHaveFusionName", location.FileNameAttempted));
                            break;

                        case NoMatchReason.NotInGac:
                            Log.LogMessageFromResources(importance, "ResolveAssemblyReference.EightSpaceIndent", Log.FormatResourceString("ResolveAssemblyReference.ConsideredAndRejectedBecauseNotInGac", location.FileNameAttempted));
                            break;

                        case NoMatchReason.NotAFileNameOnDisk:
                            {
                                if (!logAssemblyFoldersMinimal)
                                {
                                    Log.LogMessageFromResources(importance, "ResolveAssemblyReference.EightSpaceIndent", Log.FormatResourceString("ResolveAssemblyReference.ConsideredAndRejectedBecauseNotAFileNameOnDisk", location.FileNameAttempted));
                                }

                                break;
                            }
                        case NoMatchReason.ProcessorArchitectureDoesNotMatch:
                            Log.LogMessageFromResources(importance, "ResolveAssemblyReference.EightSpaceIndent", Log.FormatResourceString("ResolveAssemblyReference.TargetedProcessorArchitectureDoesNotMatch", location.FileNameAttempted, location.AssemblyName.AssemblyName.ProcessorArchitecture.ToString(), _request.TargetProcessorArchitecture));
                            break;
                        default:
                            Debug.Assert(false, "Should have handled this case.");
                            break;
                    }
                }
            }
        }

        /// <summary>
        /// Show the files that made this dependency necessary.
        /// </summary>
        /// <param name="reference">The reference.</param>
        /// <param name="importance">The importance of the message.</param>
        private void LogDependees(Reference reference, MessageImportance importance)
        {
            if (!reference.IsPrimary)
            {
                ICollection dependees = reference.GetSourceItems();
                foreach (ITaskItem dependee in dependees)
                {
                    Log.LogMessageFromResources(importance, "ResolveAssemblyReference.FourSpaceIndent", Log.FormatResourceString("ResolveAssemblyReference.RequiredBy", dependee.ItemSpec));
                }
            }
        }

        /// <summary>
        /// Log related files.
        /// </summary>
        /// <param name="reference">The reference.</param>
        /// <param name="importance">The importance of the message.</param>
        private void LogRelatedFiles(Reference reference, MessageImportance importance)
        {
            if (reference.IsResolved)
            {
                if (reference.FullPath.Length > 0)
                {
                    foreach (string relatedFileExtension in reference.GetRelatedFileExtensions())
                    {
                        Log.LogMessageFromResources(importance, "ResolveAssemblyReference.FourSpaceIndent", Log.FormatResourceString("ResolveAssemblyReference.FoundRelatedFile", reference.FullPathWithoutExtension + relatedFileExtension));
                    }
                }
            }
        }

        /// <summary>
        /// Log the satellite files.
        /// </summary>
        /// <param name="reference">The reference.</param>
        /// <param name="importance">The importance of the message.</param>
        private void LogSatellites(Reference reference, MessageImportance importance)
        {
            foreach (string satelliteFile in reference.GetSatelliteFiles())
            {
                Log.LogMessageFromResources(importance, "ResolveAssemblyReference.FourSpaceIndent", Log.FormatResourceString("ResolveAssemblyReference.FoundSatelliteFile", satelliteFile));
            }
        }

        /// <summary>
        /// Log the satellite files.
        /// </summary>
        /// <param name="reference">The reference.</param>
        /// <param name="importance">The importance of the message.</param>
        private void LogScatterFiles(Reference reference, MessageImportance importance)
        {
            foreach (string scatterFile in reference.GetScatterFiles())
            {
                Log.LogMessageFromResources(importance, "ResolveAssemblyReference.FourSpaceIndent", Log.FormatResourceString("ResolveAssemblyReference.FoundScatterFile", scatterFile));
            }
        }

        /// <summary>
        /// Log a message about the CopyLocal state of the reference.
        /// </summary>
        /// <param name="reference">The reference.</param>
        /// <param name="importance">The importance of the message.</param>
        private void LogCopyLocalState(Reference reference, MessageImportance importance)
        {
            if (!reference.IsUnresolvable && !reference.IsBadImage)
            {
                switch (reference.CopyLocal)
                {
                    case CopyLocalState.YesBecauseOfHeuristic:
                    case CopyLocalState.YesBecauseReferenceItemHadMetadata:
                        break;

                    case CopyLocalState.NoBecausePrerequisite:
                        Log.LogMessageFromResources(importance, "ResolveAssemblyReference.FourSpaceIndent", Log.FormatResourceString("ResolveAssemblyReference.NotCopyLocalBecausePrerequisite"));
                        break;

                    case CopyLocalState.NoBecauseReferenceItemHadMetadata:
                        Log.LogMessageFromResources(importance, "ResolveAssemblyReference.FourSpaceIndent", Log.FormatResourceString("ResolveAssemblyReference.NotCopyLocalBecauseIncomingItemAttributeOverrode"));
                        break;

                    case CopyLocalState.NoBecauseFrameworkFile:
                        Log.LogMessageFromResources(importance, "ResolveAssemblyReference.FourSpaceIndent", Log.FormatResourceString("ResolveAssemblyReference.NotCopyLocalBecauseFrameworksFiles"));
                        break;

                    case CopyLocalState.NoBecauseReferenceResolvedFromGAC:
                    case CopyLocalState.NoBecauseReferenceFoundInGAC:
                        Log.LogMessageFromResources(importance, "ResolveAssemblyReference.FourSpaceIndent", Log.FormatResourceString("ResolveAssemblyReference.NotCopyLocalBecauseReferenceFoundInGAC"));
                        break;

                    case CopyLocalState.NoBecauseConflictVictim:
                        Log.LogMessageFromResources(importance, "ResolveAssemblyReference.FourSpaceIndent", Log.FormatResourceString("ResolveAssemblyReference.NotCopyLocalBecauseConflictVictim"));
                        break;

                    case CopyLocalState.NoBecauseEmbedded:
                        Log.LogMessageFromResources(importance, "ResolveAssemblyReference.FourSpaceIndent", Log.FormatResourceString("ResolveAssemblyReference.NotCopyLocalBecauseEmbedded"));
                        break;

                    case CopyLocalState.NoBecauseParentReferencesFoundInGAC:
                        Log.LogMessageFromResources(importance, "ResolveAssemblyReference.FourSpaceIndent", Log.FormatResourceString("ResolveAssemblyReference.NoBecauseParentReferencesFoundInGac"));
                        break;

                    default:
                        Debug.Assert(false, "Should have handled this case.");
                        break;
                }
            }
        }


        /// <summary>
        /// Log a message about the imageruntime information.
        /// </summary>
        private void LogImageRuntime(Reference reference, MessageImportance importance)
        {
            if (!reference.IsUnresolvable && !reference.IsBadImage)
            {
                Log.LogMessageFromResources(importance, "ResolveAssemblyReference.FourSpaceIndent", Log.FormatResourceString("ResolveAssemblyReference.ImageRuntimeVersion", reference.ImageRuntime));

                if (reference.IsWinMDFile)
                {
                    Log.LogMessageFromResources(importance, "ResolveAssemblyReference.FourSpaceIndent", Log.FormatResourceString("ResolveAssemblyReference.IsAWinMdFile"));
                }
            }
        }

        /// <summary>
        /// Log a conflict.
        /// </summary>
        /// <param name="reference">The reference.</param>
        /// <param name="fusionName">The fusion name of the reference.</param>
        private void LogConflict(Reference reference, string fusionName)
        {
            // Set an importance level to be used for secondary messages.
            MessageImportance importance = ChooseReferenceLoggingImportance(reference);

            Log.LogMessageFromResources(importance, "ResolveAssemblyReference.ConflictFound", reference.ConflictVictorName, fusionName);
            switch (reference.ConflictLossExplanation)
            {
                case ConflictLossReason.HadLowerVersion:
                    {
                        Debug.Assert(!reference.IsPrimary, "A primary reference should never lose a conflict because of version. This is an insoluble conflict instead.");
                        string message = Log.FormatResourceString("ResolveAssemblyReference.ConflictHigherVersionChosen", reference.ConflictVictorName);
                        Log.LogMessageFromResources(importance, "ResolveAssemblyReference.FourSpaceIndent", message);
                        break;
                    }

                case ConflictLossReason.WasNotPrimary:
                    {
                        string message = Log.FormatResourceString("ResolveAssemblyReference.ConflictPrimaryChosen", reference.ConflictVictorName, fusionName);
                        Log.LogMessageFromResources(importance, "ResolveAssemblyReference.FourSpaceIndent", message);
                        break;
                    }

                case ConflictLossReason.InsolubleConflict:
                    // For primary references, there's no way an app.config binding redirect could help
                    // so log a warning.
                    if (reference.IsPrimary)
                    {
                        Log.LogWarningWithCodeFromResources("ResolveAssemblyReference.ConflictUnsolvable", reference.ConflictVictorName, fusionName);
                    }
                    else
                    {
                        // For dependencies, adding an app.config entry could help. Log a comment, there will be
                        // a summary warning later on.
                        string message;
                        string code = Log.ExtractMessageCode(Log.FormatResourceString("ResolveAssemblyReference.ConflictUnsolvable", reference.ConflictVictorName, fusionName), out message);
                        Log.LogMessage(MessageImportance.High, message);
                    }
                    break;
                // Can happen if one of the references has a dependency with the same simplename, and version but no publickeytoken and the other does.
                case ConflictLossReason.FusionEquivalentWithSameVersion:
                    break;
                default:
                    Debug.Assert(false, "Should have handled this case.");
                    break;
            }
        }
        #endregion

        #region StateFile
        /// <summary>
        /// Reads the state file (if present) into the cache.
        /// </summary>
        private void ReadStateFile()
        {
            _cache = (SystemState)StateFileBase.DeserializeCache(_request.StateFile, Log, typeof(SystemState));

            // Construct the cache if necessary.
            if (_cache == null)
            {
                _cache = new SystemState();
            }
        }

        /// <summary>
        /// Write out the state file if a state name was supplied and the cache is dirty.
        /// </summary>
        private void WriteStateFile()
        {
            if (!string.IsNullOrEmpty(_request.StateFile) && _cache.IsDirty)
            {
                _cache.SerializeCache(_request.StateFile, Log);
            }
        }
        #endregion

        #region App.config
        /// <summary>
        /// Read the app.config and get any assembly remappings from it.
        /// </summary>
        /// <returns></returns>
        private DependentAssembly[] GetAssemblyRemappingsFromAppConfig()
        {
            if (_request.AppConfigFile != null)
            {
                AppConfig appConfig = new AppConfig();
                appConfig.Load(_request.AppConfigFile);

                return appConfig.Runtime.DependentAssemblies;
            }

            return null;
        }

        #endregion
        #region ITask Members

        /// <summary>
        /// Execute the task.
        /// </summary>
        /// <param name="fileExists">Delegate used for checking for the existence of a file.</param>
        /// <param name="directoryExists">Delegate used for checking for the existence of a directory.</param>
        /// <param name="getDirectories">Delegate used for finding directories.</param>
        /// <param name="getAssemblyName">Delegate used for finding fusion names of assemblyFiles.</param>
        /// <param name="getAssemblyMetadata">Delegate used for finding dependencies of a file.</param>
        /// <param name="getRegistrySubKeyNames">Used to get registry subkey names.</param>
        /// <param name="getRegistrySubKeyDefaultValue">Used to get registry default values.</param>
        /// <param name="getLastWriteTime">Delegate used to get the last write time.</param>
        /// <returns>True if there was success.</returns>
        internal bool Execute
        (
            FileExists fileExists,
            DirectoryExists directoryExists,
            GetDirectories getDirectories,
            GetAssemblyName getAssemblyName,
            GetAssemblyMetadata getAssemblyMetadata,
#if FEATURE_WIN32_REGISTRY
            GetRegistrySubKeyNames getRegistrySubKeyNames,
            GetRegistrySubKeyDefaultValue getRegistrySubKeyDefaultValue,
#endif
            GetLastWriteTime getLastWriteTime,
            GetAssemblyRuntimeVersion getRuntimeVersion,
#if FEATURE_WIN32_REGISTRY
            OpenBaseKey openBaseKey,
#endif
            GetAssemblyPathInGac getAssemblyPathInGac,
            IsWinMDFile isWinMDFile,
            ReadMachineTypeFromPEHeader readMachineTypeFromPEHeader
        )
        {
            bool success = true;
#if (!STANDALONEBUILD)
            using (new CodeMarkerStartEnd(CodeMarkerEvent.perfMSBuildResolveAssemblyReferenceBegin, CodeMarkerEvent.perfMSBuildResolveAssemblyReferenceEnd))
#endif
            {
                try
                {
                    FrameworkNameVersioning frameworkMoniker = null;
                    if (!String.IsNullOrEmpty(_request.TargetFrameworkMoniker))
                    {
                        try
                        {
                            frameworkMoniker = new FrameworkNameVersioning(_request.TargetFrameworkMoniker);
                        }
                        catch (ArgumentException)
                        {
                            // The exception doesn't contain the bad value, so log it ourselves
                            Log.LogErrorWithCodeFromResources("ResolveAssemblyReference.InvalidParameter", "TargetFrameworkMoniker", _request.TargetFrameworkMoniker, String.Empty);
                            return false;
                        }
                    }

                    Version targetedRuntimeVersion = SetTargetedRuntimeVersion(_request.TargetedRuntimeVersion);

                    // Log task inputs.
                    LogInputs();

                    if (!VerifyInputConditions())
                    {
                        return false;
                    }

                    _logVerboseSearchResults = Environment.GetEnvironmentVariable("MSBUILDLOGVERBOSERARSEARCHRESULTS") != null;

                    // Loop through all the target framework directories that were passed in,
                    // and ensure that they all have a trailing slash.  This is necessary
                    // for the string comparisons we will do later on.
                    if (_request.TargetFrameworkDirectories != null)
                    {
                        for (int i = 0; i < _request.TargetFrameworkDirectories.Length; i++)
                        {
                            _request.TargetFrameworkDirectories[i] = FileUtilities.EnsureTrailingSlash(_request.TargetFrameworkDirectories[i]);
                        }
                    }


                    // Validate the contents of the InstalledAssemblyTables parameter.
                    AssemblyTableInfo[] installedAssemblyTableInfo = GetInstalledAssemblyTableInfo(_request.IgnoreDefaultInstalledAssemblyTables, _request.InstalledAssemblyTables, new GetListPath(RedistList.GetRedistListPathsFromDisk), _request.TargetFrameworkDirectories);
                    AssemblyTableInfo[] whiteListSubsetTableInfo = null;

                    InstalledAssemblies installedAssemblies = null;
                    RedistList redistList = null;

                    if (installedAssemblyTableInfo != null && installedAssemblyTableInfo.Length > 0)
                    {
                        redistList = RedistList.GetRedistList(installedAssemblyTableInfo);
                    }

                    Hashtable blackList = null;

                    // The name of the subset if it is generated or the name of the profile. This will be used for error messages and logging.
                    string subsetOrProfileName = null;

                    // Are we targeting a profile
                    bool targetingProfile = !String.IsNullOrEmpty(_request.ProfileName) && ((_request.FullFrameworkFolders.Length > 0) || (_request.FullFrameworkAssemblyTables.Length > 0));
                    bool targetingSubset = false;
                    List<Exception> whiteListErrors = new List<Exception>();
                    List<string> whiteListErrorFilesNames = new List<string>();

                    // Check for partial success in GetRedistList and log any tolerated exceptions.
                    if (redistList != null && redistList.Count > 0 || targetingProfile || ShouldUseSubsetBlackList())
                    {
                        // If we are not targeting a dev 10 profile and we have the required components to generate a orcas style subset, do so
                        if (!targetingProfile && ShouldUseSubsetBlackList())
                        {
                            // Based in the target framework subset names find the paths to the files
                            SubsetListFinder whiteList = new SubsetListFinder(_request.TargetFrameworkSubsets);
                            whiteListSubsetTableInfo = GetInstalledAssemblyTableInfo(_request.IgnoreDefaultInstalledAssemblySubsetTables, _request.InstalledAssemblySubsetTables, new GetListPath(whiteList.GetSubsetListPathsFromDisk), _request.TargetFrameworkDirectories);
                            if (whiteListSubsetTableInfo.Length > 0 && (redistList != null && redistList.Count > 0))
                            {
                                blackList = redistList.GenerateBlackList(whiteListSubsetTableInfo, whiteListErrors, whiteListErrorFilesNames);
                            }
                            else
                            {
                                Log.LogWarningWithCodeFromResources("ResolveAssemblyReference.NoSubsetsFound");
                            }

                            // Could get into this situation if the redist list files were full of junk and no assemblies were read in.
                            if (blackList == null)
                            {
                                Log.LogWarningWithCodeFromResources("ResolveAssemblyReference.NoRedistAssembliesToGenerateExclusionList");
                            }

                            subsetOrProfileName = GenerateSubSetName(_request.TargetFrameworkSubsets, _request.InstalledAssemblySubsetTables);
                            targetingSubset = true;
                        }
                        else
                        {
                            // We are targeting a profile
                            if (targetingProfile)
                            {
                                // When targeting a profile we want the redist list to be the full framework redist list, since this is what should be used
                                // when unifying assemblies ect. 
                                AssemblyTableInfo[] fullRedistAssemblyTableInfo = null;
                                RedistList fullFrameworkRedistList = null;

                                HandleProfile(installedAssemblyTableInfo /*This is the table info related to the profile*/, out fullRedistAssemblyTableInfo, out blackList, out fullFrameworkRedistList);

                                // Make sure the redist list and the installedAsemblyTableInfo structures point to the full framework, we replace the installedAssemblyTableInfo
                                // which contained the information about the profile redist files with the one from the full framework because when doing anything with the RAR cache
                                // we want to use the full frameworks redist list. Essentailly after generating the exclusion list the job of the profile redist list is done.
                                redistList = fullFrameworkRedistList;

                                // Save the profile redist list file locations as the whiteList
                                whiteListSubsetTableInfo = installedAssemblyTableInfo;

                                // Set the installed assembly table to the full redist list values
                                installedAssemblyTableInfo = fullRedistAssemblyTableInfo;
                                subsetOrProfileName = _profileName;
                            }
                        }

                        if (redistList != null && redistList.Count > 0)
                        {
                            installedAssemblies = new InstalledAssemblies(redistList);
                        }
                    }

                    // Print out any errors reading the redist list.
                    if (redistList != null)
                    {
                        // Some files may have been skipped. Log warnings for these.
                        for (int i = 0; i < redistList.Errors.Length; ++i)
                        {
                            Exception e = redistList.Errors[i];
                            string filename = redistList.ErrorFileNames[i];

                            // Give the user a warning about the bad file (or files).
                            Log.LogWarningWithCodeFromResources("ResolveAssemblyReference.InvalidInstalledAssemblyTablesFile", filename, RedistList.RedistListFolder, e.Message);
                        }

                        // Some files may have been skipped. Log warnings for these.
                        for (int i = 0; i < whiteListErrors.Count; ++i)
                        {
                            Exception e = whiteListErrors[i];
                            string filename = whiteListErrorFilesNames[i];

                            // Give the user a warning about the bad file (or files).
                            Log.LogWarningWithCodeFromResources("ResolveAssemblyReference.InvalidInstalledAssemblySubsetTablesFile", filename, SubsetListFinder.SubsetListFolder, e.Message);
                        }
                    }

                    // Load any prior saved state.
                    ReadStateFile();
                    _cache.SetGetLastWriteTime(getLastWriteTime);
                    _cache.SetInstalledAssemblyInformation(installedAssemblyTableInfo);

                    // Cache delegates.
                    getAssemblyName = _cache.CacheDelegate(getAssemblyName);
                    getAssemblyMetadata = _cache.CacheDelegate(getAssemblyMetadata);
                    fileExists = _cache.CacheDelegate(fileExists);
                    directoryExists = _cache.CacheDelegate(directoryExists);
                    getDirectories = _cache.CacheDelegate(getDirectories);
                    getRuntimeVersion = _cache.CacheDelegate(getRuntimeVersion);

                    _projectTargetFramework = FrameworkVersionFromString(_request.TargetFrameworkVersion);

                    // Filter out all _request.Assemblies that have SubType!='', or higher framework
                    FilterBySubtypeAndTargetFramework();

                    // Compute the set of bindingRedirect remappings.
                    DependentAssembly[] appConfigRemappedAssemblies = null;
                    if (_request.FindDependencies)
                    {
                        try
                        {
                            appConfigRemappedAssemblies = GetAssemblyRemappingsFromAppConfig();
                        }
                        catch (AppConfigException e)
                        {
                            Log.LogErrorWithCodeFromResources(null, e.FileName, e.Line, e.Column, 0, 0, "ResolveAssemblyReference.InvalidAppConfig", _request.AppConfigFile, e.Message);
                            return false;
                        }
                    }

                    SystemProcessorArchitecture processorArchitecture = TargetProcessorArchitectureToEnumeration(_request.TargetProcessorArchitecture);

                    ConcurrentDictionary<string, AssemblyMetadata> assemblyMetadataCache =
                        Traits.Instance.EscapeHatches.CacheAssemblyInformation
                            ? new ConcurrentDictionary<string, AssemblyMetadata>()
                            : null;

                    // Start the table of dependencies with all of the primary references.
                    ReferenceTable dependencyTable = new ReferenceTable
                    (
                        BuildEngine,
                        _request.FindDependencies,
                        _request.FindSatellites,
                        _request.FindSerializationAssemblies,
                        _request.FindRelatedFiles,
                        _request.SearchPaths,
                        _request.AllowedAssemblyExtensions,
                        _request.AllowedRelatedFileExtensions,
                        _request.CandidateAssemblyFiles,
                        _request.ResolvedSDKReferences,
                        _request.TargetFrameworkDirectories,
                        installedAssemblies,
                        processorArchitecture,
                        fileExists,
                        directoryExists,
                        getDirectories,
                        getAssemblyName,
                        getAssemblyMetadata,
#if FEATURE_WIN32_REGISTRY
                        getRegistrySubKeyNames,
                        getRegistrySubKeyDefaultValue,
                        openBaseKey,
#endif
                        getRuntimeVersion,
                        targetedRuntimeVersion,
                        _projectTargetFramework,
                        frameworkMoniker,
                        Log,
                        _request.LatestTargetFrameworkDirectories,
                        _request.CopyLocalDependenciesWhenParentReferenceInGac,
                        _request.DoNotCopyLocalIfInGac,
                        getAssemblyPathInGac,
                        isWinMDFile,
                        _request.IgnoreVersionForFrameworkReferences,
                        readMachineTypeFromPEHeader,
                        _request.WarnOrErrorOnTargetArchitectureMismatch,
                        _request.IgnoreTargetFrameworkAttributeVersionMismatch,
                        _request.UnresolveFrameworkAssembliesFromHigherFrameworks,
                        assemblyMetadataCache
                        );

                    dependencyTable.FindDependenciesOfExternallyResolvedReferences = _request.FindDependenciesOfExternallyResolvedReferences;

                    // If AutoUnify, then compute the set of assembly remappings.
                    ArrayList generalResolutionExceptions = new ArrayList();

                    subsetOrProfileName = targetingSubset && String.IsNullOrEmpty(_request.TargetFrameworkMoniker) ? subsetOrProfileName : _request.TargetFrameworkMoniker;
                    bool excludedReferencesExist = false;

                    DependentAssembly[] autoUnifiedRemappedAssemblies = null;
                    AssemblyNameReference[] autoUnifiedRemappedAssemblyReferences = null;
                    if (_request.AutoUnify && _request.FindDependencies)
                    {
                        // Compute all dependencies.
                        dependencyTable.ComputeClosure
                        (
                            // Use any app.config specified binding redirects so that later when we output suggested redirects
                            // for the GenerateBindingRedirects target, we don't suggest ones that the user already wrote
                            appConfigRemappedAssemblies,
                            _request.AssemblyFiles,
                            _request.Assemblies,
                            generalResolutionExceptions
                        );

                        try
                        {
                            excludedReferencesExist = false;
                            if (redistList != null && redistList.Count > 0)
                            {
                                excludedReferencesExist = dependencyTable.MarkReferencesForExclusion(blackList);
                            }
                        }
                        catch (InvalidOperationException e)
                        {
                            Log.LogErrorWithCodeFromResources("ResolveAssemblyReference.ProblemDeterminingFrameworkMembership", e.Message);
                            return false;
                        }

                        if (excludedReferencesExist)
                        {
                            dependencyTable.RemoveReferencesMarkedForExclusion(true /* Remove the reference and do not warn*/, subsetOrProfileName);
                        }


                        // Based on the closure, get a table of ideal remappings needed to 
                        // produce zero conflicts.
                        dependencyTable.ResolveConflicts
                        (
                            out autoUnifiedRemappedAssemblies,
                            out autoUnifiedRemappedAssemblyReferences
                        );
                    }

                    DependentAssembly[] allRemappedAssemblies = CombineRemappedAssemblies(appConfigRemappedAssemblies, autoUnifiedRemappedAssemblies);

                    // Compute all dependencies.
                    dependencyTable.ComputeClosure(allRemappedAssemblies, _request.AssemblyFiles, _request.Assemblies, generalResolutionExceptions);

                    try
                    {
                        excludedReferencesExist = false;
                        if (redistList != null && redistList.Count > 0)
                        {
                            excludedReferencesExist = dependencyTable.MarkReferencesForExclusion(blackList);
                        }
                    }
                    catch (InvalidOperationException e)
                    {
                        Log.LogErrorWithCodeFromResources("ResolveAssemblyReference.ProblemDeterminingFrameworkMembership", e.Message);
                        return false;
                    }

                    if (excludedReferencesExist)
                    {
                        dependencyTable.RemoveReferencesMarkedForExclusion(false /* Remove the reference and warn*/, subsetOrProfileName);
                    }

                    // Resolve any conflicts.
                    DependentAssembly[] idealAssemblyRemappings = null;
                    AssemblyNameReference[] idealAssemblyRemappingsIdentities = null;

                    dependencyTable.ResolveConflicts
                    (
                        out idealAssemblyRemappings,
                        out idealAssemblyRemappingsIdentities
                    );

                    // Build the output tables.
                    (_response.ResolvedFiles,  _resolvedDependencyFiles,
                         _relatedFiles,
                         _satelliteFiles,
                         _serializationAssemblyFiles,
                         _scatterFiles,
                         _copyLocalFiles) = dependencyTable.GetReferenceItems();

                    // If we're not finding dependencies, then don't suggest redirects (they're only about dependencies).
                    if (_request.FindDependencies)
                    {
                        // Build the table of suggested redirects. If we're auto-unifying, we want to output all the 
                        // assemblies that we auto-unified so that GenerateBindingRedirects can consume them, 
                        // not just the required ones for build to succeed
                        DependentAssembly[] remappings = _request.AutoUnify ? autoUnifiedRemappedAssemblies : idealAssemblyRemappings;
                        AssemblyNameReference[] remappedReferences = _request.AutoUnify ? autoUnifiedRemappedAssemblyReferences : idealAssemblyRemappingsIdentities;
                        PopulateSuggestedRedirects(remappings, remappedReferences);
                    }

                    bool useSystemRuntime = false;
                    bool useNetStandard = false;
                    foreach (var reference in dependencyTable.References.Keys)
                    {
                        if (string.Equals(SystemRuntimeAssemblyName, reference.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            useSystemRuntime = true;
                        }
                        if (string.Equals(NETStandardAssemblyName, reference.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            useNetStandard = true;
                        }
                        if (useSystemRuntime && useNetStandard)
                        {
                            break;
                        }
                    }

                    if ((!useSystemRuntime || !useNetStandard) && (!_request.FindDependencies || dependencyTable.SkippedFindingExternallyResolvedDependencies))
                    {
                        // when we are not producing the (full) dependency graph look for direct dependencies of primary references
                        foreach (var resolvedReference in dependencyTable.References.Values)
                        {
                            if (_request.FindDependencies && !resolvedReference.ExternallyResolved)
                            {
                                // if we're finding dependencies and a given reference was not marked as ExternallyResolved
                                // then its use of System.Runtime/.netstandard would already have been identified above.
                                continue; 
                            }

                            var rawDependencies = GetDependencies(resolvedReference, fileExists, getAssemblyMetadata, assemblyMetadataCache);
                            if (rawDependencies != null)
                            {
                                foreach (var dependentReference in rawDependencies)
                                {
                                    if (string.Equals(SystemRuntimeAssemblyName, dependentReference.Name, StringComparison.OrdinalIgnoreCase))
                                    {
                                        useSystemRuntime = true;
                                        break;
                                    }
                                    if (string.Equals(NETStandardAssemblyName, dependentReference.Name, StringComparison.OrdinalIgnoreCase))
                                    {
                                        useNetStandard = true;
                                        break;
                                    }
                                }
                            }

                            if (useSystemRuntime && useNetStandard)
                            {
                                break;
                            }
                        }
                    }

                    this.DependsOnSystemRuntime = useSystemRuntime.ToString();
                    this.DependsOnNETStandard = useNetStandard.ToString();

                    WriteStateFile();

                    // Save the new state out and put into the file exists if it is actually on disk.
                    if (_request.StateFile != null && fileExists(_request.StateFile))
                    {
                        _filesWritten.Add(new TaskItem(_request.StateFile));
                    }

                    // Log the results.
                    success = LogResults(dependencyTable, idealAssemblyRemappings, idealAssemblyRemappingsIdentities, generalResolutionExceptions);

                    DumpTargetProfileLists(installedAssemblyTableInfo, whiteListSubsetTableInfo, dependencyTable);

                    if (processorArchitecture != SystemProcessorArchitecture.None &&  _request.WarnOrErrorOnTargetArchitectureMismatch != WarnOrErrorOnTargetArchitectureMismatchBehavior.None)
                    {
                        foreach (ITaskItem item in _response.ResolvedFiles)
                        {
                            AssemblyNameExtension assemblyName = null;

                            if (fileExists(item.ItemSpec) && !Reference.IsFrameworkFile(item.ItemSpec, _request.TargetFrameworkDirectories))
                            {
                                try
                                {
                                    assemblyName = getAssemblyName(item.ItemSpec);
                                }
                                catch (System.IO.FileLoadException)
                                {
                                    // Its pretty hard to get here, you need an assembly that contains a valid reference
                                    // to a dependent assembly that, in turn, throws a FileLoadException during GetAssemblyName.
                                    // Still it happened once, with an older version of the CLR. 

                                    // ...falling through and relying on the targetAssemblyName==null behavior below...
                                }
                                catch (System.IO.FileNotFoundException)
                                {
                                    // Its pretty hard to get here, also since we do a file existence check right before calling this method so it can only happen if the file got deleted between that check and this call.
                                }
                                catch (UnauthorizedAccessException)
                                {
                                }
                                catch (BadImageFormatException)
                                {
                                }
                            }

                            if (assemblyName != null)
                            {
                                SystemProcessorArchitecture assemblyArch = assemblyName.ProcessorArchitecture;

                                // If the assembly is MSIL or none it can work anywhere so there does not need to be any warning ect.
                                if (assemblyArch == SystemProcessorArchitecture.MSIL || assemblyArch == SystemProcessorArchitecture.None)
                                {
                                    continue;
                                }

                                if (processorArchitecture != assemblyArch)
                                {
                                    if ( _request.WarnOrErrorOnTargetArchitectureMismatch == WarnOrErrorOnTargetArchitectureMismatchBehavior.Error)
                                    {
                                        Log.LogErrorWithCodeFromResources("ResolveAssemblyReference.MismatchBetweenTargetedAndReferencedArch", ProcessorArchitectureToString(processorArchitecture), item.GetMetadata("OriginalItemSpec"), ProcessorArchitectureToString(assemblyArch));
                                    }
                                    else
                                    {
                                        Log.LogWarningWithCodeFromResources("ResolveAssemblyReference.MismatchBetweenTargetedAndReferencedArch", ProcessorArchitectureToString(processorArchitecture), item.GetMetadata("OriginalItemSpec"), ProcessorArchitectureToString(assemblyArch));
                                    }
                                }
                            }
                        }
                    }
                    return success && !Log.HasLoggedErrors;
                }
                catch (ArgumentException e)
                {
                    Log.LogErrorWithCodeFromResources("General.InvalidArgument", e.Message);
                }

                // InvalidParameterValueException is thrown inside RAR when we find a specific parameter
                // has an invalid value. It's then caught up here so that we can abort the task.
                catch (InvalidParameterValueException e)
                {
                    Log.LogErrorWithCodeFromResources(null, "", 0, 0, 0, 0,
                        "ResolveAssemblyReference.InvalidParameter", e.ParamName, e.ActualValue, e.Message);
                }
            }

            return success && !Log.HasLoggedErrors;
        }

        /// <summary>
        /// Returns the raw list of direct dependent assemblies from assembly's metadata.
        /// </summary>
        /// <param name="resolvedReference">reference we are interested</param>
        /// <param name="fileExists">the delegate to check for the existence of a file.</param>
        /// <param name="getAssemblyMetadata">the delegate to access assembly metadata</param>
        /// <param name="assemblyMetadataCache">Cache of pre-extracted assembly metadata.</param>
        /// <returns>list of dependencies</returns>
        private AssemblyNameExtension[] GetDependencies(Reference resolvedReference, FileExists fileExists, GetAssemblyMetadata getAssemblyMetadata, ConcurrentDictionary<string, AssemblyMetadata> assemblyMetadataCache)
        {
            AssemblyNameExtension[] result = null;
            if (resolvedReference != null && resolvedReference.IsPrimary && !resolvedReference.IsBadImage)
            {
                System.Runtime.Versioning.FrameworkName frameworkName = null;
                string[] scatterFiles = null;
                try
                {
                    // in case of P2P that have not build the reference can be resolved but file does not exist on disk. 
                    if (fileExists(resolvedReference.FullPath))
                    {
                        getAssemblyMetadata(resolvedReference.FullPath, assemblyMetadataCache, out result, out scatterFiles, out frameworkName);
                    }
                }
                catch (Exception e)
                {
                    if (ExceptionHandling.IsCriticalException(e))
                    {
                        throw;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Combines two DependentAssembly arrays into one.
        /// </summary>
        private static DependentAssembly[] CombineRemappedAssemblies(DependentAssembly[] first, DependentAssembly[] second)
        {
            if (first == null)
                return second;

            if (second == null)
                return first;

            DependentAssembly[] combined = new DependentAssembly[first.Length + second.Length];
            first.CopyTo(combined, 0);
            second.CopyTo(combined, first.Length);

            return combined;
        }


        /// <summary>
        /// If a targeted runtime is passed in use that, if none is passed in then we need to use v2.0.50727
        /// since the common way this would be empty is if we were using RAR as an override task.
        /// </summary>
        /// <returns>The targered runtime</returns>
        internal static Version SetTargetedRuntimeVersion(string targetedRuntimeVersionRawValue)
        {
            Version versionToReturn = null;
            if (targetedRuntimeVersionRawValue != null)
            {
                versionToReturn = VersionUtilities.ConvertToVersion(targetedRuntimeVersionRawValue);
            }

            // Either the version passed in did not parse or none was passed in, lets default to 2.0 so that we can be used as an override task for tv 3.5
            if (versionToReturn == null)
            {
                versionToReturn = new Version(2, 0, 50727);
            }

            return versionToReturn;
        }

        /// <summary>
        /// For a given profile generate the exclusion list and return the list of redist list files read in so they can be logged at the end of the task execution.
        /// </summary>
        /// <param name="installedAssemblyTableInfo">Installed assembly info of the profile redist lists</param>
        /// <param name="fullRedistAssemblyTableInfo">Installed assemblyInfo for the full framework redist lists</param>
        /// <param name="blackList">Generated exclusion list</param>
        private void HandleProfile(AssemblyTableInfo[] installedAssemblyTableInfo, out AssemblyTableInfo[] fullRedistAssemblyTableInfo, out Hashtable blackList, out RedistList fullFrameworkRedistList)
        {
            // Redist list which will contain the full framework redist list.
            fullFrameworkRedistList = null;
            blackList = null;
            fullRedistAssemblyTableInfo = null;

            // Make sure the framework directory is on the FullFrameworkTablesLocation if it is being used.
            foreach (ITaskItem item in _request.FullFrameworkAssemblyTables)
            {
                // Cannot be missing the FrameworkDirectory if we are using this property
                if (String.IsNullOrEmpty(item.GetMetadata("FrameworkDirectory")))
                {
                    Log.LogErrorWithCodeFromResources("ResolveAssemblyReference.FrameworkDirectoryOnProfiles", item.ItemSpec);
                    return;
                }
            }

            fullRedistAssemblyTableInfo = GetInstalledAssemblyTableInfo(false, _request.FullFrameworkAssemblyTables, new GetListPath(RedistList.GetRedistListPathsFromDisk), _request.FullFrameworkFolders);
            if (fullRedistAssemblyTableInfo.Length > 0)
            {
                // Get the redist list which represents the Full framework, we need this so that we can generate the exclusion list
                fullFrameworkRedistList = RedistList.GetRedistList(fullRedistAssemblyTableInfo);
                if (fullFrameworkRedistList != null)
                {
                    // Generate the black list by determining what assemblies are in the full framework but not in the profile.
                    // The installedAssemblyTableInfo is the list of xml files for the Client Profile redist, these are the whitelist xml files.
                    Log.LogMessageFromResources("ResolveAssemblyReference.ProfileExclusionListWillBeGenerated");

                    // Any errors reading the profile redist list will already be logged, we do not need to re-log the errors here.
                    List<Exception> whiteListErrors = new List<Exception>();
                    List<string> whiteListErrorFilesNames = new List<string>();
                    blackList = fullFrameworkRedistList.GenerateBlackList(installedAssemblyTableInfo, whiteListErrors, whiteListErrorFilesNames);
                }

                // Could get into this situation if the redist list files were full of junk and no assemblies were read in.
                if (blackList == null)
                {
                    Log.LogWarningWithCodeFromResources("ResolveAssemblyReference.NoRedistAssembliesToGenerateExclusionList");
                }
            }
            else
            {
                Log.LogWarningWithCodeFromResources("ResolveAssemblyReference.NoProfilesFound");
            }

            if (fullFrameworkRedistList != null)
            {
                // Any errors logged for the client profile redist list will have been logged after this method returns.
                // Some files may have been skipped. Log warnings for these.
                for (int i = 0; i < fullFrameworkRedistList.Errors.Length; ++i)
                {
                    Exception e = fullFrameworkRedistList.Errors[i];
                    string filename = fullFrameworkRedistList.ErrorFileNames[i];

                    // Give the user a warning about the bad file (or files).
                    Log.LogWarningWithCodeFromResources("ResolveAssemblyReference.InvalidProfileRedistLocation", filename, RedistList.RedistListFolder, e.Message);
                }
            }
        }

        /// <summary>
        /// Given the names of the targetFrameworkSubset lists passed in generate a single name which can be used for logging.
        /// </summary>
        internal static string GenerateSubSetName(string[] frameworkSubSetNames, ITaskItem[] installedSubSetNames)
        {
            List<string> subsetNames = new List<string>();
            if (frameworkSubSetNames != null)
            {
                foreach (string subset in frameworkSubSetNames)
                {
                    if (!String.IsNullOrEmpty(subset))
                    {
                        subsetNames.Add(subset);
                    }
                }
            }

            if (installedSubSetNames != null)
            {
                foreach (ITaskItem subsetItems in installedSubSetNames)
                {
                    string fileName = subsetItems.ItemSpec;
                    if (!String.IsNullOrEmpty(fileName))
                    {
                        string fileNameNoExtension = Path.GetFileNameWithoutExtension(fileName);
                        if (!String.IsNullOrEmpty(fileNameNoExtension))
                        {
                            subsetNames.Add(fileNameNoExtension);
                        }
                    }
                }
            }

            return String.Join(", ", subsetNames.ToArray());
        }

        /// <summary>
        /// Make sure certain combinations of properties are validated before continuing with the execution of rar.
        /// </summary>
        /// <returns></returns>
        private bool VerifyInputConditions()
        {
            bool targetFrameworkSubsetIsSet = _request.TargetFrameworkSubsets.Length != 0 || _request.InstalledAssemblySubsetTables.Length != 0;

            // Make sure the inputs for profiles are correct
            bool profileNameIsSet = !String.IsNullOrEmpty(_request.ProfileName);
            bool fullFrameworkFoldersIsSet = _request.FullFrameworkFolders.Length > 0;
            bool fullFrameworkTableLocationsIsSet = _request.FullFrameworkAssemblyTables.Length > 0;
            bool profileIsSet = profileNameIsSet && (fullFrameworkFoldersIsSet || fullFrameworkTableLocationsIsSet);

            // Cannot target a subset and a profile at the same time
            if (targetFrameworkSubsetIsSet && profileIsSet)
            {
                Log.LogErrorWithCodeFromResources("ResolveAssemblyReference.CannotSetProfileAndSubSet");
                return false;
            }

            // A profile name and either a FullFrameworkFolders or ProfileTableLocation must be set is a profile is being used
            if (profileNameIsSet && (!fullFrameworkFoldersIsSet && !fullFrameworkTableLocationsIsSet))
            {
                Log.LogErrorWithCodeFromResources("ResolveAssemblyReference.MustSetProfileNameAndFolderLocations");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Log the target framework subset information.
        /// </summary>
        private void DumpTargetProfileLists(AssemblyTableInfo[] installedAssemblyTableInfo, AssemblyTableInfo[] whiteListSubsetTableInfo, ReferenceTable referenceTable)
        {
            if (installedAssemblyTableInfo != null)
            {
                string dumpFrameworkSubsetList = Environment.GetEnvironmentVariable("MSBUILDDUMPFRAMEWORKSUBSETLIST");

                if (dumpFrameworkSubsetList != null)
                {
                    Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.TargetFrameworkSubsetLogHeader");

                    Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.TargetFrameworkRedistLogHeader");
                    foreach (AssemblyTableInfo redistInfo in installedAssemblyTableInfo)
                    {
                        if (redistInfo != null)
                        {
                            Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.FourSpaceIndent", Log.FormatResourceString("ResolveAssemblyReference.FormattedAssemblyInfo", redistInfo.Path));
                        }
                    }

                    Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.TargetFrameworkWhiteListLogHeader");
                    if (whiteListSubsetTableInfo != null)
                    {
                        foreach (AssemblyTableInfo whiteListInfo in whiteListSubsetTableInfo)
                        {
                            if (whiteListInfo != null)
                            {
                                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.FourSpaceIndent", Log.FormatResourceString("ResolveAssemblyReference.FormattedAssemblyInfo", whiteListInfo.Path));
                            }
                        }
                    }

                    if (referenceTable.ListOfExcludedAssemblies != null)
                    {
                        Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.TargetFrameworkExclusionListLogHeader");
                        foreach (string assemblyFullName in referenceTable.ListOfExcludedAssemblies)
                        {
                            Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.FourSpaceIndent", assemblyFullName);
                        }
                    }
                }
            }
        }


        /// <summary>
        /// Determine if a black list should be used or not
        /// 
        /// The black list should only be used if there are TargetFrameworkSubsets to use or TargetFrameworkProfiles.
        /// 
        /// 1) If we find a Full or equivalent marker in the list of subsets passed in we do not want to generate a black list even if installedAssemblySubsets are passed in
        /// 2) If we are ignoring the default installed subset tables and we have not passed in any additional subset tables, we do not want to generate a black list
        /// 3) If no targetframework subsets were passed in and no additional subset tables were passed in, we do not want to generate a blacklist
        /// </summary>
        /// <returns>True if we should generate a black list, false if a blacklist should not be generated</returns>
        private bool ShouldUseSubsetBlackList()
        {
            // Check for full subset names in the passed in list of subsets to search for
            foreach (string fullSubsetName in _request.FullTargetFrameworkSubsetNames)
            {
                foreach (string subsetName in _request.TargetFrameworkSubsets)
                {
                    if (String.Equals(fullSubsetName, subsetName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!_request.Silent)
                        {
                            Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.NoExclusionListBecauseofFullClientName", subsetName);
                        }
                        return false;
                    }
                }
            }

            // We are going to ignore the default installed subsets and there are no additional installedAssemblySubsets passed in, we should not make the list
            if (_request.IgnoreDefaultInstalledAssemblySubsetTables && _request.InstalledAssemblySubsetTables.Length == 0)
            {
                return false;
            }

            // No subset names were passed in to search for in the targetframework directories and no installed subset tables were provided, we have nothing to use to 
            // generate the black list with, so do not continue.
            if (_request.TargetFrameworkSubsets.Length == 0 && _request.InstalledAssemblySubsetTables.Length == 0)
            {
                return false;
            }

            if (!_request.Silent)
            {
                Log.LogMessageFromResources(MessageImportance.Low, "ResolveAssemblyReference.UsingExclusionList");
            }
            return true;
        }

        /// <summary>
        /// Populates the suggested redirects output parameter.
        /// </summary>
        /// <param name="idealAssemblyRemappings">The list of ideal remappings.</param>
        /// <param name="idealAssemblyRemappedReferences">The list of of references to ideal assembly remappings.</param>
        private void PopulateSuggestedRedirects(DependentAssembly[] idealAssemblyRemappings, AssemblyNameReference[] idealAssemblyRemappedReferences)
        {
            ArrayList holdSuggestedRedirects = new ArrayList();
            if (idealAssemblyRemappings != null)
            {
                for (int i = 0; i < idealAssemblyRemappings.Length; i++)
                {
                    DependentAssembly idealRemapping = idealAssemblyRemappings[i];
                    string itemSpec = idealRemapping.PartialAssemblyName.ToString();

                    Reference reference = idealAssemblyRemappedReferences[i].reference;
                    AssemblyNameExtension[] conflictVictims = reference.GetConflictVictims();

                    // Skip any remapping that has no conflict victims since a redirect will not help.
                    if (null == conflictVictims || 0 == conflictVictims.Length)
                    {
                        continue;
                    }

                    for (int j = 0; j < idealRemapping.BindingRedirects.Length; j++)
                    {
                        ITaskItem suggestedRedirect = new TaskItem();
                        suggestedRedirect.ItemSpec = itemSpec;
                        suggestedRedirect.SetMetadata("MaxVersion", idealRemapping.BindingRedirects[j].NewVersion.ToString());
                        holdSuggestedRedirects.Add(suggestedRedirect);
                    }
                }
            }
            SuggestedRedirects = (ITaskItem[])holdSuggestedRedirects.ToArray(typeof(ITaskItem));
        }


        /// <summary>
        /// Process TargetFrameworkDirectories and an array of InstalledAssemblyTables.
        /// The goal is this:  for each installed assembly table (whether found on disk
        /// or given as an input), we wish to determine the target framework directory
        /// it is associated with.
        /// </summary>
        /// <returns>Array of AssemblyTableInfo objects (Describe the path and framework directory of a redist or subset list xml file) </returns>
        private AssemblyTableInfo[] GetInstalledAssemblyTableInfo(bool ignoreInstalledAssemblyTables, ITaskItem[] assemblyTables, GetListPath GetAssemblyListPaths, string[] targetFrameworkDirectories)
        {
            Dictionary<string, AssemblyTableInfo> tableMap = new Dictionary<string, AssemblyTableInfo>(StringComparer.OrdinalIgnoreCase);

            if (!ignoreInstalledAssemblyTables)
            {
                // first, find redist or subset files underneath the TargetFrameworkDirectories
                foreach (string targetFrameworkDirectory in targetFrameworkDirectories)
                {
                    string[] listPaths = GetAssemblyListPaths(targetFrameworkDirectory);
                    foreach (string listPath in listPaths)
                    {
                        tableMap[listPath] = new AssemblyTableInfo(listPath, targetFrameworkDirectory);
                    }
                }
            }

            // now process those provided as inputs from the project file
            foreach (ITaskItem installedAssemblyTable in assemblyTables)
            {
                string frameworkDirectory = installedAssemblyTable.GetMetadata(ItemMetadataNames.frameworkDirectory);

                // Whidbey behavior was to accept a single TargetFrameworkDirectory, and multiple
                // InstalledAssemblyTables, under the assumption that all of the InstalledAssemblyTables
                // were related to the single TargetFrameworkDirectory.  If inputs look like the Whidbey
                // case, let's make sure we behave the same way.

                if (String.IsNullOrEmpty(frameworkDirectory))
                {
                    if (_request.TargetFrameworkDirectories != null && _request.TargetFrameworkDirectories.Length == 1)
                    {
                        // Exactly one TargetFrameworkDirectory, so assume it's related to this
                        // InstalledAssemblyTable.

                        frameworkDirectory = _request.TargetFrameworkDirectories[0];
                    }
                }
                else
                {
                    // The metadata on the item was non-empty, so use it.
                    frameworkDirectory = FileUtilities.EnsureTrailingSlash(frameworkDirectory);
                }

                tableMap[installedAssemblyTable.ItemSpec] = new AssemblyTableInfo(installedAssemblyTable.ItemSpec, frameworkDirectory);
            }

            AssemblyTableInfo[] extensions = new AssemblyTableInfo[tableMap.Count];
            tableMap.Values.CopyTo(extensions, 0);

            return extensions;
        }

        /// <summary>
        /// Converts the string target framework value to a number.
        /// Accepts both "v" prefixed and no "v" prefixed formats
        /// if format is bad will log a message and return 0.
        /// </summary>
        /// <returns>Target framework version value</returns>
        private Version FrameworkVersionFromString(string version)
        {
            Version parsedVersion = null;

            if (!String.IsNullOrEmpty(version))
            {
                parsedVersion = VersionUtilities.ConvertToVersion(version);

                if (parsedVersion == null)
                {
                    Log.LogMessageFromResources(MessageImportance.Normal, "ResolveAssemblyReference.BadTargetFrameworkFormat", version);
                }
            }

            return parsedVersion;
        }

        /// <summary>
        /// Check if the assembly is available for on project's target framework.
        /// - Assuming the russian doll model. It will be available if the projects target framework is higher or equal than the assembly target framework
        /// </summary>
        /// <returns>True if the assembly is available for the project's target framework.</returns>
        private bool IsAvailableForTargetFramework(string assemblyFXVersionAsString)
        {
            Version assemblyFXVersion = FrameworkVersionFromString(assemblyFXVersionAsString);
            return (assemblyFXVersion == null) || (_projectTargetFramework == null) || (_projectTargetFramework >= assemblyFXVersion);
        }

        /// <summary>
        /// Validate and filter the _request.Assemblies that were passed in.
        /// - Check for assemblies that look like file names.
        /// - Check for assemblies where subtype!=''. These are removed.
        /// - Check for assemblies that have target framework higher than the project. These are removed.
        /// </summary>
        private void FilterBySubtypeAndTargetFramework()
        {
            ArrayList assembliesLeft = new ArrayList();
            foreach (ITaskItem assembly in _request.Assemblies)
            {
                string subType = assembly.GetMetadata(ItemMetadataNames.subType);
                if (subType != null && subType.Length > 0)
                {
                    Log.LogMessageFromResources(MessageImportance.Normal, "ResolveAssemblyReference.IgnoringBecauseNonEmptySubtype", assembly.ItemSpec, subType);
                }
                else if (!IsAvailableForTargetFramework(assembly.GetMetadata(ItemMetadataNames.targetFramework)))
                {
                    Log.LogWarningWithCodeFromResources("ResolveAssemblyReference.FailedToResolveReferenceBecauseHigherTargetFramework", assembly.ItemSpec, assembly.GetMetadata(ItemMetadataNames.targetFramework));
                }
                else
                {
                    assembliesLeft.Add(assembly);
                }
            }

            // Save the array of assemblies filtered by SubType==''.
            _request.Assemblies = (ITaskItem[])assembliesLeft.ToArray(typeof(ITaskItem));
        }

        /// <summary>
        /// Take a processor architecure and get the string representation back.
        /// </summary>
        internal static string ProcessorArchitectureToString(SystemProcessorArchitecture processorArchitecture)
        {
            if (SystemProcessorArchitecture.Amd64 == processorArchitecture)
            {
                return Microsoft.Build.Utilities.ProcessorArchitecture.AMD64;
            }
            else if (SystemProcessorArchitecture.IA64 == processorArchitecture)
            {
                return Microsoft.Build.Utilities.ProcessorArchitecture.IA64;
            }
            else if (SystemProcessorArchitecture.MSIL == processorArchitecture)
            {
                return Microsoft.Build.Utilities.ProcessorArchitecture.MSIL;
            }
            else if (SystemProcessorArchitecture.X86 == processorArchitecture)
            {
                return Microsoft.Build.Utilities.ProcessorArchitecture.X86;
            }
            else if (SystemProcessorArchitecture.Arm == processorArchitecture)
            {
                return Microsoft.Build.Utilities.ProcessorArchitecture.ARM;
            }
            return String.Empty;
        }

        // Convert the string passed into rar to a processor architecture enum so that we can properly compare it with the AssemblyName objects we find in assemblyFoldersEx
        internal static SystemProcessorArchitecture TargetProcessorArchitectureToEnumeration(string targetedProcessorArchitecture)
        {
            if (targetedProcessorArchitecture != null)
            {
                if (targetedProcessorArchitecture.Equals(Microsoft.Build.Utilities.ProcessorArchitecture.AMD64, StringComparison.OrdinalIgnoreCase))
                {
                    return SystemProcessorArchitecture.Amd64;
                }
                else if (targetedProcessorArchitecture.Equals(Microsoft.Build.Utilities.ProcessorArchitecture.IA64, StringComparison.OrdinalIgnoreCase))
                {
                    return SystemProcessorArchitecture.IA64;
                }
                else if (targetedProcessorArchitecture.Equals(Microsoft.Build.Utilities.ProcessorArchitecture.MSIL, StringComparison.OrdinalIgnoreCase))
                {
                    return SystemProcessorArchitecture.MSIL;
                }
                else if (targetedProcessorArchitecture.Equals(Microsoft.Build.Utilities.ProcessorArchitecture.X86, StringComparison.OrdinalIgnoreCase))
                {
                    return SystemProcessorArchitecture.X86;
                }
                else if (targetedProcessorArchitecture.Equals(Microsoft.Build.Utilities.ProcessorArchitecture.ARM, StringComparison.OrdinalIgnoreCase))
                {
                    return SystemProcessorArchitecture.Arm;
                }
            }

            return SystemProcessorArchitecture.MSIL;
        }

        /// <summary>
        ///  Checks to see if the assemblyName passed in is in the GAC.
        /// </summary>
        private string GetAssemblyPathInGac(AssemblyNameExtension assemblyName, SystemProcessorArchitecture targetProcessorArchitecture, GetAssemblyRuntimeVersion getRuntimeVersion, Version targetedRuntimeVersion, FileExists fileExists, bool fullFusionName, bool specificVersion)
        {
#if FEATURE_GAC
            return GlobalAssemblyCache.GetLocation(BuildEngine as IBuildEngine4, assemblyName, targetProcessorArchitecture, getRuntimeVersion, targetedRuntimeVersion, fullFusionName, fileExists, null, null, specificVersion /* this value does not matter if we are passing a full fusion name*/);
#else
            return string.Empty;
#endif
        }

        /// <summary>
        /// Execute the task.
        /// </summary>
        /// <returns>True if there was success.</returns>
        override public bool Execute()
        {
            return Execute
            (
                new FileExists(FileUtilities.FileExistsNoThrow),
                new DirectoryExists(FileUtilities.DirectoryExistsNoThrow),
                new GetDirectories(Directory.GetDirectories),
                new GetAssemblyName(AssemblyNameExtension.GetAssemblyNameEx),
                new GetAssemblyMetadata(AssemblyInformation.GetAssemblyMetadata),
#if FEATURE_WIN32_REGISTRY
                new GetRegistrySubKeyNames(RegistryHelper.GetSubKeyNames),
                new GetRegistrySubKeyDefaultValue(RegistryHelper.GetDefaultValue),
#endif
                new GetLastWriteTime(NativeMethodsShared.GetLastWriteFileUtcTime),
                new GetAssemblyRuntimeVersion(AssemblyInformation.GetRuntimeVersion),
#if FEATURE_WIN32_REGISTRY
                new OpenBaseKey(RegistryHelper.OpenBaseKey),
#endif
                new GetAssemblyPathInGac(GetAssemblyPathInGac),
                new IsWinMDFile(AssemblyInformation.IsWinMDFile),
                new ReadMachineTypeFromPEHeader(ReferenceTable.ReadMachineTypeFromPEHeader)
            );
        }

        #endregion
    }
}
