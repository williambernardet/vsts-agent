using Microsoft.TeamFoundation.Build.WebApi;
using Pipelines = Microsoft.TeamFoundation.DistributedTask.Pipelines;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.TeamFoundation.DistributedTask.Pipelines;

namespace Microsoft.VisualStudio.Services.Agent.Worker.Build
{
    public sealed class BuildJobExtension : JobExtension
    {
        public override Type ExtensionType => typeof(IJobExtension);
        public override HostTypes HostType => HostTypes.Build;

        private Pipelines.RepositoryResource Repository { set; get; }
        private ISourceProvider SourceProvider { set; get; }

        public override IStep GetExtensionPreJobStep(IExecutionContext jobContext)
        {
            return null;
        }

        public override IStep GetExtensionPostJobStep(IExecutionContext jobContext)
        {
            return null;
        }

        // 1. use source provide to solve path, if solved result is rooted, return full path.
        // 2. prefix default path root (build.sourcesDirectory), if result is rooted, return full path.
        public override string GetRootedPath(IExecutionContext context, string path)
        {
            string rootedPath = null;

            if (SourceProvider != null && Repository != null)
            {
                path = SourceProvider.GetLocalPath(context, Repository, path) ?? string.Empty;
                Trace.Info($"Build JobExtension resolving path use source provide: {path}");

                if (!string.IsNullOrEmpty(path) &&
                    path.IndexOfAny(Path.GetInvalidPathChars()) < 0 &&
                    Path.IsPathRooted(path))
                {
                    try
                    {
                        rootedPath = Path.GetFullPath(path);
                        Trace.Info($"Path resolved by source provider is a rooted path, return absolute path: {rootedPath}");
                        return rootedPath;
                    }
                    catch (Exception ex)
                    {
                        Trace.Info($"Path resolved by source provider is a rooted path, but it is not a full qualified path: {path}");
                        Trace.Error(ex);
                    }
                }
            }

            string defaultPathRoot = null;
            if (Repository != null)
            {
                defaultPathRoot = Repository.Properties.Get<string>(Pipelines.RepositoryPropertyNames.Path);
                Trace.Info($"The Default Path Root of Build JobExtension is build.sourcesDirectory: {defaultPathRoot}");
            }

            if (defaultPathRoot != null && defaultPathRoot.IndexOfAny(Path.GetInvalidPathChars()) < 0 &&
                path != null && path.IndexOfAny(Path.GetInvalidPathChars()) < 0)
            {
                path = Path.Combine(defaultPathRoot, path);
                Trace.Info($"After prefix Default Path Root provide by JobExtension: {path}");
                if (Path.IsPathRooted(path))
                {
                    try
                    {
                        rootedPath = Path.GetFullPath(path);
                        Trace.Info($"Return absolute path after prefix DefaultPathRoot: {rootedPath}");
                        return rootedPath;
                    }
                    catch (Exception ex)
                    {
                        Trace.Error(ex);
                        Trace.Info($"After prefix Default Path Root provide by JobExtension, the Path is a rooted path, but it is not full qualified, return the path: {path}.");
                        return path;
                    }
                }
            }

            return rootedPath;
        }

        public override void ConvertLocalPath(IExecutionContext context, string localPath, out string repoName, out string sourcePath)
        {
            repoName = "";

            // If no repo was found, send back an empty repo with original path.
            sourcePath = localPath;

            if (!string.IsNullOrEmpty(localPath))
            {
                foreach (var repository in context.Repositories)
                {
                    var name = Repository.Properties.Get<string>(Pipelines.RepositoryPropertyNames.Name);
                    var repoPath = Repository.Properties.Get<string>(Pipelines.RepositoryPropertyNames.Path);
                    if (!string.IsNullOrEmpty(repoPath))
                    {
                        var relativePath = IOUtil.MakeRelative(localPath, repoPath);
                        if (!string.Equals(relativePath, localPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar), IOUtil.FilePathStringComparison))
                        {
                            // If we find a repo and the file path is a relative path to the repository root path, return repo name and relative path.
                            repoName = name ?? string.Empty;
                            sourcePath = relativePath;
                            break;
                        }
                    }
                }
            }
        }

        // Prepare build directory
        // Set all build related variables
        public override void InitializeJobExtension(IExecutionContext executionContext, IList<Pipelines.JobStep> steps, Pipelines.WorkspaceOptions workspace)
        {
            // Validate args.
            Trace.Entering();
            ArgUtil.NotNull(executionContext, nameof(executionContext));

            // This flag can be false for jobs like cleanup artifacts.
            // If syncSources = false, we will not set source related build variable, not create build folder, not sync source.
            bool syncSources = executionContext.Variables.Build_SyncSources ?? true;
            if (!syncSources)
            {
                Trace.Info($"{Constants.Variables.Build.SyncSources} = false, we will not set source related build variable, not create build folder and not sync source");
                return;
            }

            // Prepare the build directory.
            executionContext.Output(StringUtil.Loc("PrepareBuildDir"));
            var directoryManager = HostContext.GetService<IBuildDirectoryManager>();
            var trackingConfig = directoryManager.PrepareDirectory(
                executionContext,
                workspace);

            // Set the directory variables.
            executionContext.Output(StringUtil.Loc("SetBuildVars"));
            string _workDirectory = HostContext.GetDirectory(WellKnownDirectory.Work);
            executionContext.SetVariable(Constants.Variables.Agent.BuildDirectory, Path.Combine(_workDirectory, trackingConfig.WorkspaceDirectory), isFilePath: true);
            executionContext.SetVariable(Constants.Variables.System.ArtifactsDirectory, Path.Combine(_workDirectory, trackingConfig.ArtifactsDirectory), isFilePath: true);
            executionContext.SetVariable(Constants.Variables.System.DefaultWorkingDirectory, Path.Combine(_workDirectory, trackingConfig.RepositoriesDirectory), isFilePath: true);
            executionContext.SetVariable(Constants.Variables.Common.TestResultsDirectory, Path.Combine(_workDirectory, trackingConfig.TestResultsDirectory), isFilePath: true);
            executionContext.SetVariable(Constants.Variables.Build.BinariesDirectory, Path.Combine(_workDirectory, trackingConfig.BinariesDirectory), isFilePath: true);
            executionContext.SetVariable(Constants.Variables.Build.SourcesDirectory, Path.Combine(_workDirectory, trackingConfig.RepositoriesDirectory), isFilePath: true);
            executionContext.SetVariable(Constants.Variables.Build.StagingDirectory, Path.Combine(_workDirectory, trackingConfig.ArtifactsDirectory), isFilePath: true);
            executionContext.SetVariable(Constants.Variables.Build.ArtifactStagingDirectory, Path.Combine(_workDirectory, trackingConfig.ArtifactsDirectory), isFilePath: true);

            // set variables triggering repository only
            var triggeringRepository = executionContext.Repositories.FirstOrDefault(x => x.Properties.Get<bool>("triggeringRepo"));
            if (triggeringRepository != null)
            {
                executionContext.Debug($"Primary repository: {triggeringRepository.Properties.Get<string>(Pipelines.RepositoryPropertyNames.Name)}. repository type: {triggeringRepository.Type}");
                // Set the repo variables.
                if (!string.IsNullOrEmpty(triggeringRepository.Id))
                {
                    executionContext.Variables.Set(Constants.Variables.Build.RepoId, triggeringRepository.Id);
                }

                executionContext.Variables.Set(Constants.Variables.Build.RepoName, triggeringRepository.Properties.Get<string>(Pipelines.RepositoryPropertyNames.Name));
                executionContext.Variables.Set(Constants.Variables.Build.RepoProvider, ConvertToLegacyRepositoryType(triggeringRepository.Type));
                executionContext.Variables.Set(Constants.Variables.Build.RepoUri, triggeringRepository.Url?.AbsoluteUri);

                var checkoutTask = steps.SingleOrDefault(x => x.IsCheckoutTask() &&
                                                              (x as Pipelines.TaskStep).Inputs.TryGetValue(Pipelines.PipelineConstants.CheckoutTaskInputs.Repository, out string checkoutRepo) &&
                                                              string.Equals(checkoutRepo, triggeringRepository.Alias, StringComparison.OrdinalIgnoreCase)) as Pipelines.TaskStep;
                if (checkoutTask != null)
                {
                    if (checkoutTask.Inputs.ContainsKey(Pipelines.PipelineConstants.CheckoutTaskInputs.Submodules))
                    {
                        executionContext.Variables.Set(Constants.Variables.Build.RepoGitSubmoduleCheckout, Boolean.TrueString);
                    }
                    else
                    {
                        executionContext.Variables.Set(Constants.Variables.Build.RepoGitSubmoduleCheckout, Boolean.FalseString);
                    }

                    // overwrite primary repository's clean value if build.repository.clean is sent from server. this is used by tfvc gated check-in
                    bool? repoClean = executionContext.Variables.GetBoolean(Constants.Variables.Build.RepoClean);
                    if (repoClean != null)
                    {
                        checkoutTask.Inputs[Pipelines.PipelineConstants.CheckoutTaskInputs.Clean] = repoClean.Value.ToString();
                    }
                    else
                    {
                        if (checkoutTask.Inputs.ContainsKey(Pipelines.PipelineConstants.CheckoutTaskInputs.Clean))
                        {
                            executionContext.Variables.Set(Constants.Variables.Build.RepoClean, checkoutTask.Inputs[Pipelines.PipelineConstants.CheckoutTaskInputs.Clean]);
                        }
                        else
                        {
                            executionContext.Variables.Set(Constants.Variables.Build.RepoClean, Boolean.FalseString);
                        }
                    }
                }

                executionContext.SetVariable(Constants.Variables.Build.RepoLocalPath, Path.Combine(_workDirectory, trackingConfig.Repositories[triggeringRepository.Alias].RepositoryDirectory), isFilePath: true);
            }
        }

        private string ConvertToLegacyRepositoryType(string pipelineRepositoryType)
        {
            if (String.Equals(pipelineRepositoryType, Pipelines.RepositoryTypes.Bitbucket, StringComparison.OrdinalIgnoreCase))
            {
                return "Bitbucket";
            }
            else if (String.Equals(pipelineRepositoryType, Pipelines.RepositoryTypes.ExternalGit, StringComparison.OrdinalIgnoreCase))
            {
                return "Git";
            }
            else if (String.Equals(pipelineRepositoryType, Pipelines.RepositoryTypes.Git, StringComparison.OrdinalIgnoreCase))
            {
                return "TfsGit";
            }
            else if (String.Equals(pipelineRepositoryType, Pipelines.RepositoryTypes.GitHub, StringComparison.OrdinalIgnoreCase))
            {
                return "GitHub";
            }
            else if (String.Equals(pipelineRepositoryType, Pipelines.RepositoryTypes.GitHubEnterprise, StringComparison.OrdinalIgnoreCase))
            {
                return "GitHubEnterprise";
            }
            else if (String.Equals(pipelineRepositoryType, Pipelines.RepositoryTypes.Svn, StringComparison.OrdinalIgnoreCase))
            {
                return "Svn";
            }
            else if (String.Equals(pipelineRepositoryType, Pipelines.RepositoryTypes.Tfvc, StringComparison.OrdinalIgnoreCase))
            {
                return "TfsVersionControl";
            }
            else
            {
                throw new NotSupportedException(pipelineRepositoryType);
            }
        }
    }
}