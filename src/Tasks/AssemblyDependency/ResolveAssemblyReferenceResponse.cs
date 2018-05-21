// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Build.Framework;

namespace Microsoft.Build.Tasks.AssemblyDependency
{
    class ResolveAssemblyReferenceResponse
    {
        public ITaskItem[] CopyLocalFiles { get; set; }
        public string DependsOnNETStandard { get; set; }
        public string DependsOnSystemRuntime { get; set; }
        public ITaskItem[] FilesWritten { get; set; }
        public ITaskItem[] RelatedFiles { get; set; }
        public ITaskItem[] ResolvedDependencyFiles { get; set; }
        public ITaskItem[] ResolvedFiles { get; set; }
        public ITaskItem[] ResolvedSDKReferences { get; set; }
        public ITaskItem[] SatelliteFiles { get; set; }
        public ITaskItem[] ScatterFiles { get; set; }
        public ITaskItem[] SuggestedRedirects { get; set; }
    }
}
