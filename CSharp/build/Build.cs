using System.Collections.Generic;
using System.Text.Json;
using Nuke.Common;
using Nuke.Common.CI.GitLab;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.Coverlet;
using Nuke.Common.Tools.Docker;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.EntityFramework;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Tools.PowerShell;
using Nuke.Common.Tools.Pulumi;
using Nuke.Common.Tools.ReportGenerator;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.ReportGenerator.ReportGeneratorTasks;
using Log = Serilog.Log;
using Project = Nuke.Common.ProjectModel.Project;

class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode

    public static int Main () => Execute<Build>(x => x.Compile);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;
    
    GitLab GitLab => GitLab.Instance;
    
    [Solution]
    readonly Solution Solution;
    [GitVersion(Framework = "net8.0")]
    GitVersion GitVersion;

    AbsolutePath SourceDirectory => RootDirectory / "src";
    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";
    AbsolutePath TestResultDirectory => ArtifactsDirectory / "test-results";
    IEnumerable<Project> TestProjects => Solution.GetAllProjects("*.Tests");
    [Parameter("NuGet server URL.")]
    readonly string NugetSource = "https://api.nuget.org/v3/index.json";
    [Parameter("API Key for the NuGet server.")]
    readonly string NugetApiKey;
    
    Target Version => _ => _
        .Executes(() =>
        {
            var output = JsonSerializer.Serialize(GitVersion, new JsonSerializerOptions()
            {
                WriteIndented = true,
            });
            Log.Information("Version information: ${Version}", output);
        });
    
    Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
            SourceDirectory.GlobDirectories("**/bin", "**/obj").ForEach(x => x.DeleteDirectory());
            ArtifactsDirectory.CreateOrCleanDirectory();
        });

    Target Restore => _ => _
        .Executes(() =>
        {
            DotNetRestore(s => s
                .SetProjectFile(Solution)
            );
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(s => s
                .EnableNoRestore()
                .SetProjectFile(Solution)
                .SetConfiguration(Configuration)
                .SetAssemblyVersion(GitVersion.AssemblySemVer)
                .SetFileVersion(GitVersion.AssemblySemFileVer)
                .SetInformationalVersion(GitVersion.InformationalVersion)
            );
        });

    Target Test => _ => _
        .DependsOn(Compile)
        .Produces(TestResultDirectory / "*.trx")
        .Produces(TestResultDirectory / "*.xml")
        .Executes(() =>
        {
            DotNetTest(_ => _
                .SetConfiguration(Configuration)
                .SetNoBuild(InvokedTargets.Contains(Compile))
                .ResetVerbosity()
                .SetProcessArgumentConfigurator(args => args.Add("--collect:\"XPlat Code Coverage\""))
                .SetResultsDirectory(TestResultDirectory)
                .When(IsServerBuild, _ => _
                    .EnableUseSourceLink())
                .CombineWith(TestProjects, (_, v) => _
                    .SetProjectFile(v)
                    .SetLoggers($"trx;LogFileName={v.Name}.trx")
                    .SetCoverletOutput(TestResultDirectory / $"{v.Name}.xml")));
        });

    string CoverageReportDirectory => ArtifactsDirectory / "coverage-report";

    Target Coverage => _ => _
        .DependsOn(Test)
        .TriggeredBy(Test)
        .Consumes(Test)
        .Executes(() =>
        {
	        var package = NuGetPackageResolver.GetGlobalInstalledPackage("dotnet-reportgenerator-globaltool", "5.1.26", null);
            
	        ReportGenerator(_ => _
                .SetProcessToolPath(package.Directory / "tools/net7.0/any/ReportGenerator.dll")
                .SetReports(TestResultDirectory / "**/*.xml")
                .SetReportTypes(ReportTypes.HtmlInline)
                .SetTargetDirectory(CoverageReportDirectory)
                .SetFramework("net7.0"));
        });

    string DockerDotnetSdkPlaywrightImageName => $"ghcr.io/sandwichstack/dotnet-sdk-playwright:8.0-{GitVersion.SemVer}";

    Target BuildDockerDotnetSdkPlaywright => _ => _
        .Executes(() =>
        {
            DotNetTasks.DotNetBuild(c => c
                .SetConfiguration("Debug")
                .SetProcessWorkingDirectory(RootDirectory / "Docker" / "dotnet-sdk-playwright" /
                                            "DownloadPlaywright"));
            DockerTasks.DockerBuild(c => c
                .SetPath(RootDirectory / "Docker" / "dotnet-sdk-playwright")
                .SetTag(DockerDotnetSdkPlaywrightImageName));
        });

    Target PushDockerDotnetSdkPlaywright => _ => _
        .After(BuildDockerDotnetSdkPlaywright)
        .Executes(() =>
        {
            DockerTasks.DockerPush(c => c
                .SetName(DockerDotnetSdkPlaywrightImageName));
        });

    string DockerDockerBashImageName => $"ghcr.io/sandwichstack/docker-bash:24.0.7-cli-alpine3.18-{GitVersion.SemVer}";

    Target BuildDockerDockerBash => _ => _
        .Executes(() =>
        {
            DockerTasks.DockerBuild(c => c
                .SetPath(RootDirectory / "Docker" / "docker-bash")
                .SetTag(DockerDockerBashImageName));
        });

    Target PushDockerDockerBash => _ => _
        .After(BuildDockerDockerBash)
        .Executes(() =>
        {
            DockerTasks.DockerPush(c => c
                .SetName(DockerDockerBashImageName));
        });

    string DockerHtmlPlaceholderImageName => $"ghcr.io/sandwichstack/html-placeholder:{GitVersion.SemVer}";

    Target BuildDockerHtmlPlaceholder => _ => _
        .Executes(() =>
        {
            DockerTasks.DockerBuild(c => c
                .SetPath(RootDirectory / "Docker" / "html-placeholder")
                .SetTag(DockerHtmlPlaceholderImageName));
        });

    Target PushDockerHtmlPlaceholder => _ => _
        .After(BuildDockerHtmlPlaceholder)
        .Executes(() =>
        {
            DockerTasks.DockerPush(c => c
                .SetName(DockerHtmlPlaceholderImageName));
        });

    string DockerDotnetSdkKanikoImageName => $"ghcr.io/sandwichstack/dotnet-sdk-kaniko:v1.18.0-debug-{GitVersion.SemVer}";

    Target BuildDockerDotnetSdkKaniko => _ => _
        .Executes(() =>
        {
            DockerTasks.DockerBuild(c => c
                .SetPath(RootDirectory / "Docker" / "dotnet-sdk-kaniko")
                .SetTag(DockerDotnetSdkKanikoImageName));
        });

    Target PushDockerDotnetSdkKaniko => _ => _
        .After(BuildDockerDotnetSdkKaniko)
        .Executes(() =>
        {
            DockerTasks.DockerPush(c => c
                .SetName(DockerDotnetSdkKanikoImageName));
        });

    Target PackExtensionsConfigurationInfisical => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetPack(s => s
                .EnableNoRestore()
                .EnableNoBuild()
                .SetProject(Solution.GetProject("SandwhichStack.Extensions.Configuration.Infisical"))
                .SetConfiguration(Configuration)
                .SetOutputDirectory(ArtifactsDirectory)
                .SetVersion(GitVersion.SemVer)
                .SetIncludeSymbols(true)
                .SetSymbolPackageFormat(DotNetSymbolPackageFormat.snupkg)
            );
        });

    Target NugetPush => _ => _
        .After(PackExtensionsConfigurationInfisical)
        .Requires(() => NugetApiKey)
        .Requires(() => Configuration == Configuration.Release)
        .Executes(() =>
        {
            DotNetNuGetPush(s => s
                .SetSource(NugetSource)
                .SetApiKey(NugetApiKey)
                .SetSkipDuplicate(true)
                .CombineWith(ArtifactsDirectory.GlobFiles("*.nupkg"), (s, v) => s
                    .SetTargetPath(v)
                )
            );
        });
}
