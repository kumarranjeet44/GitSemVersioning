#tool "nuget:?package=NuGet.CommandLine&version=5.5.1"
#tool "nuget:?package=GitVersion.CommandLine&version=5.3.5"
#tool "nuget:?package=OpenCover&version=4.7.922"
#addin "nuget:?package=Cake.Curl&version=4.1.0"
#addin "nuget:?package=Cake.Git&version=1.0.1"
#addin "nuget:?package=Cake.FileHelpers&version=3.0.0"
#tool "nuget:?package=Microsoft.TestPlatform&version=16.6.1"
#addin "nuget:?package=Newtonsoft.Json&version=9.0.1&prerelease"
#tool "nuget:?package=coverlet.console&version=3.1.2"
#tool "nuget:?package=Microsoft.CodeCoverage&version=16.9.4"
using Cake.Common.Tools.GitVersion;
using Newtonsoft.Json; 
using System.Net.Http;
using System.Threading;
using Cake.Core.Text;

public const string PROVIDED_BY_GITHUB = "PROVIDED_BY_GITHUB";

var solution = Argument("solution", "./GitSemVersioning.sln");
var target = Argument("do", "build");
var configuration = Argument("configuration", "Release");
var testResultsDir = Directory("./TestResults");
var buildVersion = "1.1";
var ouputDir = Directory("./obj");

// Removed sonarQube and artifactory arguments

var gitVersion = GitVersion(new GitVersionSettings {});
var githubBuildNumber = gitVersion.CommitsSinceVersionSource;
var gitProjectVersionNumber = gitVersion.MajorMinorPatch;
public string completeVersionForAssemblyInfo = string.Concat(gitProjectVersionNumber,".",githubBuildNumber);
public string completeVersionForWix = string.Concat(gitProjectVersionNumber,".",githubBuildNumber);

var gitUserName = Argument("gitusername", "PROVIDED_BY_GITHUB"); 
var gitUserPassword = Argument("gituserpassword", "PROVIDED_BY_GITHUB"); 

// Removed artifactory repo variables
var zipPath = new DirectoryPath("./artifact");

var EXG401UIAssemblyVersion = completeVersionForWix;
Information("Version (401 BL Application): {0}", completeVersionForWix); 
Information("BranchName: {0}", gitVersion.BranchName);



Task("Clean").Does(() => {
	CleanDirectories("./artifact");
    CleanDirectories("./TestResults");
	CleanDirectories("**/bin/" + configuration);
	CleanDirectories("**/obj/" + configuration);
});

Task("Restore")
    .Does(() => {
        DotNetRestore("./GitSemVersioning.sln");
    });

Task("Build").IsDependentOn("Restore").Does(() => 
{
    DotNetBuild("./GitSemVersioning.sln", new DotNetBuildSettings
    {
        Configuration = configuration,
		OutputDirectory = ouputDir
    });

});

// Note: ContinueOnError for test Task to allow Bamboo capture TestResults produced and halt pipeline from there.

Task("Test").ContinueOnError().Does(() =>
{
    var testProjects = GetFiles("./**/*.Test.csproj");
    foreach (var project in testProjects)
    {
        var projectName = project.GetFilenameWithoutExtension();
        var testSettings = new DotNetTestSettings
        {
            Loggers = new[] { $"trx;LogFileName={projectName}.trx" },
            ArgumentCustomization = args => args
                .Append("--collect:\"XPlat Code Coverage\"")
                .Append("/p:CollectCoverage=true")
                .Append("-- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover")
        };
        DotNetTest(project.FullPath, testSettings);
    }

    // Copy Test Results and Coverage Reports
    var testResultsDir = Directory("./TestResults");
    var coverageResultsDir = Directory("./CoverageResults");
    EnsureDirectoryExists(testResultsDir);
    EnsureDirectoryExists(coverageResultsDir);

    var trxFiles = GetFiles("./**/*.trx");
    foreach (var file in trxFiles)
    {
        CopyFileToDirectory(file, testResultsDir);
    }

    var coverageFiles = GetFiles("./**/coverage*.xml");
    foreach (var file in coverageFiles)
    {
        CopyFileToDirectory(file, coverageResultsDir);
    }

    Information("Test Results:");
    foreach (var file in GetFiles(testResultsDir.Path.FullPath + "/*.trx"))
    {
        Information(file.FullPath);
    }

    Information("Coverage Results:");
    foreach (var file in GetFiles(coverageResultsDir.Path.FullPath + "/*.xml"))
    {
        Information(file.FullPath);
    }

});

Task("Tagmaster").Does(() => {
    Information("Tagging with gitUserName: {0}", gitUserName);
    Information("Tagging with gitUserPassword: {0}", gitUserPassword);

    // Check if running in GitHub Actions
    var isGitHubActions = EnvironmentVariable("GITHUB_ACTIONS") == "true";
    if (!isGitHubActions)
    {
        Information("Not running inside GitHub Actions, skipping tagging.");
        return;
    }

    Information("Running inside GitHub Actions.");
    Information("GitVersion details: {0}", JsonConvert.SerializeObject(gitVersion, Formatting.Indented));

    var currentBranch = gitVersion.BranchName;
    if (currentBranch != "master" && currentBranch != "develop")
    {
        Information($"Current branch '{currentBranch}' is not master or develop. Skipping tagging.");
        return;
    }

    if (string.IsNullOrEmpty(gitUserName) || string.IsNullOrEmpty(gitUserPassword) ||
        gitUserName == "PROVIDED_BY_GITHUB" || gitUserPassword == "PROVIDED_BY_GITHUB")
    {
        throw new Exception("Git Username/Password not provided.");
    }

    // Determine the tag format
    string tagName;
    if (currentBranch == "master")
    {
        tagName = $"v{gitVersion.MajorMinorPatch}.{gitVersion.CommitsSinceVersionSource}";
    }
    else if (currentBranch == "develop")
    {
        var mergeSource = EnvironmentVariable("GITHUB_HEAD_REF") ?? ""; // fallback in PR context
        if (string.IsNullOrEmpty(mergeSource))
        {
            // In push context, fallback to detecting last commit branch
            var result = StartProcess("git", new ProcessSettings
            {
                Arguments = "log -1 --pretty=format:%s",
                RedirectStandardOutput = true
            });
            mergeSource = result == 0 ? System.IO.File.ReadAllText("./.git/ORIG_HEAD").Trim() : "";
        }

        if (mergeSource.StartsWith("feature/") || mergeSource.StartsWith("bugfix/"))
        {
            tagName = $"v{gitVersion.MajorMinorPatch}-alpha.{gitVersion.CommitsSinceVersionSource}";
        }
        else if (mergeSource.StartsWith("release/"))
        {
            tagName = $"v{gitVersion.MajorMinorPatch}-beta.{gitVersion.CommitsSinceVersionSource}";
        }
        else
        {
            Information("Merge source branch is not feature/bugfix/release, skipping tagging.");
            return;
        }
    }
    else
    {
        // Should never reach here due to earlier branch check
        return;
    }

    // Check if tag already exists
    var currentTags = GitTags(".");
    if (currentTags.Any(t => t.FriendlyName == tagName))
    {
        Information($"Tag '{tagName}' already exists. Skipping.");
        return;
    }

    // Create the tag
    var workingDir = MakeAbsolute(Directory("./"));
    Information($"Creating local tag: {tagName} in {workingDir}");
    GitTag(workingDir, tagName);

    // Push the tag to origin
    Information("Pushing tag to origin...");
    var pushTagResult = StartProcess("git", new ProcessSettings
    {
        Arguments = new ProcessArgumentBuilder()
            .Append("push")
            .Append("origin")
            .Append(tagName),
        RedirectStandardOutput = true,
        RedirectStandardError = true
    });

    if (pushTagResult != 0)
    {
        Error("Failed to push tag to origin.");
        Environment.Exit(1);
    }
    else
    {
        Information($"Tag '{tagName}' successfully pushed to origin.");
    }
});


Task("full")
    .IsDependentOn("Clean")
    .IsDependentOn("Build")
    .IsDependentOn("Test")
    .IsDependentOn("Tagmaster");

RunTarget(target);