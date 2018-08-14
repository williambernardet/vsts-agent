using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using System;
using System.IO;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.VisualStudio.Services.Agent.Worker.Maintenance;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Diagnostics;
using Microsoft.TeamFoundation.DistributedTask.Pipelines;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Microsoft.VisualStudio.Services.Agent.Worker.Build
{
    public static class RepositoryResourceExtensions
    {
        public static string GetSourceDirectoryHashKey(this RepositoryResource repository, IExecutionContext executionContext)
        {
            // Validate parameters.
            ArgUtil.NotNull(executionContext, nameof(executionContext));
            ArgUtil.NotNull(executionContext.Variables, nameof(executionContext.Variables));
            ArgUtil.NotNull(repository.Url, nameof(repository.Url));

            // Calculate the hash key.
            const string Format = "{{{{ \r\n    \"system\" : \"build\", \r\n    \"collectionId\" = \"{0}\", \r\n    \"definitionId\" = \"{1}\", \r\n    \"repositoryUrl\" = \"{2}\", \r\n    \"sourceFolder\" = \"{{0}}\",\r\n    \"hashKey\" = \"{{1}}\"\r\n}}}}";
            string hashInput = string.Format(
                CultureInfo.InvariantCulture,
                Format,
                executionContext.Variables.System_CollectionId,
                executionContext.Variables.System_DefinitionId,
                repository.Url.AbsoluteUri);
            using (SHA1 sha1Hash = SHA1.Create())
            {
                byte[] data = sha1Hash.ComputeHash(Encoding.UTF8.GetBytes(hashInput));
                StringBuilder hexString = new StringBuilder();
                for (int i = 0; i < data.Length; i++)
                {
                    hexString.Append(data[i].ToString("x2"));
                }

                return hexString.ToString();
            }
        }

        public static Boolean TestOverrideBuildDirectory(this RepositoryResource repository, AgentSettings settings)
        {
            if (repository.Type == TeamFoundation.DistributedTask.Pipelines.RepositoryTypes.Tfvc)
            {
                return settings.IsHosted;
            }
            else
            {
                return false;
            }
        }
    }

    [ServiceLocator(Default = typeof(BuildDirectoryManager))]
    public interface IBuildDirectoryManager : IAgentService
    {
        PipelineTrackingConfig PrepareDirectory(
            IExecutionContext executionContext,
            WorkspaceOptions workspace);
    }

    public sealed class BuildDirectoryManager : AgentService, IBuildDirectoryManager, IMaintenanceServiceProvider
    {
        public string MaintenanceDescription => StringUtil.Loc("DeleteUnusedBuildDir");
        public Type ExtensionType => typeof(IMaintenanceServiceProvider);

        public PipelineTrackingConfig PrepareDirectory(
            IExecutionContext executionContext,
            WorkspaceOptions workspace)
        {
            // Validate parameters.
            Trace.Entering();
            ArgUtil.NotNull(executionContext, nameof(executionContext));
            var trackingManager = HostContext.GetService<ITrackingManager>();

            // Load the existing tracking file if one already exists.
            string trackingFile = Path.Combine(
                HostContext.GetDirectory(WellKnownDirectory.Work),
                "RootMapping",
                executionContext.Variables.System_CollectionId,
                executionContext.Variables.System_DefinitionId,
                "pipeline.json");

            Trace.Verbose($"Loading tracking config if exists: {trackingFile}");
            PipelineTrackingConfig trackingConfig = trackingManager.LoadIfExistsV2(executionContext, trackingFile);

            if (trackingConfig == null)
            {
                // try convert existing build tracking file
                Trace.Verbose($"Try convert existing build tracking config.");
                trackingConfig = ConvertToPipelineTrackingFile(executionContext);
            }

            if (trackingConfig == null)
            {
                Trace.Verbose("Creating a new tracking config file.");
                trackingConfig = trackingManager.Create(executionContext, trackingFile);
            }
            else
            {
                // Update tracking information for existing tracking config file:
                // 1. update the job execution properties. (collection url/definition url etc.)
                // 2. update the job resources. (repositories changes)
                Trace.Verbose("Updating job execution properties and resources into tracking config file.");
                trackingManager.UpdateJobRunProperties(executionContext, trackingConfig, trackingFile);
            }

            // We should have a tracking config at this point.
            ArgUtil.NotNull(trackingConfig, nameof(trackingConfig));

            // Prepare the job directory.
            // There are 2 ways to provide build directory clean policy.
            //     1> set definition variable build.clean or agent.clean.buildDirectory. (on-prem user need to use this, since there is no Web UI in TFS 2016)
            //     2> provide clean option in Pipelines.WorkspaceOptions (VSTS will have this option in definition designer UI and YAML)
            string cleanOption = GetWorkspaceCleanOption(executionContext, workspace);

            CreateDirectory(
                executionContext,
                description: "job directory",
                path: Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), trackingConfig.WorkspaceDirectory),
                deleteExisting: cleanOption == PipelineConstants.WorkspaceCleanOptions.All);
            CreateDirectory(
                executionContext,
                description: "artifacts directory",
                path: Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), trackingConfig.ArtifactsDirectory),
                deleteExisting: true);
            CreateDirectory(
                executionContext,
                description: "test results directory",
                path: Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), trackingConfig.TestResultsDirectory),
                deleteExisting: true);
            CreateDirectory(
                executionContext,
                description: "binaries directory",
                path: Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), trackingConfig.BinariesDirectory),
                deleteExisting: cleanOption == PipelineConstants.WorkspaceCleanOptions.Outputs);
            CreateDirectory(
                executionContext,
                description: "repositories directory",
                path: Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), trackingConfig.RepositoriesDirectory),
                deleteExisting: cleanOption == PipelineConstants.WorkspaceCleanOptions.Resources);

            // Create directory for each repository resource
            // Set each repository's path (calculated by tracking file) back to repository's property
            foreach (var repo in executionContext.Repositories)
            {
                string repositoryDirectory = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), trackingConfig.Repositories[repo.Alias].RepositoryDirectory);
                CreateDirectory(
                    executionContext,
                    description: $"repository directory {repo.Alias}",
                    path: repositoryDirectory,
                    deleteExisting: false);

                repo.Properties.Set<string>(RepositoryPropertyNames.Path, repositoryDirectory);
            }

            return trackingConfig;
        }

        public async Task RunMaintenanceOperation(IExecutionContext executionContext)
        {
            Trace.Entering();
            ArgUtil.NotNull(executionContext, nameof(executionContext));

            // this might be not accurate when the agent is configured for old TFS server
            int totalAvailableTimeInMinutes = executionContext.Variables.GetInt("maintenance.jobtimeoutinminutes") ?? 60;

            // start a timer to track how much time we used
            Stopwatch totalTimeSpent = Stopwatch.StartNew();

            var trackingManager = HostContext.GetService<ITrackingManager>();
            int staleBuildDirThreshold = executionContext.Variables.GetInt("maintenance.deleteworkingdirectory.daysthreshold") ?? 0;
            if (staleBuildDirThreshold > 0)
            {
                // scan unused build directories
                executionContext.Output(StringUtil.Loc("DiscoverBuildDir", staleBuildDirThreshold));
                trackingManager.MarkExpiredForGarbageCollection(executionContext, TimeSpan.FromDays(staleBuildDirThreshold));
            }
            else
            {
                executionContext.Output(StringUtil.Loc("GCBuildDirNotEnabled"));
                return;
            }

            executionContext.Output(StringUtil.Loc("GCBuildDir"));

            // delete unused build directories
            trackingManager.DisposeCollectedGarbage(executionContext);

            // give source provider a chance to run maintenance operation
            Trace.Info("Scan all SourceFolder tracking files.");
            string searchRoot = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), Constants.Build.Path.SourceRootMappingDirectory);
            if (!Directory.Exists(searchRoot))
            {
                executionContext.Output(StringUtil.Loc("GCDirNotExist", searchRoot));
                return;
            }

            // <tracking config, tracking file path>
            List<Tuple<TrackingConfig, string>> optimizeTrackingFiles = new List<Tuple<TrackingConfig, string>>();
            var allTrackingFiles = Directory.EnumerateFiles(searchRoot, Constants.Build.Path.TrackingConfigFile, SearchOption.AllDirectories);
            Trace.Verbose($"Find {allTrackingFiles.Count()} tracking files.");
            foreach (var trackingFile in allTrackingFiles)
            {
                executionContext.Output(StringUtil.Loc("EvaluateTrackingFile", trackingFile));
                TrackingConfigBase tracking = trackingManager.LoadIfExists(executionContext, trackingFile);

                // detect whether the tracking file is in new format.
                TrackingConfig newTracking = tracking as TrackingConfig;
                if (newTracking == null)
                {
                    executionContext.Output(StringUtil.Loc("GCOldFormatTrackingFile", trackingFile));
                }
                else if (string.IsNullOrEmpty(newTracking.RepositoryType))
                {
                    // repository not been set.
                    executionContext.Output(StringUtil.Loc("SkipTrackingFileWithoutRepoType", trackingFile));
                }
                else
                {
                    optimizeTrackingFiles.Add(new Tuple<TrackingConfig, string>(newTracking, trackingFile));
                }
            }

            // Sort the all tracking file ASC by last maintenance attempted time
            foreach (var trackingInfo in optimizeTrackingFiles.OrderBy(x => x.Item1.LastMaintenanceAttemptedOn))
            {
                // maintenance has been cancelled.
                executionContext.CancellationToken.ThrowIfCancellationRequested();

                bool runMainenance = false;
                TrackingConfig trackingConfig = trackingInfo.Item1;
                string trackingFile = trackingInfo.Item2;
                if (trackingConfig.LastMaintenanceAttemptedOn == null)
                {
                    // this folder never run maintenance before, we will do maintenance if there is more than half of the time remains.
                    if (totalTimeSpent.Elapsed.TotalMinutes < totalAvailableTimeInMinutes / 2)  // 50% time left
                    {
                        runMainenance = true;
                    }
                    else
                    {
                        executionContext.Output($"Working directory '{trackingConfig.BuildDirectory}' has never run maintenance before. Skip since we may not have enough time.");
                    }
                }
                else if (trackingConfig.LastMaintenanceCompletedOn == null)
                {
                    // this folder did finish maintenance last time, this might indicate we need more time for this working directory
                    if (totalTimeSpent.Elapsed.TotalMinutes < totalAvailableTimeInMinutes / 4)  // 75% time left
                    {
                        runMainenance = true;
                    }
                    else
                    {
                        executionContext.Output($"Working directory '{trackingConfig.BuildDirectory}' didn't finish maintenance last time. Skip since we may not have enough time.");
                    }
                }
                else
                {
                    // estimate time for running maintenance
                    TimeSpan estimateTime = trackingConfig.LastMaintenanceCompletedOn.Value - trackingConfig.LastMaintenanceAttemptedOn.Value;

                    // there is more than 10 mins left after we run maintenance on this repository directory
                    if (totalAvailableTimeInMinutes > totalTimeSpent.Elapsed.TotalMinutes + estimateTime.TotalMinutes + 10)
                    {
                        runMainenance = true;
                    }
                    else
                    {
                        executionContext.Output($"Working directory '{trackingConfig.BuildDirectory}' may take about '{estimateTime.TotalMinutes}' mins to finish maintenance. It's too risky since we only have '{totalAvailableTimeInMinutes - totalTimeSpent.Elapsed.TotalMinutes}' mins left for maintenance.");
                    }
                }

                if (runMainenance)
                {
                    var extensionManager = HostContext.GetService<IExtensionManager>();
                    ISourceProvider sourceProvider = extensionManager.GetExtensions<ISourceProvider>().FirstOrDefault(x => string.Equals(x.RepositoryType, trackingConfig.RepositoryType, StringComparison.OrdinalIgnoreCase));
                    if (sourceProvider != null)
                    {
                        try
                        {
                            trackingManager.MaintenanceStarted(trackingConfig, trackingFile);
                            string repositoryPath = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), trackingConfig.SourcesDirectory);
                            await sourceProvider.RunMaintenanceOperations(executionContext, repositoryPath);
                            trackingManager.MaintenanceCompleted(trackingConfig, trackingFile);
                        }
                        catch (Exception ex)
                        {
                            executionContext.Error(StringUtil.Loc("ErrorDuringBuildGC", trackingFile));
                            executionContext.Error(ex);
                        }
                    }
                }
            }
        }

        public PipelineTrackingConfig ConvertToPipelineTrackingFile(IExecutionContext executionContext)
        {
            var trackingManager = HostContext.GetService<ITrackingManager>();

            // Load the existing tracking file if one already exists.
            string trackingFile = Path.Combine(
                HostContext.GetDirectory(WellKnownDirectory.Work),
                Constants.Build.Path.SourceRootMappingDirectory,
                executionContext.Variables.System_CollectionId,
                executionContext.Variables.System_DefinitionId,
                Constants.Build.Path.TrackingConfigFile);
            Trace.Verbose($"Loading tracking config if exists: {trackingFile}");
            TrackingConfigBase existingConfig = trackingManager.LoadIfExists(executionContext, trackingFile);
            if (existingConfig == null)
            {
                Trace.Verbose($"Tracking config doesn't exists: {trackingFile}");
                return null;
            }

            TrackingConfig existingConfigV2 = null;
            RepositoryResource existingRepository = null;
            foreach (var repository in executionContext.Repositories)
            {
                if (string.Equals(existingConfig.HashKey, repository.GetSourceDirectoryHashKey(executionContext), StringComparison.OrdinalIgnoreCase))
                {
                    existingRepository = repository;
                    break;
                }
            }

            if (existingRepository == null)
            {
                Trace.Verbose($"Can't find repository with same hash key for existing tracking config. Existing key: {existingConfig.HashKey}");
                Trace.Verbose("Marking existing config for garbage collection.");
                trackingManager.MarkForGarbageCollection(executionContext, existingConfig);
                return null;
            }
            else
            {
                // Convert legacy format to the new format if required.
                existingConfigV2 = ConvertToNewFormat(executionContext, existingRepository, existingConfig);
                PipelineTrackingConfig pipelineTrackingConfig = new PipelineTrackingConfig()
                {
                    CollectionId = existingConfigV2.CollectionId,
                    CollectionUrl = existingConfigV2.CollectionId,
                    DefinitionId = existingConfigV2.DefinitionId,
                    DefinitionName = existingConfigV2.DefinitionName,
                    LastRunOn = existingConfigV2.LastRunOn,
                    WorkspaceDirectory = existingConfigV2.BuildDirectory,
                    ArtifactsDirectory = existingConfigV2.ArtifactsDirectory,
                    BinariesDirectory = Path.Combine(existingConfigV2.BuildDirectory, Constants.Build.Path.BinariesDirectory),
                    TestResultsDirectory = existingConfigV2.TestResultsDirectory,
                    RepositoriesDirectory = existingConfigV2.SourcesDirectory,
                    Repositories = {
                        {
                            existingRepository.Alias,
                            new RepositoryTrackingConfig()
                            {
                                RepositoryType = existingConfigV2.RepositoryType,
                                RepositoryUrl = existingConfigV2.RepositoryUrl,
                                RepositoryDirectory=existingConfigV2.SourcesDirectory,
                                LastRunOn = existingConfigV2.LastRunOn,
                                LastMaintenanceAttemptedOn = existingConfigV2.LastMaintenanceAttemptedOn,
                                LastMaintenanceCompletedOn = existingConfigV2.LastMaintenanceCompletedOn
                            }
                        }
                    }
                };
                return pipelineTrackingConfig;
            }
        }

        private TrackingConfig ConvertToNewFormat(
            IExecutionContext executionContext,
            RepositoryResource repository,
            TrackingConfigBase config)
        {
            Trace.Entering();

            // If it's already in the new format, return it.
            TrackingConfig newConfig = config as TrackingConfig;
            if (newConfig != null)
            {
                return newConfig;
            }

            // Delete the legacy artifact/staging directories.
            LegacyTrackingConfig legacyConfig = config as LegacyTrackingConfig;
            DeleteDirectory(
                executionContext,
                description: "legacy artifacts directory",
                path: Path.Combine(legacyConfig.BuildDirectory, Constants.Build.Path.LegacyArtifactsDirectory));
            DeleteDirectory(
                executionContext,
                description: "legacy staging directory",
                path: Path.Combine(legacyConfig.BuildDirectory, Constants.Build.Path.LegacyStagingDirectory));

            // Determine the source directory name. Check if the directory is named "s" already.
            // Convert the source directory to be named "s" if there is a problem with the old name.
            string sourcesDirectoryNameOnly = Constants.Build.Path.SourcesDirectory;
            string repositoryName = repository.Properties.Get<string>(RepositoryPropertyNames.Name);
            if (!Directory.Exists(Path.Combine(legacyConfig.BuildDirectory, sourcesDirectoryNameOnly))
                && !String.Equals(repositoryName, Constants.Build.Path.ArtifactsDirectory, StringComparison.OrdinalIgnoreCase)
                && !String.Equals(repositoryName, Constants.Build.Path.LegacyArtifactsDirectory, StringComparison.OrdinalIgnoreCase)
                && !String.Equals(repositoryName, Constants.Build.Path.LegacyStagingDirectory, StringComparison.OrdinalIgnoreCase)
                && !String.Equals(repositoryName, Constants.Build.Path.TestResultsDirectory, StringComparison.OrdinalIgnoreCase)
                && !repositoryName.Contains("\\")
                && !repositoryName.Contains("/")
                && Directory.Exists(Path.Combine(legacyConfig.BuildDirectory, repositoryName)))
            {
                sourcesDirectoryNameOnly = repositoryName;
            }

            // Convert to the new format.
            newConfig = new TrackingConfig(
                executionContext,
                legacyConfig,
                sourcesDirectoryNameOnly,
                repository.Type,
                // The legacy artifacts directory has been deleted at this point - see above - so
                // switch the configuration to using the new naming scheme.
                useNewArtifactsDirectoryName: true);
            return newConfig;
        }

        private void CreateDirectory(IExecutionContext executionContext, string description, string path, bool deleteExisting)
        {
            // Delete.
            if (deleteExisting)
            {
                executionContext.Debug($"Delete existing {description}: '{path}'");
                DeleteDirectory(executionContext, description, path);
            }

            // Create.
            if (!Directory.Exists(path))
            {
                executionContext.Debug($"Creating {description}: '{path}'");
                Trace.Info($"Creating {description}.");
                Directory.CreateDirectory(path);
            }
        }

        private void DeleteDirectory(IExecutionContext executionContext, string description, string path)
        {
            Trace.Info($"Checking if {description} exists: '{path}'");
            if (Directory.Exists(path))
            {
                executionContext.Debug($"Deleting {description}: '{path}'");
                IOUtil.DeleteDirectory(path, executionContext.CancellationToken);
            }
        }

        private String GetWorkspaceCleanOption(IExecutionContext executionContext, WorkspaceOptions workspace)
        {
            BuildCleanOption cleanOption = executionContext.Variables.Build_Clean ?? BuildCleanOption.None;
            switch (cleanOption)
            {
                case BuildCleanOption.Source:
                    return PipelineConstants.WorkspaceCleanOptions.Resources;
                case BuildCleanOption.Binary:
                    return PipelineConstants.WorkspaceCleanOptions.Outputs;
                case BuildCleanOption.All:
                    return PipelineConstants.WorkspaceCleanOptions.All;
            }

            if (!string.IsNullOrEmpty(workspace.Clean))
            {
                Dictionary<string, string> workspaceClean = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                workspaceClean[nameof(workspace.Clean)] = workspace.Clean;
                executionContext.Variables.ExpandValues(target: workspaceClean);
                VarUtil.ExpandEnvironmentVariables(HostContext, target: workspaceClean);
                return workspaceClean[nameof(workspace.Clean)];
            }
            else
            {
                return string.Empty;
            }
        }
    }


    // TODO: use enum defined in build2.webapi when it's available.
    public enum RepositoryCleanOptions
    {
        Source,
        SourceAndOutput,
        SourceDir,
        AllBuildDir,
    }
}