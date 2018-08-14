using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Globalization;
using Microsoft.TeamFoundation.DistributedTask.Pipelines;

namespace Microsoft.VisualStudio.Services.Agent.Worker.Build
{
    [ServiceLocator(Default = typeof(TrackingManager))]
    public interface ITrackingManager : IAgentService
    {
        TrackingConfig Create(
            IExecutionContext executionContext,
            RepositoryResource repository,
            string hashKey,
            string file,
            bool overrideBuildDirectory);

        PipelineTrackingConfig Create(
            IExecutionContext executionContext,
            string file);

        TrackingConfigBase LoadIfExists(IExecutionContext executionContext, string file);
        PipelineTrackingConfig LoadIfExistsV2(IExecutionContext executionContext, string file);

        void MarkForGarbageCollection(IExecutionContext executionContext, TrackingConfigBase config);

        void UpdateJobRunProperties(IExecutionContext executionContext, PipelineTrackingConfig config, string file);
        void UpdateJobRunProperties(IExecutionContext executionContext, TrackingConfig config, string file);

        void MarkExpiredForGarbageCollection(IExecutionContext executionContext, TimeSpan expiration);

        void DisposeCollectedGarbage(IExecutionContext executionContext);

        void MaintenanceStarted(TrackingConfig config, string file);

        void MaintenanceCompleted(TrackingConfig config, string file);
    }

    public sealed class TrackingManager : AgentService, ITrackingManager
    {
        public PipelineTrackingConfig Create(
            IExecutionContext executionContext,
            string file)
        {
            Trace.Entering();

            // Get or create the top-level tracking config.
            PipelineTopLevelTrackingConfig topLevelConfig = null;
            string topLevelFile = Path.Combine(
                HostContext.GetDirectory(WellKnownDirectory.Work),
                Constants.Build.Path.SourceRootMappingDirectory,
                Constants.Build.Path.TopLevelTrackingConfigFile);
            Trace.Verbose($"Loading top-level tracking config if exists: {topLevelFile}");

            if (File.Exists(topLevelFile))
            {
                topLevelConfig = IOUtil.LoadObject<PipelineTopLevelTrackingConfig>(File.ReadAllText(topLevelFile));
                if (topLevelConfig == null)
                {
                    var legacyTopLevelConfig = JsonConvert.DeserializeObject<TopLevelTrackingConfig>(File.ReadAllText(topLevelFile));
                    topLevelConfig = new PipelineTopLevelTrackingConfig()
                    {
                        LastPipelineDirectoryNumber = legacyTopLevelConfig.LastBuildDirectoryNumber,
                        LastPipelineDirectoryCreatedOn = legacyTopLevelConfig.LastBuildDirectoryCreatedOn
                    };
                }
            }

            // Try to populate the right directory number base on the current folder structure.
            if (topLevelConfig == null)
            {
                topLevelConfig = new PipelineTopLevelTrackingConfig();
                bool populatedDirectoryNumber = false;
                DirectoryInfo workDir = new DirectoryInfo(HostContext.GetDirectory(WellKnownDirectory.Work));
                foreach (var dir in workDir.EnumerateDirectories())
                {
                    // we scan the entire _work directory and find the directory with the highest integer number.
                    if (int.TryParse(dir.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int lastPipelineNumber) &&
                        lastPipelineNumber > topLevelConfig.LastPipelineDirectoryNumber)
                    {
                        populatedDirectoryNumber = true;
                        topLevelConfig.LastPipelineDirectoryNumber = lastPipelineNumber;
                    }
                }

                if (populatedDirectoryNumber)
                {
                    Trace.Info($"Populate last pipeline directory number '{topLevelConfig.LastPipelineDirectoryNumber}' base on existing job directories.");
                }
            }

            // Determine the pipeline directory.
            var configurationStore = HostContext.GetService<IConfigurationStore>();
            AgentSettings settings = configurationStore.GetSettings();
            if (settings.IsHosted && (executionContext.Repositories.Any(x => x.Type == "TfsVersionControl")))
            {
                // This should only occur during hosted builds. This was added due to TFVC.
                // TFVC does not allow a local path for a single machine to be mapped in multiple
                // workspaces. The machine name for a hosted images is not unique.
                //
                // So if a customer is running two hosted builds at the same time, they could run
                // into the local mapping conflict.
                //
                // The workaround is to force the build directory to be different across all concurrent
                // hosted builds (for TFVC). The agent ID will be unique across all concurrent hosted
                // builds so that can safely be used as the build directory.
                ArgUtil.Equal(default(int), topLevelConfig.LastPipelineDirectoryNumber, nameof(topLevelConfig.LastPipelineDirectoryNumber));
                topLevelConfig.LastPipelineDirectoryNumber = settings.AgentId;
            }
            else
            {
                topLevelConfig.LastPipelineDirectoryNumber++;
            }

            // Update the top-level tracking config.
            topLevelConfig.LastPipelineDirectoryCreatedOn = DateTimeOffset.Now;
            WriteToFile(topLevelFile, topLevelConfig);

            // Create the new tracking config.
            PipelineTrackingConfig config = new PipelineTrackingConfig(executionContext, topLevelConfig.LastPipelineDirectoryNumber.ToString(CultureInfo.InvariantCulture));
            WriteToFile(file, config);
            return config;
        }

        public TrackingConfig Create(
            IExecutionContext executionContext,
            RepositoryResource repository,
            string hashKey,
            string file,
            bool overrideBuildDirectory)
        {
            Trace.Entering();

            // Get or create the top-level tracking config.
            TopLevelTrackingConfig topLevelConfig;
            string topLevelFile = Path.Combine(
                HostContext.GetDirectory(WellKnownDirectory.Work),
                Constants.Build.Path.SourceRootMappingDirectory,
                Constants.Build.Path.TopLevelTrackingConfigFile);
            Trace.Verbose($"Loading top-level tracking config if exists: {topLevelFile}");
            if (!File.Exists(topLevelFile))
            {
                topLevelConfig = new TopLevelTrackingConfig();
            }
            else
            {
                topLevelConfig = JsonConvert.DeserializeObject<TopLevelTrackingConfig>(File.ReadAllText(topLevelFile));
                if (topLevelConfig == null)
                {
                    executionContext.Warning($"Rebuild corruptted top-level tracking configure file {topLevelFile}.");
                    // save the corruptted file in case we need to investigate more.
                    File.Copy(topLevelFile, $"{topLevelFile}.corruptted", true);

                    topLevelConfig = new TopLevelTrackingConfig();
                    DirectoryInfo workDir = new DirectoryInfo(HostContext.GetDirectory(WellKnownDirectory.Work));

                    foreach (var dir in workDir.EnumerateDirectories())
                    {
                        // we scan the entire _work directory and find the directory with the highest integer number.
                        if (int.TryParse(dir.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int lastBuildNumber) &&
                            lastBuildNumber > topLevelConfig.LastBuildDirectoryNumber)
                        {
                            topLevelConfig.LastBuildDirectoryNumber = lastBuildNumber;
                        }
                    }
                }
            }

            // Determine the build directory.
            if (overrideBuildDirectory)
            {
                // This should only occur during hosted builds. This was added due to TFVC.
                // TFVC does not allow a local path for a single machine to be mapped in multiple
                // workspaces. The machine name for a hosted images is not unique.
                //
                // So if a customer is running two hosted builds at the same time, they could run
                // into the local mapping conflict.
                //
                // The workaround is to force the build directory to be different across all concurrent
                // hosted builds (for TFVC). The agent ID will be unique across all concurrent hosted
                // builds so that can safely be used as the build directory.
                ArgUtil.Equal(default(int), topLevelConfig.LastBuildDirectoryNumber, nameof(topLevelConfig.LastBuildDirectoryNumber));
                var configurationStore = HostContext.GetService<IConfigurationStore>();
                AgentSettings settings = configurationStore.GetSettings();
                topLevelConfig.LastBuildDirectoryNumber = settings.AgentId;
            }
            else
            {
                topLevelConfig.LastBuildDirectoryNumber++;
            }

            // Update the top-level tracking config.
            topLevelConfig.LastBuildDirectoryCreatedOn = DateTimeOffset.Now;
            WriteToFile(topLevelFile, topLevelConfig);

            // Create the new tracking config.
            TrackingConfig config = new TrackingConfig(
                executionContext,
                repository,
                topLevelConfig.LastBuildDirectoryNumber,
                hashKey);
            WriteToFile(file, config);
            return config;
        }

        public void UpdateJobRunProperties(IExecutionContext executionContext, PipelineTrackingConfig config, string file)
        {
            // Update basic information
            config.CollectionUrl = executionContext.Variables.System_TFCollectionUrl;
            config.LastRunOn = DateTimeOffset.Now;
            switch (executionContext.Variables.System_HostType)
            {
                case HostTypes.Build:
                    config.DefinitionName = executionContext.Variables.Build_DefinitionName;
                    break;
                case HostTypes.Release | HostTypes.Deployment:
                    config.DefinitionName = executionContext.Variables.Get(Constants.Variables.Release.ReleaseDefinitionName);
                    break;
                default:
                    break;
            }

            // Deal with repository changes (TFVC is not supported for multi-checkout)
            // Loop through all incoming repositories, 
            //   Make sure local repositories not collision with incoming one (same alias but differnet repository url), move collision local repository to "gc" folder
            //   Update local repositories' lastRunOn to UTC.Now
            //   When add new repository, restructure the folder if there were only one repository.
            if (executionContext.Repositories.Count > 1 &&
                executionContext.Repositories.Any(x => string.Equals(x.Type, RepositoryTypes.Tfvc, StringComparison.OrdinalIgnoreCase)))
            {
                throw new NotSupportedException(RepositoryTypes.Tfvc);
            }

            foreach (var repository in executionContext.Repositories)
            {
                if (config.Repositories.TryGetValue(repository.Alias, out var localRepository))
                {
                    // a local repo with same alias exist, check repo type/url
                    if (string.Equals(localRepository.RepositoryType, repository.Type, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(localRepository.RepositoryUrl, repository.Url.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
                    {
                        // Update last run on
                        config.Repositories[repository.Alias].LastRunOn = DateTime.UtcNow;
                    }
                    else
                    {
                        // we need to gc the collision folder
                        var collisionDirectory = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), localRepository.RepositoryDirectory);
                        var gcDirectory = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), Constants.Agent.Path.GarbageCollectionDirectory, Guid.NewGuid().ToString("D"));
                        try
                        {
                            Directory.Move(collisionDirectory, gcDirectory);
                        }
                        catch (Exception ex)
                        {
                            // if we can't move the directory, we have to delete it to clear the way out for new repository. 
                            // this process is best effort, if we can't move the folder and also can't delete the folder, we will failed the job.
                            executionContext.Warning($"Fail to move directory {collisionDirectory} to GC directory, will try delete the directory. Error: {ex.ToString()}");
                            IOUtil.DeleteDirectory(collisionDirectory, CancellationToken.None);
                        }

                        config.Repositories[repository.Alias].RepositoryType = repository.Type;
                        config.Repositories[repository.Alias].RepositoryUrl = repository.Url.AbsoluteUri;
                        config.Repositories[repository.Alias].LastRunOn = DateTime.UtcNow;
                    }
                }
                else
                {
                    if (config.Repositories.Count == 0)
                    {
                        // this might happen when maintenance delete unused repositories
                        config.Repositories[repository.Alias] = new RepositoryTrackingConfig()
                        {
                            RepositoryType = repository.Type,
                            RepositoryUrl = repository.Url.AbsoluteUri,
                            RepositoryDirectory = config.RepositoriesDirectory,
                            LastRunOn = DateTime.UtcNow
                        };
                    }
                    else
                    {
                        // there is only one repo and it's repository folder is still 1/s, we need to move it from 1/s to 1/s/alias or delete it when move failed
                        var firstRepository = config.Repositories.First();
                        if (config.Repositories.Count == 1 &&
                            string.Equals(firstRepository.Value.RepositoryDirectory, config.RepositoriesDirectory, StringComparison.OrdinalIgnoreCase))
                        {
                            var currentDirectory = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), firstRepository.Value.RepositoryDirectory);
                            var newDirectory = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), config.RepositoriesDirectory, firstRepository.Key);
                            if (Directory.Exists(currentDirectory))
                            {
                                // move current /s to /s/alias since we have more repositories need to stored
                                executionContext.Debug($"Move existing repository directory from '{currentDirectory}' to '{newDirectory}'");
                                var stagingDir = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Temp), Guid.NewGuid().ToString("D"));
                                try
                                {
                                    Directory.Move(currentDirectory, stagingDir);
                                    Directory.Move(stagingDir, newDirectory);
                                }
                                catch (Exception ex)
                                {
                                    Trace.Error(ex);
                                    // if we can't move the folder and we can't delete the folder, just fail the job.
                                    IOUtil.DeleteDirectory(currentDirectory, CancellationToken.None);
                                }
                            }

                            firstRepository.Value.RepositoryDirectory = Path.Combine(config.RepositoriesDirectory, firstRepository.Key);
                        }

                        config.Repositories[repository.Alias] = new RepositoryTrackingConfig()
                        {
                            RepositoryType = repository.Type,
                            RepositoryUrl = repository.Url.AbsoluteUri,
                            RepositoryDirectory = Path.Combine(config.RepositoriesDirectory, repository.Alias),
                            LastRunOn = DateTime.UtcNow
                        };
                    }
                }
            }

            WriteToFile(file, config);
        }

        public TrackingConfigBase LoadIfExists(IExecutionContext executionContext, string file)
        {
            Trace.Entering();

            // The tracking config will not exist for a new definition.
            if (!File.Exists(file))
            {
                return null;
            }

            // Load the content and distinguish between tracking config file
            // version 1 and file version 2.
            string content = File.ReadAllText(file);
            string fileFormatVersionJsonProperty = StringUtil.Format(
                @"""{0}""",
                TrackingConfig.FileFormatVersionJsonProperty);
            if (content.Contains(fileFormatVersionJsonProperty))
            {
                // The config is the new format.
                Trace.Verbose("Parsing new tracking config format.");
                return JsonConvert.DeserializeObject<TrackingConfig>(content);
            }

            // Attempt to parse the legacy format.
            Trace.Verbose("Parsing legacy tracking config format.");
            LegacyTrackingConfig config = LegacyTrackingConfig.TryParse(content);
            if (config == null)
            {
                executionContext.Warning(StringUtil.Loc("UnableToParseBuildTrackingConfig0", content));
            }

            return config;
        }

        public PipelineTrackingConfig LoadIfExistsV2(IExecutionContext executionContext, string file)
        {
            Trace.Entering();

            // The tracking config will not exist for a new definition.
            if (!File.Exists(file))
            {
                return null;
            }

            return IOUtil.LoadObject<PipelineTrackingConfig>(file);
        }

        public void MarkForGarbageCollection(IExecutionContext executionContext, TrackingConfigBase config)
        {
            Trace.Entering();

            // Convert legacy format to the new format.
            LegacyTrackingConfig legacyConfig = config as LegacyTrackingConfig;
            if (legacyConfig != null)
            {
                // Convert legacy format to the new format.
                config = new TrackingConfig(
                    executionContext,
                    legacyConfig,
                    // The repository type and sources folder wasn't stored in the legacy format - only the
                    // build folder was stored. Since the hash key has changed, it is
                    // unknown what the source folder was named. Just set the folder name
                    // to "s" so the property isn't left blank. 
                    repositoryType: string.Empty,
                    sourcesDirectoryNameOnly: Constants.Build.Path.SourcesDirectory);
            }

            // Write a copy of the tracking config to the GC folder.
            string gcDirectory = Path.Combine(
                HostContext.GetDirectory(WellKnownDirectory.Work),
                Constants.Build.Path.SourceRootMappingDirectory,
                Constants.Build.Path.GarbageCollectionDirectory);
            string file = Path.Combine(
                gcDirectory,
                StringUtil.Format("{0}.json", Guid.NewGuid()));
            WriteToFile(file, config);
        }

        public void UpdateJobRunProperties(IExecutionContext executionContext, TrackingConfig config, string file)
        {
            Trace.Entering();

            // Update the info properties and save the file.
            config.UpdateJobRunProperties(executionContext);
            WriteToFile(file, config);
        }

        public void MaintenanceStarted(TrackingConfig config, string file)
        {
            Trace.Entering();
            config.LastMaintenanceAttemptedOn = DateTimeOffset.Now;
            config.LastMaintenanceCompletedOn = null;
            WriteToFile(file, config);
        }

        public void MaintenanceCompleted(TrackingConfig config, string file)
        {
            Trace.Entering();
            config.LastMaintenanceCompletedOn = DateTimeOffset.Now;
            WriteToFile(file, config);
        }

        public void MarkExpiredForGarbageCollection(IExecutionContext executionContext, TimeSpan expiration)
        {
            Trace.Entering();
            Trace.Info("Scan all SourceFolder tracking files.");
            string searchRoot = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), Constants.Build.Path.SourceRootMappingDirectory);
            if (!Directory.Exists(searchRoot))
            {
                executionContext.Output(StringUtil.Loc("GCDirNotExist", searchRoot));
                return;
            }

            var allTrackingFiles = Directory.EnumerateFiles(searchRoot, Constants.Build.Path.TrackingConfigFile, SearchOption.AllDirectories);
            Trace.Verbose($"Find {allTrackingFiles.Count()} tracking files.");

            executionContext.Output(StringUtil.Loc("DirExpireLimit", expiration.TotalDays));
            executionContext.Output(StringUtil.Loc("CurrentUTC", DateTime.UtcNow.ToString("o")));

            // scan all sourcefolder tracking file, find which folder has never been used since UTC-expiration
            // the scan and garbage discovery should be best effort.
            // if the tracking file is in old format, just delete the folder since the first time the folder been use we will convert the tracking file to new format.
            foreach (var trackingFile in allTrackingFiles)
            {
                try
                {
                    executionContext.Output(StringUtil.Loc("EvaluateTrackingFile", trackingFile));
                    TrackingConfigBase tracking = LoadIfExists(executionContext, trackingFile);

                    // detect whether the tracking file is in new format.
                    TrackingConfig newTracking = tracking as TrackingConfig;
                    if (newTracking == null)
                    {
                        LegacyTrackingConfig legacyConfig = tracking as LegacyTrackingConfig;
                        ArgUtil.NotNull(legacyConfig, nameof(LegacyTrackingConfig));

                        Trace.Verbose($"{trackingFile} is a old format tracking file.");

                        executionContext.Output(StringUtil.Loc("GCOldFormatTrackingFile", trackingFile));
                        MarkForGarbageCollection(executionContext, legacyConfig);
                        IOUtil.DeleteFile(trackingFile);
                    }
                    else
                    {
                        Trace.Verbose($"{trackingFile} is a new format tracking file.");
                        ArgUtil.NotNull(newTracking.LastRunOn, nameof(newTracking.LastRunOn));
                        executionContext.Output(StringUtil.Loc("BuildDirLastUseTIme", Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), newTracking.BuildDirectory), newTracking.LastRunOnString));
                        if (DateTime.UtcNow - expiration > newTracking.LastRunOn)
                        {
                            executionContext.Output(StringUtil.Loc("GCUnusedTrackingFile", trackingFile, expiration.TotalDays));
                            MarkForGarbageCollection(executionContext, newTracking);
                            IOUtil.DeleteFile(trackingFile);
                        }
                    }
                }
                catch (Exception ex)
                {
                    executionContext.Error(StringUtil.Loc("ErrorDuringBuildGC", trackingFile));
                    executionContext.Error(ex);
                }
            }
        }

        public void DisposeCollectedGarbage(IExecutionContext executionContext)
        {
            Trace.Entering();
            PrintOutDiskUsage(executionContext);

            string gcDirectory = Path.Combine(
                HostContext.GetDirectory(WellKnownDirectory.Work),
                Constants.Build.Path.SourceRootMappingDirectory,
                Constants.Build.Path.GarbageCollectionDirectory);

            if (!Directory.Exists(gcDirectory))
            {
                executionContext.Output(StringUtil.Loc("GCDirNotExist", gcDirectory));
                return;
            }

            IEnumerable<string> gcTrackingFiles = Directory.EnumerateFiles(gcDirectory, "*.json");
            if (gcTrackingFiles == null || gcTrackingFiles.Count() == 0)
            {
                executionContext.Output(StringUtil.Loc("GCDirIsEmpty", gcDirectory));
                return;
            }

            Trace.Info($"Find {gcTrackingFiles.Count()} GC tracking files.");

            if (gcTrackingFiles.Count() > 0)
            {
                foreach (string gcFile in gcTrackingFiles)
                {
                    // maintenance has been cancelled.
                    executionContext.CancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        var gcConfig = LoadIfExists(executionContext, gcFile) as TrackingConfig;
                        ArgUtil.NotNull(gcConfig, nameof(TrackingConfig));

                        string fullPath = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Work), gcConfig.BuildDirectory);
                        executionContext.Output(StringUtil.Loc("Deleting", fullPath));
                        IOUtil.DeleteDirectory(fullPath, executionContext.CancellationToken);

                        executionContext.Output(StringUtil.Loc("DeleteGCTrackingFile", fullPath));
                        IOUtil.DeleteFile(gcFile);
                    }
                    catch (Exception ex)
                    {
                        executionContext.Error(StringUtil.Loc("ErrorDuringBuildGCDelete", gcFile));
                        executionContext.Error(ex);
                    }
                }

                PrintOutDiskUsage(executionContext);
            }
        }

        private void PrintOutDiskUsage(IExecutionContext context)
        {
            // Print disk usage should be best effort, since DriveInfo can't detect usage of UNC share.
            try
            {
                context.Output($"Disk usage for working directory: {HostContext.GetDirectory(WellKnownDirectory.Work)}");
                var workDirectoryDrive = new DriveInfo(HostContext.GetDirectory(WellKnownDirectory.Work));
                long freeSpace = workDirectoryDrive.AvailableFreeSpace;
                long totalSpace = workDirectoryDrive.TotalSize;
#if OS_WINDOWS
                context.Output($"Working directory belongs to drive: '{workDirectoryDrive.Name}'");
#else
                context.Output($"Information about file system on which working directory resides.");
#endif
                context.Output($"Total size: '{totalSpace / 1024.0 / 1024.0} MB'");
                context.Output($"Available space: '{freeSpace / 1024.0 / 1024.0} MB'");
            }
            catch (Exception ex)
            {
                context.Warning($"Unable inspect disk usage for working directory {HostContext.GetDirectory(WellKnownDirectory.Work)}.");
                Trace.Error(ex);
                context.Debug(ex.ToString());
            }
        }

        private void WriteToFile(string file, object value)
        {
            Trace.Entering();
            Trace.Verbose($"Writing config to file: {file}");

            // Create the directory if it does not exist.
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            IOUtil.SaveObject(value, file);
        }
    }
}