using Microsoft.TeamFoundation.DistributedTask.Pipelines;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;

namespace Microsoft.VisualStudio.Services.Agent.Worker.Build
{
    public sealed class TrackingConfig : TrackingConfigBase
    {
        public const string FileFormatVersionJsonProperty = "fileFormatVersion";

        // The parameterless constructor is required for deserialization.
        public TrackingConfig()
        {
        }

        public TrackingConfig(
            IExecutionContext executionContext,
            LegacyTrackingConfig copy,
            string sourcesDirectoryNameOnly,
            string repositoryType,
            bool useNewArtifactsDirectoryName = false)
        {
            // Set the directories.
            BuildDirectory = Path.GetFileName(copy.BuildDirectory); // Just take the portion after _work folder.
            string artifactsDirectoryNameOnly =
                useNewArtifactsDirectoryName ? Constants.Build.Path.ArtifactsDirectory : Constants.Build.Path.LegacyArtifactsDirectory;
            ArtifactsDirectory = Path.Combine(BuildDirectory, artifactsDirectoryNameOnly);
            SourcesDirectory = Path.Combine(BuildDirectory, sourcesDirectoryNameOnly);
            TestResultsDirectory = Path.Combine(BuildDirectory, Constants.Build.Path.TestResultsDirectory);

            // Set the other properties.
            CollectionId = copy.CollectionId;
            CollectionUrl = executionContext.Variables.System_TFCollectionUrl;
            DefinitionId = copy.DefinitionId;
            HashKey = copy.HashKey;
            RepositoryType = repositoryType;
            RepositoryUrl = copy.RepositoryUrl;
            System = copy.System;
        }

        public TrackingConfig(
            IExecutionContext executionContext,
            RepositoryResource repository,
            int buildDirectory,
            string hashKey)
        {
            // Set the directories.
            BuildDirectory = buildDirectory.ToString(CultureInfo.InvariantCulture);
            ArtifactsDirectory = Path.Combine(BuildDirectory, Constants.Build.Path.ArtifactsDirectory);
            SourcesDirectory = Path.Combine(BuildDirectory, Constants.Build.Path.SourcesDirectory);
            TestResultsDirectory = Path.Combine(BuildDirectory, Constants.Build.Path.TestResultsDirectory);

            // Set the other properties.
            CollectionId = executionContext.Variables.System_CollectionId;
            DefinitionId = executionContext.Variables.System_DefinitionId;
            HashKey = hashKey;
            RepositoryUrl = repository.Url.AbsoluteUri;
            RepositoryType = repository.Type;
            System = BuildSystem;
            UpdateJobRunProperties(executionContext);
        }

        [JsonProperty("build_artifactstagingdirectory")]
        public string ArtifactsDirectory { get; set; }

        [JsonProperty("agent_builddirectory")]
        public string BuildDirectory { get; set; }

        public string CollectionUrl { get; set; }

        public string DefinitionName { get; set; }

        [JsonProperty(FileFormatVersionJsonProperty)]
        public int FileFormatVersion
        {
            get
            {
                return 3;
            }

            set
            {
                // Version 3 changes:
                //   CollectionName was removed.
                //   CollectionUrl was added.
                switch (value)
                {
                    case 3:
                    case 2:
                        break;
                    default:
                        // Should never reach here.
                        throw new NotSupportedException();
                }
            }
        }

        [JsonIgnore]
        public DateTimeOffset? LastRunOn { get; set; }

        [JsonProperty("lastRunOn")]
        [EditorBrowsableAttribute(EditorBrowsableState.Never)]
        public string LastRunOnString
        {
            get
            {
                return string.Format(CultureInfo.InvariantCulture, "{0}", LastRunOn);
            }

            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    LastRunOn = null;
                    return;
                }

                LastRunOn = DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
            }
        }

        public string RepositoryType { get; set; }

        [JsonIgnore]
        public DateTimeOffset? LastMaintenanceAttemptedOn { get; set; }

        [JsonProperty("lastMaintenanceAttemptedOn")]
        [EditorBrowsableAttribute(EditorBrowsableState.Never)]
        public string LastMaintenanceAttemptedOnString
        {
            get
            {
                return string.Format(CultureInfo.InvariantCulture, "{0}", LastMaintenanceAttemptedOn);
            }

            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    LastMaintenanceAttemptedOn = null;
                    return;
                }

                LastMaintenanceAttemptedOn = DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
            }
        }

        [JsonIgnore]
        public DateTimeOffset? LastMaintenanceCompletedOn { get; set; }

        [JsonProperty("lastMaintenanceCompletedOn")]
        [EditorBrowsableAttribute(EditorBrowsableState.Never)]
        public string LastMaintenanceCompletedOnString
        {
            get
            {
                return string.Format(CultureInfo.InvariantCulture, "{0}", LastMaintenanceCompletedOn);
            }

            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    LastMaintenanceCompletedOn = null;
                    return;
                }

                LastMaintenanceCompletedOn = DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
            }
        }

        [JsonProperty("build_sourcesdirectory")]
        public string SourcesDirectory { get; set; }

        [JsonProperty("common_testresultsdirectory")]
        public string TestResultsDirectory { get; set; }

        public void UpdateJobRunProperties(IExecutionContext executionContext)
        {
            CollectionUrl = executionContext.Variables.System_TFCollectionUrl;
            DefinitionName = executionContext.Variables.Build_DefinitionName;
            LastRunOn = DateTimeOffset.Now;
        }
    }

    public class PipelineTrackingConfig
    {
        private Dictionary<string, RepositoryTrackingConfig> _repositories;

        public PipelineTrackingConfig()
        { }

        public PipelineTrackingConfig(
            IExecutionContext executionContext,
            string workspaceDirectory)
        {
            // Set basic properties
            CollectionId = executionContext.Variables.System_CollectionId;
            CollectionUrl = executionContext.Variables.System_TFCollectionUrl;
            DefinitionId = executionContext.Variables.System_DefinitionId;
            switch (executionContext.Variables.System_HostType)
            {
                case HostTypes.Build:
                    DefinitionName = executionContext.Variables.Build_DefinitionName;
                    break;
                case HostTypes.Release | HostTypes.Deployment:
                    DefinitionName = executionContext.Variables.Get(Constants.Variables.Release.ReleaseDefinitionName);
                    break;
                default:
                    break;
            }
            LastRunOn = DateTimeOffset.Now;

            // Set the directories.
            WorkspaceDirectory = workspaceDirectory;
            RepositoriesDirectory = Path.Combine(workspaceDirectory, Constants.Agent.Path.SourcesDirectory);    // 1/s dir
            TestResultsDirectory = Path.Combine(workspaceDirectory, Constants.Agent.Path.TestResultsDirectory); // 1/TestResult dir
            ArtifactsDirectory = Path.Combine(workspaceDirectory, Constants.Agent.Path.ArtifactsDirectory);     // 1/a dir
            BinariesDirectory = Path.Combine(workspaceDirectory, Constants.Agent.Path.BinariesDirectory);       // 1/b dir

            // tracking repository resources
            if (executionContext.Repositories.Count > 0)
            {
                // if there is one repository, we will keep using the layout format we have today, _work/1/s 
                // if there are multiple repositories, we will put each repository under the sub-dir of its alias, _work/1/s/self
                if (executionContext.Repositories.Count == 1)
                {
                    var repo = executionContext.Repositories[0];
                    Repositories[repo.Alias] = new RepositoryTrackingConfig()
                    {
                        RepositoryType = repo.Type,
                        RepositoryUrl = repo.Url.AbsoluteUri,
                        RepositoryDirectory = RepositoriesDirectory,
                        LastRunOn = DateTime.UtcNow
                    };
                }
                else
                {
                    // multiple repositories
                    foreach (var repo in executionContext.Repositories)
                    {
                        Repositories[repo.Alias] = new RepositoryTrackingConfig()
                        {
                            RepositoryType = repo.Type,
                            RepositoryUrl = repo.Url.AbsoluteUri,
                            RepositoryDirectory = Path.Combine(RepositoriesDirectory, repo.Alias),
                            LastRunOn = DateTime.UtcNow
                        };
                    }
                }
            }
        }

        public string CollectionId { get; set; }
        public string CollectionUrl { get; set; }
        public string DefinitionId { get; set; }
        public string DefinitionName { get; set; }

        [JsonIgnore]
        public DateTimeOffset? LastRunOn { get; set; }

        [JsonProperty("lastRunOn")]
        [EditorBrowsableAttribute(EditorBrowsableState.Never)]
        public string LastRunOnString
        {
            get
            {
                return string.Format(CultureInfo.InvariantCulture, "{0}", LastRunOn);
            }

            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    LastRunOn = null;
                    return;
                }

                LastRunOn = DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
            }
        }

        public string WorkspaceDirectory { get; set; }
        public string TestResultsDirectory { get; set; }
        public string RepositoriesDirectory { get; set; }
        public string ArtifactsDirectory { get; set; }
        public string BinariesDirectory { get; set; }

        public Dictionary<string, RepositoryTrackingConfig> Repositories
        {
            get
            {
                if (_repositories == null)
                {
                    _repositories = new Dictionary<string, RepositoryTrackingConfig>(StringComparer.OrdinalIgnoreCase);
                }
                return _repositories;
            }
        }
    }

    public sealed class RepositoryTrackingConfig
    {
        public string RepositoryUrl { get; set; }
        public string RepositoryType { get; set; }
        public string RepositoryDirectory { get; set; }

        [JsonIgnore]
        public DateTimeOffset? LastRunOn { get; set; }

        [JsonProperty("lastRunOn")]
        [EditorBrowsableAttribute(EditorBrowsableState.Never)]
        public string LastRunOnString
        {
            get
            {
                return string.Format(CultureInfo.InvariantCulture, "{0}", LastRunOn);
            }

            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    LastRunOn = null;
                    return;
                }

                LastRunOn = DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
            }
        }

        [JsonIgnore]
        public DateTimeOffset? LastMaintenanceAttemptedOn { get; set; }

        [JsonProperty("lastMaintenanceAttemptedOn")]
        [EditorBrowsableAttribute(EditorBrowsableState.Never)]
        public string LastMaintenanceAttemptedOnString
        {
            get
            {
                return string.Format(CultureInfo.InvariantCulture, "{0}", LastMaintenanceAttemptedOn);
            }

            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    LastMaintenanceAttemptedOn = null;
                    return;
                }

                LastMaintenanceAttemptedOn = DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
            }
        }

        [JsonIgnore]
        public DateTimeOffset? LastMaintenanceCompletedOn { get; set; }

        [JsonProperty("lastMaintenanceCompletedOn")]
        [EditorBrowsableAttribute(EditorBrowsableState.Never)]
        public string LastMaintenanceCompletedOnString
        {
            get
            {
                return string.Format(CultureInfo.InvariantCulture, "{0}", LastMaintenanceCompletedOn);
            }

            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    LastMaintenanceCompletedOn = null;
                    return;
                }

                LastMaintenanceCompletedOn = DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
            }
        }
    }
}