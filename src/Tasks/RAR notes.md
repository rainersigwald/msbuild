# RAR notes

## Inputs

### Task inputs

```
Assemblies="@(Reference)"
AssemblyFiles="@(_ResolvedProjectReferencePaths);@(_ExplicitReference)"
TargetFrameworkDirectories="@(_ReferenceInstalledAssemblyDirectory)"
InstalledAssemblyTables="@(InstalledAssemblyTables);@(RedistList)"
IgnoreDefaultInstalledAssemblyTables="$(IgnoreDefaultInstalledAssemblyTables)"
IgnoreDefaultInstalledAssemblySubsetTables="$(IgnoreInstalledAssemblySubsetTables)"
CandidateAssemblyFiles="@(Content);@(None)"
SearchPaths="$(AssemblySearchPaths)"
AllowedAssemblyExtensions="$(AllowedReferenceAssemblyFileExtensions)"
AllowedRelatedFileExtensions="$(AllowedReferenceRelatedFileExtensions)"
TargetProcessorArchitecture="$(ProcessorArchitecture)"
AppConfigFile="@(_ResolveAssemblyReferencesApplicationConfigFileForExes)"
AutoUnify="$(AutoUnifyAssemblyReferences)"
SupportsBindingRedirectGeneration="$(GenerateBindingRedirectsOutputType)"
IgnoreVersionForFrameworkReferences="$(IgnoreVersionForFrameworkReferences)"
FindDependencies="$(_FindDependencies)"
FindSatellites="$(BuildingProject)"
FindSerializationAssemblies="$(BuildingProject)"
FindRelatedFiles="$(BuildingProject)"
Silent="$(ResolveAssemblyReferencesSilent)"
TargetFrameworkVersion="$(TargetFrameworkVersion)"
TargetFrameworkMoniker="$(TargetFrameworkMoniker)"
TargetFrameworkMonikerDisplayName="$(TargetFrameworkMonikerDisplayName)"
TargetedRuntimeVersion="$(TargetedRuntimeVersion)"
StateFile="$(ResolveAssemblyReferencesStateFile)"
InstalledAssemblySubsetTables="@(InstalledAssemblySubsetTables)"
TargetFrameworkSubsets="@(_ReferenceInstalledAssemblySubsets)"
FullTargetFrameworkSubsetNames="$(FullReferenceAssemblyNames)"
FullFrameworkFolders="$(_FullFrameworkReferenceAssemblyPaths)"
FullFrameworkAssemblyTables="@(FullFrameworkAssemblyTables)"
ProfileName="$(TargetFrameworkProfile)"
LatestTargetFrameworkDirectories="@(LatestTargetFrameworkDirectories)"
CopyLocalDependenciesWhenParentReferenceInGac="$(CopyLocalDependenciesWhenParentReferenceInGac)"
DoNotCopyLocalIfInGac="$(DoNotCopyLocalIfInGac)"
ResolvedSDKReferences="@(ResolvedSDKReference)"
WarnOrErrorOnTargetArchitectureMismatch="$(ResolveAssemblyWarnOrErrorOnTargetArchitectureMismatch)"
IgnoreTargetFrameworkAttributeVersionMismatch ="$(ResolveAssemblyReferenceIgnoreTargetFrameworkAttributeVersionMismatch)"
FindDependenciesOfExternallyResolvedReferences="$(FindDependenciesOfExternallyResolvedReferences)"
```

### Environment

MSBUILDLOGVERBOSERARSEARCHRESULTS

## Outputs

* Logging

```xml
      <Output TaskParameter="ResolvedFiles" ItemName="ReferencePath"/>
      <Output TaskParameter="ResolvedFiles" ItemName="_ResolveAssemblyReferenceResolvedFiles"/>
      <Output TaskParameter="ResolvedDependencyFiles" ItemName="ReferenceDependencyPaths"/>
      <Output TaskParameter="RelatedFiles" ItemName="_ReferenceRelatedPaths"/>
      <Output TaskParameter="SatelliteFiles" ItemName="ReferenceSatellitePaths"/>
      <Output TaskParameter="SerializationAssemblyFiles" ItemName="_ReferenceSerializationAssemblyPaths"/>
      <Output TaskParameter="ScatterFiles" ItemName="_ReferenceScatterPaths"/>
      <Output TaskParameter="CopyLocalFiles" ItemName="ReferenceCopyLocalPaths"/>
      <Output TaskParameter="SuggestedRedirects" ItemName="SuggestedBindingRedirects"/>
      <Output TaskParameter="FilesWritten" ItemName="FileWrites"/>
      <Output TaskParameter="DependsOnSystemRuntime" PropertyName="DependsOnSystemRuntime"/>
      <Output TaskParameter="DependsOnNETStandard" PropertyName="_DependsOnNETStandard"/>
```