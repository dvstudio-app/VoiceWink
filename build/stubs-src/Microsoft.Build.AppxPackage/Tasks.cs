// Auto-generated from the original stub assembly (see rebuild-stubs.ps1).
// Each type mirrors the task surface the Windows App SDK targets bind against.
using Microsoft.Build.Framework;

namespace Microsoft.Build.AppxPackage
{
    public class ExpandPayloadDirectories : Microsoft.Build.Utilities.Task
    {
        public Microsoft.Build.Framework.ITaskItem[] Inputs { get; set; }
        public Microsoft.Build.Framework.ITaskItem[] TargetDirsToExclude { get; set; }
        public Microsoft.Build.Framework.ITaskItem[] TargetFilesToExclude { get; set; }
        public string VsTelemetrySession { get; set; }
        [Output]
        public Microsoft.Build.Framework.ITaskItem[] Expanded { get; set; }
        public override bool Execute() { Expanded = Inputs ?? new ITaskItem[0]; return true; }
    }

    public class GetDefaultResourceLanguage : Microsoft.Build.Utilities.Task
    {
        public string DefaultLanguage { get; set; }
        public Microsoft.Build.Framework.ITaskItem[] SourceAppxManifest { get; set; }
        public string VsTelemetrySession { get; set; }
        [Output]
        public string DefaultResourceLanguage { get; set; }
        public override bool Execute() { DefaultResourceLanguage = DefaultLanguage ?? "en-US"; return true; }
    }

    public class GetPackageArchitecture : Microsoft.Build.Utilities.Task
    {
        public string Platform { get; set; }
        public Microsoft.Build.Framework.ITaskItem[] ProjectArchitecture { get; set; }
        public Microsoft.Build.Framework.ITaskItem[] RecursiveProjectArchitecture { get; set; }
        public string VsTelemetrySession { get; set; }
        [Output]
        public string PackageArchitecture { get; set; }
        public override bool Execute() { PackageArchitecture = Platform ?? "x64"; return true; }
    }

    public class GetSdkFileFullPath : Microsoft.Build.Utilities.Task
    {
        public string FileName { get; set; }
        public string FullFilePath { get; set; }
        public string FileArchitecture { get; set; }
        public bool RequireExeExtension { get; set; }
        public string TargetPlatformSdkRootOverride { get; set; }
        public string SDKIdentifier { get; set; }
        public string SDKVersion { get; set; }
        public string TargetPlatformIdentifier { get; set; }
        public string TargetPlatformMinVersion { get; set; }
        public string TargetPlatformVersion { get; set; }
        public bool MSBuildExtensionsPath64Exists { get; set; }
        public string VsTelemetrySession { get; set; }
        [Output]
        public string ActualFullFilePath { get; set; }
        [Output]
        public string ActualFileArchitecture { get; set; }
        public override bool Execute() { ActualFullFilePath = FullFilePath ?? ""; ActualFileArchitecture = FileArchitecture ?? ""; return true; }
    }

    public class GetSdkPropertyValue : Microsoft.Build.Utilities.Task
    {
        public string TargetPlatformSdkRootOverride { get; set; }
        public string SDKIdentifier { get; set; }
        public string SDKVersion { get; set; }
        public string TargetPlatformIdentifier { get; set; }
        public string TargetPlatformMinVersion { get; set; }
        public string TargetPlatformVersion { get; set; }
        public string PropertyName { get; set; }
        public string VsTelemetrySession { get; set; }
        [Output]
        public string PropertyValue { get; set; }
        public override bool Execute() { PropertyValue = ""; return true; }
    }

    public class RemovePayloadDuplicates : Microsoft.Build.Utilities.Task
    {
        public Microsoft.Build.Framework.ITaskItem[] Inputs { get; set; }
        public string ProjectName { get; set; }
        public string Platform { get; set; }
        public string VsTelemetrySession { get; set; }
        [Output]
        public Microsoft.Build.Framework.ITaskItem[] Filtered { get; set; }
        public override bool Execute() { Filtered = Inputs ?? new ITaskItem[0]; return true; }
    }

    public class RemoveRedundantXamlFilesFromSdkPayload : Microsoft.Build.Utilities.Task
    {
        public Microsoft.Build.Framework.ITaskItem[] Inputs { get; set; }
        public string VsTelemetrySession { get; set; }
        [Output]
        public Microsoft.Build.Framework.ITaskItem[] Filtered { get; set; }
        public override bool Execute() { Filtered = Inputs ?? new ITaskItem[0]; return true; }
    }

    public class ValidateConfiguration : Microsoft.Build.Utilities.Task
    {
        public string TargetPlatformMinVersion { get; set; }
        public string TargetPlatformVersion { get; set; }
        public string ProjectLanguage { get; set; }
        public string VsTelemetrySession { get; set; }
        public string TargetPlatformIdentifier { get; set; }
        public string Platform { get; set; }
        public override bool Execute() => true;
    }

}


