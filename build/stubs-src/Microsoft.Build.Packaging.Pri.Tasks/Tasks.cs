// Auto-generated from the original stub assembly (see rebuild-stubs.ps1).
// Each type mirrors the task surface the Windows App SDK targets bind against.
using Microsoft.Build.Framework;

namespace Microsoft.Build.AppxPackage
{
    public class ExpandPayloadDirectories : Microsoft.Build.Utilities.Task
    {
        public Microsoft.Build.Framework.ITaskItem[] Items { get; set; }
        [Output]
        public Microsoft.Build.Framework.ITaskItem[] ExpandedItems { get; set; }
        public override bool Execute() => true;
    }

    public class GetDefaultResourceLanguage : Microsoft.Build.Utilities.Task
    {
        [Output]
        public string DefaultResourceLanguage { get; set; }
        public string ManifestPath { get; set; }
        public override bool Execute() => true;
    }

    public class GetPackageArchitecture : Microsoft.Build.Utilities.Task
    {
        [Output]
        public string PackageArchitecture { get; set; }
        public override bool Execute() => true;
    }

    public class GetSdkFileFullPath : Microsoft.Build.Utilities.Task
    {
        [Output]
        public string SdkFileFullPath { get; set; }
        public string SdkToolsPath { get; set; }
        public string FileName { get; set; }
        public override bool Execute() => true;
    }

    public class GetSdkPropertyValue : Microsoft.Build.Utilities.Task
    {
        [Output]
        public string PropertyValue { get; set; }
        public string PropertyName { get; set; }
        public override bool Execute() => true;
    }

    public class RemovePayloadDuplicates : Microsoft.Build.Utilities.Task
    {
        public Microsoft.Build.Framework.ITaskItem[] Items { get; set; }
        [Output]
        public Microsoft.Build.Framework.ITaskItem[] FilteredItems { get; set; }
        public override bool Execute() => true;
    }

    public class RemoveRedundantXamlFilesFromSdkPayload : Microsoft.Build.Utilities.Task
    {
        public Microsoft.Build.Framework.ITaskItem[] Items { get; set; }
        [Output]
        public Microsoft.Build.Framework.ITaskItem[] FilteredItems { get; set; }
        public override bool Execute() => true;
    }

    public class ValidateConfiguration : Microsoft.Build.Utilities.Task
    {
        public override bool Execute() => true;
    }

}

namespace Microsoft.Build.Packaging.Pri.Tasks
{
    public class CreatePriConfigXmlForFullIndex : Microsoft.Build.Utilities.Task
    {
        public override bool Execute() => true;
    }

    public class CreatePriConfigXmlForMainPackageFileMap : Microsoft.Build.Utilities.Task
    {
        public override bool Execute() => true;
    }

    public class CreatePriConfigXmlForSplitting : Microsoft.Build.Utilities.Task
    {
        public Microsoft.Build.Framework.ITaskItem[] Items { get; set; }
        public string OutputFile { get; set; }
        [Output]
        public Microsoft.Build.Framework.ITaskItem[] OutputItems { get; set; }
        public override bool Execute() => true;
    }

    public class CreatePriFilesForPortableLibraries : Microsoft.Build.Utilities.Task
    {
        public override bool Execute() => true;
    }

    public class ExpandPriContent : Microsoft.Build.Utilities.Task
    {
        [Output]
        public Microsoft.Build.Framework.ITaskItem[] Expanded { get; set; }
        [Output]
        public Microsoft.Build.Framework.ITaskItem[] IntermediateFileWrites { get; set; }
        public Microsoft.Build.Framework.ITaskItem[] Inputs { get; set; }
        public string MakePriExeFullPath { get; set; }
        public string MakePriExtensionPath { get; set; }
        public string IntermediateDirectory { get; set; }
        public string AdditionalMakepriExeParameters { get; set; }
        public string VsTelemetrySession { get; set; }
        public bool ExcludeXamlFromLibraryLayoutsWhenXbfIsPresent { get; set; }
        public override bool Execute() => true;
    }

    public class GenerateMainPriConfigurationFile : Microsoft.Build.Utilities.Task
    {
        public override bool Execute() => true;
    }

    public class GeneratePriConfigurationFiles : Microsoft.Build.Utilities.Task
    {
        public override bool Execute() => true;
    }

    public class GenerateProjectPriFile : Microsoft.Build.Utilities.Task
    {
        [Output]
        public Microsoft.Build.Framework.ITaskItem[] OutputPriFile { get; set; }
        [Output]
        public Microsoft.Build.Framework.ITaskItem[] IntermediateFileWrites { get; set; }
        public Microsoft.Build.Framework.ITaskItem[] Inputs { get; set; }
        public string MakePriExeFullPath { get; set; }
        public string ConfigXmlFile { get; set; }
        public string OutputFileName { get; set; }
        public string IntermediateDirectory { get; set; }
        public string VsTelemetrySession { get; set; }
        public override bool Execute() => true;
    }

    public class RemoveDuplicatePriFiles : Microsoft.Build.Utilities.Task
    {
        public override bool Execute() => true;
    }

    public class UpdateMainPackageFileMap : Microsoft.Build.Utilities.Task
    {
        public override bool Execute() => true;
    }

}


